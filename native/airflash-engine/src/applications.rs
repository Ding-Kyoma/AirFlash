//! Application identities and Windows process-tree loopback activation.
use anyhow::{Context, Result, ensure};
use serde::{Deserialize, Serialize};
use std::{
    collections::{HashMap, HashSet},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    time::{Duration, Instant},
};
use windows::{
    Win32::{
        Foundation::*,
        Media::Audio::*,
        System::{
            Com::{StructuredStorage::*, *},
            Diagnostics::ToolHelp::*,
            LibraryLoader::*,
            SystemInformation::OSVERSIONINFOW,
            Threading::*,
            Variant::VT_BLOB,
        },
        UI::WindowsAndMessaging::*,
    },
    core::{BOOL, Interface, Ref, implement},
};

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Application {
    pub path: String,
    pub name: String,
    pub process_id: u32,
    pub started_at: u64,
}
pub struct Process(pub HANDLE);
impl Drop for Process {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}
impl Process {
    pub fn open(pid: u32) -> Result<Self> {
        Ok(Self(unsafe {
            OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
                false,
                pid,
            )
        }?))
    }
    pub fn alive(&self) -> bool {
        (unsafe { WaitForSingleObject(self.0, 0) }) == WAIT_TIMEOUT
    }
    fn identity(&self, pid: u32) -> Result<Application> {
        let mut buffer = vec![0u16; 32768];
        let mut size = buffer.len() as u32;
        unsafe {
            QueryFullProcessImageNameW(
                self.0,
                PROCESS_NAME_WIN32,
                windows::core::PWSTR(buffer.as_mut_ptr()),
                &mut size,
            )
        }?;
        let path = String::from_utf16_lossy(&buffer[..size as usize]);
        let (mut created, mut exited, mut kernel, mut user) = (
            FILETIME::default(),
            FILETIME::default(),
            FILETIME::default(),
            FILETIME::default(),
        );
        unsafe { GetProcessTimes(self.0, &mut created, &mut exited, &mut kernel, &mut user) }?;
        Ok(Application {
            name: std::path::Path::new(&path)
                .file_name()
                .unwrap_or_default()
                .to_string_lossy()
                .into_owned(),
            path,
            process_id: pid,
            started_at: (u64::from(created.dwHighDateTime) << 32)
                | u64::from(created.dwLowDateTime),
        })
    }
    pub fn verify(application: &Application) -> Result<Self> {
        let process = Self::open(application.process_id)?;
        let current = process.identity(application.process_id)?;
        ensure!(
            current.path.eq_ignore_ascii_case(&application.path)
                && current.started_at == application.started_at
                && process.alive(),
            "selected application instance changed"
        );
        Ok(process)
    }
}
pub fn supported() -> bool {
    // Query the real build without depending on an executable compatibility manifest.
    unsafe {
        let Ok(module) = GetModuleHandleW(windows::core::w!("ntdll.dll")) else {
            return false;
        };
        let Some(address) = GetProcAddress(module, windows::core::s!("RtlGetVersion")) else {
            return false;
        };
        let version: unsafe extern "system" fn(*mut OSVERSIONINFOW) -> i32 =
            std::mem::transmute(address);
        let mut info = OSVERSIONINFOW {
            dwOSVersionInfoSize: std::mem::size_of::<OSVERSIONINFOW>() as u32,
            ..Default::default()
        };
        version(&mut info) == 0 && info.dwMajorVersion >= 10 && info.dwBuildNumber >= 20348
    }
}
fn processes() -> Result<(HashMap<u32, Application>, HashMap<u32, u32>)> {
    let snapshot = Process(unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) }?);
    let mut entry = PROCESSENTRY32W {
        dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
        ..Default::default()
    };
    let mut parents = HashMap::new();
    let mut items = HashMap::new();
    let mut more = unsafe { Process32FirstW(snapshot.0, &mut entry) }.is_ok();
    while more {
        let pid = entry.th32ProcessID;
        parents.insert(pid, entry.th32ParentProcessID);
        if pid != unsafe { GetCurrentProcessId() } {
            if let Ok(item) = Process::open(pid).and_then(|process| process.identity(pid)) {
                items.insert(pid, item);
            }
        }
        more = unsafe { Process32NextW(snapshot.0, &mut entry) }.is_ok();
    }
    Ok((items, parents))
}
fn root(pid: u32, items: &HashMap<u32, Application>, parents: &HashMap<u32, u32>) -> u32 {
    let mut current = pid;
    let mut visited = HashSet::new();
    while visited.insert(current) {
        let Some(parent) = parents.get(&current).and_then(|p| items.get(p)) else {
            break;
        };
        let Some(child) = items.get(&current) else {
            break;
        };
        if !child.path.eq_ignore_ascii_case(&parent.path) || parent.started_at > child.started_at {
            break;
        }
        current = parent.process_id;
    }
    current
}
pub fn matching(path: &str) -> Result<Vec<Application>> {
    let (items, parents) = processes()?;
    let roots: HashSet<_> = items
        .values()
        .filter(|p| p.path.eq_ignore_ascii_case(path))
        .map(|p| root(p.process_id, &items, &parents))
        .collect();
    Ok(roots
        .into_iter()
        .filter_map(|pid| items.get(&pid).cloned())
        .collect())
}
/// A stale identity may follow one replacement, but must never choose among
/// independent instances solely because Windows reused the old process ID.
pub fn select(candidates: &[Application], instance: Option<(u32, u64)>) -> Option<Application> {
    instance
        .and_then(|(pid, started)| {
            candidates
                .iter()
                .find(|p| p.process_id == pid && p.started_at == started)
        })
        .cloned()
        .or_else(|| (candidates.len() == 1).then(|| candidates[0].clone()))
}
unsafe extern "system" fn window(hwnd: HWND, data: LPARAM) -> BOOL {
    if unsafe { IsWindowVisible(hwnd) }.as_bool() && unsafe { GetWindowTextLengthW(hwnd) } > 0 {
        let mut pid = 0;
        unsafe {
            GetWindowThreadProcessId(hwnd, Some(&mut pid));
        }
        unsafe { &mut *(data.0 as *mut HashSet<u32>) }.insert(pid);
    }
    BOOL(1)
}
pub fn list() -> Result<Vec<Application>> {
    unsafe { CoInitializeEx(None, COINIT_MULTITHREADED).ok() }?;
    struct Com;
    impl Drop for Com {
        fn drop(&mut self) {
            unsafe {
                CoUninitialize();
            }
        }
    }
    let _com = Com;
    let mut visible = HashSet::<u32>::new();
    unsafe { EnumWindows(Some(window), LPARAM(&mut visible as *mut _ as isize)) }?;
    let enumerator: IMMDeviceEnumerator =
        unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }?;
    let devices = unsafe { enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE) }?;
    for index in 0..unsafe { devices.GetCount() }? {
        let mut collect = || -> Result<()> {
            let device = unsafe { devices.Item(index) }?;
            let manager: IAudioSessionManager2 = unsafe { device.Activate(CLSCTX_ALL, None) }?;
            let sessions = unsafe { manager.GetSessionEnumerator() }?;
            for index in 0..unsafe { sessions.GetCount() }? {
                if let Ok(session) = unsafe { sessions.GetSession(index) }
                    .and_then(|s| s.cast::<IAudioSessionControl2>())
                {
                    if let Ok(pid) = unsafe { session.GetProcessId() } {
                        visible.insert(pid);
                    }
                }
            }
            Ok(())
        };
        // A disappearing endpoint must not prevent listing other applications.
        if let Ok(()) = collect() {}
    }
    let (items, parents) = processes()?;
    let roots: HashSet<_> = visible
        .into_iter()
        .filter(|pid| items.contains_key(pid))
        .map(|pid| root(pid, &items, &parents))
        .collect();
    let mut result: Vec<_> = roots
        .into_iter()
        .filter_map(|pid| items.get(&pid).cloned())
        .collect();
    result.sort_by_key(|p| (p.name.to_lowercase(), p.process_id));
    Ok(result)
}

