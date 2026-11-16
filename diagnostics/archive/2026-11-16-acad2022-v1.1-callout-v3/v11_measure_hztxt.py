import json, pathlib, subprocess, sys

HERE = pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\cad2022-test")
CLIENT_DIR = pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\src\cad-agent-foundation-main\artifacts\packages\20261116-fix13\client")
sys.path.insert(0, str(CLIENT_DIR))
import foundation as F
PY = sys.executable
CLIENT = CLIENT_DIR / "foundation.py"
CAD = r"F:\Program Files\Autodesk\AutoCAD 2022"
WS = HERE / "v11-wallAU"
SRC = HERE / "v11-wallAT" / "s3-hztxt.dwg"
WS.mkdir(parents=True, exist_ok=True)
install = json.loads((HERE / "v11-wallX" / "installation.json").read_text(encoding="utf-8"))
(WS / "installation.json").write_text(json.dumps(install, ensure_ascii=False, indent=1), encoding="utf-8")
cmd = [PY, str(CLIENT), "--workspace", str(WS), "--acad-year", "2022", "--cad", CAD,
       "start", "--source", str(SRC), "--write", "--headless"]
p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", timeout=300)
print("start", p.returncode, p.stderr[-300:])

texts = [
    ("m1", "240厚烧结页岩砖墙，M7.5水泥砂浆砌筑"),
    ("m2", "20厚1:2水泥砂浆抹面（双面）"),
    ("m3", "C25混凝土压顶60厚"),
    ("m4", "370厚砖砌大放脚"),
    ("m5", "C15混凝土条形基础"),
    ("m6", "室外地面±0.000"),
]
with F.Gateway(WS) as g:
    steps = []
    for i, (sid, text) in enumerate(texts):
        steps.append({"id": sid, "op": "mtext", "text": text,
                      "position": [1.0, 4.0 - i * 0.15], "height": 0.06,
                      "rotation": 0, "width": 2.0, "layer": "标注-文字",
                      "color": 7, "text_style": "WALL_TXT"})
    plan = {"schema": "foundation-drawing-plan-v1", "source_role": "annotation",
            "units": "raw_drawing_units", "steps": steps}
    res = g.apply(plan, None)
    print("apply", json.dumps({k: res.get(k) for k in ("request_id", "status", "phase", "effects", "verification", "error", "next_action") if k in res}, ensure_ascii=False))
    ents = ((g.submit_read("dwg_query_entities", {"entity_type": "MText", "layer": "标注-文字", "limit": 50}).get("data") or {}).get("entities")) or []
    handles = [e["handle"] for e in ents if e.get("handle")]
    props = {}
    for i in range(0, len(handles), 40):
        r = g.submit_read("dwg_get_entity_properties", {"handles": json.dumps(handles[i:i+40]), "include_geometry": True})
        for e in ((r.get("data") or {}).get("entities") or []):
            props[e.get("handle")] = e.get("entity") or {}
    for h, e in props.items():
        if e.get("text") in dict(texts).values() or e.get("text") in [t for _, t in texts]:
            print(json.dumps({"handle": h, "text": e.get("text"), "actual_width": e.get("actual_width"),
                              "actual_height": e.get("actual_height"), "extents": e.get("extents")}, ensure_ascii=False))
