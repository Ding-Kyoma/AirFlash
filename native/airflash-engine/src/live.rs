//! Native playback loopback or recording input -> one resampler -> bounded PCM queue.
use crate::{
    applications::MAX_CAPTURE_INSTANCES,
    rtp::FRAMES,
    wasapi::{Mmcss, qpc_ns},
};
use anyhow::{Context, Result, ensure};
use rubato::{
    Resampler, SincFixedIn, SincInterpolationParameters, SincInterpolationType, WindowFunction,
};
use serde::{Deserialize, Serialize};
use std::{
    collections::{BTreeMap, VecDeque},
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
        Foundation::{CloseHandle, WAIT_EVENT, WAIT_FAILED, WAIT_TIMEOUT},
        Media::Audio::*,
        System::{
            Com::{StructuredStorage::*, *},
            Threading::*,
        },
    },
    core::Interface,
};
/// Consecutive failed rebinds before the capture thread gives up and lets the
/// session restart (the desktop then reconnects, as it did before P3-8).
const REBIND_MAX_FAILURES: u32 = 3;
/// Pause between rebind attempts so a vanished device does not spin the thread.
const REBIND_BACKOFF: Duration = Duration::from_millis(250);
/// A stream that delivers nothing at all only becomes a rebind probe after ten
/// seconds without packets past the normal poll grace. A short burst of stale samples does not
/// restore the budget: only thirty seconds of contiguous audible audio re-arms
/// the probe, and repeated probes back off along `SILENT_PROBE_INTERVALS`.
const SILENT_PROBE_WINDOW: Duration = Duration::from_secs(10);
const SILENT_PROBE_REARM: Duration = Duration::from_secs(30);
/// Gaps between consecutive silence probes: one minute, then five, then twenty.
/// A probe that did not restore sustained audio must not run every two seconds;
/// that pattern replayed the stale loopback tail and rebuilt the pipeline
/// forever instead of leaving a merely idle endpoint alone.
const SILENT_PROBE_INTERVALS: [Duration; 3] = [
    Duration::from_secs(60),
    Duration::from_secs(300),
    Duration::from_secs(1200),
];
/// Without packets, wait this long after the last one before counting silence.
/// Zero-filled packets mean the endpoint is alive and never trigger a probe.
const NO_PACKET_SILENCE_AFTER: Duration = Duration::from_millis(250);
/// Report three successful rebinds inside this window once per `STORM_WARNING_GAP`.
const REBIND_STORM_WINDOW: Duration = Duration::from_secs(60);
const REBIND_STORM_COUNT: usize = 3;
const STORM_WARNING_GAP: Duration = Duration::from_secs(300);
const WARNING_QUEUE_CAP: usize = 16;

#[derive(Clone, Copy)]
struct Frame {
    left: f32,
    right: f32,
    qpc: u64,
}
struct QueuedFrame {
    frame: Frame,
    splice_before: bool,
}
#[derive(Default, Clone, Serialize, Deserialize)]
pub struct Metrics {
    pub transition_count: u64,
    pub silence_pad_frames: u64,
    pub scheduled_silence_frames: u64,
    pub discontinuities: u64,
    /// Discontinuity flags that were proven benign (the packet continues the last).
    pub discontinuities_ignored: u64,
    pub last_discontinuity: Option<String>,
    pub mmcss_registered: bool,
    pub mmcss_priority_set: bool,
    pub dropped_frames: u64,
    pub underrun_packets: u64,
    /// Missing capture frame slots at the output rate, before equalization.
    #[serde(default)]
    pub padded_frames: u64,
    /// Shared pulls missing every capture slot, excluding scheduled silence.
    #[serde(default)]
    pub fully_padded_packets: u64,
    pub capture_frames: u64,
    pub max_queue_age_ms: f64,
    pub capture_to_send_p95_ms: Option<f64>,
    pub last_audio_qpc_ns: u64,
    pub input_rate: u32,
    /// Successful in-process rebinds (endpoint reopen or process reattach).
    #[serde(default)]
    pub rebinds: u64,
    /// Rebind attempts that could not open a replacement stream.
    #[serde(default)]
    pub rebind_failures: u64,
    /// Application instances currently captured; one for an endpoint stream.
    #[serde(default)]
    pub capture_instances: u32,
    /// Silence probes that triggered a rebind (one per backoff interval).
    #[serde(default)]
    pub silent_probes: u64,
    /// `capture_rebind_storm` warnings raised: the endpoint kept being reopened
    /// without sustained audio returning, so the rebinds were not recovery.
    #[serde(default)]
    pub rebind_storms: u64,
    /// Most successful rebinds seen inside one `REBIND_STORM_WINDOW`.
    #[serde(default)]
    pub rebind_window_max: u32,
    /// Why the last rebind happened (`process_exit`, `endpoint_lost`,
    /// `silent_probe`, `stream_error`).
    #[serde(default)]
    pub last_rebind_reason: Option<String>,
}
impl Metrics {
    /// Keep the legacy waiting-event shape when no capture worker exists.
    pub fn unavailable_snapshot() -> serde_json::Value {
        let mut metrics = serde_json::json!(Self::default());
        if let Some(fields) = metrics.as_object_mut() {
            fields.remove("padded_frames");
            fields.remove("fully_padded_packets");
        }
        metrics
    }
}
/// A `{"event":"warning"}`-shaped state change raised by the capture thread.
/// The session owns the JSON framing because the capture thread has no emitter.
#[derive(Clone, Debug)]
pub struct CaptureWarning {
    pub code: &'static str,
    pub recovered: bool,
    pub message: String,
}
struct State {
    splice: crate::continuity::Splice,
    quantizer: crate::continuity::Quantizer,
    boundary_pending: bool,
    rebind_boundary_pending: bool,
    last_pull_ended_missing: bool,
    equalizer: crate::equalizer::Processor,
    frames: VecDeque<QueuedFrame>,
    capacity: usize,
    target: usize,
    error: Option<String>,
    ended: bool,
    metrics: Metrics,
    ages: VecDeque<f64>,
    warnings: VecDeque<CaptureWarning>,
}
impl State {
    fn push_frame(&mut self, frame: Frame) {
        if frame.left.abs().max(frame.right.abs()) > 0.004 {
            self.metrics.last_audio_qpc_ns = frame.qpc;
        }
        if self.frames.len() >= self.capacity {
            self.frames.pop_front();
            self.metrics.dropped_frames += 1;
            self.boundary_pending = true;
            self.metrics.last_discontinuity = Some("queue_overflow".into());
        }
        self.frames.push_back(QueuedFrame {
            frame,
            splice_before: std::mem::take(&mut self.rebind_boundary_pending),
        });
    }
    /// Keep queued output from the old stream. Splice when the sender reaches
    /// the first frame produced by the reopened stream.
    fn note_rebind(&mut self, reason: &str) {
        self.rebind_boundary_pending = true;
        self.metrics.discontinuities += 1;
        self.metrics.last_discontinuity = Some(reason.into());
    }
    fn warn(&mut self, warning: CaptureWarning) {
        if self.warnings.len() == WARNING_QUEUE_CAP {
            self.warnings.pop_front();
        }
        self.warnings.push_back(warning);
    }
}
fn warn(state: &Arc<Mutex<State>>, code: &'static str, recovered: bool, message: String) {
    state.lock().unwrap().warn(CaptureWarning {
        code,
        recovered,
        message,
    });
}
enum Target {
    Endpoint(Option<String>),
    Application {
        path: String,
        pinned: Option<(u32, u64)>,
    },
}

