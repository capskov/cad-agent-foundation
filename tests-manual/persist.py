"""DWG persistence acceptance for AutoCAD 2022.

write mode  : write a plan, finalize it, then persist the drawing to a real .dwg
recheck mode: in a fresh process, reopen the same file and read the entities back

Nothing touches a project drawing: the output path is inside a scratch directory
and the source document is a brand new unsaved Drawing1.
"""
import json
import pathlib
import socket
import sys
import time

DISC = pathlib.Path(sys.argv[1])
DWG = sys.argv[2]
MODE = sys.argv[3]
HANDLES = sys.argv[4].split(",") if len(sys.argv) > 4 and sys.argv[4] else []

info = json.loads(DISC.read_text(encoding="utf-8"))
host, port, auth = info["host"], int(info["port"]), info["auth_token"]
control = info["control_pipe_name"]
print(f"discovery: pid={info['pid']} year={info['acad_year']} tcp={port} control={control}")


def tcp(cmd, params=None, rid="t"):
    req = json.dumps({"id": rid, "auth": auth, "cmd": cmd, "params": params or {}})
    s = socket.create_connection((host, port), timeout=120)
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


def ctl(cmd, params, rid="c"):
    path = "\\\\.\\pipe\\" + control
    payload = json.dumps({"id": rid, "auth": auth, "cmd": cmd, "params": params}) + "\n"
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
        deadline = time.time() + 40
        while b"\n" not in buf and time.time() < deadline:
            chunk = f.read(65536)
            if not chunk:
                break
            buf += chunk
    finally:
        f.close()
    return json.loads(buf.decode("utf-8", "replace").strip())


def wait_terminal(req):
    for i in range(25):
        body = ctl("job_get", {"request_id": req, "wait_ms": 0}, f"g{i}").get("result") or {}
        job = body.get("job") or {}
        print(f"  poll {i}: phase={job.get('phase')} effects={job.get('effects')} "
              f"verification={job.get('verification')} err={job.get('error_code')}")
        if job.get("phase") in ("completed", "failed", "cancelled"):
            return job
        time.sleep(1.0)
    return None


ctx_body = tcp("bind_active_document", {}, "b1")
ctx = ctx_body["result"]["context_id"]
if MODE == "recheck":
    di = ctx_body["result"]["document_info"]
    print(f"[reopened] path={di.get('drawing_path')} dbmod={di.get('dbmod')}")

if MODE == "write":
    plan = {"schema": "foundation-drawing-plan-v1", "source_role": "test",
            "units": "raw_drawing_units",
            "steps": [
                {"id": "l1", "op": "line", "start": [10.0, 10.0], "end": [110.0, 10.0],
                 "layer": "0", "color": 7},
                {"id": "c1", "op": "circle", "center": [60.0, 30.0], "radius": 7.0,
                 "layer": "0", "color": 7},
            ]}
    req = "persist-write-01"
    sub = tcp("job_submit", {"request_id": req, "context_id": ctx,
                             "tool_name": "foundation_apply_plan",
                             "canonical_parameters_json": json.dumps(plan, sort_keys=True, separators=(",", ":"))}, "s1")
    if not sub.get("ok"):
        print("submit failed:", sub.get("error"))
        sys.exit(2)
    print("\n[write] submitted, waiting for terminal state")
    job = wait_terminal(req)
    print("  effects:", job and job.get("effects"), " verification:", job and job.get("verification"))
    receipt = json.loads(job["result_json"])
    print("  outcome:", receipt.get("outcome"), " objects:", json.dumps(receipt.get("objects"), ensure_ascii=False))
    hs = [v["entity"] for v in (receipt.get("objects") or {}).values()]
    print("  created handles:", hs)

    print("\n[finalize]")
    row = None
    import sqlite3
    db = pathlib.Path(sys.argv[5]) if len(sys.argv) > 5 else None
    if db and db.exists():
        conn = sqlite3.connect(f"file:{db.as_posix()}?mode=ro", uri=True, timeout=5)
        conn.row_factory = sqlite3.Row
        row = conn.execute("select * from plugin_jobs where request_id=?", (req,)).fetchone()
        conn.close()
    fin = ctl("job_write_finalize", {
        "request_id": req, "context_id": row["context_id"],
        "staged_plugin_sequence": row["verification_staged_sequence"],
        "readback_revision": row["readback_revision"],
        "readback_digest": row["readback_digest"],
        "committed_result_digest": row["committed_result_digest"]}, "f1")
    print("  ok:", fin.get("ok"), fin.get("error"), (fin.get("result") or {}).get("disposition"))

    print("\n[save] persist to disk")
    sreq = "persist-save-01"
    sp = {"output_path": DWG, "confirm": True, "overwrite_existing": True, "allow_repo_output": True}
    sub = tcp("job_submit", {"request_id": sreq, "context_id": ctx, "tool_name": "save_drawing",
                             "canonical_parameters_json": json.dumps(sp, sort_keys=True, separators=(",", ":"))}, "s2")
    print("  submit ok:", sub.get("ok"), "error:", sub.get("error"))
    if sub.get("ok"):
        sjob = wait_terminal(sreq)
        print("  result_json:", str((sjob or {}).get("result_json"))[:300])
    print("  HANDLES=" + ",".join(hs))
    p = pathlib.Path(DWG)
    print("  file exists:", p.exists(), " bytes:", p.stat().st_size if p.exists() else 0)

if MODE == "recheck":
    print(f"\n[recheck] reading back handles: {HANDLES}")
    params = {"handles": HANDLES}
    sub = tcp("job_submit", {"request_id": "persist-recheck-01", "context_id": ctx,
                             "tool_name": "get_entity_properties",
                             "canonical_parameters_json": json.dumps(params, sort_keys=True, separators=(",", ":"))}, "r1")
    print("  submit ok:", sub.get("ok"), "error:", sub.get("error"))
    if sub.get("ok"):
        job = wait_terminal("persist-recheck-01")
        print("  result_json:", str((job or {}).get("result_json"))[:1500])

print("\nPERSIST_DONE")
