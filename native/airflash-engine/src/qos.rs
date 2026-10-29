//! Best-effort QoS marking for the media socket.
//!
//! Wi-Fi access points honour WMM categories, and the category is chosen from the
//! DSCP value in the IP header. Without a mark, audio competes with a Windows
//! Update download at the same priority and the receiver hears it. Marking the
//! socket as Expedited Forwarding (DSCP 46) is one `setsockopt` and needs no
//! lifecycle management; qWAVE would additionally reserve the flow at the NDIS
//! layer, but that needs a handle per socket and a fallback when the service is
//! missing, so it is deliberately left out until the simple mark is shown to be
//! insufficient.

#[cfg(windows)]
pub fn mark_audio_socket(socket: &std::net::UdpSocket) -> bool {
    use std::os::windows::io::AsRawSocket;
    use windows::Win32::Networking::WinSock::{SOCKET, setsockopt};
    // winsock2.h values; stable across Windows versions.
    const IPPROTO_IP: i32 = 0;
    const IP_TOS: i32 = 3;
    // DSCP 46 (EF) lives in the upper six bits; the low two are ECN.
    const EF: u32 = 46 << 2;
    unsafe {
        setsockopt(
            SOCKET(socket.as_raw_socket() as usize),
            IPPROTO_IP,
            IP_TOS,
            Some(EF.to_ne_bytes().as_slice()),
        ) == 0
    }
}

#[cfg(not(windows))]
pub fn mark_audio_socket(_socket: &std::net::UdpSocket) -> bool {
    false
}

#[cfg(test)]
mod tests {
    #[test]
    fn marking_a_loopback_socket_never_fails_the_session() {
        // Whether Windows honours IP_TOS is not something we can assert here; the
        // contract is that trying it is safe and idempotent.
        let socket = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        super::mark_audio_socket(&socket);
        assert!(super::mark_audio_socket(&socket));
    }
}
