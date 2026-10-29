//! Best-effort QoS for the media socket.
//!
//! Wi-Fi access points honour WMM categories, and the category follows the DSCP
//! value in the IP header. Without a mark, audio competes with a Windows Update
//! download at the same priority and the receiver hears it.
//!
//! qWAVE is the supported path: `QOSAddSocketToFlow` registers the socket with the
//! QoS packet scheduler and `QOSTrafficTypeVoice` puts it in the voice queue, which
//! is what a HomePod needs. It can fail where the QoS service is disabled, so every
//! failure degrades to "no mark" instead of failing the session: audio without a
//! mark is fine, audio without a socket is not.

/// Created once and kept for the process lifetime: closing it removes every flow.
#[cfg(windows)]
fn qwave_handle() -> Option<*mut core::ffi::c_void> {
    use std::sync::OnceLock;
    use windows::Win32::Foundation::HANDLE;
    use windows::Win32::NetworkManagement::QoS::{QOSCreateHandle, QOS_VERSION};
    static QWAVE: OnceLock<Option<usize>> = OnceLock::new();
    (*QWAVE.get_or_init(|| unsafe {
        let version = QOS_VERSION {
            MajorVersion: 1,
            MinorVersion: 0,
        };
        let mut handle = HANDLE::default();
        QOSCreateHandle(&raw const version, &raw mut handle)
            .as_bool()
            .then_some(handle.0 as usize)
    }))
    .map(|value| value as *mut core::ffi::c_void)
}

#[cfg(windows)]
pub fn mark_audio_socket(socket: &std::net::UdpSocket) -> bool {
    use std::os::windows::io::AsRawSocket;
    use windows::Win32::Foundation::HANDLE;
    use windows::Win32::NetworkManagement::QoS::{QOSAddSocketToFlow, QOSTrafficTypeVoice};
    use windows::Win32::Networking::WinSock::SOCKET;
    let Some(handle) = qwave_handle() else {
        return false;
    };
    let mut flow = 0u32;
    unsafe {
        QOSAddSocketToFlow(
            HANDLE(handle),
            SOCKET(socket.as_raw_socket() as usize),
            None,
            QOSTrafficTypeVoice,
            Some(0),
            &raw mut flow,
        )
    }
    .as_bool()
}

#[cfg(not(windows))]
pub fn mark_audio_socket(_socket: &std::net::UdpSocket) -> bool {
    false
}

#[cfg(test)]
mod tests {
    #[test]
    fn marking_a_loopback_socket_never_fails_the_session() {
        // Whether the QoS service is available is not something a test can assert;
        // the contract is that marking is safe, idempotent and never panics.
        let socket = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        super::mark_audio_socket(&socket);
        super::mark_audio_socket(&socket);
    }
}
