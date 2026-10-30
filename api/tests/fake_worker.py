"""Real-pipe fixture, never used by the application."""
import json
from pathlib import Path
import sys
import time

print(json.dumps({"ok": True, "event": "ready"}), flush=True)
pending = None
cleanup_delay = 0
cleanup_exit = 0
for line in sys.stdin:
    request = json.loads(line)
    command = request["cmd"]
    if command == "deferred":
        pending = request["path"]
    if command == "deferred_after_ack":
        pending = request["path"]
        cleanup_delay = request.get("delay", .15)
        cleanup_exit = request.get("exit_code", 0)
    if command in ("shutdown", "quit"):
        if pending and not cleanup_delay:
            Path(pending).write_text("complete", encoding="utf-8")
        print(json.dumps({"ok": True, "event": "shutdown"}), flush=True)
        if cleanup_delay:
            time.sleep(cleanup_delay)
            Path(pending).write_text("complete", encoding="utf-8")
        if cleanup_exit:
            sys.exit(cleanup_exit)
        break
    if command == "slow":
        time.sleep(1)
    if command == "invalid":
        print("not-json", flush=True)
        continue
    if command == "flood":
        for _ in range(10000):
            print("x" * 100, file=sys.stderr)
    if command == "crash":
        sys.exit(3)
    print(json.dumps({"type": "progress", "message": "working"}), flush=True)
    if command == "fail":
        reply = {"ok": False, "error": "deliberate failure"}
    elif command == "partial":
        reply = {"ok": True, "results": [{"ok": True}, {"ok": False}], "error": "1 of 2 images failed"}
    else:
        reply = {"ok": True, "echo": request}
    print(json.dumps(reply), flush=True)
