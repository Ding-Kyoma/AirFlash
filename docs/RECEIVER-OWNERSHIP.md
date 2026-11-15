# Receiver ownership and automatic recovery

AirFlash yields when another controller takes over a receiver. If ownership
cannot be confirmed, it pauses automatic recovery and offers manual reconnect.
Both outcomes restore local audio. Rediscovery, settings changes and returning
capture audio do not clear this protection; an explicit playback action does.

The native engine checks ownership after unexpected feedback connection loss,
after PTP silence exceeds its existing grace period, and after a
receiver-initiated TEARDOWN. A healthy RTP path means the receiver's active
session is ours: clock and feedback symptoms are auxiliary-channel evidence and
cannot by themselves turn our own session bit into another controller's. Only a
receiver-initiated TEARDOWN can outvote healthy RTP.

A receiver-initiated TEARDOWN followed by a confirmed active session produces
`preempted`. A probe that cannot reach a verdict (timeout, unreachable peer, or
contradictory rounds) produces `ownership_unconfirmed` with `retryable=false`
unless healthy media already proves the session is ours. Successful local UDP
writes and TCP connects are not ownership evidence; event-channel failure alone
remains an auxiliary-channel degradation.

A probe that settles on `ours_active` clears the uncertainty latch. An ordinary
stop then sends TEARDOWN again, and a paused clock fault resumes as soon as PTP
is fresh. A PTP path that stays silent for 30 seconds ends with the retryable
`clock_lost` instead. If any group member is preempted or still uncertain, the
whole group stops and skips the old associations' TEARDOWN requests. This
prevents cleanup from disturbing the controller now using the stereo pair.

IPC `start` accepts `intent: "manual" | "automatic"`. Omission defaults to
`manual` for existing explicit script invocations. Desktop retries, discovery
connections, settings rebuilds and native standby/source resumes are automatic.
Before establishing such a session, every receiver must report an inactive
session in two read-only `/info` rounds separated by 500 ms. No SETUP or RECORD
is sent if any member fails the check. The engine advertises
`receiver_ownership_guard`; the desktop requires it before starting playback.

Retry budgets reset only after 60 seconds of fresh evidence from every member:
successful feedback continues, its failure counter does not increase, feedback
is not delayed, and each required PTP association remains ready. A missing
member, stale feedback or a metrics gap breaks this healthy interval. Merely
remaining in the Streaming or Standby UI state does not reset retries.

Diagnostic warnings retain the original feedback fault or `clock_lost`, status
probe flags and the final recovery decision. A `clock_lost` warning is closed
with `recovered:true` once ownership is confirmed and PTP is fresh again; a
silent PTP path that outlasts 30 seconds becomes a retryable `clock_lost`
instead of a manual `ownership_unconfirmed`. The desktop also records when
manual reconnect is required and when verified health replenishes the budget.
The automated regression fixtures use localhost and simulated desktop services;
they do not replace testing a particular receiver's firmware.
