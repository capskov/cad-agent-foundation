"""ACAD2022 automated smoke regression.

One owned AutoCAD 2022 session, one packaged client, graded checks:
discovery -> read-only -> write/verify -> fence conflict -> save/finalize ->
idempotent requery -> callout batch -> reopen verification.

Usage:
  python run_regression.py --package <package dir> --workspace <dir> [--cad <acad root>]
Exit code 0 only when every automated check passes. Manual drawing-inspection
checks are listed in the report as not automated.
"""
import argparse, importlib.util, json, pathlib, subprocess, sys, time

MANUAL = [
    "图形观感（图层/颜色/文字位置）需人工看图",
    "PDF/CTB 出图流程未纳入自动回归",
]


def load_client(package):
    path = pathlib.Path(package) / "client" / "foundation.py"
    spec = importlib.util.spec_from_file_location("foundation", str(path))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--package", required=True)
    ap.add_argument("--workspace", required=True)
    ap.add_argument("--cad", default=r"F:\Program Files\Autodesk\AutoCAD 2022")
    ap.add_argument("--source", default=r"F:\AI\cad-agent-foundation-acad2022\cad2022-test\rc3-matrix\drawings\matrix-a.dwg")
    args = ap.parse_args()
    work = pathlib.Path(args.workspace)
    client = pathlib.Path(args.package) / "client" / "foundation.py"
    checks = []

    def record(name, ok, detail):
        checks.append({"check": name, "status": "pass" if ok else "fail", "detail": str(detail)[:600]})
        print(("PASS " if ok else "FAIL ") + name + " :: " + str(detail)[:200])

    def cli(*argv):
        return subprocess.run([sys.executable, str(client), *argv], capture_output=True, text=True, encoding="utf-8")

    out = cli("install", "--workspace", str(work), "--acad-year", "2022", "--cad", args.cad)
    record("install", out.returncode == 0, (out.stdout or "").strip()[-160:])
    out = cli("start", "--workspace", str(work), "--acad-year", "2022", "--write", "--allow-save",
              "--cad", args.cad, "--source", args.source, "--headless")
    record("start(discovery)", out.returncode == 0, (out.stdout or "").strip()[-160:])
    F = load_client(args.package)

    plan_line = {"schema": "foundation-drawing-plan-v1", "source_role": "test", "units": "raw_drawing_units",
                 "steps": [{"op": "line", "id": "r1", "layer": "0", "color": 7, "start": [0, 0], "end": [100, 0]},
                           {"op": "circle", "id": "r2", "layer": "0", "color": 7, "center": [50, 60], "radius": 20}]}
    plan_callout = {"schema": "foundation-drawing-plan-v1", "source_role": "test", "units": "raw_drawing_units",
                    "steps": [{"op": "callout", "id": "c1", "layer": "0", "color": 7,
                               "text": "交通安全设施\\P回归标注", "position": [206, 108], "height": 3.5,
                               "width": 70, "vertices": [[50, 80], [140, 112], [200, 112]],
                               "dot_radius": 0.8, "text_color": 7, "gap": 0}]}

    with F.Gateway(work) as g:
        record("context-bound", bool(g.context), g.context)
        layers = g.submit_read("dwg_list_layers", {})
        count = len(((layers.get("data") or {}).get("layers") or []))
        record("read-only.list_layers", bool(layers.get("found")) and count > 0, "layers=%d" % count)

        write = g.apply_with_optional_save(plan_line, "reg-line")
        summary = write.get("outcome_summary") or {}
        # Without --save-to a write is verified in memory but must stay unpersisted:
        # the fence is held on purpose until a save exists.
        record("write.verified-not-persisted",
               summary.get("outcome") == "committed_verification_pending"
               and summary.get("next_action") == "inspect_existing_handles"
               and write.get("persisted") is False
               and str(write.get("next_action") or "").startswith("verified_not_persisted"),
               "%s/%s/%s" % (summary.get("outcome"), summary.get("next_action"), write.get("persisted")))

        blocked = g.apply(plan_line, "reg-line-2")
        record("fence.rejected-second-write",
               blocked.get("status") == "rejected_before_submission",
               "%s :: %s" % (blocked.get("reason"), blocked.get("error")))
        record("fence.no-ghost-job", not g.journal_has("reg-line-2"), g.journal_has("reg-line-2"))

        save_to = work / "drawings" / "reg-line.dwg"
        saved = g.persist(save_to)
        saved_path = ((saved.get("saved") or {}).get("saved_path")) or saved.get("saved_path")
        record("save.persisted", bool(saved_path) and pathlib.Path(saved_path).exists(), saved_path)
        after = g.query("reg-line")
        final = after.get("job") or {}
        released = after.get("write_fence_released")
        if released is None:
            released = final.get("write_fence_released")
        record("save.finalized",
               released is True
               and (after.get("outcome_summary") or {}).get("outcome") == "committed_verified",
               "%s/%s" % (((after.get("outcome_summary") or {}).get("outcome")), released))

        repeat = g.apply(plan_line, "reg-line")
        record("idempotent.requery", (repeat.get("outcome_summary") or {}).get("outcome") == "committed_verified",
               (repeat.get("outcome_summary") or {}).get("outcome"))

        callout = g.apply_with_optional_save(plan_callout, "reg-callout",
                                             save_to=str(work / "drawings" / "reg-callout.dwg"))
        summary = callout.get("outcome_summary") or {}
        raw = (callout.get("job") or {}).get("result_json")
        handles = []
        if isinstance(raw, str):
            try:
                handles = json.loads(raw).get("created_handles") or []
            except Exception:
                handles = []
        record("callout.write+save", summary.get("outcome") == "committed_verified" and callout.get("persisted"),
               "%s handles=%d" % (summary.get("outcome"), len(handles)))

        cancel = g.call("dwg_job_cancel", {"context_id": g.context, "request_id": "reg-callout"})
        record("cancel.already-terminal", cancel.get("disposition") == "already_terminal", cancel.get("disposition"))

        typechecks = []
        if handles:
            props = g.submit_read("dwg_get_entity_properties", {"handles": json.dumps(handles), "include_geometry": False})
            for e in ((props.get("data") or {}).get("entities") or []):
                ent = e.get("entity") or {}
                typechecks.append({"handle": e.get("handle"), "type": ent.get("type"),
                                   "layer": ent.get("layer"), "color": ent.get("color_index")})
        record("callout.four-entities", len(handles) == 4 and len(typechecks) == 4,
               json.dumps(typechecks, ensure_ascii=False)[:400])

    report = {"package": args.package, "workspace": str(work), "generated_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
              "checks": checks, "manual_not_automated": MANUAL,
              "passed": sum(1 for c in checks if c["status"] == "pass"), "failed": sum(1 for c in checks if c["status"] == "fail")}
    (work / "regression-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print("SUMMARY pass=%d fail=%d" % (report["passed"], report["failed"]))
    return 0 if report["failed"] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
