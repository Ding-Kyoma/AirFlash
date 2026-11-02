# Receiver ownership and automatic recovery

AirFlash yields when another controller takes over a receiver. If ownership
cannot be confirmed, it pauses automatic recovery and offers manual reconnect.
Both outcomes restore local audio. Rediscovery, settings changes and returning
capture audio do not clear this protection; an explicit playback action does.

The native engine checks ownership after unexpected feedback connection loss
and after PTP silence exceeds its existing grace period. A receiver-initiated
TEARDOWN followed by a confirmed active session produces `preempted`. Missing,
failed or contradictory probes, or an active session without proof of its
owner, produce `ownership_unconfirmed` with `retryable=false`. Successful local
UDP writes and TCP connects are not ownership evidence. Event-channel failure
alone remains an auxiliary-channel degradation.

If any group member is preempted or uncertain, the whole group stops and skips
the old associations' TEARDOWN requests. This prevents cleanup from disturbing
the controller now using the stereo pair.

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
probe flags and the final recovery decision. The desktop also records when
manual reconnect is required and when verified health replenishes the budget.
The automated regression fixtures use localhost and simulated desktop services;
they do not replace testing a particular receiver's firmware.
