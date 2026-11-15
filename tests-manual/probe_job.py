"""Stage-2b probe: does the 2022 execution queue actually run the job?

`job_submit` is the only control surface available over TCP on 2022 (job_get,
job_cancel, job_get_historical and job_write_finalize are control-pipe-only and
the pipe is not ported yet). So the job is observed by re-submitting the same
request_id -- the journal answers idempotently with the existing row, whose
phase/result reveal whether the queue drained it.

Read-only only: list_layers cannot modify or save the drawing.
"""
import json
import pathlib
import socket
import sqlite3
import sys
import time

DISC = pathlib.Path(sys.argv[1])
DB = pathlib.Path(sys.argv[2])

info = json.loads(DISC.read_text(encoding="utf-8"))
host, port, auth = info["host"], int(info["port"]), info["auth_token"]
print(f"discovery: acad_year={info['acad_year']} pid={info['pid']} {host}:{port}")


def call(cmd, params=None, rid="p", timeout=120):
    req = json.dumps({"id": rid, "auth": auth, "cmd": cmd, "params": params or {}})
    s = socket.create_connection((host, port), timeout=timeout)
    s.settimeout(timeout)
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
    return json.loads(buf.decode("utf-8", "replace").strip())


ctx = call("bind_active_document", {}, "ctx")["result"]["context_id"]
print("context_id:", ctx)

REQ = "probe-cell-02"
payload = {
    "request_id": REQ,
    "context_id": ctx,
    "tool_name": "list_layers",
    "canonical_parameters_json": "{}",
}

print(f"\n[poll] submitting {REQ} repeatedly, watching phase")
final = None
for attempt in range(12):
    r = call("job_submit", payload, f"j{attempt}")
    if not r.get("ok"):
        print(f"  attempt {attempt}: FAILED {r.get('error')}")
        break
    job = r["result"]["job"]
    phase = job.get("phase")
    disp = r["result"].get("disposition")
    has_result = job.get("result_json") is not None
    print(f"  attempt {attempt}: disposition={disp} phase={phase} "
          f"effects={job.get('effects')} has_result={has_result} "
          f"error_code={job.get('error_code')}")
    if phase in ("completed", "failed", "cancelled") or has_result:
        final = job
        break
    time.sleep(1.5)

if final:
    print("\n[final job]")
    for k in ("phase", "effects", "verification", "is_write", "error_code",
              "error_message", "plugin_sequence", "created_at_utc", "updated_at_utc"):
        print(f"  {k}: {final.get(k)}")
    rj = final.get("result_json")
    if rj:
        print("  result_json:", rj[:700])
else:
    print("\nWARNING: job never left 'queued' within the poll window")

print("\n[journal db]")
try:
    conn = sqlite3.connect(f"file:{DB.as_posix()}?mode=ro", uri=True, timeout=5)
    tables = [r[0] for r in conn.execute(
        "select name from sqlite_master where type='table' order by name")]
    print("  tables:", tables)
    for t in tables:
        n = conn.execute(f"select count(*) from {t}").fetchone()[0]
        print(f"  {t}: {n} rows")
    conn.close()
except Exception as e:
    print("  db read failed:", e)
