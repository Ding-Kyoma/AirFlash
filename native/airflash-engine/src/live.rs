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
        Foundation::{CloseHandle, WAIT_TIMEOUT},
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
/// A silent stream only becomes a rebind probe after two seconds, and at most
/// once per minute. Non-silent audio returns the probe budget immediately.
const SILENT_PROBE_WINDOW: Duration = Duration::from_secs(2);
const SILENT_PROBE_INTERVAL: Duration = Duration::from_secs(60);
/// Without packets, wait this long after the last one before counting silence.
/// A render endpoint that simply has nothing playing is left alone: the probe
/// additionally requires that a discontinuity or rebind already happened.
const NO_PACKET_SILENCE_AFTER: Duration = Duration::from_millis(250);
const WARNING_QUEUE_CAP: usize = 16;

#[derive(Clone, Copy)]
struct Frame {
    left: f32,
    right: f32,
    qpc: u64,
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
    /// Silence probes that triggered a rebind (at most one per minute).
    #[serde(default)]
    pub silent_probes: u64,
    /// Why the last rebind happened (`process_exit`, `endpoint_lost`,
    /// `silent_probe`, `stream_error`).
    #[serde(default)]
    pub last_rebind_reason: Option<String>,
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
    equalizer: crate::equalizer::Processor,
    frames: VecDeque<Frame>,
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
        self.frames.push_back(frame);
    }
    /// A rebind keeps everything already encoded in `frames`: those samples are
    /// still valid and discarding them would only add a hole. Only the resampler
    /// input (owned by the capture thread) is flushed, and a splice boundary is
    /// recorded so the receiver can conceal the gap cleanly.
    fn note_rebind(&mut self, reason: &str) {
        self.boundary_pending = true;
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
        let mut underrun = false;
        if s.boundary_pending {
            s.splice.boundary();
            s.boundary_pending = false;
        }
        for _ in 0..FRAMES {
            let missing = force_silence || s.frames.is_empty();
            let frame = if let Some(f) = if force_silence {
                None
            } else {
                s.frames.pop_front()
            } {
                f
            } else {
                underrun |= !force_silence;
                if force_silence {
                    s.metrics.scheduled_silence_frames += 1;
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
            let age = now.saturating_sub(frame.qpc) as f64 / 1e6;
            s.metrics.max_queue_age_ms = s.metrics.max_queue_age_ms.max(age);
            if s.ages.len() == 4000 {
                s.ages.pop_front();
            }
            s.ages.push_back(age);
            let processed = s.equalizer.frame([frame.left, frame.right]);
            for value in s.splice.frame(processed, gain, missing) {
                out.extend(s.quantizer.quantize(value).to_be_bytes());
            }
        }
        if underrun {
            s.metrics.underrun_packets += 1;
        }
        s.metrics.transition_count = s.splice.transitions;
        Ok((out, marker))
    }
    /// Restore the capture queue to its normal target after a scheduler stall.
    pub fn discard_stale(&self) {
        let mut s = self.state.lock().unwrap();
        let cutoff = qpc_ns().saturating_sub(20_000_000);
        while s.frames.front().is_some_and(|frame| frame.qpc < cutoff) || s.frames.len() > s.target
        {
            s.frames.pop_front();
            s.metrics.dropped_frames += 1;
        }
        s.boundary_pending = true;
        s.metrics.last_discontinuity = Some("scheduler_recovery".into());
    }
    /// Age of the oldest queued capture frame: how stale the audio about to be
    /// sent already is. Feeds the `capture_age` telemetry histogram.
    pub fn sample_age_ms(&self) -> f64 {
        let s = self.state.lock().unwrap();
        s.frames
            .front()
            .map_or(0.0, |f| qpc_ns().saturating_sub(f.qpc) as f64 / 1e6)
    }
    /// `(queued, capacity)` in source frames.
    pub fn depth(&self) -> (usize, usize) {
        let s = self.state.lock().unwrap();
        (s.frames.len(), s.capacity)
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
            let plan = crate::applications::bind_plan(
                path,
                &candidates,
                *pinned,
                MAX_CAPTURE_INSTANCES,
            );
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
                return Err(last_error
                    .unwrap_or_else(|| anyhow::anyhow!("no running instance of {path}")));
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

/// Energy is only used as a probe. The cheap case (a shared-mode loopback that
/// was already reopened) emits no packets and loses nothing, so the rate limit
/// matters more than the threshold: never probe twice in a minute, and never
/// before a real break or rebind has shown that this session can self-heal.
struct SilenceProbe {
    window: Duration,
    interval: Duration,
    silent_since: Option<Instant>,
    last_probe: Option<Instant>,
}
impl SilenceProbe {
    fn new(window: Duration, interval: Duration) -> Self {
        Self {
            window,
            interval,
            silent_since: None,
            last_probe: None,
        }
    }
    fn observe(&mut self, now: Instant, silent: bool, after_discontinuity: bool) -> bool {
        if !silent {
            self.silent_since = None;
            self.last_probe = None;
            return false;
        }
        if !after_discontinuity {
            self.silent_since = None;
            return false;
        }
        let since = *self.silent_since.get_or_insert(now);
        if now.saturating_duration_since(since) < self.window {
            return false;
        }
        if self
            .last_probe
            .is_some_and(|last| now.saturating_duration_since(last) < self.interval)
        {
            return false;
        }
        self.last_probe = Some(now);
        self.silent_since = Some(now);
        true
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
fn mark_discontinuity(state: &Arc<Mutex<State>>, seen: &mut bool) {
    let mut shared = state.lock().unwrap();
    shared.metrics.dropped_frames += shared.frames.len() as u64;
    shared.frames.clear();
    shared.boundary_pending = true;
    shared.metrics.discontinuities += 1;
    shared.metrics.last_discontinuity = Some("wasapi_discontinuity".into());
    *seen = true;
}

#[allow(clippy::too_many_arguments)]
fn drain_single(
    binding: &mut Binding,
    pipeline: &mut Pipeline,
    packets: &mut usize,
    audio: &mut bool,
    last_packet: &mut Instant,
    state: &Arc<Mutex<State>>,
    servo: &Arc<Mutex<crate::servo::LatenessServo>>,
    drift: &Arc<Mutex<crate::servo::DriftEstimator>>,
    regulator: &mut crate::servo::QueueRegulator,
    last_tick: &mut Instant,
    discontinuity_seen: &mut bool,
) -> std::result::Result<(), ReadFailure> {
    while unsafe { binding.reader.GetNextPacketSize() }.map_err(ReadFailure::windows)? > 0 {
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
                mark_discontinuity(state, discontinuity_seen);
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
    discontinuity_seen: &mut bool,
) -> std::result::Result<(), ReadFailure> {
    let mut round_end: Option<u64> = None;
    for binding in bindings.iter_mut() {
        let mut binding_end = None;
        while unsafe { binding.reader.GetNextPacketSize() }.map_err(ReadFailure::windows)? > 0 {
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
                    mark_discontinuity(state, discontinuity_seen);
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
            binding_end = Some(
                timestamp.saturating_add(frames as u64 * 1_000_000_000 / binding.rate as u64),
            );
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
    let mut probe = SilenceProbe::new(SILENT_PROBE_WINDOW, SILENT_PROBE_INTERVAL);
    let mut discontinuity_seen = false;
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
        let mut read_failure = None;
        if waited != WAIT_TIMEOUT {
            let result = if bindings.len() == 1 {
                drain_single(
                    &mut bindings[0],
                    &mut pipeline,
                    &mut packets,
                    &mut audio,
                    &mut last_packet,
                    state,
                    servo,
                    drift,
                    &mut regulator,
                    &mut last_tick,
                    &mut discontinuity_seen,
                )
            } else {
                drain_multi(
                    &mut bindings,
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
                    &mut discontinuity_seen,
                )
            };
            if let Err(failure) = result {
                read_failure = Some(failure);
            }
        }
        if let Some(failure) = read_failure {
            trigger = Some(failure.reason);
            read_message = Some(failure.message);
        }
        let now = Instant::now();
        if trigger.is_none()
            && packets == 0
            && now.saturating_duration_since(last_packet) > Duration::from_secs(2)
        {
            // A silent render endpoint emits no packets. Ask each stream whether
            // it still exists; a disconnected one is a rebind, not a fatal error.
            for binding in &bindings {
                if let Err(error) = unsafe { binding.reader.GetNextPacketSize() } {
                    trigger = Some(ReadFailure::windows(error).reason);
                    break;
                }
            }
        }
        if trigger.is_none() && budget.can_attempt() {
            let silent = if packets > 0 {
                !audio
            } else {
                now.saturating_duration_since(last_packet) > NO_PACKET_SILENCE_AFTER
            };
            if probe.observe(now, silent, discontinuity_seen) {
                state.lock().unwrap().metrics.silent_probes += 1;
                trigger = Some("silent_probe");
            }
        }
        let Some(reason) = trigger else {
            continue;
        };
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
                        warn(
                            state,
                            "capture_endpoint_reopened",
                            true,
                            format!(
                                "Capture stream reopened after {reason}{detail} ({count} instance(s))"
                            ),
                        );
                    }
                    discontinuity_seen = true;
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

    #[test]
    fn silence_probe_needs_the_window_a_prior_break_and_one_probe_per_minute() {
        let start = Instant::now();
        let mut probe = SilenceProbe::new(Duration::from_secs(2), Duration::from_secs(60));
        // Playback has not started: no discontinuity, so silence is never a probe.
        for seconds in [0, 1, 2, 10, 120] {
            assert!(!probe.observe(start + Duration::from_secs(seconds), true, false));
        }
        // A real break happened: the two-second window starts now.
        assert!(!probe.observe(start, true, true));
        assert!(!probe.observe(start + Duration::from_millis(1999), true, true));
        assert!(probe.observe(start + Duration::from_secs(2), true, true));
        // One probe per minute, measured from the probe itself.
        assert!(!probe.observe(start + Duration::from_secs(3), true, true));
        assert!(!probe.observe(start + Duration::from_secs(61), true, true));
        assert!(probe.observe(start + Duration::from_secs(62), true, true));
        // Non-silent audio clears the run and the probe budget.
        assert!(!probe.observe(start + Duration::from_secs(63), false, true));
        assert!(!probe.observe(start + Duration::from_secs(64), true, true));
        assert!(probe.observe(start + Duration::from_secs(66), true, true));
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
    const TARGET: usize = crate::rtp::RATE as usize * 20 / 1000;
    pub(crate) fn source() -> Loopback {
        let equalizer_control =
            crate::equalizer::Control::new(crate::equalizer::Settings::default(), crate::rtp::RATE)
                .unwrap();
        Loopback {
            state: Arc::new(Mutex::new(State {
                splice: crate::continuity::Splice::new(crate::rtp::RATE),
                quantizer: crate::continuity::Quantizer::new(),
                boundary_pending: false,
                equalizer: crate::equalizer::Processor::new(
                    equalizer_control.initial(),
                    crate::rtp::RATE,
                ),
                frames: VecDeque::new(),
                capacity: CAPACITY,
                target: TARGET,
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
                crate::rtp::RATE as f64,
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
        assert!((i32::from(left) - i32::from(expected)).abs() <= 1, "{left} vs {expected}");
        assert!((i32::from(right) + i32::from(expected)).abs() <= 1, "{right} vs -{expected}");
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
    fn rebind_keeps_already_encoded_frames_and_only_marks_a_boundary() {
        let source = source();
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
            let before = s.metrics.discontinuities;
            assert!(!s.boundary_pending);
            s.note_rebind("process_exit");
            // Reopening the capture endpoint must not throw away valid audio.
            assert_eq!(s.frames.len(), queued);
            assert_eq!(s.metrics.dropped_frames, 0);
            assert!(s.boundary_pending);
            assert_eq!(s.metrics.discontinuities, before + 1);
            assert_eq!(
                s.metrics.last_discontinuity.as_deref(),
                Some("process_exit")
            );
        }
        assert!(source.packet(0.1).unwrap().0.iter().any(|b| *b != 0));
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
