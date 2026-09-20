//! Event-driven WASAPI observation with hardware QPC timestamps for qualification.
//! COM and MMCSS are initialized and released on the owning capture thread.
use anyhow::{Context, Result, ensure};
use serde::Serialize;
use std::{
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use windows::{
    Win32::{
        Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0},
        Media::Audio::*,
        System::{Com::*, Performance::*, Threading::*},
    },
    core::w,
};

pub fn qpc_ns() -> u64 {
    let mut count = 0;
    let mut frequency = 0;
    unsafe {
        QueryPerformanceCounter(&mut count).expect("QPC");
        QueryPerformanceFrequency(&mut frequency).expect("QPF");
    }
    (count as u128 * 1_000_000_000 / frequency as u128) as u64
}
pub struct Mmcss(Option<HANDLE>);
impl Default for Mmcss {
    fn default() -> Self {
        Self::new()
    }
}
impl Mmcss {
    pub fn new() -> Self {
        let mut index = 0;
        Self(unsafe { AvSetMmThreadCharacteristicsW(w!("Pro Audio"), &mut index) }.ok())
    }
}
impl Drop for Mmcss {
    fn drop(&mut self) {
        if let Some(h) = self.0.take() {
            unsafe {
                let _ = AvRevertMmThreadCharacteristics(h);
            }
        }
    }
}
struct Com;
impl Drop for Com {
    fn drop(&mut self) {
        unsafe { CoUninitialize() }
    }
}
struct Event(HANDLE);
impl Drop for Event {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}
struct Format(*mut WAVEFORMATEX);
impl Drop for Format {
    fn drop(&mut self) {
        unsafe { CoTaskMemFree(Some(self.0.cast())) }
    }
}
#[derive(Serialize)]
pub struct Block {
    offset: usize,
    frames: usize,
    qpc_ns: u64,
    device_position: u64,
    flags: u32,
}
#[derive(Serialize)]
pub struct Recording {
    pub sample_rate: u32,
    pub frames: usize,
    pub blocks: Vec<Block>,
    pub path: PathBuf,
}
pub struct Microphone {
    stop: Arc<AtomicBool>,
    worker: Option<JoinHandle<Result<Recording>>>,
}
impl Microphone {
    pub fn start(path: PathBuf) -> Result<Self> {
        let stop = Arc::new(AtomicBool::new(false));
        let done = stop.clone();
        let (tx, rx) = mpsc::sync_channel(1);
        let worker = thread::Builder::new()
            .name("wasapi-microphone".into())
            .spawn(move || {
                let result = capture(path, done, &tx);
                if let Err(e) = &result {
                    let _ = tx.try_send(Err(format!("{e:#}")));
                }
                result
            })?;
        let status = rx.recv_timeout(Duration::from_secs(3));
        match status {
            Ok(Ok(())) => Ok(Self {
                stop,
                worker: Some(worker),
            }),
            other => {
                stop.store(true, Ordering::Release);
                let _ = worker.join();
                anyhow::bail!("microphone initialization failed: {other:?}");
            }
        }
    }
    pub fn finish(mut self) -> Result<Recording> {
        self.stop.store(true, Ordering::Release);
        self.worker
            .take()
            .unwrap()
            .join()
            .map_err(|_| anyhow::anyhow!("microphone worker panicked"))?
    }
}
impl Drop for Microphone {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
    }
}
fn capture(
    path: PathBuf,
    stop: Arc<AtomicBool>,
    ready: &mpsc::SyncSender<std::result::Result<(), String>>,
) -> Result<Recording> {
    unsafe {
        CoInitializeEx(None, COINIT_MULTITHREADED).ok()?;
    }
    let _com = Com;
    let _priority = Mmcss::new();
    let enumerator: IMMDeviceEnumerator =
        unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }?;
    let device = unsafe { enumerator.GetDefaultAudioEndpoint(eCapture, eConsole) }?;
    let client: IAudioClient = unsafe { device.Activate(CLSCTX_ALL, None) }?;
    let format = Format(unsafe { client.GetMixFormat() }?);
    let spec = unsafe { format.0.read_unaligned() };
    let channels = spec.nChannels as usize;
    let bits = spec.wBitsPerSample;
    let rate = spec.nSamplesPerSec;
    let tag = if spec.wFormatTag == 0xfffe {
        ensure!(spec.cbSize >= 22, "invalid extensible format");
        unsafe { (format.0 as *const WAVEFORMATEXTENSIBLE).read_unaligned() }
            .SubFormat
            .data1 as u16
    } else {
        spec.wFormatTag
    };
    ensure!(
        channels > 0 && channels <= 8 && (8000..=192000).contains(&rate),
        "unsupported microphone format"
    );
    ensure!(
        (tag == 3 && bits == 32) || (tag == 1 && [16, 24, 32].contains(&bits)),
        "unsupported microphone sample type"
    );
    let event = Event(unsafe { CreateEventW(None, false, false, None) }?);
    unsafe {
        client.Initialize(
            AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_NOPERSIST,
            1_000_000,
            0,
            format.0,
            None,
        )?;
        client.SetEventHandle(event.0)?;
    }
    let reader: IAudioCaptureClient = unsafe { client.GetService() }?;
    unsafe { client.Start() }?;
    let _ = ready.send(Ok(()));
    let start = Instant::now();
    let mut samples = Vec::<f32>::new();
    let mut blocks = Vec::new();
    let result = (|| -> Result<()> {
        while !stop.load(Ordering::Acquire) && start.elapsed() < Duration::from_secs(55) {
            if unsafe { WaitForSingleObject(event.0, 50) } != WAIT_OBJECT_0 {
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
                let offset = samples.len();
                let bytes_per_sample = bits as usize / 8;
                if flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0 {
                    samples.resize(offset + frames as usize, 0.0);
                } else if !data.is_null() {
                    let bytes = unsafe {
                        std::slice::from_raw_parts(
                            data,
                            frames as usize * spec.nBlockAlign as usize,
                        )
                    };
                    for frame in bytes.chunks_exact(spec.nBlockAlign as usize) {
                        let b = &frame[..bytes_per_sample];
                        let value = match (tag, bits) {
                            (3, 32) => f32::from_le_bytes(b.try_into().unwrap()),
                            (1, 16) => i16::from_le_bytes(b.try_into().unwrap()) as f32 / 32768.0,
                            (1, 24) => {
                                i32::from_le_bytes([0, b[0], b[1], b[2]]) as f32 / 2147483648.0
                            }
                            (1, 32) => {
                                i32::from_le_bytes(b.try_into().unwrap()) as f32 / 2147483648.0
                            }
                            _ => unreachable!(),
                        };
                        samples.push(if value.is_finite() { value } else { 0.0 });
                    }
                } else {
                    unsafe { reader.ReleaseBuffer(frames) }?;
                    anyhow::bail!("null microphone buffer");
                }
                blocks.push(Block {
                    offset,
                    frames: frames as usize,
                    qpc_ns: qpc * 100,
                    device_position: position,
                    flags,
                });
                unsafe { reader.ReleaseBuffer(frames) }?;
                ensure!(
                    samples.len() <= rate as usize * 56,
                    "microphone recording limit exceeded"
                );
            }
        }
        Ok(())
    })();
    unsafe { client.Stop() }?;
    result?;
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let mut writer = hound::WavWriter::create(
        &path,
        hound::WavSpec {
            channels: 1,
            sample_rate: rate,
            bits_per_sample: 32,
            sample_format: hound::SampleFormat::Float,
        },
    )
    .context("create microphone recording")?;
    for value in &samples {
        writer.write_sample(*value)?;
    }
    writer.finalize()?;
    let report = Recording {
        sample_rate: rate,
        frames: samples.len(),
        blocks,
        path: path.clone(),
    };
    std::fs::write(
        path.with_extension("timestamps.json"),
        serde_json::to_vec_pretty(&report)?,
    )?;
    Ok(report)
}
