"""RC4 real drawing, step 2: assume an owned session already exists."""
import importlib.util, json, pathlib, sys

work = pathlib.Path(sys.argv[1])
client = sys.argv[2]
spec = importlib.util.spec_from_file_location("foundation", client)
F = importlib.util.module_from_spec(spec)
spec.loader.exec_module(F)

with F.Gateway(work) as g:
    print("[ctx]", g.context)
    layers = g.submit_read("dwg_list_layers", {})
    data = layers.get("data") or {}
    print("[layers]", len(data.get("layers") or []), [l.get("name") for l in (data.get("layers") or [])][:10])

    lines = g.submit_read("dwg_query_entities", {"entity_type": "Line", "limit": 2})
    records = (lines.get("data") or {}).get("entities") or []
    print("[lines-count]", len(records))
    print("[line0]", json.dumps(records[0], ensure_ascii=False)[:700] if records else "none")

    anchor = (0.0, 0.0)
    if records:
        start = ((records[0].get("geometry") or {}).get("start") or {})
        if start:
            anchor = (float(start["x"]) + 10.0, float(start["y"]) + 10.0)
    print("[anchor]", anchor)
    before = {"handle": records[0].get("handle"), "digest": records[0].get("entity_digest")} if records else {}

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
                                 "handles": handles, "before": before}, ensure_ascii=False))
    (work / "real-result.json").write_text(
        json.dumps({"reply": reply, "before": before}, ensure_ascii=False)[:40000], encoding="utf-8")
