"""Analyze a native E2E report and its WASAPI microphone recording."""
import argparse
import json
from pathlib import Path

if __package__:
    from .probe_analysis import analyze
else:
    from probe_analysis import analyze

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("report", type=Path)
    parser.add_argument("recording", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = analyze(args.report, args.recording)
    text = json.dumps(result, ensure_ascii=False, indent=2)
    print(text)
    if args.output:
        args.output.write_text(text + "\n", encoding="utf-8")