pub struct Loopback {
    equalizer_control: crate::equalizer::Control,
    state: Arc<Mutex<State>>,
    stop: Arc<AtomicBool>,
    /// Slow outer loop: observed playout lateness -> resampler bias.
    servo: Arc<Mutex<crate::servo::LatenessServo>>,
    /// Monitor only: capture-clock drift estimate surfaced in telemetry.
    drift: Arc<Mutex<crate::servo::DriftEstimator>>,
    worker: Option<JoinHandle<()>>,
}
impl Loopback {
    pub fn start(
        endpoint: Option<String>,
        rate: u32,
        equalizer: crate::equalizer::Control,
    ) -> Result<Self> {
        Self::start_target(Target::Endpoint(endpoint), rate, equalizer)
    }
    /// Capture one application target. `pinned` is true when the IPC request
    /// named a pid + creation time: while that instance lives, it is the only
    /// one captured; after it exits the path-wide plan takes over.
    pub fn start_application(
        application: crate::applications::Application,
        pinned: bool,
        rate: u32,
        equalizer: crate::equalizer::Control,
    ) -> Result<Self> {
        Self::start_target(
            Target::Application {
                path: application.path,
                pinned: pinned.then_some((application.process_id, application.started_at)),
            },
            rate,
            equalizer,
        )
    }
    fn start_target(
        target: Target,
        rate: u32,
        equalizer: crate::equalizer::Control,
    ) -> Result<Self> {
        let capacity = rate as usize * 60 / 1000;
        let queue_target = rate as usize * 20 / 1000;
        let state = Arc::new(Mutex::new(State {
            splice: crate::continuity::Splice::new(rate),
            quantizer: crate::continuity::Quantizer::new(),
            boundary_pending: false,
            rebind_boundary_pending: false,
            last_pull_ended_missing: false,
            equalizer: crate::equalizer::Processor::new(equalizer.initial(), rate),
            frames: VecDeque::with_capacity(capacity),
            capacity,
            target: queue_target,
            error: None,
            ended: false,
            metrics: Metrics::default(),
            ages: VecDeque::with_capacity(4000),
            warnings: VecDeque::new(),
        }));
        let stop = Arc::new(AtomicBool::new(false));
        let child_stop = stop.clone();
        let child_state = state.clone();
        let servo = Arc::new(Mutex::new(crate::servo::LatenessServo::default()));
        let drift = Arc::new(Mutex::new(crate::servo::DriftEstimator::new(
            Duration::from_secs(30),
            rate as f64,
        )));
        let child_servo = servo.clone();
        let child_drift = drift.clone();
        let (tx, rx) = mpsc::sync_channel(1);
        let worker = thread::Builder::new()
            .name("wasapi-capture".into())
            .spawn(move || {
                if let Err(e) = capture(
                    target,
                    rate,
                    child_stop,
                    &child_state,
                    &child_servo,
                    &child_drift,
                    &tx,
                ) {
                    let message = format!("{e:#}");
                    child_state.lock().unwrap().error = Some(message.clone());
                    let _ = tx.try_send(Err(message));
                }
            })?;
        match rx.recv_timeout(Duration::from_secs(3)) {
            Ok(Ok(())) => Ok(Self {
                equalizer_control: equalizer,
                state,
                stop,
                servo,
                drift,
                worker: Some(worker),
            }),
            result => {
                stop.store(true, Ordering::Release);
                let _ = worker.join();
                anyhow::bail!("audio capture initialization: {result:?}");
            }
        }
    }
    pub fn last_audio(&self) -> u64 {
        self.state.lock().unwrap().metrics.last_audio_qpc_ns
    }
    /// One lateness sample per packet from the send loop. Positive means the packet
    /// woke after its deadline; the servo turns a sustained bias into a small
    /// resampler correction so the steady-state error is removed, not just damped.
    pub fn report_lateness(&self, late_ms: f64) {
        self.servo.lock().unwrap().update(late_ms, Instant::now());
    }
    /// Snapshot for telemetry: nothing here is needed to run the loop.
    pub fn servo_metrics(&self) -> crate::servo::ServoCounters {
        self.servo.lock().unwrap().counters()
    }
    /// Captured drift estimate in parts per billion; zero until the window fills.
    pub fn drift_ppb(&self) -> f64 {
        self.drift.lock().unwrap().ppb()
    }
    pub fn ended(&self) -> bool {
        self.state.lock().unwrap().ended
    }
    /// Capture-side warnings queued since the last call. The session turns them
    /// into the same `warning` events as every other subsystem.
    pub fn take_warnings(&self) -> Vec<CaptureWarning> {
        self.state.lock().unwrap().warnings.drain(..).collect()
    }
    pub fn ready(&self) -> Result<bool> {
        let s = self.state.lock().unwrap();
        if let Some(e) = &s.error {
            anyhow::bail!("{e}");
        }
        Ok(s.frames.len() >= s.target)
    }
    pub fn packet(&self, gain: f32) -> Result<(Vec<u8>, Option<u64>)> {
        self.packet_inner(gain, false)
    }
    pub fn silence_packet(&self, gain: f32) -> Result<(Vec<u8>, Option<u64>)> {
        self.packet_inner(gain, true)
    }
    fn packet_inner(&self, gain: f32, force_silence: bool) -> Result<(Vec<u8>, Option<u64>)> {
        let mut s = self.state.lock().unwrap();
        if let Some((sequence, prepared)) = self.equalizer_control.latest() {
            s.equalizer.update(sequence, prepared);
        }
        if let Some(e) = &s.error {
            anyhow::bail!("capture stopped: {e}");
        }
        let mut out = Vec::with_capacity(FRAMES * 4);
        let mut marker = None;
        let now = qpc_ns();
        let mut padded = 0u64;
        // A trim or a return from missing audio already splices at the queue head.
        let splice_at_head = s.boundary_pending || s.last_pull_ended_missing;
        if s.boundary_pending {
            s.splice.boundary();
            s.boundary_pending = false;
        }
        for index in 0..FRAMES {
            let missing = force_silence || s.frames.is_empty();
            let frame = if let Some(queued) = if force_silence {
                None
            } else {
                s.frames.pop_front()
            } {
                if queued.splice_before && (index != 0 || !splice_at_head) {
                    s.splice.boundary();
                }
                let age = now.saturating_sub(queued.frame.qpc) as f64 / 1e6;
                s.metrics.max_queue_age_ms = s.metrics.max_queue_age_ms.max(age);
                if s.ages.len() == 4000 {
                    s.ages.pop_front();
                }
                s.ages.push_back(age);
                queued.frame
            } else {
                if force_silence {
                    s.metrics.scheduled_silence_frames += 1;
                } else {
                    padded += 1;
                }
                s.metrics.silence_pad_frames += 1;
                Frame {
                    left: 0.0,
                    right: 0.0,
                    qpc: now,
                }
            };
            if frame.left.abs().max(frame.right.abs()) > 0.004 {
                marker.get_or_insert(frame.qpc);
            }
            let processed = s.equalizer.frame([frame.left, frame.right]);
            for value in s.splice.frame(processed, gain, missing) {
                out.extend(s.quantizer.quantize(value).to_be_bytes());
            }
        }
        // While this pull holds the lock, missing slots can only be a suffix.
        s.last_pull_ended_missing = force_silence || padded != 0;
        if padded != 0 {
            s.metrics.underrun_packets += 1;
            s.metrics.padded_frames = s.metrics.padded_frames.saturating_add(padded);
            if padded == FRAMES as u64 {
                s.metrics.fully_padded_packets = s.metrics.fully_padded_packets.saturating_add(1);
            }
        }
        s.metrics.transition_count = s.splice.transitions;
        Ok((out, marker))
    }
    /// Restore the capture queue to its normal target after a scheduler stall.
    pub fn discard_stale(&self) {
        let mut s = self.state.lock().unwrap();
        let cutoff = qpc_ns().saturating_sub(20_000_000);
        while s
            .frames
            .front()
            .is_some_and(|queued| queued.frame.qpc < cutoff)
            || s.frames.len() > s.target
        {
            s.frames.pop_front();
            s.metrics.dropped_frames += 1;
        }
        s.boundary_pending = true;
        s.metrics.last_discontinuity = Some("scheduler_recovery".into());
    }
    /// Age of the oldest capture frame queued at a sender deadline. An empty
    /// queue has no observation for the `capture_age` histogram.
    pub fn sample_age_ms(&self) -> Option<f64> {
        let s = self.state.lock().unwrap();
        let queued = s.frames.front()?;
        Some(qpc_ns().saturating_sub(queued.frame.qpc) as f64 / 1e6)
    }
    /// `(queued, capacity)` in source frames.
    pub fn depth(&self) -> (usize, usize) {
        let s = self.state.lock().unwrap();
        (s.frames.len(), s.capacity)
    }
    pub fn metrics(&self) -> Metrics {
        let (mut metrics, mut ages) = {
            let s = self.state.lock().unwrap();
            (
                s.metrics.clone(),
                s.ages.iter().copied().collect::<Vec<_>>(),
            )
        };
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
/// Owns the mixer format returned by `GetMixFormat` until the stream has been
/// initialized; the callback owns the pointer until then.
struct MixFormat(*mut WAVEFORMATEX);
impl Drop for MixFormat {
    fn drop(&mut self) {
        unsafe { CoTaskMemFree(Some(self.0.cast())) }
    }
}
// IDs take precedence over friendly names, including names that resemble an ID.
fn endpoint_index(name: &str, endpoints: &[(String, Option<String>)]) -> Result<usize> {
    if let Some(index) = endpoints.iter().position(|(id, _)| id == name) {
        return Ok(index);
    }
    let mut matches = endpoints
        .iter()
        .enumerate()
        .filter(|(_, (_, friendly))| friendly.as_deref() == Some(name));
    let Some((index, _)) = matches.next() else {
        anyhow::bail!("capture endpoint unavailable: {name}");
    };
    ensure!(
        matches.next().is_none(),
        "capture endpoint name is ambiguous; select a device ID: {name}"
    );
    Ok(index)
}

/// A WASAPI discontinuity flag is benign when this packet starts exactly where the
/// previous one ended: the loopback buffer wrapped, the stream did not break.
pub(crate) fn continues_previous(last_end: Option<u64>, position: u64) -> bool {
    last_end.is_some_and(|end| end == position)
}

fn capture_flags(flow: EDataFlow) -> Result<u32> {
    let flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_NOPERSIST;
    if flow == eRender {
        Ok(flags | AUDCLNT_STREAMFLAGS_LOOPBACK)
    } else {
        ensure!(flow == eCapture, "unsupported capture endpoint direction");
        Ok(flags)
    }
}

fn find_device(enumerator: &IMMDeviceEnumerator, name: Option<&str>) -> Result<IMMDevice> {
    let Some(name) = name.filter(|s| !s.is_empty()) else {
        return Ok(unsafe { enumerator.GetDefaultAudioEndpoint(eRender, eConsole) }?);
    };
    let devices = unsafe { enumerator.EnumAudioEndpoints(eAll, DEVICE_STATE_ACTIVE) }?;
    let mut endpoints = Vec::new();
    for i in 0..unsafe { devices.GetCount() }? {
        let device = unsafe { devices.Item(i) }?;
        let id = unsafe { device.GetId() }?;
        let identifier = unsafe { id.to_string() };
        unsafe { CoTaskMemFree(Some(id.0.cast())) };
        endpoints.push((identifier?, None));
    }
    // Avoid reading unrelated friendly-name properties when the request uses an ID.
    if let Some(index) = endpoints.iter().position(|(id, _)| id == name) {
        return Ok(unsafe { devices.Item(index as u32) }?);
    }
    for (i, (_, friendly)) in endpoints.iter_mut().enumerate() {
        let device = unsafe { devices.Item(i as u32) }?;
        let store = unsafe { device.OpenPropertyStore(STGM_READ) }?;
        let mut value = unsafe { store.GetValue(&PKEY_Device_FriendlyName) }?;
        if let Ok(text) = unsafe { PropVariantToStringAlloc(&value) } {
            *friendly = unsafe { text.to_string() }.ok();
            unsafe { CoTaskMemFree(Some(text.0.cast())) };
        }
        unsafe { PropVariantClear(&mut value) }?;
    }
    Ok(unsafe { devices.Item(endpoint_index(name, &endpoints)? as u32) }?)
}

#[derive(Clone, Copy)]
struct Format {
    tag: u16,
    channels: usize,
    bits: usize,
    rate: u32,
}

fn initialize_client(
    client: &IAudioClient,
    flags: u32,
    pointer: *const WAVEFORMATEX,
    prefer_engine_period: bool,
) -> windows::core::Result<()> {
    let initialized = prefer_engine_period
        .then(|| client.cast::<IAudioClient3>().ok())
        .flatten()
        .and_then(|client3| {
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
            .ok()?;
            unsafe { client3.InitializeSharedAudioStream(flags, default, pointer, None) }.ok()
        });
    match initialized {
        Some(()) => Ok(()),
        None => unsafe {
            client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 200_000, 0, pointer, None)
        },
    }
}

struct Binding {
    // Stops the stream before the client reference and event handle go away.
    _audio: AudioGuard,
    reader: IAudioCaptureClient,
    event: EventGuard,
    process: Option<crate::applications::Process>,
    spec: WAVEFORMATEX,
    tag: u16,
    channels: usize,
    bits: usize,
    rate: u32,
    /// Device position where this client's previous packet ended; used to tell
    /// a benign loopback wrap from a real capture gap.
    last_end: Option<u64>,
}
impl Binding {
    fn finish(
        client: IAudioClient,
        spec: WAVEFORMATEX,
        tag: u16,
        process: Option<crate::applications::Process>,
    ) -> Result<Self> {
        let channels = spec.nChannels as usize;
        let bits = spec.wBitsPerSample as usize;
        let rate = spec.nSamplesPerSec;
        ensure!(
            channels > 0 && channels <= 8 && (8000..=192000).contains(&rate),
            "unsupported capture format"
        );
        ensure!(
            (tag == 3 && bits == 32) || (tag == 1 && [16, 24, 32].contains(&bits)),
            "unsupported capture sample type"
        );
        let event = EventGuard(unsafe { CreateEventW(None, false, false, None) }?);
        unsafe { client.SetEventHandle(event.0) }?;
        let reader: IAudioCaptureClient = unsafe { client.GetService() }?;
        unsafe { client.Start() }?;
        Ok(Self {
            _audio: AudioGuard(client),
            reader,
            event,
            process,
            spec,
            tag,
            channels,
            bits,
            rate,
            last_end: None,
        })
    }
    fn open_endpoint(name: Option<&str>) -> Result<Self> {
        let enumerator: IMMDeviceEnumerator =
            unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }?;
        let device = find_device(&enumerator, name)?;
        let flow = unsafe { device.cast::<IMMEndpoint>()?.GetDataFlow() }?;
        let client: IAudioClient = unsafe { device.Activate(CLSCTX_ALL, None) }?;
        let pointer = MixFormat(unsafe { client.GetMixFormat() }?);
        let spec = unsafe { pointer.0.read_unaligned() };
        let tag = if spec.wFormatTag == 0xfffe {
            unsafe { (pointer.0 as *const WAVEFORMATEXTENSIBLE).read_unaligned() }
                .SubFormat
                .data1 as u16
        } else {
            spec.wFormatTag
        };
        initialize_client(&client, capture_flags(flow)?, pointer.0, true)?;
        Self::finish(client, spec, tag, None)
    }
    fn open_application(
        application: &crate::applications::Application,
        stream_rate: u32,
        stop: &AtomicBool,
    ) -> Result<Self> {
        let process = crate::applications::Process::verify(application)?;
        let client = crate::applications::activate(application.process_id, stop)?;
        let spec = WAVEFORMATEX {
            wFormatTag: 1,
            nChannels: 2,
            nSamplesPerSec: stream_rate,
            nAvgBytesPerSec: stream_rate * 4,
            nBlockAlign: 4,
            wBitsPerSample: 16,
            cbSize: 0,
        };
        let flags = AUDCLNT_STREAMFLAGS_LOOPBACK
            | AUDCLNT_STREAMFLAGS_EVENTCALLBACK
            | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM;
        initialize_client(&client, flags, &spec, false)?;
        Self::finish(client, spec, spec.wFormatTag, Some(process))
    }
    fn format(&self) -> Format {
        Format {
            tag: self.tag,
            channels: self.channels,
            bits: self.bits,
            rate: self.rate,
        }
    }
    /// Decode one interleaved sample. Mono sources are duplicated; extra
    /// channels beyond stereo are ignored (the previous behaviour).
    fn sample(&self, bytes: &[u8], frame: usize, channel: usize) -> f32 {
        let width = self.bits / 8;
        let offset =
            frame * self.spec.nBlockAlign as usize + channel.min(self.channels - 1) * width;
        let b = &bytes[offset..offset + width];
        match (self.tag, self.bits) {
            (3, 32) => f32::from_le_bytes(b.try_into().unwrap()),
            (1, 16) => i16::from_le_bytes(b.try_into().unwrap()) as f32 / 32768.0,
            (1, 24) => i32::from_le_bytes([0, b[0], b[1], b[2]]) as f32 / 2147483648.0,
            (1, 32) => i32::from_le_bytes(b.try_into().unwrap()) as f32 / 2147483648.0,
            _ => 0.0,
        }
    }
}

