"""Save-failure path: a failed save must not look like success, and the fence must stay held."""
import importlib.util, json, pathlib, sys

work = pathlib.Path(sys.argv[1])
client = sys.argv[2]
spec = importlib.util.spec_from_file_location("foundation", client)
F = importlib.util.module_from_spec(spec)
spec.loader.exec_module(F)

plan = {"schema": "foundation-drawing-plan-v1", "source_role": "test", "units": "raw_drawing_units",
        "steps": [{"op": "line", "id": "f1", "layer": "0", "color": 7, "start": [400, 400], "end": [460, 400]}]}

with F.Gateway(work) as g:
    print("[ctx]", g.context)
    bad = g.apply_with_optional_save(plan, "fail-1", save_to=r"Z:\no-such-drive\x.dwg")
    summary = bad.get("outcome_summary") or {}
    print("[bad-save]", json.dumps({"status": bad.get("status"), "outcome": summary.get("outcome"),
                                    "persisted": bad.get("persisted"),
                                    "next_action": bad.get("next_action"),
                                    "save_phase": (bad.get("save_reply") or {}).get("job", {}).get("phase"),
                                    "save_error": (bad.get("save_reply") or {}).get("error")}, ensure_ascii=False)[:400])
    print("[fence-still-held]", g.query("fail-1").get("write_fence_released"))

    good = g.persist(work / "drawings" / "fail-recover.dwg")
    print("[good-save]", (good.get("saved") or {}).get("saved_path"))
    final = g.query("fail-1")
    print("[final]", json.dumps({"outcome": (final.get("outcome_summary") or {}).get("outcome"),
                                 "fence_released": final.get("write_fence_released")}, ensure_ascii=False))
