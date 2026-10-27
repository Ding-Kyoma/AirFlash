# Troubleshooting PTP startup

`PTP exchange did not become ready` means a receiver did not complete the
required clock exchanges before the startup deadline. Discovery and successful
audio-format negotiation do not establish that UDP timing traffic can pass.

Reproduce one connection failure and collect the matching `Session failure`
entry from `%APPDATA%\AirFlash\logs\wpf-YYYY-MM-DD.log`. Its `ClockStatus`
snapshot includes the engine executable path, per-receiver role and local
address, packet counts, rejected-packet reasons, socket errors, and clock phase.
Use the snapshot from that attempt; an engine update can change the executable
path. Do not include pairing files or credentials.

| Peer phase | Meaning and next check |
| --- | --- |
| `no_rx` | No datagram from this receiver reached the PTP sockets. Check UDP 319/320, the active Windows network profile, VPN/routing, and receiver-side reachability. This alone does not prove a firewall block. |
| `rx_rejected` | Traffic arrived but failed source-port or packet validation. Inspect `packets.last_rejection`. |
| `waiting_announce` | Receiver-clock mode has not identified the receiver's announced clock. Confirm the selected clock mode and receiver behavior. |
| `waiting_sync_pair` | A fresh matching Sync/Follow_Up pair is missing. Inspect the packet types and rejection reason. |
| `waiting_delay_response` | Receiver-clock mode still needs a matching delay response and a valid offset sample. |
| `waiting_exchanges` | More valid exchanges are needed, or earlier exchanges expired. Inspect exchange count, timeout and offset/RTT rejection reasons. |
| `ready` | The required timing exchanges passed. This is not an acoustic latency measurement. |

`received_by_type` uses the PTP message type as its index: Sync 0, Delay_Req 1,
Pdelay_Req 2, Follow_Up 8, Delay_Resp 9, Announce 11 and Signaling 12.
`network` counters include datagrams rejected before peer validation.
An OS error code in `last_send_error` or `last_receive_error` identifies a local
socket failure. Normal nonblocking polling is not logged as an error.

## Windows Firewall

The process owning PTP sockets is
`%LOCALAPPDATA%\AirFlash\engine\<hash>\airflash-engine.exe`, extracted from the
application. A program rule for the outer `AirFlash.exe` does not match that
process. The hash directory changes when the engine changes.

In Windows Defender Firewall with Advanced Security, inspect rules applying to
the engine path in the failure snapshot:

- Timing uses **UDP**, not TCP. Check the actual protocol field, not the rule name.
- Incoming timing traffic uses local ports **319 and 320**.
- If outbound traffic is restricted, outgoing timing traffic uses remote ports
  **319 and 320**.
- The rule's profile must match the current connection. A Private-only rule
  does not apply to a connection categorized as Public.
- Restrict any exception to the trusted receiver or local subnet and the
  intended network profile. An explicit block rule can override an allow rule.
- Recheck the program path after an update; an exception for an older hash
  directory does not cover the new engine.

AirFlash reports the path and timing evidence; it does not change firewall
policy automatically. On managed computers, local rules may be overridden by
administrator policy.

If timing still fails, include the complete failure snapshot and the HomePod
software version in the issue. Packet capture around the failed attempt, filtered
to UDP 319/320, can distinguish traffic blocked before the process from packets
the process receives and rejects. Playback or loud test tones are not needed.
