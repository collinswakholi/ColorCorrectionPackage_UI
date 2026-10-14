"""Real-pipe fixture, never used by the application."""
import json
from pathlib import Path
import sys
import time

print(json.dumps({"ok": True, "event": "ready"}), flush=True)
pending = None
for line in sys.stdin:
    request = json.loads(line)
    command = request["cmd"]
    if command == "deferred":
        pending = request["path"]
    if command == "shutdown":
        if pending:
            Path(pending).write_text("complete", encoding="utf-8")
        print(json.dumps({"ok": True, "event": "shutdown"}), flush=True)
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
