import json, os, subprocess, sys, importlib.util, shutil
from pathlib import Path

PY = sys.executable
CLI = r"F:\AI\cad-agent-foundation-acad2022\releases\ACAD2022-V1.1-Preview\runtime\client\foundation.py"
CAD = r"F:\Program Files\Autodesk\AutoCAD 2022"
SRC = r"F:\AI\cad-agent-foundation-acad2022\三管闸阀井_内部评审版\三管共用闸阀井_内部评审版V4.dwg"
WS = r"F:\AI\cad-agent-foundation-acad2022\cad2022-test\resolve-discard-diag-01"


def run(*args):
    p = subprocess.run([PY, CLI] + list(args) + ["--workspace", WS], capture_output=True, text=True, encoding="utf-8")
    return p.returncode, p.stdout, p.stderr


def jl(t):
    try:
        return json.loads(t)
    except Exception:
        return {"raw": str(t)[:300]}


os.makedirs(os.path.dirname(WS), exist_ok=True)
if os.path.isdir(WS):
    shutil.rmtree(WS)
os.makedirs(WS, exist_ok=True)
print("install", run("install", "--acad-year", "2022", "--cad", CAD)[0])
r = run("start", "--acad-year", "2022", "--cad", CAD, "--write", "--allow-save", "--source", SRC)
print("START", r[0], r[1][:120], r[2][:100])
sess = json.load(open(os.path.join(WS, "session.json"), encoding="utf-8-sig"))
drawing = sess["drawing"]; pid = sess["pid"]; started = sess["started"]
print("session.drawing =", drawing)
print("session.pid     =", pid)
print("session.started =", started)

plan = {"schema": "foundation-drawing-plan-v1", "source_role": "test", "units": "raw_drawing_units",
        "steps": [{"id": "line1", "op": "line", "start": [0, 0], "end": [1000, 0], "layer": "0", "color": 1}]}
open(os.path.join(WS, "plan.json"), "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False))
c, o, e = run("apply", "--plan", os.path.join(WS, "plan.json"), "--request-id", "diag-create", "--save-to", drawing, "--overwrite")
j = jl(o)
print("APPLY:", j.get("status"), (j.get("outcome_summary") or {}).get("outcome"), e[:120])
print("save_reply.saved_path =", (j.get("save_reply") or {}).get("saved_path"))

reqp = os.path.join(WS, "requests", "diag-create.json")
req = json.load(open(reqp, encoding="utf-8")) if os.path.exists(reqp) else {}
print("req.context_id =", req.get("context_id"))
print("req.complete_target_json =", json.dumps(req.get("complete_target_json"), ensure_ascii=False)[:300])

spec = importlib.util.spec_from_file_location("fnd", CLI)
F = importlib.util.module_from_spec(spec); sys.modules["fnd"] = F; spec.loader.exec_module(F)

g = F.Gateway(Path(WS))
try:
    g.__enter__()
except BaseException as ex:
    print("GATE ERROR:", ex)
bound = getattr(g, "bound", None) or {}
snap = bound.get("binding_snapshot") or {}
print("bound.context_id   =", bound.get("context_id"))
print("snapshot.drawing_path =", snap.get("drawing_path"))
print("snapshot.owner_pid =", snap.get("owner_pid"), " expected:", pid)
try:
    print("norm(snapshot.drawing_path) =", F.E.normalize_absolute_dwg_path(snap.get("drawing_path"), "snap"))
except Exception as ex:
    print("norm(snap) err:", ex)
try:
    print("norm(session.drawing)       =", F.E.normalize_absolute_dwg_path(drawing, "sess"))
except Exception as ex:
    print("norm(sess) err:", ex)
try:
    print("document_info.doc_name =", (bound.get("document_info") or {}).get("name") or (bound.get("document_info") or {}).get("doc_name"))
except Exception:
    pass
print("INSPECT", run("resolve", "--inspect", "--request-id", "diag-create"))
print("DISCARD", run("resolve", "--discard", "--request-id", "diag-create", "--confirm", "--operator-note", "diag"))
print("STATUS", run("status"))
print("STOP", run("stop"))

try:
    tl = subprocess.run(["tasklist", "/FI", "PID eq " + str(pid)], capture_output=True, text=True).stdout
    if str(pid) in tl:
        subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True, text=True)
        print("killed", pid)
except Exception as ex:
    print("kill err", ex)
print("done")
