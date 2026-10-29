//! Loopback sockets for tests that must keep the protocol-fixed PTP ports.
//!
//! The AirPlay PTP receiver protocol always uses ports 319 (event) and 320
//! (general), so a simulated receiver cannot choose its port with `bind` to 0
//! the way the RTSP/RTP mocks do. Each simulated receiver instead gets a fresh
//! loopback address: the ports stay protocol-accurate, while parallel test
//! suites can no longer collide on the same `address:port` pair.

use std::net::{IpAddr, Ipv4Addr, UdpSocket};

/// A loopback address that another test process is not expected to hold.
fn unique_loopback() -> IpAddr {
    let bytes = rand::random::<u32>().to_be_bytes();
    IpAddr::V4(Ipv4Addr::new(
        127,
        1 + bytes[1] % 254,
        1 + bytes[2] % 254,
        1 + bytes[3] % 254,
    ))
}

/// Bind the receiver half of a PTP pair (ports 319/320 are protocol-fixed) on a
/// fresh loopback address. Retries on the unlikely collision with another
/// suite that picked the same address.
pub(crate) fn ptp_receiver_sockets() -> (IpAddr, UdpSocket, UdpSocket) {
    loop {
        let ip = unique_loopback();
        let Ok(event) = UdpSocket::bind((ip, 319)) else {
            continue;
        };
        let Ok(general) = UdpSocket::bind((ip, 320)) else {
            continue;
        };
        return (ip, event, general);
    }
}
