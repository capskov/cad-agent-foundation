"""Control-pipe probe for AutoCAD 2022.

Verifies that the ported control channel exposes exactly the four governance
commands, that the data channel still refuses them, and that a bad token is
rejected before dispatch.
"""
import json
import pathlib
import socket
import sys
import time

DISC = pathlib.Path(sys.argv[1])
REQ = "probe-ctl-01"

info = json.loads(DISC.read_text(encoding="utf-8"))
host, port, auth = info["host"], int(info["port"]), info["auth_token"]
control = info.get("control_pipe_name")
print(f"discovery: year={info['acad_year']} tcp={port} control_pipe={control}")
if not control:
    print("FAIL: control_pipe_name missing from discovery")
    sys.exit(2)


def tcp_call(cmd, params=None, rid="t"):
    req = json.dumps({"id": rid, "auth": auth, "cmd": cmd, "params": params or {}})
    s = socket.create_connection((host, port), timeout=60)
    try:
        s.sendall((req + "\n").encode("utf-8"))
        buf = b""
        while b"\n" not in buf:
            chunk = s.recv(65536)
            if not chunk:
                break
            buf += chunk
    finally:
        s.close()
    line = buf.decode("utf-8", "replace").strip()
    try:
        return json.loads(line)
    except Exception:
        return {"raw": line}


def ctl_call(cmd, params=None, rid="c", token=None):
    path = "\\\\.\\pipe\\" + control
    payload = json.dumps({"id": rid, "auth": token or auth, "cmd": cmd,
                          "params": params or {}}) + "\n"
    # The listener may be recreating its instance; a short retry is expected.
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
        deadline = time.time() + 20
        while b"\n" not in buf and time.time() < deadline:
            chunk = f.read(65536)
            if not chunk:
                break
            buf += chunk
    finally:
        f.close()
    line = buf.decode("utf-8", "replace").strip()
    try:
        return json.loads(line)
    except Exception:
        return {"raw": line}


print("\n[1] bind + submit over TCP")
ctx = tcp_call("bind_active_document", {}, "b1")["result"]["context_id"]
print("  context_id:", ctx)
sub = tcp_call("job_submit", {
    "request_id": REQ, "context_id": ctx, "tool_name": "list_layers",
    "canonical_parameters_json": "{}"}, "b2")
print("  submit:", sub.get("ok"), (sub.get("result") or {}).get("disposition"),
      ((sub.get("result") or {}).get("job") or {}).get("phase"))

print("\n[2] job_get over the control pipe, polled until terminal")
final = None
for i in range(15):
    r = ctl_call("job_get", {"request_id": REQ, "wait_ms": 0}, f"g{i}")
    if not r.get("ok"):
        print(f"  {i}: not ok -> {r.get('error')}")
        break
    body = r.get("result") or {}
    job = body.get("job") or {}
    print(f"  {i}: found={body.get('found')} phase={job.get('phase')} "
          f"effects={job.get('effects')} result={str(job.get('result_json'))[:80]}")
    if job.get("phase") in ("completed", "failed", "cancelled"):
        final = job
        break
    time.sleep(1.0)

print("\n[3] the data channel must still refuse control commands")
r3 = tcp_call("job_get", {"request_id": REQ, "wait_ms": 0}, "d1")
print("  tcp job_get ->", r3.get("ok"), r3.get("error"))

print("\n[4] a wrong token must be rejected before dispatch")
r4 = ctl_call("job_get", {"request_id": REQ, "wait_ms": 0}, "c4", token="deadbeef")
print("  bad auth ->", r4.get("ok"), r4.get("error"))

print("\n[5] a non-control command must be refused on the control pipe")
r5 = ctl_call("list_layers", {}, "c5")
print("  list_layers on control ->", r5.get("ok"), r5.get("error"))

print("\n[6] job_get_historical")
r6 = ctl_call("job_get_historical", {"request_id": REQ}, "c6")
print("  historical ->", r6.get("ok"), str(r6.get("error")),
      str((r6.get("result") or {}))[:200])

print("\nCONTROL_PROBE_DONE")
