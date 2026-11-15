"""RC4 real drawing reopen verification: created handles + untouched original entity."""
import importlib.util, json, pathlib, sqlite3, subprocess, sys

work = pathlib.Path(sys.argv[1])
client = sys.argv[2]
db = work / "native" / "Dwg" / "execution" / "plugin-execution.sqlite"
conn = sqlite3.connect(str(db))
row = conn.execute("select result_json from plugin_jobs where request_id='real-co'").fetchone()
handles = []
if row and row[0]:
    data = json.loads(row[0])
    handles = data.get("created_handles") or []
print("[journal-handles]", handles)

before = None
for rid in ("read-723b93024fe644a1b63109127119650d",):
    r = conn.execute("select result_json from plugin_jobs where request_id=?", (rid,)).fetchone()
    if r and r[0]:
        q = json.loads(r[0])
        ents = q.get("entities") or []
        if ents:
            before = {"handle": ents[0].get("handle"), "digest": ents[0].get("entity_digest")}
print("[before]", before)

verify = work.parent / "rc4-real-verify"
verify.mkdir(parents=True, exist_ok=True)
cli = [sys.executable, client]
out = subprocess.run(cli + ["install", "--workspace", str(verify), "--acad-year", "2022",
                            "--cad", r"F:\Program Files\Autodesk\AutoCAD 2022"],
                     capture_output=True, text=True, encoding="utf-8")
print("[install]", (out.stdout or "").strip()[-160:])
saved = work / "drawings" / "real-callout.dwg"
out = subprocess.run(cli + ["start", "--workspace", str(verify), "--acad-year", "2022",
                            "--cad", r"F:\Program Files\Autodesk\AutoCAD 2022",
                            "--source", str(saved), "--headless"],
                     capture_output=True, text=True, encoding="utf-8")
print("[start]", (out.stdout or "").strip()[-160:])

spec = importlib.util.spec_from_file_location("foundation", client)
F = importlib.util.module_from_spec(spec)
spec.loader.exec_module(F)
target = list(handles) + ([before["handle"]] if before else [])
with F.Gateway(verify) as g:
    print("[ctx]", g.context)
    reply = g.submit_read("dwg_get_entity_properties", {"handles": json.dumps(target), "include_geometry": False})
    entities = ((reply.get("data") or {}).get("entities")) or []
    for e in entities:
        ent = e.get("entity") or {}
        print(json.dumps({"handle": e.get("handle"), "ok": e.get("ok"), "type": ent.get("type"),
                          "layer": ent.get("layer"), "color": ent.get("color_index"),
                          "text": (ent.get("text") or "")[:40], "loc": ent.get("location"),
                          "digest": (e.get("entity_digest") or "")[:24]}, ensure_ascii=False))
