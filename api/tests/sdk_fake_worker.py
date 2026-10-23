"""Executable SDK test fixture using the real NDJSON transport and file artifacts."""
import json
import os
from pathlib import Path
import sys
import time

caps = [] if os.environ.get("CCP_TEST_OLD_WORKER") else ["custom_references_v1", "chart_identity_v1", "combined_lut65_v1"]
print(json.dumps({"ok": True, "event": "ready", "capabilities": caps}), flush=True)
for line in sys.stdin:
    request = json.loads(line)
    command = request["cmd"]
    if command == "shutdown":
        print(json.dumps({"ok": True}), flush=True)
        break
    print(json.dumps({"type": "progress", "message": "working"}), flush=True)
    reply = {"ok": True, "echo": request, "apply_backend": "cpu"}
    if command == "detect":
        if "slow" in request["image"]:
            time.sleep(1)
        faces = [{"id": index, "chart_id": f"fixture-{index}", "cli_chart": "classic", "rows": 4, "cols": 6,
                  "patch_obbs": [[[0, 0], [1, 0], [1, 1], [0, 1]]] * 24}
                 for index in range(2 if "multi" in request["image"] else 1)]
        reply.update(n_charts=len(faces), device="cpu", handshake={"charts": faces, "snapshot_id": "fixture-snapshot", "backend": "onnx"})
    elif command == "run":
        model = {"version": 2, "kind": "rpcc2", "correction": {}, "do_ffc": request.get("do_ffc", False)}
        Path(request["model_out"]).write_text(json.dumps(model), encoding="utf-8")
        report = {"do_ffc": model["do_ffc"], "delta_e_2000": {"mean_after": 1.2}, "validation_scope": "cc-only"}
        Path(request["report"]).write_text(json.dumps(report), encoding="utf-8")
        Path(request["output"]).write_text("image", encoding="utf-8")
        reply.update(report_json=report, **{key: request[key] for key in ("output", "report", "model_out")})
    elif command == "apply":
        rows = []
        for index, source in enumerate(request["images"]):
            if "bad" in source:
                rows.append({"ok": False, "image": source, "error": "Invalid image"})
                continue
            dest = Path(request["output_dir"]) / f"image-{index}.png"
            if request.get("write_output", True):
                dest.write_text("corrected", encoding="utf-8")
            rows.append({"ok": True, "image": source, "output": str(dest), "apply_backend": "cpu"})
        reply.update(results=rows)
    elif command == "export_cube":
        Path(request["path"]).write_text("LUT_3D_SIZE 2", encoding="utf-8")
        reply["path"] = request["path"]
    print(json.dumps(reply), flush=True)
