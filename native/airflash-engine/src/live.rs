//! Native loopback -> one resampler -> time-bounded PCM queue.
use crate::{
    rtp::FRAMES,
    wasapi::{Mmcss, qpc_ns},
};
use anyhow::{Context, Result, ensure};
use rubato::{
    Resampler, SincFixedIn, SincInterpolationParameters, SincInterpolationType, WindowFunction,
};
use serde::Serialize;
use std::{
    collections::VecDeque,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use windows::{
    Win32::{
        Devices::FunctionDiscovery::PKEY_Device_FriendlyName,
        Foundation::{CloseHandle, WAIT_OBJECT_0},
        Media::Audio::*,
        System::{
            Com::{StructuredStorage::*, *},
            Threading::*,
        },
    },
    core::Interface,
};
#[derive(Clone, Copy)]
struct Frame {
    left: f32,
    right: f32,
    qpc: u64,
}
#[derive(Default, Clone, Serialize)]
pub struct Metrics {
    pub dropped_frames: u64,
    pub underrun_packets: u64,
    pub capture_frames: u64,
    pub max_queue_age_ms: f64,
    pub capture_to_send_p95_ms: Option<f64>,
    pub last_audio_qpc_ns: u64,
    pub input_rate: u32,
}
struct State {
    frames: VecDeque<Frame>,
    capacity: usize,
    target: usize,
    error: Option<String>,
    metrics: Metrics,
    ages: VecDeque<f64>,
}
impl State {
    fn push_frame(&mut self, frame: Frame) {
        if self.frames.len() >= self.capacity {
            self.frames.pop_front();
            self.metrics.dropped_frames += 1;
        }
        self.frames.push_back(frame);
    }
}
pub struct Loopback {
    state: Arc<Mutex<State>>,
    stop: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
}
impl Loopback {
    pub fn start(endpoint: Option<String>, rate: u32) -> Result<Self> {
        let capacity = rate as usize * 60 / 1000;
        let target = rate as usize * 20 / 1000;
        let state = Arc::new(Mutex::new(State {
            frames: VecDeque::with_capacity(capacity),
            capacity,
            target,
            error: None,
            metrics: Metrics::default(),
            ages: VecDeque::with_capacity(4000),
        }));
        let stop = Arc::new(AtomicBool::new(false));
        let child_stop = stop.clone();
        let child_state = state.clone();
        let (tx, rx) = mpsc::sync_channel(1);
        let worker = thread::Builder::new()
            .name("wasapi-loopback".into())
            .spawn(move || {
                if let Err(e) = capture(endpoint, rate, child_stop, &child_state, &tx) {
                    let message = format!("{e:#}");
                    child_state.lock().unwrap().error = Some(message.clone());
                    let _ = tx.try_send(Err(message));
                }
            })?;
        match rx.recv_timeout(Duration::from_secs(3)) {
            Ok(Ok(())) => Ok(Self {
                state,
                stop,
                worker: Some(worker),
            }),
            result => {
                stop.store(true, Ordering::Release);
                let _ = worker.join();
                anyhow::bail!("loopback initialization: {result:?}");
            }
        }
    }
    pub fn ready(&self) -> Result<bool> {
        let s = self.state.lock().unwrap();
        if let Some(e) = &s.error {
            anyhow::bail!("{e}");
        }
        Ok(s.frames.len() >= s.target)
    }
    pub fn packet(&self, gain: f32) -> Result<(Vec<u8>, Option<u64>)> {
        let mut s = self.state.lock().unwrap();
        if let Some(e) = &s.error {
            anyhow::bail!("capture stopped: {e}");
        }
        let mut out = Vec::with_capacity(FRAMES * 4);
        let mut marker = None;
        let now = qpc_ns();
        let mut underrun = false;
        for _ in 0..FRAMES {
            let frame = if let Some(f) = s.frames.pop_front() {
                f
            } else {
                underrun = true;
                Frame {
                    left: 0.0,
                    right: 0.0,
                    qpc: now,
                }
            };
            if frame.left.abs().max(frame.right.abs()) > 0.004 {
                s.metrics.last_audio_qpc_ns = frame.qpc;
                marker.get_or_insert(frame.qpc);
            }
            let age = now.saturating_sub(frame.qpc) as f64 / 1e6;
            s.metrics.max_queue_age_ms = s.metrics.max_queue_age_ms.max(age);
            if s.ages.len() == 4000 {
                s.ages.pop_front();
            }
            s.ages.push_back(age);
            for sample in [frame.left, frame.right] {
                let value = (sample * gain).clamp(-1.0, 1.0);
                out.extend(((value * 32767.0).round() as i16).to_be_bytes());
            }
        }
        if underrun {
            s.metrics.underrun_packets += 1;
        }
        Ok((out, marker))
    }
    /// Restore the capture queue to its normal target after a scheduler stall.
    pub fn discard_stale(&self) {
        let mut s = self.state.lock().unwrap();
        let cutoff = qpc_ns().saturating_sub(20_000_000);
        while s.frames.front().is_some_and(|frame| frame.qpc < cutoff) || s.frames.len() > s.target {
            s.frames.pop_front();
            s.metrics.dropped_frames += 1;
        }
    }
    pub fn metrics(&self) -> Metrics {
        let s = self.state.lock().unwrap();
        let mut metrics = s.metrics.clone();
        let mut ages: Vec<_> = s.ages.iter().copied().collect();
        ages.sort_by(f64::total_cmp);
        metrics.capture_to_send_p95_ms = ages.get(ages.len() * 95 / 100).copied();
        metrics
    }
}
impl Drop for Loopback {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
    }
}
struct ComGuard;
impl Drop for ComGuard {
    fn drop(&mut self) {
        unsafe { CoUninitialize() }
    }
}
struct AudioGuard(IAudioClient);
impl Drop for AudioGuard {
    fn drop(&mut self) {
        unsafe {
            let _ = self.0.Stop();
        }
    }
}
struct EventGuard(windows::Win32::Foundation::HANDLE);
impl Drop for EventGuard {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}
fn find_device(enumerator: &IMMDeviceEnumerator, name: Option<&str>) -> Result<IMMDevice> {
    let Some(name) = name.filter(|s| !s.is_empty()) else {
        return Ok(unsafe { enumerator.GetDefaultAudioEndpoint(eRender, eConsole) }?);
    };
    let devices = unsafe { enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE) }?;
    for i in 0..unsafe { devices.GetCount() }? {
        let device = unsafe { devices.Item(i) }?;
        let id = unsafe { device.GetId() }?;
        let identifier = unsafe { id.to_string() }?;
        unsafe { CoTaskMemFree(Some(id.0.cast())) };
        if identifier == name {
            return Ok(device);
        }
        let store = unsafe { device.OpenPropertyStore(STGM_READ) }?;
        let mut value = unsafe { store.GetValue(&PKEY_Device_FriendlyName) }?;
        let text = unsafe { PropVariantToStringAlloc(&value) };
        let friendly = match text {
            Ok(text) => {
                let result = unsafe { text.to_string() };
                unsafe { CoTaskMemFree(Some(text.0.cast())) };
                result.ok()
            }
            Err(_) => None,
        };
        unsafe { PropVariantClear(&mut value) }?;
        if friendly.as_deref() == Some(name) {
            return Ok(device);
        }
    }
    anyhow::bail!("capture endpoint unavailable: {name}")
}
fn capture(
    endpoint: Option<String>,
    stream_rate: u32,
    stop: Arc<AtomicBool>,
    state: &Arc<Mutex<State>>,
    ready: &mpsc::SyncSender<std::result::Result<(), String>>,
) -> Result<()> {
    unsafe { CoInitializeEx(None, COINIT_MULTITHREADED).ok() }?;
    let _com = ComGuard;
    let _priority = Mmcss::new();
    let enumerator: IMMDeviceEnumerator =
        unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }?;
    let device = find_device(&enumerator, endpoint.as_deref())?;
    let client: IAudioClient = unsafe { device.Activate(CLSCTX_ALL, None) }?;
    let pointer = unsafe { client.GetMixFormat() }?;
    let spec = unsafe { pointer.read_unaligned() };
    let channels = spec.nChannels as usize;
    let bits = spec.wBitsPerSample;
    let rate = spec.nSamplesPerSec;
    let tag = if spec.wFormatTag == 0xfffe {
        unsafe { (pointer as *const WAVEFORMATEXTENSIBLE).read_unaligned() }
            .SubFormat
            .data1 as u16
    } else {
        spec.wFormatTag
    };
    let flags = AUDCLNT_STREAMFLAGS_LOOPBACK
        | AUDCLNT_STREAMFLAGS_EVENTCALLBACK
        | AUDCLNT_STREAMFLAGS_NOPERSIST;
    let event = EventGuard(unsafe { CreateEventW(None, false, false, None) }?);
    // Prefer the shared engine period; fall back to documented shared initialization.
    let initialized = if let Ok(client3) = client.cast::<IAudioClient3>() {
        let (mut default, mut fundamental, mut minimum, mut maximum) = (0, 0, 0, 0);
        unsafe {
            client3.GetSharedModeEnginePeriod(
                pointer,
                &mut default,
                &mut fundamental,
                &mut minimum,
                &mut maximum,
            )
        }
        .and_then(|_| unsafe { client3.InitializeSharedAudioStream(flags, default, pointer, None) })
    } else {
        Err(windows::core::Error::from_hresult(windows::core::HRESULT(
            -1,
        )))
    };
    let initialized = initialized.or_else(|_| unsafe {
        client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 200_000, 0, pointer, None)
    });
    unsafe { CoTaskMemFree(Some(pointer.cast())) };
    initialized?;
    ensure!(
        channels > 0 && channels <= 8 && (8000..=192000).contains(&rate),
        "unsupported capture format"
    );
    ensure!(
        (tag == 3 && bits == 32) || (tag == 1 && [16, 24, 32].contains(&bits)),
        "unsupported capture sample type"
    );
    unsafe { client.SetEventHandle(event.0) }?;
    let reader: IAudioCaptureClient = unsafe { client.GetService() }?;
    let chunk = (rate / 100) as usize;
    let mut resampler = SincFixedIn::<f32>::new(
        stream_rate as f64 / rate as f64,
        1.001,
        SincInterpolationParameters {
            sinc_len: 64,
            f_cutoff: 0.9,
            interpolation: SincInterpolationType::Cubic,
            oversampling_factor: 128,
            window: WindowFunction::BlackmanHarris2,
        },
        chunk,
        2,
    )?;
    let mut inputs = [VecDeque::<f32>::new(), VecDeque::<f32>::new()];
    let mut input_times = VecDeque::new();
    unsafe { client.Start() }?;
    let _audio = AudioGuard(client);
    state.lock().unwrap().metrics.input_rate = rate;
    let _ = ready.send(Ok(()));
    let mut last_packet = Instant::now();
    while !stop.load(Ordering::Acquire) {
        if unsafe { WaitForSingleObject(event.0, 50) } != WAIT_OBJECT_0 {
            // Silent render endpoints may emit no loopback packets. Sender pads silence.
            if last_packet.elapsed() > Duration::from_secs(2) {
                unsafe { reader.GetNextPacketSize() }.context("capture endpoint disconnected")?;
            }
            continue;
        }
        while unsafe { reader.GetNextPacketSize() }? > 0 {
            let (mut data, mut frames, mut flags, mut position, mut qpc) =
                (std::ptr::null_mut(), 0, 0, 0, 0);
            unsafe {
                reader.GetBuffer(
                    &mut data,
                    &mut frames,
                    &mut flags,
                    Some(&mut position),
                    Some(&mut qpc),
                )
            }?;
            let silent = flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0;
            let timestamp = if flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR.0 as u32 != 0 {
                qpc_ns().saturating_sub(frames as u64 * 1_000_000_000 / rate as u64)
            } else {
                qpc * 100
            };
            if flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY.0 as u32 != 0 {
                inputs[0].clear();
                inputs[1].clear();
                input_times.clear();
                resampler.reset();
            }
            let bytes = if data.is_null() {
                &[][..]
            } else {
                unsafe {
                    std::slice::from_raw_parts(data, frames as usize * spec.nBlockAlign as usize)
                }
            };
            for i in 0..frames as usize {
                for (channel, input) in inputs.iter_mut().enumerate() {
                    let value = if silent || bytes.is_empty() {
                        0.0
                    } else {
                        let offset = i * spec.nBlockAlign as usize
                            + channel.min(channels - 1) * bits as usize / 8;
                        let b = &bytes[offset..offset + bits as usize / 8];
                        match (tag, bits) {
                            (3, 32) => f32::from_le_bytes(b.try_into().unwrap()),
                            (1, 16) => i16::from_le_bytes(b.try_into().unwrap()) as f32 / 32768.0,
                            (1, 24) => {
                                i32::from_le_bytes([0, b[0], b[1], b[2]]) as f32 / 2147483648.0
                            }
                            (1, 32) => {
                                i32::from_le_bytes(b.try_into().unwrap()) as f32 / 2147483648.0
                            }
                            _ => unreachable!(),
                        }
                    };
                    input.push_back(if value.is_finite() { value } else { 0.0 });
                }
                input_times.push_back(timestamp + i as u64 * 1_000_000_000 / rate as u64);
            }
            unsafe { reader.ReleaseBuffer(frames) }?;
            last_packet = Instant::now();
            state.lock().unwrap().metrics.capture_frames += frames as u64;
            while inputs[0].len() >= chunk {
                let start = input_times[0];
                let input: Vec<Vec<f32>> = inputs
                    .iter_mut()
                    .map(|q| q.drain(..chunk).collect())
                    .collect();
                input_times.drain(..chunk);
                let (have, target) = {
                    let s = state.lock().unwrap();
                    (s.frames.len(), s.target)
                };
                let correction =
                    ((target as f64 - have as f64) / stream_rate as f64 * 0.01).clamp(-0.0005, 0.0005);
                resampler.set_resample_ratio_relative(1.0 + correction, true)?;
                let output = resampler.process(&input, None)?;
                let delay = resampler.output_delay() as u64 * 1_000_000_000 / stream_rate as u64;
                let mut shared = state.lock().unwrap();
                for (i, (&left, &right)) in output[0].iter().zip(&output[1]).enumerate() {
                    shared.push_frame(Frame {
                        left,
                        right,
                        qpc: start.saturating_sub(delay) + i as u64 * 1_000_000_000 / stream_rate as u64,
                    });
                }
            }
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    const CAPACITY: usize = crate::rtp::RATE as usize * 60 / 1000;
    const TARGET: usize = crate::rtp::RATE as usize * 20 / 1000;
    fn source() -> Loopback {
        Loopback {
            state: Arc::new(Mutex::new(State {
                frames: VecDeque::new(),
                capacity: CAPACITY,
                target: TARGET,
                error: None,
                metrics: Metrics::default(),
                ages: VecDeque::new(),
            })),
            stop: Arc::new(AtomicBool::new(false)),
            worker: None,
        }
    }
    #[test]
    fn underrun_pads_silence_and_can_resume_audio() {
        let source = source();
        assert!(source.packet(0.1).unwrap().0.iter().all(|b| *b == 0));
        assert_eq!(source.metrics().underrun_packets, 1);
        for _ in 0..FRAMES {
            source.state.lock().unwrap().push_frame(Frame {
                left: 0.01,
                right: 0.01,
                qpc: qpc_ns(),
            });
        }
        assert!(source.packet(0.1).unwrap().0.iter().any(|b| *b != 0));
        assert_eq!(source.metrics().underrun_packets, 1);
    }
    #[test]
    fn overflow_and_scheduler_recovery_drop_old_frames_without_failure() {
        let source = source();
        {
            let mut s = source.state.lock().unwrap();
            for _ in 0..CAPACITY + 100 {
                s.push_frame(Frame {
                    left: 0.01,
                    right: 0.01,
                    qpc: 0,
                });
            }
            assert_eq!(s.frames.len(), CAPACITY);
        }
        assert_eq!(source.metrics().dropped_frames, 100);
        source.discard_stale();
        assert_eq!(source.metrics().dropped_frames, (CAPACITY + 100) as u64);
        assert!(source.packet(0.1).unwrap().0.iter().all(|b| *b == 0));
        assert_eq!(source.metrics().underrun_packets, 1);
    }
}
