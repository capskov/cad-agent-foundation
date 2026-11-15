"""Operator recovery probe (RC3): resolve a fenced request after its owner process died.

Usage:
  python rc3_resolve.py <discovery.json> <request_id> [operator_note]

Sends job_recovery_resolve over the control pipe only. It never resubmits or
replays the original write.
"""
import json
import pathlib
import sys
import time

DISC = pathlib.Path(sys.argv[1])
REQ = sys.argv[2]
NOTE = sys.argv[3] if len(sys.argv) > 3 else "operator reviewed saved dwg and journal"

info = json.loads(DISC.read_text(encoding="utf-8"))
control = info.get("control_pipe_name")
auth = info["auth_token"]
print(f"discovery pid={info['pid']} control_pipe={control}")
if not control:
    print("FAIL: control_pipe_name missing")
    sys.exit(2)

path = "\\\\.\\pipe\\" + control
payload = json.dumps({"id": "resolve-1", "auth": auth, "cmd": "job_recovery_resolve",
                      "params": {"request_id": REQ, "confirm": True, "operator_note": NOTE}}) + "\n"
last = None
for _ in range(20):
    try:
        f = open(path, "r+b", buffering=0)
        break
    except OSError as e:
        last = e
        time.sleep(0.1)
else:
    raise last
try:
    f.write(payload.encode("utf-8"))
    buf = b""
    deadline = time.time() + 25
    while b"\n" not in buf and time.time() < deadline:
        chunk = f.read(65536)
        if not chunk:
            break
        buf += chunk
finally:
    f.close()
line = buf.decode("utf-8", "replace").strip()
print("[resolve-raw]", line[:1200])
