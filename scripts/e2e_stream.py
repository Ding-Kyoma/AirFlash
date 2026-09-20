"""Native HomePod E2E: always <=5s, 440Hz/.05 WAV and sender gain <=.1."""

import argparse
import asyncio
from pathlib import Path

if __package__:
    from .native_probe import run_probe
else:
    from native_probe import run_probe


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--host", required=True)
    p.add_argument("--peer-host", action="append", default=[])
    p.add_argument("--engine", choices=["native"], default="native")
    p.add_argument("--duration", type=float, default=5)
    p.add_argument("--gain", type=float, default=0.1)
    p.add_argument("--sample-rate", type=int, choices=[44100, 48000], default=44100)
    p.add_argument("--latency-ms", type=int, default=150)
    p.add_argument("--timing", choices=["ptp", "ntp"], default="ptp")
    p.add_argument("--handshake-only", action="store_true")
    p.add_argument("--group-id")
    p.add_argument("--report", type=Path)
    p.add_argument("--record-mic", type=Path)
    p.add_argument("--source", choices=["wav", "loopback"], default="wav")
    a = p.parse_args()
    if a.duration > 5:
        print("--duration capped at 5 seconds")
    raise SystemExit(
        asyncio.run(
            run_probe(
                [a.host, *a.peer_host],
                min(a.duration, 5),
                a.gain,
                a.latency_ms,
                a.timing,
                a.handshake_only,
                a.report,
                a.group_id,
                a.record_mic,
                a.source,
                a.sample_rate,
            )
        )
    )


if __name__ == "__main__":
    main()