fn bind_target(target: &Target, stream_rate: u32, stop: &AtomicBool) -> Result<Vec<Binding>> {
    match target {
        Target::Endpoint(name) => Ok(vec![Binding::open_endpoint(name.as_deref())?]),
        Target::Application { path, pinned } => {
            // A pinned request already carries a verified identity. It may name
            // a process that path discovery intentionally ignores (the engine's
            // own process is excluded from `matching`), so try it directly before
            // enumerating replacements.
            if let Some((pid, started_at)) = pinned {
                let identity = crate::applications::Application {
                    path: path.clone(),
                    name: std::path::Path::new(path)
                        .file_name()
                        .unwrap_or_default()
                        .to_string_lossy()
                        .into_owned(),
                    process_id: *pid,
                    started_at: *started_at,
                };
                if crate::applications::Process::verify(&identity).is_ok() {
                    return Ok(vec![Binding::open_application(
                        &identity,
                        stream_rate,
                        stop,
                    )?]);
                }
            }
            let candidates = crate::applications::matching(path)?;
            let plan =
                crate::applications::bind_plan(path, &candidates, *pinned, MAX_CAPTURE_INSTANCES);
            ensure!(!plan.is_empty(), "no running instance of {path}");
            let mut bindings = Vec::with_capacity(plan.len());
            let mut last_error = None;
            for application in &plan {
                match Binding::open_application(application, stream_rate, stop) {
                    Ok(binding) => bindings.push(binding),
                    Err(error) => last_error = Some(error),
                }
            }
            if bindings.is_empty() {
                return Err(
                    last_error.unwrap_or_else(|| anyhow::anyhow!("no running instance of {path}"))
                );
            }
            Ok(bindings)
        }
    }
}

/// Why a read failed: some stream errors are worth a reopen, none should kill
/// the capture thread while the rebind budget still has room.
struct ReadFailure {
    reason: &'static str,
    message: String,
}
impl ReadFailure {
    fn windows(error: windows::core::Error) -> Self {
        Self {
            reason: if error.code() == AUDCLNT_E_DEVICE_INVALIDATED {
                "endpoint_lost"
            } else {
                "stream_error"
            },
            message: error.to_string(),
        }
    }
    fn other(error: anyhow::Error) -> Self {
        Self {
            reason: "stream_error",
            message: format!("{error:#}"),
        }
    }
}

fn drain_after_capture_wait(
    waited: WAIT_EVENT,
    stop: &AtomicBool,
    process_exited: bool,
    drain: impl FnOnce() -> std::result::Result<(), ReadFailure>,
) -> Result<Option<ReadFailure>> {
    if stop.load(Ordering::Acquire) || (waited == WAIT_TIMEOUT && process_exited) {
        return Ok(None);
    }
    if waited == WAIT_FAILED {
        return Err(windows::core::Error::from_win32()).context("waiting for capture events");
    }
    // A timed-out event wait can still leave readable packets in the endpoint buffer.
    Ok(drain().err())
}

/// Bounded work done without a successful rebind. A successful rebind resets
/// the failure streak; exhaustion is what finally ends the capture session.
struct RebindBudget {
    max_failures: u32,
    consecutive_failures: u32,
    rebinds: u64,
    failures: u64,
}
impl RebindBudget {
    fn new(max_failures: u32) -> Self {
        Self {
            max_failures,
            consecutive_failures: 0,
            rebinds: 0,
            failures: 0,
        }
    }
    fn can_attempt(&self) -> bool {
        self.consecutive_failures < self.max_failures
    }
    fn succeeded(&mut self) {
        self.consecutive_failures = 0;
        self.rebinds += 1;
    }
    fn failed(&mut self) {
        self.consecutive_failures += 1;
        self.failures += 1;
    }
    fn exhausted(&self) -> bool {
        self.consecutive_failures >= self.max_failures
    }
}

/// What the capture loop saw since the previous iteration. `Idle` means the
/// endpoint simply had nothing ready yet and must not move any timer: the loop
/// polls far faster than a renderer produces packets.
enum ProbeInput {
    Idle,
    Audible,
    DigitalSilence,
    Missing,
}
impl ProbeInput {
    fn from_capture(received_packet: bool, audio: bool, since_packet: Duration) -> Self {
        if received_packet {
            if audio {
                Self::Audible
            } else {
                Self::DigitalSilence
            }
        } else if since_packet > NO_PACKET_SILENCE_AFTER {
            Self::Missing
        } else {
            Self::Idle
        }
    }
}
/// Deliberately sticky silence gate. Clearing the budget on a single non-zero
/// sample made a silent endpoint re-probe forever: every rebind delivered a
/// burst of the stale loopback tail, which looked like "audio is back" and
/// re-armed the probe, then the silence that followed fired it again four
/// seconds later — the 2.4 s rebind storm in the 2026-11 logs.
struct SilenceProbe {
    window: Duration,
    intervals: [Duration; 3],
    silent_since: Option<Instant>,
    last_probe: Option<Instant>,
    audible_since: Option<Instant>,
    ever_audible: bool,
    silent_probes: usize,
}
impl SilenceProbe {
    fn new(window: Duration, intervals: [Duration; 3]) -> Self {
        Self {
            window,
            intervals,
            silent_since: None,
            last_probe: None,
            audible_since: None,
            ever_audible: false,
            silent_probes: 0,
        }
    }
    /// Returns true when the missing audio has lasted long enough to justify
    /// one rebind.
    fn observe(&mut self, now: Instant, input: ProbeInput) -> bool {
        match input {
            ProbeInput::Idle => return false,
            ProbeInput::Audible => {
                self.silent_since = None;
                let since = *self.audible_since.get_or_insert(now);
                if now.saturating_duration_since(since) >= SILENT_PROBE_REARM {
                    self.ever_audible = true;
                    self.silent_probes = 0;
                    self.last_probe = None;
                }
                return false;
            }
            ProbeInput::DigitalSilence => {
                self.interrupt();
                return false;
            }
            ProbeInput::Missing => self.audible_since = None,
        }
        if !self.ever_audible {
            self.silent_since = None;
            return false;
        }
        let since = *self.silent_since.get_or_insert(now);
        if now.saturating_duration_since(since) < self.window {
            return false;
        }
        // The first probe uses the one-minute gap; each probe that did not
        // restore sustained audio moves the ladder one step down.
        let step = self
            .silent_probes
            .saturating_sub(1)
            .min(self.intervals.len() - 1);
        let interval = self.intervals[step];
        if self
            .last_probe
            .is_some_and(|last| now.saturating_duration_since(last) < interval)
        {
            return false;
        }
        self.last_probe = Some(now);
        self.silent_since = Some(now);
        self.silent_probes = self.silent_probes.saturating_add(1);
        true
    }
    fn interrupt(&mut self) {
        self.silent_since = None;
        self.audible_since = None;
    }
}

/// Sums application instances on one source-sample grid. Process loopback
/// clients are separate endpoints, so their QPC bases may differ; the first
/// observed timestamp anchors the grid and later contributions are rounded to
/// the nearest sample. Missing indices become exact zeros so the resampler sees
/// an unbroken timeline.
struct Mixer {
    rate: u32,
    origin: Option<u64>,
    next: i64,
    pending: BTreeMap<i64, (f32, f32)>,
}
impl Mixer {
    fn new(rate: u32) -> Self {
        Self {
            rate,
            origin: None,
            next: 0,
            pending: BTreeMap::new(),
        }
    }
    fn reset(&mut self) {
        self.origin = None;
        self.next = 0;
        self.pending.clear();
    }
    fn index(&self, origin: u64, time: u64) -> i64 {
        let delta = time as i128 - origin as i128;
        ((delta * self.rate as i128 + 500_000_000) / 1_000_000_000) as i64
    }
    fn add(&mut self, time: u64, left: f32, right: f32) {
        let origin = *self.origin.get_or_insert(time);
        let index = self.index(origin, time);
        let entry = self.pending.entry(index).or_insert((0.0, 0.0));
        entry.0 += left;
        entry.1 += right;
    }
    /// Move every contribution older than `cutoff` into the pipeline, filling
    /// skipped indices with silence.
    fn flush(&mut self, cutoff: u64, pipeline: &mut Pipeline) {
        let Some(origin) = self.origin else {
            return;
        };
        let cutoff = self.index(origin, cutoff);
        let keep = self.pending.split_off(&cutoff);
        let ready = std::mem::replace(&mut self.pending, keep);
        for (index, (left, right)) in ready {
            if index < self.next {
                continue;
            }
            // A timestamp jump (device restarted, clock reset) must not make us
            // synthesize minutes of silence.
            if index - self.next > self.rate as i64 {
                self.next = index;
            }
            while self.next < index {
                let time = origin
                    .saturating_add((self.next as i128 * 1_000_000_000 / self.rate as i128) as u64);
                pipeline.push(time, 0.0, 0.0);
                self.next += 1;
            }
            let time =
                origin.saturating_add((index as i128 * 1_000_000_000 / self.rate as i128) as u64);
            pipeline.push(time, left.clamp(-1.0, 1.0), right.clamp(-1.0, 1.0));
            self.next = index + 1;
        }
    }
}

