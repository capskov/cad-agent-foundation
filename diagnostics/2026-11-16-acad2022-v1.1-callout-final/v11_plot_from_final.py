import json, pathlib, subprocess, sys

HERE = pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\cad2022-test")
CLIENT_DIR = pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\src\cad-agent-foundation-main\artifacts\packages\20261116-fix13\client")
sys.path.insert(0, str(CLIENT_DIR))
import foundation as F

PY = sys.executable
CLIENT = CLIENT_DIR / "foundation.py"
CAD = r"F:\Program Files\Autodesk\AutoCAD 2022"
WS = HERE / "v11-wallAN"
SRC = HERE / "v11-wallAM" / "s11-page.dwg"

WS.mkdir(parents=True, exist_ok=True)
install = json.loads((HERE / "v11-wallX" / "installation.json").read_text(encoding="utf-8"))
(WS / "installation.json").write_text(json.dumps(install, ensure_ascii=False, indent=1), encoding="utf-8")

cmd = [PY, str(CLIENT), "--workspace", str(WS), "--acad-year", "2022", "--cad", CAD,
       "start", "--source", str(SRC), "--write", "--headless"]
p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", timeout=300)
print("start rc", p.returncode, p.stderr[-400:])
if p.returncode != 0:
    raise SystemExit("start failed")

with F.Gateway(WS) as g:
    digests = g.bound["document_info"]["foundation_catalog"]["layout_digests"]
    print("digests", json.dumps(digests, ensure_ascii=False))
    plan = {"schema": "foundation-drawing-plan-v1", "source_role": "annotation",
            "units": "raw_drawing_units", "steps": [{
                "id": "pdf-a3", "op": "plot_pdf", "layout": "Model",
                "expected_sha256": digests["Model"], "file_name": "wall-section.pdf"}]}
    (WS / "plan-plot.json").write_text(json.dumps(plan, ensure_ascii=False, indent=1), encoding="utf-8")
    res = g.apply(plan, None)
    print("plot", json.dumps({k: res.get(k) for k in ("request_id", "status", "phase", "effects", "verification", "pending", "error", "next_action", "outcome_summary") if k in res}, ensure_ascii=False))
    print("plot full", json.dumps(res, ensure_ascii=False, default=str)[:1600])

pdf = WS / "drawings" / "outputs" / "wall-section.pdf"
print("pdf", pdf.exists(), pdf.stat().st_size if pdf.exists() else 0)
