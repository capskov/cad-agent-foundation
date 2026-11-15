"""RC3 state matrix: one session, several governance cases.

Cases:
  A  default apply without --save-to      -> verified but not persisted, fence held
  B  same request_id again                -> idempotent replay of the same answer
  C  new write while a fence is held      -> refused before submission, nothing queued
  D  explicit save of A + re-query        -> save then finalize, fence released
  E  save to an illegal path              -> refused, no saved file
  F  cancel an already completed request  -> refused (queued-only)

Usage:
  python rc3_matrix.py <workspace>
"""
import json, pathlib, sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve()))

work = pathlib.Path(sys.argv[1])
ROOT = pathlib.Path(work.parent.parent)

import importlib.util  # noqa: E402

spec = importlib.util.spec_from_file_location(
    "foundation", r"F:\AI\cad-agent-foundation-acad2022\src\cad-agent-foundation-main\artifacts\packages\20261115-140318\client\foundation.py")
F = importlib.util.module_from_spec(spec)
spec.loader.exec_module(F)

PLAN_A = json.loads(pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\cad2022-test\rc2-plan-write.json").read_text(encoding="utf-8"))
PLAN_C = json.loads(json.dumps(PLAN_A))
PLAN_C["steps"][0]["end"] = [100, 40]

results = {}


def brief(reply, keys=("status", "outcome", "outcome_summary", "persisted", "next_action", "write_fence_released", "reason", "error")):
    out = {}
    for k in keys:
        if k == "outcome_summary" and isinstance(reply.get(k), dict):
            s = reply[k]
            out["outcome"] = s.get("outcome")
            out["next_action"] = s.get("next_action")
            out["effects"] = s.get("effects")
            out["verification"] = s.get("verification")
            out["fence_released"] = s.get("write_fence_released")
        elif k in reply and k != "outcome_summary":
            out[k] = reply[k]
    return out


with F.Gateway(work) as g:
    ctx = g.context
    print("[ctx]", ctx)

    write_a = g.apply(PLAN_A, "matrix-a")
    results["A"] = brief(write_a)
    print("[A] default apply:", json.dumps(results["A"], ensure_ascii=False))

    write_b = g.apply(PLAN_A, "matrix-a")
    results["B"] = brief(write_b)
    print("[B] same request_id:", json.dumps(results["B"], ensure_ascii=False))

    try:
        write_c = g.apply(PLAN_C, "matrix-c")
        results["C"] = brief(write_c)
        results["C"]["journal_has_c"] = g.journal_has("matrix-c")
    except Exception as ex:
        results["C"] = {"exception": str(ex), "journal_has_c": g.journal_has("matrix-c")}
    print("[C] second write while held:", json.dumps(results["C"], ensure_ascii=False))

    bad = pathlib.Path(work) / ".." / ".." / "escape.dwg"
    try:
        saved_e = g.persist(str(bad))
        results["E"] = brief(saved_e) | {"saved_path": (saved_e.get("saved") or {}).get("saved_path")}
    except Exception as ex:
        results["E"] = {"exception": str(ex)}
    print("[E] illegal save path:", json.dumps(results["E"], ensure_ascii=False))

    target = pathlib.Path(work) / "drawings" / "matrix-a.dwg"
    saved_d = g.persist(str(target), request_id="save-matrix-a")
    results["D1"] = brief(saved_d) | {"saved_path": (saved_d.get("saved") or {}).get("saved_path")}
    print("[D1] save:", json.dumps(results["D1"], ensure_ascii=False))

    final = g.query({"request_id": "matrix-a", "context_id": ctx}, wait=120)
    results["D2"] = brief(final) | {"persisted": final.get("persisted")}
    print("[D2] re-query original write:", json.dumps(results["D2"], ensure_ascii=False))

    try:
        cancelled = g.call("dwg_job_cancel", {"context_id": ctx, "request_id": "matrix-a"})
        results["F"] = {"status": cancelled.get("status"), "error": cancelled.get("error")}
    except Exception as ex:
        results["F"] = {"exception": str(ex)}
    print("[F] cancel completed job:", json.dumps(results["F"], ensure_ascii=False))

db = pathlib.Path(work) / "native" / "Dwg" / "execution" / "plugin-execution.sqlite"
results["journal"] = {"exists": db.exists()}
out = work / "matrix-results.json"
out.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
print("[written]", out, "db:", db)