struct Pipeline {
    resampler: SincFixedIn<f32>,
    inputs: [VecDeque<f32>; 2],
    input_times: VecDeque<u64>,
    chunk: usize,
    stream_rate: u32,
}
impl Pipeline {
    fn new(stream_rate: u32, rate: u32) -> Result<Self> {
        let chunk = (rate / 100) as usize;
        Ok(Self {
            resampler: SincFixedIn::<f32>::new(
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
            )?,
            inputs: [VecDeque::new(), VecDeque::new()],
            input_times: VecDeque::new(),
            chunk,
            stream_rate,
        })
    }
    fn reset(&mut self) {
        self.inputs[0].clear();
        self.inputs[1].clear();
        self.input_times.clear();
        self.resampler.reset();
    }
    fn push(&mut self, time: u64, left: f32, right: f32) {
        self.inputs[0].push_back(left);
        self.inputs[1].push_back(right);
        self.input_times.push_back(time);
    }
    fn pump(
        &mut self,
        state: &Arc<Mutex<State>>,
        servo: &Arc<Mutex<crate::servo::LatenessServo>>,
        drift: &Arc<Mutex<crate::servo::DriftEstimator>>,
        regulator: &mut crate::servo::QueueRegulator,
        last_tick: &mut Instant,
    ) -> Result<()> {
        while self.input_times.len() >= self.chunk {
            let start = self.input_times[0];
            let input: Vec<Vec<f32>> = self
                .inputs
                .iter_mut()
                .map(|q| q.drain(..self.chunk).collect())
                .collect();
            self.input_times.drain(..self.chunk);
            let (have, target) = {
                let s = state.lock().unwrap();
                (s.frames.len(), s.target)
            };
            let now = Instant::now();
            let dt = now.saturating_duration_since(*last_tick);
            *last_tick = now;
            // Inner loop: hold the queue at its target (PI, so a constant
            // capture-clock offset no longer parks at a depth error).
            let error_frames = target as f64 - have as f64;
            let queue_ppm = regulator.update(error_frames, dt);
            // Outer loop: a sustained playout lateness bias, in the same units.
            let servo_ppm = servo.lock().unwrap().correction_ppm();
            // Positive ratio change produces more frames, which drains the
            // deficit; both controllers already speak that sign.
            let correction = ((queue_ppm + servo_ppm) / 1e6).clamp(-0.0005, 0.0005);
            drift.lock().unwrap().observe(now, have as f64);
            self.resampler
                .set_resample_ratio_relative(1.0 + correction, true)?;
            let output = self.resampler.process(&input, None)?;
            let delay =
                self.resampler.output_delay() as u64 * 1_000_000_000 / self.stream_rate as u64;
            let mut shared = state.lock().unwrap();
            for (i, (&left, &right)) in output[0].iter().zip(&output[1]).enumerate() {
                shared.push_frame(Frame {
                    left,
                    right,
                    qpc: start
                        .saturating_sub(delay)
                        .saturating_add(i as u64 * 1_000_000_000 / self.stream_rate as u64),
                });
            }
        }
        Ok(())
    }
}

fn clear_pipeline(pipeline: &mut Pipeline, mixer: Option<&mut Mixer>) {
    pipeline.reset();
    if let Some(mixer) = mixer {
        mixer.reset();
    }
}

/// The existing true-discontinuity behaviour: drop samples that belong to the
/// broken stream, but keep the already-encoded queue semantics intact.
fn mark_discontinuity(state: &Arc<Mutex<State>>) {
    let mut shared = state.lock().unwrap();
    shared.metrics.dropped_frames += shared.frames.len() as u64;
    shared.frames.clear();
    shared.boundary_pending = true;
    shared.metrics.discontinuities += 1;
    shared.metrics.last_discontinuity = Some("wasapi_discontinuity".into());
}

