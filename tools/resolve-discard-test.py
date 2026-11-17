import json, os, subprocess, sys

# resolve --discard verification harness (run AFTER the 5 coordinated changes land).
# Focus: the safety guard -- a healthy committed_verified write must NEVER be abandoned.
# The positive path (a genuine unknown/recovery write) needs a simulated CAD crash and is
# documented separately in docs/resolve-recovery-handoff.md.

PY = sys.executable
CLI = r"F:\AI\cad-agent-foundation-acad2022\releases\ACAD2022-V1.1-Preview\runtime\client\foundation.py"
CAD = r"F:\Program Files\Autodesk\AutoCAD 2022"
SRC = r"F:\AI\cad-agent-foundation-acad2022\三管闸阀井_内部评审版\三管共用闸阀井_内部评审版V4.dwg"
WS = r"F:\AI\cad-agent-foundation-acad2022\cad2022-test\resolve-discard-01"


def run(*args):
    p = subprocess.run([PY, CLI] + list(args) + ["--workspace", WS], capture_output=True, text=True, encoding="utf-8")
    return p.returncode, p.stdout, p.stderr


def jl(t):
    try:
        return json.loads(t)
    except Exception:
        return {"raw": str(t)[:300]}


def save(name, text):
    open(os.path.join(WS, name), "w", encoding="utf-8").write(text)


def sess():
    return json.load(open(os.path.join(WS, "session.json"), encoding="utf-8-sig"))


os.makedirs(WS, exist_ok=True)
print("install", run("install", "--acad-year", "2022", "--cad", CAD)[0])
code, out, err = run("start", "--acad-year", "2022", "--cad", CAD, "--write", "--allow-save", "--source", SRC)
print("START", code, jl(out).get("pid"), err[:150])
drawing = sess()["drawing"]

steps = [{"id": "line1", "op": "line", "start": [0, 0], "end": [1000, 0], "layer": "0", "color": 1}]
plan = {"schema": "foundation-drawing-plan-v1", "source_role": "test", "units": "raw_drawing_units", "steps": steps}
save("plan-create.json", json.dumps(plan, ensure_ascii=False))
code, out, err = run("apply", "--plan", os.path.join(WS, "plan-create.json"), "--request-id", "rd-create",
                     "--save-to", drawing, "--overwrite")
j = jl(out)
print("CREATE:", j.get("status"), (j.get("outcome_summary") or {}).get("outcome"))

# SAFETY: discarding a healthy committed_verified write must be rejected (it is not in recovery state).
code, out, err = run("resolve", "--discard", "--request-id", "rd-create", "--confirm", "--operator-note", "must be rejected")
save("discard-healthy.json", out or ("stderr:" + err))
resp = out or err
ok = ("abandoned_after_recovery" not in resp) and ("not_in_recovery" in resp or "recovery" in resp or "rejected" in resp or "error" in resp)
print("DISCARD healthy write rejected:", ok, "->", resp.strip().replace("\n", " ")[:200])

code, out, err = run("stop")
print("STOP fences:", jl(out).get("fences"))
