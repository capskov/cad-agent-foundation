import json, pathlib, subprocess, sys, math

HERE = pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\cad2022-test")
CLIENT_DIR = pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\src\cad-agent-foundation-main\artifacts\packages\20261116-fix13\client")
sys.path.insert(0, str(CLIENT_DIR))
import foundation as F

PY = sys.executable
CLIENT = CLIENT_DIR / "foundation.py"
CAD = r"F:\Program Files\Autodesk\AutoCAD 2022"
WS = HERE / "v11-wallAO"
SRC = HERE / "v11-wallB" / "s3.dwg"
PLAN = HERE / "v11-wall" / "plan-06e-parallel.json"
OUT = WS / "s11-final2.dwg"

WS.mkdir(parents=True, exist_ok=True)
install = json.loads((HERE / "v11-wallX" / "installation.json").read_text(encoding="utf-8"))
(WS / "installation.json").write_text(json.dumps(install, ensure_ascii=False, indent=1), encoding="utf-8")

cmd = [PY, str(CLIENT), "--workspace", str(WS), "--acad-year", "2022", "--cad", CAD,
       "start", "--source", str(SRC), "--write", "--allow-save", "--headless"]
p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", timeout=300)
print("start rc", p.returncode, p.stderr[-400:])
if p.returncode != 0:
    raise SystemExit("start failed")


def ents(g, args):
    r = g.submit_read("dwg_query_entities", args)
    return ((r.get("data") or {}).get("entities")) or []


def props(g, handles):
    out = {}
    for i in range(0, len(handles), 40):
        r = g.submit_read("dwg_get_entity_properties",
                          {"handles": json.dumps(handles[i:i + 40]), "include_geometry": True})
        for e in ((r.get("data") or {}).get("entities") or []):
            out[e.get("handle")] = e.get("entity") or {}
    return out


with F.Gateway(WS) as g:
    before_l = ents(g, {"entity_type": "Leader", "layer": "标注-引线", "limit": 50})
    before_t = ents(g, {"entity_type": "MText", "layer": "标注-文字", "limit": 50})
    print("before callouts", len(before_l), len(before_t))
    if before_l or before_t:
        raise SystemExit("source already has callouts; refusing to stack")

    plan = json.loads(PLAN.read_text(encoding="utf-8"))
    res = g.apply(plan, None)
    print("apply", json.dumps({k: res.get(k) for k in ("request_id", "status", "phase", "effects", "verification", "write_fence_released", "error", "next_action") if k in res}, ensure_ascii=False))
    print("apply full", json.dumps(res, ensure_ascii=False, default=str)[:1600])
    if res.get("verification") != "passed":
        raise SystemExit("apply verification not passed")

    leaders = ents(g, {"entity_type": "Leader", "layer": "标注-引线", "limit": 50})
    texts = ents(g, {"entity_type": "MText", "layer": "标注-文字", "limit": 50})
    print("after callouts", len(leaders), len(texts))
    lp = props(g, [e["handle"] for e in leaders if e.get("handle")])
    tp = props(g, [e["handle"] for e in texts if e.get("handle")])
    rows = []
    for h, e in lp.items():
        v = e.get("vertices") or []
        if len(v) >= 3:
            rows.append({"leader": h, "A": v[0], "B": v[1], "C": v[2]})
    for h, e in tp.items():
        rows.append({"text_handle": h, "text": e.get("text"), "insert": e.get("insert"),
                     "attachment": e.get("attachment"), "actual_width": e.get("actual_width"),
                     "actual_height": e.get("actual_height"), "extents": e.get("extents")})
    print("readback", json.dumps(rows, ensure_ascii=False, indent=1))

    if len(leaders) != 6 or len(texts) != 6:
        raise SystemExit("expected exactly 6 callouts after apply")

    rid = res.get("request_id")
    p2 = g.persist(OUT, overwrite=False)
    print("persist", json.dumps({k: p2.get(k) for k in ("status", "phase", "verification", "write_fence_released", "pending", "error", "saved", "next_action") if k in p2}, ensure_ascii=False))
    if rid:
        q2 = g.query(rid, wait=30)
        print("query-after-save", json.dumps({k: q2.get(k) for k in ("status", "phase", "verification", "write_fence_released", "pending", "error", "next_action") if k in q2}, ensure_ascii=False))

print("s11-final2", OUT.exists(), OUT.stat().st_size if OUT.exists() else 0)