#[allow(clippy::too_many_arguments)]
fn drain_single(
    binding: &mut Binding,
    stop: &AtomicBool,
    pipeline: &mut Pipeline,
    packets: &mut usize,
    audio: &mut bool,
    last_packet: &mut Instant,
    state: &Arc<Mutex<State>>,
    servo: &Arc<Mutex<crate::servo::LatenessServo>>,
    drift: &Arc<Mutex<crate::servo::DriftEstimator>>,
    regulator: &mut crate::servo::QueueRegulator,
    last_tick: &mut Instant,
) -> std::result::Result<(), ReadFailure> {
    while !stop.load(Ordering::Acquire)
        && unsafe { binding.reader.GetNextPacketSize() }.map_err(ReadFailure::windows)? > 0
    {
        let (mut data, mut frames, mut flags, mut position, mut qpc) =
            (std::ptr::null_mut(), 0, 0, 0, 0);
        unsafe {
            binding.reader.GetBuffer(
                &mut data,
                &mut frames,
                &mut flags,
                Some(&mut position),
                Some(&mut qpc),
            )
        }
        .map_err(ReadFailure::windows)?;
        let silent = flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0;
        let timestamp = if flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR.0 as u32 != 0 {
            qpc_ns().saturating_sub(frames as u64 * 1_000_000_000 / binding.rate as u64)
        } else {
            qpc * 100
        };
        if flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY.0 as u32 != 0 {
            // WASAPI raises this flag on benign loopback wraparound too. A packet
            // that starts exactly where the previous one ended continues the
            // stream; flushing there would drop 20–60 ms of audio for nothing.
            if continues_previous(binding.last_end, position) {
                state.lock().unwrap().metrics.discontinuities_ignored += 1;
            } else {
                clear_pipeline(pipeline, None);
                mark_discontinuity(state);
            }
        }
        binding.last_end = Some(position.saturating_add(u64::from(frames)));
        let bytes = if data.is_null() {
            &[][..]
        } else {
            unsafe {
                std::slice::from_raw_parts(
                    data,
                    frames as usize * binding.spec.nBlockAlign as usize,
                )
            }
        };
        for i in 0..frames as usize {
            let (left, right) = if silent || bytes.is_empty() {
                (0.0, 0.0)
            } else {
                (binding.sample(bytes, i, 0), binding.sample(bytes, i, 1))
            };
            pipeline.push(
                timestamp.saturating_add(i as u64 * 1_000_000_000 / binding.rate as u64),
                left,
                right,
            );
            if left != 0.0 || right != 0.0 {
                *audio = true;
            }
        }
        unsafe { binding.reader.ReleaseBuffer(frames) }.map_err(ReadFailure::windows)?;
        *last_packet = Instant::now();
        *packets += 1;
        state.lock().unwrap().metrics.capture_frames += frames as u64;
        pipeline
            .pump(state, servo, drift, regulator, last_tick)
            .map_err(ReadFailure::other)?;
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn drain_multi(
    bindings: &mut [Binding],
    stop: &AtomicBool,
    pipeline: &mut Pipeline,
    mixer: &mut Mixer,
    packets: &mut usize,
    audio: &mut bool,
    last_packet: &mut Instant,
    state: &Arc<Mutex<State>>,
    servo: &Arc<Mutex<crate::servo::LatenessServo>>,
    drift: &Arc<Mutex<crate::servo::DriftEstimator>>,
    regulator: &mut crate::servo::QueueRegulator,
    last_tick: &mut Instant,
) -> std::result::Result<(), ReadFailure> {
    let mut round_end: Option<u64> = None;
    for binding in bindings.iter_mut() {
        let mut binding_end = None;
        while !stop.load(Ordering::Acquire)
            && unsafe { binding.reader.GetNextPacketSize() }.map_err(ReadFailure::windows)? > 0
        {
            let (mut data, mut frames, mut flags, mut position, mut qpc) =
                (std::ptr::null_mut(), 0, 0, 0, 0);
            unsafe {
                binding.reader.GetBuffer(
                    &mut data,
                    &mut frames,
                    &mut flags,
                    Some(&mut position),
                    Some(&mut qpc),
                )
            }
            .map_err(ReadFailure::windows)?;
            let silent = flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0;
            let timestamp = if flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR.0 as u32 != 0 {
                qpc_ns().saturating_sub(frames as u64 * 1_000_000_000 / binding.rate as u64)
            } else {
                qpc * 100
            };
            if flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY.0 as u32 != 0 {
                if continues_previous(binding.last_end, position) {
                    state.lock().unwrap().metrics.discontinuities_ignored += 1;
                } else {
                    clear_pipeline(pipeline, Some(&mut *mixer));
                    mark_discontinuity(state);
                }
            }
            binding.last_end = Some(position.saturating_add(u64::from(frames)));
            let bytes = if data.is_null() {
                &[][..]
            } else {
                unsafe {
                    std::slice::from_raw_parts(
                        data,
                        frames as usize * binding.spec.nBlockAlign as usize,
                    )
                }
            };
            for i in 0..frames as usize {
                let (left, right) = if silent || bytes.is_empty() {
                    (0.0, 0.0)
                } else {
                    (binding.sample(bytes, i, 0), binding.sample(bytes, i, 1))
                };
                mixer.add(
                    timestamp.saturating_add(i as u64 * 1_000_000_000 / binding.rate as u64),
                    left,
                    right,
                );
                if left != 0.0 || right != 0.0 {
                    *audio = true;
                }
            }
            unsafe { binding.reader.ReleaseBuffer(frames) }.map_err(ReadFailure::windows)?;
            *last_packet = Instant::now();
            *packets += 1;
            state.lock().unwrap().metrics.capture_frames += frames as u64;
            binding_end =
                Some(timestamp.saturating_add(frames as u64 * 1_000_000_000 / binding.rate as u64));
        }
        if let Some(end) = binding_end {
            round_end = Some(round_end.map_or(end, |current| current.min(end)));
        }
    }
    if let Some(cutoff) = round_end {
        mixer.flush(cutoff, pipeline);
        pipeline
            .pump(state, servo, drift, regulator, last_tick)
            .map_err(ReadFailure::other)?;
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn capture(
    target: Target,
    stream_rate: u32,
    stop: Arc<AtomicBool>,
    state: &Arc<Mutex<State>>,
    servo: &Arc<Mutex<crate::servo::LatenessServo>>,
    drift: &Arc<Mutex<crate::servo::DriftEstimator>>,
    ready: &mpsc::SyncSender<std::result::Result<(), String>>,
) -> Result<()> {
    unsafe { CoInitializeEx(None, COINIT_MULTITHREADED).ok() }?;
    let _com = ComGuard;
    let _priority = Mmcss::new();
    let mut regulator = crate::servo::QueueRegulator::default();
    let mut last_tick = Instant::now();
    {
        let mut s = state.lock().unwrap();
        s.metrics.mmcss_registered = _priority.registered();
        s.metrics.mmcss_priority_set = _priority.priority_set();
    }
    let mut bindings = bind_target(&target, stream_rate, &stop)?;
    let mut format = bindings
        .first()
        .context("capture produced no stream")?
        .format();
    ensure!(
        bindings.iter().all(|b| {
            let other = b.format();
            other.rate == format.rate
                && other.channels == format.channels
                && other.bits == format.bits
                && other.tag == format.tag
        }),
        "capture stream formats differ"
    );
    let mut pipeline = Pipeline::new(stream_rate, format.rate)?;
    let mut mixer = Mixer::new(format.rate);
    {
        let mut s = state.lock().unwrap();
        s.metrics.input_rate = format.rate;
        s.metrics.capture_instances = bindings.len() as u32;
    }
    let _ = ready.send(Ok(()));
    let mut budget = RebindBudget::new(REBIND_MAX_FAILURES);
    let mut probe = SilenceProbe::new(SILENT_PROBE_WINDOW, SILENT_PROBE_INTERVALS);
    // Successful rebinds that are still inside the storm window, plus the last
    // storm warning so a genuinely dead endpoint does not log every rebind.
    let mut rebind_times: VecDeque<Instant> = VecDeque::with_capacity(8);
    let mut storm_warned_at: Option<Instant> = None;
    let mut last_packet = Instant::now();
    while !stop.load(Ordering::Acquire) {
        // One liveness probe per loop iteration, not per packet.
        let mut trigger = bindings
            .iter()
            .any(|binding| {
                binding
                    .process
                    .as_ref()
                    .is_some_and(|process| !process.alive())
            })
            .then_some("process_exit");
        let handles: Vec<_> = bindings.iter().map(|binding| binding.event.0).collect();
        let waited = unsafe { WaitForMultipleObjects(&handles, false, 50) };
        let mut packets = 0usize;
        let mut audio = false;
        let mut read_message = None;
        let read_failure = drain_after_capture_wait(waited, &stop, trigger.is_some(), || {
            if bindings.len() == 1 {
                drain_single(
                    &mut bindings[0],
                    &stop,
                    &mut pipeline,
                    &mut packets,
                    &mut audio,
                    &mut last_packet,
                    state,
                    servo,
                    drift,
                    &mut regulator,
                    &mut last_tick,
                )
            } else {
                drain_multi(
                    &mut bindings,
                    &stop,
                    &mut pipeline,
                    &mut mixer,
                    &mut packets,
                    &mut audio,
                    &mut last_packet,
                    state,
                    servo,
                    drift,
                    &mut regulator,
                    &mut last_tick,
                )
            }
        })?;
        if stop.load(Ordering::Acquire) {
            break;
        }
        if let Some(failure) = read_failure {
            trigger = Some(failure.reason);
            read_message = Some(failure.message);
        }
        let now = Instant::now();
        let input = ProbeInput::from_capture(
            packets > 0,
            audio,
            now.saturating_duration_since(last_packet),
        );
        if trigger.is_none() && budget.can_attempt() && probe.observe(now, input) {
            state.lock().unwrap().metrics.silent_probes += 1;
            trigger = Some("silent_probe");
        }
        let Some(reason) = trigger else {
            continue;
        };
        probe.interrupt();
        // How long the endpoint had been quiet when the probe fired: the log line
        // separates a real loss from a probe that only replayed a stale tail.
        let silent_ms = now.saturating_duration_since(last_packet).as_millis();
        // Rebind, not a queue flush: drop the old streams, keep the samples that
        // were already encoded and only reset the resampler input.
        clear_pipeline(&mut pipeline, Some(&mut mixer));
        {
            let mut s = state.lock().unwrap();
            s.note_rebind(reason);
            s.metrics.capture_instances = 0;
        }
        bindings.clear();
        loop {
            if stop.load(Ordering::Acquire) {
                return Ok(());
            }
            match bind_target(&target, stream_rate, &stop) {
                Ok(new_bindings) => {
                    let new_format = new_bindings
                        .first()
                        .context("capture produced no stream")?
                        .format();
                    ensure!(
                        new_bindings.iter().all(|b| {
                            b.rate == new_format.rate
                                && b.channels == new_format.channels
                                && b.bits == new_format.bits
                                && b.tag == new_format.tag
                        }),
                        "capture stream formats differ"
                    );
                    format = new_format;
                    if new_format.rate != mixer.rate {
                        pipeline = Pipeline::new(stream_rate, new_format.rate)?;
                        mixer = Mixer::new(new_format.rate);
                    }
                    bindings = new_bindings;
                    budget.succeeded();
                    let count = bindings.len() as u32;
                    let (rebinds, failures) = (budget.rebinds, budget.failures);
                    {
                        let mut s = state.lock().unwrap();
                        s.metrics.input_rate = format.rate;
                        s.metrics.capture_instances = count;
                        s.metrics.rebinds = rebinds;
                        s.metrics.rebind_failures = failures;
                        s.metrics.last_rebind_reason = Some(reason.into());
                    }
                    // Several rebinds close together are worth reporting even
                    // when each individual open succeeded.
                    let stamp = Instant::now();
                    rebind_times.push_back(stamp);
                    while rebind_times.front().is_some_and(|oldest| {
                        stamp.saturating_duration_since(*oldest) > REBIND_STORM_WINDOW
                    }) {
                        rebind_times.pop_front();
                    }
                    let window = rebind_times.len() as u32;
                    let storm = window >= REBIND_STORM_COUNT as u32
                        && storm_warned_at.is_none_or(|last| {
                            stamp.saturating_duration_since(last) >= STORM_WARNING_GAP
                        });
                    {
                        let mut s = state.lock().unwrap();
                        s.metrics.rebind_window_max = s.metrics.rebind_window_max.max(window);
                        if storm {
                            storm_warned_at = Some(stamp);
                            s.metrics.rebind_storms += 1;
                        }
                    }
                    if reason == "process_exit" {
                        warn(
                            state,
                            "capture_process_exited",
                            true,
                            format!(
                                "Application capture process exited; rebound {count} instance(s)"
                            ),
                        );
                    } else {
                        let detail = read_message
                            .take()
                            .map(|message| format!(": {message}"))
                            .unwrap_or_default();
                        let quiet = if reason == "silent_probe" {
                            format!("; silent {silent_ms} ms")
                        } else {
                            String::new()
                        };
                        warn(
                            state,
                            "capture_endpoint_reopened",
                            true,
                            format!(
                                "Capture stream reopened after {reason}{detail} ({count} instance(s)){quiet}; rebinds {window} in {} s",
                                REBIND_STORM_WINDOW.as_secs()
                            ),
                        );
                    }
                    if storm {
                        warn(
                            state,
                            "capture_rebind_storm",
                            false,
                            format!(
                                "Capture stream rebound {window} times in {} s after {reason} (last packet {silent_ms} ms ago)",
                                REBIND_STORM_WINDOW.as_secs()
                            ),
                        );
                    }
                    last_packet = Instant::now();
                    break;
                }
                Err(error) => {
                    budget.failed();
                    {
                        let mut s = state.lock().unwrap();
                        s.metrics.rebind_failures = budget.failures;
                    }
                    warn(
                        state,
                        "capture_rebind_failed",
                        false,
                        format!("Capture rebind failed: {error:#}"),
                    );
                    if budget.exhausted() {
                        let mut s = state.lock().unwrap();
                        s.ended = true;
                        s.warn(CaptureWarning {
                            code: "capture_rebind_exhausted",
                            recovered: false,
                            message: format!(
                                "Capture rebind budget exhausted after {} failures; the session will restart",
                                budget.failures
                            ),
                        });
                        return Ok(());
                    }
                    let until = Instant::now() + REBIND_BACKOFF;
                    while Instant::now() < until && !stop.load(Ordering::Acquire) {
                        thread::sleep(Duration::from_millis(20));
                    }
                }
            }
        }
    }
    Ok(())
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use windows::Win32::Foundation::{HANDLE, WAIT_OBJECT_0};

    #[test]
    fn capture_wait_drains_queued_audio_after_a_signal_or_timeout() {
        for (waited, frames, amplitude) in [
            (WAIT_OBJECT_0, FRAMES, 0.01),
            (WAIT_TIMEOUT, FRAMES, 0.01),
            (WAIT_TIMEOUT, FRAMES, 0.0),
            (WAIT_TIMEOUT, 0, 0.0),
        ] {
            let source = source();
            let stop = AtomicBool::new(false);
            let mut pending_frames = frames;
            let mut drains = 0;
            let failure = drain_after_capture_wait(waited, &stop, false, || {
                drains += 1;
                enqueue(&source, std::mem::take(&mut pending_frames), amplitude);
                Ok(())
            })
            .unwrap();
            assert!(failure.is_none());
            assert_eq!(drains, 1);
            assert_eq!(pending_frames, 0);
            let (_, marker) = source.packet(1.0).unwrap();
            assert_eq!(marker.is_some(), amplitude > 0.0);
            assert_eq!(source.metrics().padded_frames, (FRAMES - frames) as u64);
        }
    }

    #[test]
    fn capture_wait_skips_drain_for_stop_or_an_exited_process_on_timeout() {
        let stop = AtomicBool::new(false);
        assert!(
            drain_after_capture_wait(WAIT_TIMEOUT, &stop, true, || panic!("process exited"))
                .unwrap()
                .is_none()
        );
        stop.store(true, Ordering::Release);
        assert!(
            drain_after_capture_wait(WAIT_OBJECT_0, &stop, false, || panic!("capture stopped"))
                .unwrap()
                .is_none()
        );
    }

    #[test]
    fn capture_wait_distinguishes_wait_failure_from_endpoint_invalidation() {
        let stop = AtomicBool::new(false);
        let waited = unsafe { WaitForMultipleObjects(&[HANDLE::default()], false, 0) };
        assert_eq!(waited, WAIT_FAILED);
        let failure = drain_after_capture_wait(waited, &stop, false, || panic!("wait failed"))
            .err()
            .expect("a failed wait must end capture");
        assert_eq!(failure.to_string(), "waiting for capture events");
        let error = failure.downcast_ref::<windows::core::Error>().unwrap();
        assert_eq!(error.code().0 as u32, 0x8007_0006); // HRESULT_FROM_WIN32(ERROR_INVALID_HANDLE)

        let read_failure = drain_after_capture_wait(WAIT_TIMEOUT, &stop, false, || {
            Err(ReadFailure::windows(windows::core::Error::from_hresult(
                AUDCLNT_E_DEVICE_INVALIDATED,
            )))
        })
        .unwrap()
        .expect("endpoint invalidation must reach the rebind path");
        assert_eq!(read_failure.reason, "endpoint_lost");
    }

    #[test]
    fn discontinuity_is_only_real_when_the_device_position_jumps() {
        // Loopback wraparound: the next packet continues the last one.
        assert!(continues_previous(Some(960), 960));
        assert!(continues_previous(Some(0), 0));
        // A genuine gap: position moved past where the previous packet ended.
        assert!(!continues_previous(Some(960), 1200));
        // Rewind (endpoint restarted): also a real discontinuity.
        assert!(!continues_previous(Some(960), 0));
        // Nothing to compare against yet: treat the packet as a real break.
        assert!(!continues_previous(None, 960));
    }

    #[test]
    fn capture_direction_selects_loopback_only_for_playback() {
        let common = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_NOPERSIST;
        assert_eq!(
            capture_flags(eRender).unwrap(),
            common | AUDCLNT_STREAMFLAGS_LOOPBACK
        );
        assert_eq!(capture_flags(eCapture).unwrap(), common);
        assert!(capture_flags(eAll).is_err());
    }

    #[test]
    fn endpoint_selection_prefers_id_and_rejects_ambiguous_names() {
        let endpoints = vec![
            ("playback-id".into(), Some("recording-id".into())),
            ("recording-id".into(), Some("Voicemeeter Output".into())),
            ("another-id".into(), Some("Voicemeeter Output".into())),
        ];
        assert_eq!(endpoint_index("recording-id", &endpoints).unwrap(), 1);
        assert!(
            endpoint_index("Voicemeeter Output", &endpoints)
                .unwrap_err()
                .to_string()
                .contains("ambiguous")
        );
        assert!(
            endpoint_index("missing", &endpoints)
                .unwrap_err()
                .to_string()
                .contains("unavailable")
        );
        assert_eq!(
            endpoint_index("Voicemeeter Output", &endpoints[..2]).unwrap(),
            1
        );
    }

    #[test]
    fn rebind_budget_exhausts_after_consecutive_failures_and_success_resets_it() {
        let mut budget = RebindBudget::new(3);
        assert!(budget.can_attempt());
        budget.failed();
        assert_eq!(budget.failures, 1);
        assert!(budget.can_attempt());
        budget.succeeded();
        assert_eq!((budget.rebinds, budget.failures), (1, 1));
        assert!(!budget.exhausted());
        budget.failed();
        budget.failed();
        assert!(budget.can_attempt());
        budget.failed();
        assert!(budget.exhausted());
        assert!(!budget.can_attempt());
        assert_eq!(budget.failures, 4);
    }

    fn probe() -> SilenceProbe {
        SilenceProbe::new(
            Duration::from_secs(10),
            [
                Duration::from_secs(60),
                Duration::from_secs(300),
                Duration::from_secs(1200),
            ],
        )
    }

    #[test]
    fn silence_probe_waits_for_audible_audio_then_the_full_window() {
        let start = Instant::now();
        let mut probe = probe();
        // A stream that never produced audio is never probed, however long the
        // endpoint sits idle: a fresh loopback has no stale tail to replay.
        for seconds in [0, 5, 30, 90] {
            assert!(!probe.observe(start + Duration::from_secs(seconds), ProbeInput::Missing));
        }
        // Idle polls between packets must not move either timer.
        assert!(!probe.observe(start + Duration::from_secs(91), ProbeInput::Idle));
        // Thirty seconds of contiguous audio arm the probe; 29 is not enough.
        assert!(!probe.observe(start + Duration::from_secs(100), ProbeInput::Audible));
        assert!(!probe.observe(start + Duration::from_secs(129), ProbeInput::Audible));
        assert!(!probe.observe(start + Duration::from_secs(130), ProbeInput::Audible));
        // Ten seconds of contiguous silence are required, not ten seconds since
        // the last audible packet.
        assert!(!probe.observe(start + Duration::from_secs(131), ProbeInput::Missing));
        assert!(!probe.observe(start + Duration::from_secs(140), ProbeInput::Missing));
        assert!(probe.observe(start + Duration::from_secs(141), ProbeInput::Missing));
    }

    #[test]
    fn captured_digital_silence_does_not_trigger_a_rebind_probe() {
        let start = Instant::now();
        let mut probe = probe();
        probe.observe(start, ProbeInput::Audible);
        probe.observe(start + SILENT_PROBE_REARM, ProbeInput::Audible);
        for seconds in 31..125 {
            assert!(!probe.observe(
                start + Duration::from_secs(seconds),
                ProbeInput::from_capture(true, false, Duration::ZERO)
            ));
        }

        let missing = ProbeInput::from_capture(
            false,
            false,
            NO_PACKET_SILENCE_AFTER + Duration::from_millis(1),
        );
        let missing_since = start + Duration::from_secs(125);
        assert!(!probe.observe(missing_since, missing));
        let missing = ProbeInput::from_capture(false, false, SILENT_PROBE_WINDOW);
        assert!(probe.observe(missing_since + SILENT_PROBE_WINDOW, missing));
    }

    #[test]
    fn a_short_stale_burst_does_not_restore_the_probe_budget() {
        let start = Instant::now();
        let mut probe = probe();
        probe.observe(start, ProbeInput::Audible);
        probe.observe(start + Duration::from_secs(30), ProbeInput::Audible);
        assert!(!probe.observe(start + Duration::from_secs(30), ProbeInput::Missing));
        assert!(probe.observe(start + Duration::from_secs(40), ProbeInput::Missing));
        // The rebound stream replays ~50 ms of the stale loopback tail, then goes
        // quiet again. That burst used to clear `last_probe` and restart the
        // two-second window, which is exactly the 2.36 s storm cadence.
        assert!(!probe.observe(start + Duration::from_secs(41), ProbeInput::Audible));
        assert!(!probe.observe(start + Duration::from_millis(41_050), ProbeInput::Missing));
        assert!(!probe.observe(start + Duration::from_secs(50), ProbeInput::Missing));
        assert!(!probe.observe(start + Duration::from_secs(99), ProbeInput::Missing));
        assert!(probe.observe(start + Duration::from_secs(100), ProbeInput::Missing));
    }

    #[test]
    fn repeated_silent_probes_back_off_to_five_then_twenty_minutes() {
        let start = Instant::now();
        let mut probe = probe();
        probe.observe(start, ProbeInput::Audible);
        probe.observe(start + Duration::from_secs(30), ProbeInput::Audible);
        probe.observe(start + Duration::from_secs(30), ProbeInput::Missing);
        assert!(probe.observe(start + Duration::from_secs(40), ProbeInput::Missing));
        assert!(!probe.observe(start + Duration::from_secs(99), ProbeInput::Missing));
        assert!(probe.observe(start + Duration::from_secs(100), ProbeInput::Missing));
        assert!(!probe.observe(start + Duration::from_secs(399), ProbeInput::Missing));
        assert!(probe.observe(start + Duration::from_secs(400), ProbeInput::Missing));
        assert!(!probe.observe(start + Duration::from_secs(1599), ProbeInput::Missing));
        assert!(probe.observe(start + Duration::from_secs(1600), ProbeInput::Missing));
    }

    #[test]
    fn sustained_audio_resets_the_probe_budget() {
        let start = Instant::now();
        let mut probe = probe();
        probe.observe(start, ProbeInput::Audible);
        probe.observe(start + Duration::from_secs(30), ProbeInput::Audible);
        probe.observe(start + Duration::from_secs(30), ProbeInput::Missing);
        assert!(probe.observe(start + Duration::from_secs(40), ProbeInput::Missing));
        assert!(!probe.observe(start + Duration::from_secs(99), ProbeInput::Missing));
        assert!(probe.observe(start + Duration::from_secs(100), ProbeInput::Missing));
        // Audio really returns for half a minute: the ladder restarts at a minute.
        assert!(!probe.observe(start + Duration::from_secs(200), ProbeInput::Audible));
        assert!(!probe.observe(start + Duration::from_secs(230), ProbeInput::Audible));
        assert!(!probe.observe(start + Duration::from_secs(231), ProbeInput::Missing));
        assert!(probe.observe(start + Duration::from_secs(241), ProbeInput::Missing));
    }

    #[test]
    fn the_storm_pattern_produces_a_handful_of_probes_not_hundreds() {
        // 11-08 15:48–16:15: every probe reopened the stream, the fresh binding
        // replayed a ~50 ms stale burst of the loopback tail, then the endpoint
        // went quiet again. The old state machine treated that burst as "audio is
        // back", re-armed, waited two seconds and probed again: 2.36 s cadence for
        // 27 minutes. Here the window and the interval ladder must absorb it.
        let start = Instant::now();
        let mut probe = probe();
        assert!(!probe.observe(start, ProbeInput::Audible));
        assert!(!probe.observe(start + Duration::from_secs(30), ProbeInput::Audible));
        let mut fired = 0;
        let mut clock = Duration::from_secs(30);
        let deadline = Duration::from_secs(30 + 27 * 60);
        while clock < deadline {
            if probe.observe(start + clock, ProbeInput::Missing) {
                fired += 1;
                // The rebind itself replays the stale tail, then silence returns.
                clock += Duration::from_millis(50);
                assert!(!probe.observe(start + clock, ProbeInput::Audible));
            }
            clock += Duration::from_millis(100);
        }
        assert!((1..=4).contains(&fired), "probe fired {fired} times");
    }

    #[test]
    fn mixer_sums_instances_on_one_grid_and_fills_gaps_with_exact_silence() {
        let mut mixer = Mixer::new(1000);
        let mut pipeline = Pipeline {
            resampler: SincFixedIn::<f32>::new(
                1.0,
                1.001,
                SincInterpolationParameters {
                    sinc_len: 64,
                    f_cutoff: 0.9,
                    interpolation: SincInterpolationType::Cubic,
                    oversampling_factor: 128,
                    window: WindowFunction::BlackmanHarris2,
                },
                100,
                2,
            )
            .unwrap(),
            inputs: [VecDeque::new(), VecDeque::new()],
            input_times: VecDeque::new(),
            chunk: 100,
            stream_rate: 1000,
        };
        mixer.add(1_000_000, 0.5, -0.5);
        mixer.add(1_000_000, 0.25, 0.25);
        mixer.add(3_000_000, 0.1, 0.1);
        mixer.flush(4_000_000, &mut pipeline);
        let samples: Vec<_> = pipeline.inputs[0].iter().copied().collect();
        assert_eq!(samples, [0.75, 0.0, 0.1]);
        let times: Vec<_> = pipeline.input_times.iter().copied().collect();
        assert_eq!(times, [1_000_000, 2_000_000, 3_000_000]);
        // Sums are clamped before the resampler, never wrapped.
        mixer.reset();
        mixer.add(0, 0.9, 0.9);
        mixer.add(0, 0.9, 0.9);
        mixer.flush(1_000_000, &mut pipeline);
        assert_eq!(pipeline.inputs[0].back().copied(), Some(1.0));
    }

    #[test]
    fn device_loss_is_an_endpoint_reopen_not_a_fatal_stream_error() {
        let lost = ReadFailure::windows(windows::core::Error::from_hresult(
            AUDCLNT_E_DEVICE_INVALIDATED,
        ));
        assert_eq!(lost.reason, "endpoint_lost");
        let other = ReadFailure::windows(windows::core::Error::empty());
        assert_eq!(other.reason, "stream_error");
    }

    // Opt-in local WASAPI check: no receiver, playback, or audio file is involved.
    #[test]
    #[ignore = "requires AIRFLASH_CAPTURE_ENDPOINT and a recording endpoint with an active signal"]
    fn recording_endpoint_captures_nonzero_pcm() {
        let endpoint = std::env::var("AIRFLASH_CAPTURE_ENDPOINT").expect("recording device ID");
        unsafe { CoInitializeEx(None, COINIT_MULTITHREADED).ok() }.unwrap();
        let _com = ComGuard;
        let enumerator: IMMDeviceEnumerator =
            unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }.unwrap();
        let device = find_device(&enumerator, Some(&endpoint)).unwrap();
        let flow = unsafe { device.cast::<IMMEndpoint>().unwrap().GetDataFlow() }.unwrap();
        assert!(
            flow == eCapture,
            "local check requires a recording endpoint"
        );
        let equalizer =
            crate::equalizer::Control::new(crate::equalizer::Settings::default(), 44100).unwrap();
        let source = Loopback::start(Some(endpoint), 44100, equalizer).unwrap();
        let deadline = Instant::now() + Duration::from_secs(2);
        let mut nonzero = false;
        while Instant::now() < deadline {
            thread::sleep(Duration::from_millis(8));
            nonzero |= source.packet(0.1).unwrap().0.iter().any(|b| *b != 0);
        }
        assert!(
            source.metrics().capture_frames > 0,
            "no WASAPI frames received"
        );
        assert!(nonzero, "recording endpoint supplied only silence");
        println!(
            "captured {} frames at {} Hz; nonzero PCM verified",
            source.metrics().capture_frames,
            source.metrics().input_rate
        );
    }
    const CAPACITY: usize = crate::rtp::RATE as usize * 60 / 1000;
    pub(crate) fn source() -> Loopback {
        source_with(crate::rtp::RATE, crate::equalizer::Settings::default())
    }
    fn source_with(rate: u32, settings: crate::equalizer::Settings) -> Loopback {
        let equalizer_control = crate::equalizer::Control::new(settings, rate).unwrap();
        Loopback {
            state: Arc::new(Mutex::new(State {
                splice: crate::continuity::Splice::new(rate),
                quantizer: crate::continuity::Quantizer::new(),
                boundary_pending: false,
                rebind_boundary_pending: false,
                last_pull_ended_missing: false,
                equalizer: crate::equalizer::Processor::new(equalizer_control.initial(), rate),
                frames: VecDeque::new(),
                capacity: rate as usize * 60 / 1000,
                target: rate as usize * 20 / 1000,
                error: None,
                ended: false,
                metrics: Metrics::default(),
                ages: VecDeque::new(),
                warnings: VecDeque::new(),
            })),
            stop: Arc::new(AtomicBool::new(false)),
            servo: Arc::new(Mutex::new(crate::servo::LatenessServo::default())),
            drift: Arc::new(Mutex::new(crate::servo::DriftEstimator::new(
                Duration::from_secs(30),
                f64::from(rate),
            ))),
            worker: None,
            equalizer_control,
        }
    }
    #[test]
    fn lateness_samples_reach_the_servo_without_touching_the_audio_path() {
        let source = source();
        for step in 0..400 {
            source.report_lateness(30.0);
            assert_eq!(step + 1, source.servo_metrics().samples);
        }
        assert!(source.servo_metrics().engagements >= 1);
        assert!(source.servo_metrics().correction_ppm > 0.0);
        assert_eq!(source.drift_ppb(), 0.0, "the drift window is still empty");
    }
    pub(crate) fn feed(source: &Loopback, amplitude: f32) {
        let mut state = source.state.lock().unwrap();
        for _ in 0..FRAMES {
            state.push_frame(Frame {
                left: amplitude,
                right: amplitude,
                qpc: qpc_ns(),
            });
        }
    }
    #[test]
    fn activity_is_detected_without_sender_and_before_gain() {
        let source = source();
        feed(&source, 0.003);
        assert_eq!(source.last_audio(), 0);
        feed(&source, 0.01);
        assert!(source.last_audio() > 0);
        let activity = source.last_audio();
        assert!(source.packet(0.0).unwrap().0.iter().all(|b| *b == 0));
        assert_eq!(source.last_audio(), activity);
        source.discard_stale();
        assert_eq!(source.last_audio(), activity);
    }
    fn enqueue(source: &Loopback, count: usize, sample: f32) {
        let mut state = source.state.lock().unwrap();
        let qpc = qpc_ns();
        for _ in 0..count {
            state.push_frame(Frame {
                left: sample,
                right: sample,
                qpc,
            });
        }
    }
    #[test]
    fn capture_age_ignores_padding_and_scheduled_silence() {
        for rate in [44100, 48000] {
            let source = source_with(rate, crate::equalizer::Settings::default());
            source.packet(0.1).unwrap();
            assert_eq!(source.metrics().capture_to_send_p95_ms, None);

            // Genuine captured digital silence still has a capture timestamp.
            enqueue(&source, 3, 0.0);
            source.silence_packet(0.1).unwrap();
            assert_eq!(source.depth().0, 3);
            assert_eq!(source.metrics().capture_to_send_p95_ms, None);

            source.packet(0.1).unwrap();
            assert_eq!(source.state.lock().unwrap().ages.len(), 3);
            let captured = source.metrics();
            assert!(captured.capture_to_send_p95_ms.is_some());
            assert_eq!(captured.padded_frames, (2 * FRAMES - 3) as u64);
            assert_eq!(captured.fully_padded_packets, 1);

            // Synthetic silence must not evict previous capture observations.
            for _ in 0..12 {
                source.silence_packet(0.1).unwrap();
            }
            assert_eq!(source.state.lock().unwrap().ages.len(), 3);
            assert_eq!(
                source.metrics().capture_to_send_p95_ms,
                captured.capture_to_send_p95_ms
            );
        }
    }
    #[test]
    fn sender_deadline_sample_distinguishes_an_empty_queue_from_zero_age() {
        let source = source();
        assert_eq!(source.sample_age_ms(), None);
        {
            let mut state = source.state.lock().unwrap();
            // A future timestamp gives an exact zero after saturating subtraction.
            for qpc in [u64::MAX, 0] {
                state.push_frame(Frame {
                    left: 0.0,
                    right: 0.0,
                    qpc,
                });
            }
        }
        assert_eq!(source.sample_age_ms(), Some(0.0));
        source.packet(0.1).unwrap();
        assert_eq!(source.sample_age_ms(), None);
    }
    #[test]
    fn padding_counts_missing_output_frames_at_both_rates() {
        for rate in [44100, 48000] {
            for count in [0, FRAMES - 1, FRAMES] {
                let source = source_with(rate, crate::equalizer::Settings::default());
                enqueue(&source, count, 0.01);
                let (packet, marker) = source.packet(0.1).unwrap();
                assert_eq!(packet.len(), FRAMES * 4);
                assert_eq!(marker.is_some(), count > 0);
                let metrics = source.metrics();
                assert_eq!(metrics.padded_frames, (FRAMES - count) as u64);
                assert_eq!(metrics.fully_padded_packets, if count == 0 { 1 } else { 0 });
                assert_eq!(metrics.underrun_packets, if count < FRAMES { 1 } else { 0 });
                assert!(source.state.lock().unwrap().frames.is_empty());
            }
        }
    }
    #[test]
    fn padding_accumulates_across_missing_and_resumed_capture_without_input_rate_math() {
        for rate in [44100, 48000] {
            let source = source_with(rate, crate::equalizer::Settings::default());
            {
                let mut state = source.state.lock().unwrap();
                state.metrics.input_rate = 96000;
                state.metrics.capture_frames = 100_000;
            }
            enqueue(&source, FRAMES - 1, 0.01);
            assert!(source.packet(0.1).unwrap().0.iter().any(|byte| *byte != 0));
            assert_eq!(source.metrics().padded_frames, 1);
            source.packet(0.1).unwrap();
            let before_resume = source.metrics();
            enqueue(&source, FRAMES, 0.01);
            assert!(source.packet(0.1).unwrap().0.iter().any(|byte| *byte != 0));
            let after_resume = source.metrics();
            assert_eq!(after_resume.padded_frames, FRAMES as u64 + 1);
            assert_eq!(after_resume.fully_padded_packets, 1);
            assert_eq!(after_resume.underrun_packets, 2);
            assert_eq!(after_resume.padded_frames, before_resume.padded_frames);
            assert_eq!(
                after_resume.fully_padded_packets,
                before_resume.fully_padded_packets
            );
            assert_eq!(after_resume.input_rate, 96000);
            assert_eq!(after_resume.capture_frames, 100_000);
        }
    }
    #[test]
    fn captured_silence_and_zero_gain_are_not_padding() {
        for rate in [44100, 48000] {
            for (sample, gain) in [(0.0, 0.1), (0.5, 0.0)] {
                let source = source_with(rate, crate::equalizer::Settings::default());
                enqueue(&source, FRAMES, sample);
                let (packet, marker) = source.packet(gain).unwrap();
                assert!(packet.iter().all(|byte| *byte == 0));
                assert_eq!(marker.is_some(), sample > 0.004);
                let metrics = source.metrics();
                assert_eq!(metrics.padded_frames, 0);
                assert_eq!(metrics.fully_padded_packets, 0);
                assert_eq!(metrics.underrun_packets, 0);
            }
        }
    }
    #[test]
    fn fully_padded_input_can_have_an_equalizer_tail_without_an_audio_marker() {
        for rate in [44100, 48000] {
            let mut settings = crate::equalizer::Settings {
                enabled: true,
                ..Default::default()
            };
            settings.band_gains_db[0] = 12.0;
            let source = source_with(rate, settings);
            enqueue(&source, 1, 0.5);
            assert!(source.packet(1.0).unwrap().1.is_some());
            let last_audio = source.metrics().last_audio_qpc_ns;
            let (tail, marker) = source.packet(1.0).unwrap();
            assert!(tail.iter().any(|byte| *byte != 0));
            assert!(marker.is_none());
            let metrics = source.metrics();
            assert_eq!(metrics.padded_frames, FRAMES as u64 * 2 - 1);
            assert_eq!(metrics.fully_padded_packets, 1);
            assert_eq!(metrics.underrun_packets, 2);
            assert_eq!(metrics.last_audio_qpc_ns, last_audio);
        }
    }
    #[test]
    fn padding_counters_saturate_and_serialize_without_wrapping_or_counting_reads() {
        for rate in [44100, 48000] {
            let source = source_with(rate, crate::equalizer::Settings::default());
            {
                let mut state = source.state.lock().unwrap();
                state.metrics.padded_frames = u64::MAX - 100;
                state.metrics.fully_padded_packets = u64::MAX - 1;
            }
            source.packet(0.1).unwrap();
            source.packet(0.1).unwrap();
            for _ in 0..2 {
                let metrics = source.metrics();
                assert_eq!(metrics.padded_frames, u64::MAX);
                assert_eq!(metrics.fully_padded_packets, u64::MAX);
                assert_eq!(metrics.underrun_packets, 2);
                let event = serde_json::json!({ "event": "capture_metrics", "metrics": metrics });
                let wire = serde_json::to_vec(&event).unwrap();
                let received: serde_json::Value = serde_json::from_slice(&wire).unwrap();
                assert_eq!(
                    received["metrics"]["padded_frames"].as_u64(),
                    Some(u64::MAX)
                );
                assert_eq!(
                    received["metrics"]["fully_padded_packets"].as_u64(),
                    Some(u64::MAX)
                );
                assert_eq!(received["metrics"]["underrun_packets"].as_u64(), Some(2));
            }
        }
    }
    #[test]
    fn capture_error_preserves_padding_snapshot_and_a_fresh_attempt_starts_at_zero() {
        for rate in [44100, 48000] {
            let source = source_with(rate, crate::equalizer::Settings::default());
            source.packet(0.1).unwrap();
            let before_fault = source.metrics();
            enqueue(&source, 1, 0.01);
            source.state.lock().unwrap().error = Some("synthetic capture failure".into());
            assert!(source.packet(0.1).is_err());
            let after_fault = source.metrics();
            assert_eq!(after_fault.padded_frames, before_fault.padded_frames);
            assert_eq!(
                after_fault.fully_padded_packets,
                before_fault.fully_padded_packets
            );
            assert_eq!(after_fault.underrun_packets, before_fault.underrun_packets);
            assert_eq!(source.state.lock().unwrap().frames.len(), 1);
            let fresh = source_with(rate, crate::equalizer::Settings::default());
            assert_eq!(fresh.metrics().padded_frames, 0);
            assert_eq!(fresh.metrics().fully_padded_packets, 0);
            assert_eq!(fresh.metrics().underrun_packets, 0);
            assert_eq!(before_fault.padded_frames, FRAMES as u64);
            assert_eq!(before_fault.fully_padded_packets, 1);
        }
    }
    #[test]
    fn scheduled_warmup_and_final_fade_are_not_missing_capture() {
        for rate in [44100, 48000] {
            for count in [0, FRAMES - 1, FRAMES] {
                let source = source_with(rate, crate::equalizer::Settings::default());
                enqueue(&source, count, 0.01);
                for gain in [0.1, 0.0] {
                    source.silence_packet(gain).unwrap();
                    let metrics = source.metrics();
                    assert_eq!(metrics.padded_frames, 0);
                    assert_eq!(metrics.fully_padded_packets, 0);
                    assert_eq!(metrics.underrun_packets, 0);
                    assert_eq!(source.state.lock().unwrap().frames.len(), count);
                }
                assert_eq!(
                    source.metrics().scheduled_silence_frames,
                    (FRAMES * 2) as u64
                );
                assert_eq!(source.metrics().silence_pad_frames, (FRAMES * 2) as u64);
                source.packet(0.1).unwrap();
                assert_eq!(source.metrics().padded_frames, (FRAMES - count) as u64);
                assert_eq!(
                    source.metrics().fully_padded_packets,
                    if count == 0 { 1 } else { 0 }
                );
                assert_eq!(
                    source.metrics().scheduled_silence_frames,
                    (FRAMES * 2) as u64
                );
            }
        }
    }
    #[test]
    fn stereo_channel_fanout_does_not_count_another_capture_pull() {
        for rate in [44100, 48000] {
            let source = source_with(rate, crate::equalizer::Settings::default());
            let (pcm, _) = source.packet(0.1).unwrap();
            for channel in [
                crate::profile::Channel::Left,
                crate::profile::Channel::Right,
            ] {
                assert_eq!(channel.map(&pcm).len(), FRAMES * 4);
            }
            let metrics = source.metrics();
            assert_eq!(metrics.padded_frames, FRAMES as u64);
            assert_eq!(metrics.fully_padded_packets, 1);
            assert_eq!(metrics.underrun_packets, 1);
            // Paused standby/discard operations retain this capture worker. A new
            // application capture creates another Loopback with fresh counters.
            source.discard_stale();
            assert_eq!(source.metrics().padded_frames, FRAMES as u64);
            let fresh = source_with(rate, crate::equalizer::Settings::default());
            assert_eq!(fresh.metrics().padded_frames, 0);
            assert_eq!(fresh.metrics().fully_padded_packets, 0);
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
    fn equalizer_precedes_master_gain_and_pcm_conversion() {
        let source = source();
        let settings = crate::equalizer::Settings {
            enabled: true,
            preamp_db: -12.0,
            ..Default::default()
        };
        source.equalizer_control.set(
            1,
            crate::equalizer::Prepared::new(settings, crate::rtp::RATE).unwrap(),
        );
        for _ in 0..FRAMES * 3 {
            source.state.lock().unwrap().push_frame(Frame {
                left: 0.25,
                right: -0.25,
                qpc: qpc_ns(),
            });
        }
        source.packet(0.5).unwrap();
        source.packet(0.5).unwrap();
        let (pcm, marker) = source.packet(0.5).unwrap();
        let last = &pcm[pcm.len() - 4..];
        let expected = (0.25 * 10f32.powf(-12.0 / 20.0) * 0.5 * 32767.0).round() as i16;
        // Output is TPDF-dithered, so a single sample may sit one LSB either side.
        let left = i16::from_be_bytes([last[0], last[1]]);
        let right = i16::from_be_bytes([last[2], last[3]]);
        assert!(
            (i32::from(left) - i32::from(expected)).abs() <= 1,
            "{left} vs {expected}"
        );
        assert!(
            (i32::from(right) + i32::from(expected)).abs() <= 1,
            "{right} vs -{expected}"
        );
        assert!(marker.is_some());
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
    #[test]
    fn rebind_splices_at_the_first_new_frame_inside_a_packet() {
        for rate in [44100, 48000] {
            let source = source_with(rate, crate::equalizer::Settings::default());
            let join = 23;
            enqueue(&source, FRAMES, 0.25);
            source.packet(1.0).unwrap();
            let before = source.metrics().transition_count;

            enqueue(&source, FRAMES + join, 0.25);
            source.state.lock().unwrap().note_rebind("silent_probe");
            enqueue(&source, FRAMES, -0.25);
            source.packet(1.0).unwrap();
            let before_join = source.metrics().transition_count;

            let (pcm, _) = source.packet(1.0).unwrap();
            let left =
                |frame: usize| i32::from(i16::from_be_bytes([pcm[frame * 4], pcm[frame * 4 + 1]]));
            let jump = (left(join) - left(join - 1)).abs();
            assert!(
                jump <= 2,
                "PCM jumped {jump} LSB at the rebind join at {rate} Hz"
            );
            assert!(left(join - 1) > 8000 && left(FRAMES - 1) < -8000);
            assert_eq!(before_join, before);
            assert_eq!(source.metrics().transition_count, before + 1);
            assert_eq!(source.metrics().padded_frames, 0);
        }
    }
    #[test]
    fn queued_rebind_splices_survive_scheduled_silence() {
        let source = source();
        enqueue(&source, FRAMES, 0.25);
        source.packet(1.0).unwrap();
        enqueue(&source, FRAMES, 0.25);
        for sample in [-0.25, 0.25] {
            source.state.lock().unwrap().note_rebind("silent_probe");
            enqueue(&source, FRAMES, sample);
        }

        source.silence_packet(1.0).unwrap();
        let before = source.metrics().transition_count;
        // One missing-to-present splice for the old audio, then one per rebind.
        for added in 1..=3 {
            source.packet(1.0).unwrap();
            assert_eq!(source.metrics().transition_count, before + added);
        }
    }
    #[test]
    fn a_rebind_at_the_queue_head_reuses_the_existing_splice() {
        for missing in [false, true] {
            let source = source();
            enqueue(&source, FRAMES, 0.25);
            source.packet(1.0).unwrap();
            if !missing {
                enqueue(&source, 1, 0.25);
            }
            source.state.lock().unwrap().note_rebind("silent_probe");
            // In the trim case, overflow drops the last old frame and puts the
            // first new frame at the head with an immediate splice pending.
            enqueue(&source, if missing { FRAMES } else { CAPACITY }, -0.25);
            if missing {
                source.silence_packet(1.0).unwrap();
            }
            let before = source.metrics().transition_count;
            source.packet(1.0).unwrap();
            assert_eq!(source.metrics().transition_count, before + 1);
            assert_eq!(source.metrics().dropped_frames, if missing { 0 } else { 1 });
        }
    }
    #[test]
    fn rebind_keeps_already_encoded_frames_and_only_marks_a_boundary() {
        let source = source();
        source.packet(0.1).unwrap();
        let before = source.metrics().transition_count;
        {
            let mut s = source.state.lock().unwrap();
            for _ in 0..FRAMES {
                s.push_frame(Frame {
                    left: 0.01,
                    right: 0.01,
                    qpc: qpc_ns(),
                });
            }
            let queued = s.frames.len();
            let discontinuities = s.metrics.discontinuities;
            s.note_rebind("process_exit");
            // Reopening the capture endpoint must not throw away valid audio.
            assert_eq!(s.frames.len(), queued);
            assert_eq!(s.metrics.dropped_frames, 0);
            assert_eq!(s.metrics.discontinuities, discontinuities + 1);
            assert_eq!(
                s.metrics.last_discontinuity.as_deref(),
                Some("process_exit")
            );
        }
        assert!(source.packet(0.1).unwrap().0.iter().any(|b| *b != 0));
        let metrics = source.metrics();
        assert_eq!(metrics.transition_count, before + 1);
        assert_eq!(metrics.padded_frames, FRAMES as u64);
        assert_eq!(metrics.fully_padded_packets, 1);
        assert_eq!(metrics.underrun_packets, 1);
    }
    #[test]
    fn waiting_without_a_worker_does_not_report_measured_zero_padding() {
        let waiting = Metrics::unavailable_snapshot();
        assert!(waiting.get("padded_frames").is_none());
        assert!(waiting.get("fully_padded_packets").is_none());
        assert_eq!(waiting["underrun_packets"].as_u64(), Some(0));
        let active = serde_json::json!(source().metrics());
        assert_eq!(active["padded_frames"].as_u64(), Some(0));
        assert_eq!(active["fully_padded_packets"].as_u64(), Some(0));
    }
    #[test]
    fn capture_warnings_are_queued_and_drained_once() {
        let source = source();
        warn(
            &source.state,
            "capture_process_exited",
            true,
            "rebound".into(),
        );
        let warnings = source.take_warnings();
        assert_eq!(warnings.len(), 1);
        assert_eq!(warnings[0].code, "capture_process_exited");
        assert!(warnings[0].recovered);
        assert!(source.take_warnings().is_empty());
    }
}
