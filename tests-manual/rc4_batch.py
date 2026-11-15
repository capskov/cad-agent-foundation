"""RC4 batch: guard checks + multi-callout write in one owned session."""

import importlib.util, json, pathlib, sys

work = pathlib.Path(sys.argv[1])
client_path = sys.argv[2]
spec = importlib.util.spec_from_file_location("foundation", client_path)
F = importlib.util.module_from_spec(spec)
spec.loader.exec_module(F)

plan = json.loads(pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\cad2022-test\rc4-plan-batch-callout.json").read_text(encoding="utf-8"))
save_to = pathlib.Path(work) / "drawings" / "batch-callout.dwg"

with F.Gateway(work) as g:
    print("[ctx]", g.context, "save_enabled", g.session.get("save_enabled"))

    try:
        g.persist(r"..\..\escape3.dwg")
        print("[E1] UNEXPECTED: traversal accepted")
    except Exception as ex:
        print("[E1] client refused:", type(ex).__name__, str(ex)[:140])

    payload = json.dumps({"output_path": str(pathlib.Path(work).parent.parent / "escape3.dwg"),
                          "confirm": True, "overwrite_existing": True, "allow_repo_output": False},
                         ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    try:
        reply = g.call("dwg_foundation_save_drawing",
                       {"context_id": g.context, "request_id": "traversal-02", "request_json": payload})
        keys = ("status", "registered", "effects", "error")
        print("[E2] server reply:", json.dumps({k: reply.get(k) for k in keys}, ensure_ascii=False)[:400])
    except Exception as ex:
        print("[E2] server refused:", type(ex).__name__, str(ex)[:200])

    write = g.apply_with_optional_save(plan, "batch-co", save_to=str(save_to))
    summary = write.get("outcome_summary") or {}
    print("[BATCH]", json.dumps({"status": write.get("status"), "outcome": summary.get("outcome"),
                                 "next_action": write.get("next_action") or summary.get("next_action"),
                                 "persisted": write.get("persisted"),
                                 "fence_released": write.get("write_fence_released"),
                                 "saved_path": (write.get("save_reply") or {}).get("saved_path")},
                                ensure_ascii=False))
    raw = (write.get("job") or {}).get("result_json")
    handles = None
    if isinstance(raw, str):
        try:
            data = json.loads(raw)
            handles = data.get("handles") or data.get("created_handles") or data
        except Exception:
            pass
    print("[HANDLES]", json.dumps(handles, ensure_ascii=False)[:1500])
    (pathlib.Path(work) / "batch-result.json").write_text(
        json.dumps(write, ensure_ascii=False, indent=2)[:40000], encoding="utf-8")

    try:
        reply = g.call("dwg_job_cancel", {"context_id": g.context, "request_id": "batch-co"})
        print("[F] cancel completed job:", json.dumps(reply, ensure_ascii=False)[:400])
    except Exception as ex:
        print("[F] exception:", type(ex).__name__, str(ex)[:200])
