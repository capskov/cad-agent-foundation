import json, pathlib, subprocess, sys

HERE = pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\cad2022-test")
CLIENT_DIR = pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\src\cad-agent-foundation-main\artifacts\packages\20261116-fix13\client")
sys.path.insert(0, str(CLIENT_DIR))
import foundation as F

PY = sys.executable
CLIENT = CLIENT_DIR / "foundation.py"
CAD = r"F:\Program Files\Autodesk\AutoCAD 2022"
WS = HERE / "v11-wallAP"
SRC = HERE / "v11-wallAO" / "s11-final2.dwg"
OUT = WS / "s11-page2.dwg"

WS.mkdir(parents=True, exist_ok=True)
install = json.loads((HERE / "v11-wallX" / "installation.json").read_text(encoding="utf-8"))
(WS / "installation.json").write_text(json.dumps(install, ensure_ascii=False, indent=1), encoding="utf-8")

cmd = [PY, str(CLIENT), "--workspace", str(WS), "--acad-year", "2022", "--cad", CAD,
       "start", "--source", str(SRC), "--write", "--allow-save", "--headless"]
p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", timeout=300)
print("start rc", p.returncode, p.stderr[-400:])
if p.returncode != 0:
    raise SystemExit("start failed")

with F.Gateway(WS) as g:
    digest = g.bound["document_info"]["foundation_catalog"]["layout_digests"]["Model"]
    print("digest", digest)
    plan = {"schema": "foundation-drawing-plan-v1", "source_role": "annotation",
            "units": "metres", "steps": [{
                "id": "setup-model", "op": "page_setup", "layout": "Model",
                "expected_sha256": digest, "device": "DWG To PDF.pc3",
                "media": "ISO_full_bleed_A3_(420.00_x_297.00_MM)",
                "plot_style": "monochrome.ctb", "rotation": 0,
                "plot_area": "extents", "scale_numerator": 1,
                "scale_denominator": 0.02, "center": True}]}
    res = g.apply_with_optional_save(plan, None, OUT, overwrite=False)
    print("page", json.dumps({k: res.get(k) for k in ("status", "verification", "persisted", "save_reply", "error", "next_action") if k in res}, ensure_ascii=False))
print("s11-page2", OUT.exists(), OUT.stat().st_size if OUT.exists() else 0)