// Both callback and capture worker belong to the process MTA. The client is
// moved once under the mutex and thereafter used only by the capture worker.
struct MtaClient(IAudioClient);
unsafe impl Send for MtaClient {}

#[implement(IActivateAudioInterfaceCompletionHandler, IAgileObject)]
struct Activation {
    result: Arc<Mutex<Option<windows::core::Result<MtaClient>>>>,
    // Retained by COM until the callback has completed, including after cancellation.
    params: AUDIOCLIENT_ACTIVATION_PARAMS,
}
impl IAgileObject_Impl for Activation_Impl {}
impl IActivateAudioInterfaceCompletionHandler_Impl for Activation_Impl {
    fn ActivateCompleted(
        &self,
        operation: Ref<'_, IActivateAudioInterfaceAsyncOperation>,
    ) -> windows::core::Result<()> {
        let result = (|| {
            let mut hr = windows::core::HRESULT(0);
            let mut interface = None;
            unsafe { operation.ok()?.GetActivateResult(&mut hr, &mut interface) }?;
            hr.ok()?;
            interface
                .ok_or_else(windows::core::Error::empty)?
                .cast::<IAudioClient>()
                .map(MtaClient)
        })();
        *self.result.lock().unwrap() = Some(result);
        Ok(())
    }
}
pub fn activate(pid: u32, stop: &AtomicBool) -> Result<IAudioClient> {
    ensure!(
        supported(),
        "Application capture requires Windows build 20348 or later. Use a virtual audio endpoint on this Windows version."
    );
    let result = Arc::new(Mutex::new(None));
    let handler = Activation {
        result: result.clone(),
        params: AUDIOCLIENT_ACTIVATION_PARAMS {
            ActivationType: AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            Anonymous: AUDIOCLIENT_ACTIVATION_PARAMS_0 {
                ProcessLoopbackParams: AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS {
                    TargetProcessId: pid,
                    ProcessLoopbackMode: PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
                },
            },
        },
    };
    let handler: windows::core::ComObject<Activation> = windows::core::ComObject::new(handler);
    // The blob borrows callback-owned Rust memory; PropVariantClear must not free it.
    let variant = std::mem::ManuallyDrop::new(PROPVARIANT {
        Anonymous: PROPVARIANT_0 {
            Anonymous: std::mem::ManuallyDrop::new(PROPVARIANT_0_0 {
                vt: VT_BLOB,
                wReserved1: 0,
                wReserved2: 0,
                wReserved3: 0,
                Anonymous: PROPVARIANT_0_0_0 {
                    blob: BLOB {
                        cbSize: std::mem::size_of::<AUDIOCLIENT_ACTIVATION_PARAMS>() as u32,
                        pBlobData: &handler.params as *const _ as *mut u8,
                    },
                },
            }),
        },
    });
    let callback: IActivateAudioInterfaceCompletionHandler = handler.to_interface();
    let _operation = unsafe {
        ActivateAudioInterfaceAsync(
            VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
            &IAudioClient::IID,
            Some(&*variant),
            &callback,
        )
    }
    .context("activate application capture")?;
    let deadline = Instant::now() + Duration::from_secs(2);
    loop {
        if let Some(result) = result.lock().unwrap().take() {
            return result
                .map(|client| client.0)
                .context("initialize application capture");
        }
        ensure!(
            !stop.load(Ordering::Acquire),
            "application capture cancelled"
        );
        ensure!(
            Instant::now() < deadline,
            "application capture activation timed out"
        );
        std::thread::sleep(Duration::from_millis(5));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn follows_unique_restarts_and_requires_selection_for_independent_instances() {
        let original = Application {
            path: "C:\\player.exe".into(),
            name: "player".into(),
            process_id: 1,
            started_at: 10,
        };
        let replacement = Application {
            process_id: 2,
            started_at: 20,
            ..original.clone()
        };
        let reused = Application {
            started_at: 30,
            ..original.clone()
        };
        let hint = Some((original.process_id, original.started_at));
        assert!(select(&[], hint).is_none());
        assert_eq!(
            select(std::slice::from_ref(&replacement), hint)
                .unwrap()
                .process_id,
            2
        );
        assert!(select(&[replacement.clone(), reused.clone()], hint).is_none());
        assert!(select(&[replacement.clone(), reused.clone()], None).is_none());
        assert_eq!(
            select(&[replacement, reused], Some((1, 30)))
                .unwrap()
                .started_at,
            30
        );
    }
    #[test]
    fn roots_include_children_but_not_independent_or_reused_parent_ids() {
        let items: HashMap<_, _> = [(1, 10), (2, 20), (3, 30), (4, 5)]
            .into_iter()
            .map(|(pid, at)| {
                (
                    pid,
                    Application {
                        path: "C:\\player.exe".into(),
                        name: "player".into(),
                        process_id: pid,
                        started_at: at,
                    },
                )
            })
            .collect();
        let parents = HashMap::from([(2, 1), (3, 2), (4, 3)]);
        assert_eq!(root(3, &items, &parents), 1);
        assert_eq!(root(4, &items, &parents), 4);
        assert_eq!(root(1, &items, &parents), 1);
    }
    #[test]
    fn verifies_identity_and_rejects_reused_pid() {
        let pid = unsafe { GetCurrentProcessId() };
        let mut identity = Process::open(pid).unwrap().identity(pid).unwrap();
        assert!(Process::verify(&identity).is_ok());
        identity.started_at += 1;
        assert!(Process::verify(&identity).is_err());
    }
    #[test]
    fn supported_windows_can_activate_a_silent_process_without_opening_a_receiver() {
        if !supported() {
            return;
        }
        let pid = unsafe { GetCurrentProcessId() };
        let identity = Process::open(pid).unwrap().identity(pid).unwrap();
        let control =
            crate::equalizer::Control::new(crate::equalizer::Settings::default(), 44100).unwrap();
        let capture = crate::live::Loopback::start_application(identity, 44100, control).unwrap();
        std::thread::sleep(Duration::from_millis(40));
        assert_eq!(capture.last_audio(), 0);
        capture.ready().unwrap();
        drop(capture);
    }
}
