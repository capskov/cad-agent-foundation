"""RC3 follow-up: traversal guard at both layers + cancel reply shape.

Usage: python rc3_fixup.py <workspace>
"""
import importlib.util, json, pathlib, sys

work = pathlib.Path(sys.argv[1])
client_path = sys.argv[2] if len(sys.argv) > 2 else r"F:\AI\cad-agent-foundation-acad2022\src\cad-agent-foundation-main\artifacts\packages\20261115-140318\client\foundation.py"
spec = importlib.util.spec_from_file_location("foundation", client_path)
F = importlib.util.module_from_spec(spec)
spec.loader.exec_module(F)

with F.Gateway(work) as g:
    print("[ctx]", g.context)

    # E1: client must refuse traversal before it ever resolves the path.
    try:
        g.persist(r"..\..\escape2.dwg")
        print("[E1] UNEXPECTED: traversal accepted")
    except Exception as ex:
        print("[E1] client refused:", type(ex).__name__, str(ex)[:160])

    # E2: bypass the client; the server must refuse the same payload itself.
    payload = json.dumps({"output_path": str(pathlib.Path(work).parent.parent / "escape2.dwg"),
                          "confirm": True, "overwrite_existing": True, "allow_repo_output": False},
                         ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    try:
        reply = g.call("dwg_foundation_save_drawing",
                       {"context_id": g.context, "request_id": "traversal-01", "request_json": payload})
        print("[E2] server reply:", json.dumps({k: reply.get(k) for k in ("status", "registered", "effects", "error")},
                                               ensure_ascii=False)[:400])
    except Exception as ex:
        print("[E2] server refused:", type(ex).__name__, str(ex)[:200])

    # F: exact cancel reply for an already completed request.
    for rid in ("matrix-a", "traversal-01"):
        try:
            reply = g.call("dwg_job_cancel", {"context_id": g.context, "request_id": rid})
            print(f"[F:{rid}]", json.dumps(reply, ensure_ascii=False)[:500])
        except Exception as ex:
            print(f"[F:{rid}] exception:", type(ex).__name__, str(ex)[:200])
