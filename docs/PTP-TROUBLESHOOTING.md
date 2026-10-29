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
| `waiting_delay_response` | Receiver-clock mode has no offset sample at all. A complete Sync/Follow_Up pair normally supplies one, so this phase means the pair cache is empty as well. |
| `waiting_exchanges` | More valid exchanges are needed, or earlier exchanges expired. Inspect exchange count, timeout and offset/RTT rejection reasons. |
| `ready` | The required timing exchanges passed. This is not an acoustic latency measurement. |

`received_by_type` uses the PTP message type as its index: Sync 0, Delay_Req 1,
Pdelay_Req 2, Follow_Up 8, Delay_Resp 9, Announce 11 and Signaling 12.

`offset_source` reports which exchange produced the receiver-to-local offset.
`sync` means a complete Sync/Follow_Up pair supplied it; that estimate omits the
path delay. `delay` means a matching delay response refined it by measuring the
round trip. That measurement remains preferred for two seconds after the last
accepted delay response; after it expires, current Sync pairs supply the offset
and `offset_source` returns to `sync`. Rejected responses do not extend this
period. A receiver that answers the sync exchange but never supplies a consistent
delay response therefore still reaches `ready` with `offset_source=sync`.

A rejected delay response is recorded in `packets.last_rejection` with the
computed round trip in `packets.last_round_trip_ns`. `round_trip` means the
computed round trip is outside the accepted 0–250 ms range; the sample is
discarded without refreshing the delay measurement. Report that number together
with the receiver model when a `sync`-sourced offset behaves incorrectly.

`network` counters include datagrams rejected before peer validation.
An OS error code in `last_send_error` or `last_receive_error` identifies a local
socket failure. Normal nonblocking polling is not logged as an error.

## Connected but no audio on a standalone HomePod

A standalone HomePod on HomePod software 27 keeps announcing itself as the PTP
grandmaster and stays silent when the sender also supplies the timeline.
AirFlash recognises the standalone shape from the receiver TXT record (`model`
starting with `AudioAccessory`, `igl=1`, no `pgid`, no `tsid`, and `osvers` 27 or
later) and follows that receiver's clock instead. Releases before 0.5.0 always
acted as the grandmaster, so they establish the session and send audio that the
HomePod never renders.

The `Receiver negotiated:` line in `wpf-YYYY-MM-DD.log` records the selected
codec, transport, timing and `clock_role`. `clock_role follow` means the
receiver's clock is in use. `clock_role sender` means AirFlash supplies the
timeline, which is correct for a stereo pair or an Apple TV output but not for a
standalone OS 27 HomePod. An NTP-timed realtime stream is also silent on a
HomePod, so a followed receiver stays on PTP even when the PTP feature bit is not
advertised.

If a current release still streams without audio while `clock_role follow` is
reported, include that line and the `clock_status` snapshot. UDP 319/320 must
reach the engine for the follow exchange, so the firewall checks below still
apply.

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
