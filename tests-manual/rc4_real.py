"""RC4 real drawing: operate on an owned copy of a real engineering DWG.

Usage: python rc4_real.py <workspace> <client.py> <source.dwg> <cad_root>
"""
import importlib.util, json, pathlib, subprocess, sys

work = pathlib.Path(sys.argv[1])
client = sys.argv[2]
source = sys.argv[3]
cad = sys.argv[4]

def run(*args):
    out = subprocess.run([sys.executable, client, *args], capture_output=True, text=True, encoding="utf-8")
    if out.returncode != 0:
        print("[cli-fail]", " ".join(args[:2]), (out.stdout or "")[-400:], (out.stderr or "")[-400:])
    return out

work.mkdir(parents=True, exist_ok=True)
run("install", "--workspace", str(work), "--acad-year", "2022", "--cad", cad)
started = run("start", "--workspace", str(work), "--acad-year", "2022", "--write", "--allow-save",
              "--cad", cad, "--source", source, "--headless")
print("[start]", (started.stdout or "").strip()[-220:])

spec = importlib.util.spec_from_file_location("foundation", client)
F = importlib.util.module_from_spec(spec)
spec.loader.exec_module(F)

with F.Gateway(work) as g:
    print("[ctx]", g.context)
    layers = g.submit_read("dwg_list_layers", {})
    data = layers.get("data") or {}
    names = [l.get("name") for l in (data.get("layers") or [])][:12]
    print("[layers]", len(data.get("layers") or []), names)

    lines = g.submit_read("dwg_query_entities", {"entity_type": "Line", "limit": 2})
    records = (lines.get("data") or {}).get("entities") or []
    print("[lines]", json.dumps(records, ensure_ascii=False)[:600])

    anchor = None
    if records:
        g0 = records[0].get("geometry") or {}
        start = g0.get("start") or {}
        if start:
            anchor = (float(start["x"]) + 10.0, float(start["y"]) + 10.0)
    if anchor is None:
        anchor = (0.0, 0.0)
    print("[anchor]", anchor)

    before_handle = records[0].get("handle") if records else None
    before_digest = records[0].get("entity_digest") if records else None

    plan = {
        "schema": "foundation-drawing-plan-v1",
        "source_role": "test",
        "units": "raw_drawing_units",
        "steps": [{
            "op": "callout", "id": "rco1", "layer": "0", "color": 7,
            "text": "交通安全设施\\P迁移验证标注",
            "position": [anchor[0] + 60.0, anchor[1] + 30.0],
            "height": 3.5, "width": 70,
            "vertices": [[anchor[0], anchor[1]], [anchor[0] + 40.0, anchor[1] + 20.0], [anchor[0] + 56.0, anchor[1] + 20.0]],
            "dot_radius": 0.8, "text_color": 7, "gap": 0,
        }],
    }
    target = work / "drawings" / "real-callout.dwg"
    reply = g.apply_with_optional_save(plan, "real-co", save_to=str(target))
    summary = reply.get("outcome_summary") or {}
    handles = []
    raw = (reply.get("job") or {}).get("result_json")
    if isinstance(raw, str):
        try:
            handles = json.loads(raw).get("created_handles") or []
        except Exception:
            pass
    print("[apply]", json.dumps({"status": reply.get("status"), "outcome": summary.get("outcome"),
                                 "persisted": reply.get("persisted"),
                                 "fence": reply.get("write_fence_released"),
                                 "saved": (reply.get("save_reply") or {}).get("saved_path"),
                                 "handles": handles,
                                 "before_handle": before_handle, "before_digest": before_digest},
                                ensure_ascii=False))
    (work / "real-result.json").write_text(json.dumps({"reply": reply, "before_handle": before_handle,
                                                       "before_digest": before_digest}, ensure_ascii=False)[:40000],
                                           encoding="utf-8")
