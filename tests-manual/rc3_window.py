"""RC3 probe: crash in the window between a completed save and finalize.

stage crash : apply plan (write) -> persist (save job) -> hard-kill the owned CAD
              before the server can finalize. Nothing else is written.
stage after : relaunch CAD on the same work drawing / native root, then query the
              original request by its original context id and report the real state.

Usage:
  python rc3_window.py crash <workspace> <plan.json> <request_id> <save_to.dwg>
  python rc3_window.py after <workspace> <request_id>
"""
import json, os, pathlib, subprocess, sys, time, ctypes

ROOT = pathlib.Path(os.environ.get("RC3_PKG") or r"F:\AI\cad-agent-foundation-acad2022\src\cad-agent-foundation-main")
sys.path.insert(0, str(ROOT / "client"))
import foundation as F  # noqa: E402


def load_session(work):
    return json.loads((work / "session.json").read_text(encoding="utf-8"))


def kill_owned_cad(session):
    pid = session["pid"]
    out = subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True, text=True)
    print("[kill]", pid, out.stdout.strip() or out.stderr.strip())
    time.sleep(2)


def relaunch(work, session):
    drawing = pathlib.Path(session["drawing"])
    script = work / "start.scr"
    native = pathlib.Path(session["native_root"])
    env = F.clean_env(native)
    env["BIMWRIGHT_FOUNDATION_WRITE_ENABLED"] = "1" if session.get("write_enabled") else "0"
    if session.get("save_enabled"):
        env["BIMWRIGHT_FOUNDATION_SAVE_ENABLED"] = "1"
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = 0
    cad = pathlib.Path(session["installation"]["cad"]) / "acad.exe"
    proc = subprocess.Popen([str(cad), str(drawing), "/b", str(script), "/nologo"], env=env, startupinfo=startup)
    birth = F.process_birth(proc.pid)
    year = int(session.get("acad_year") or F.DEFAULT_ACAD_YEAR)
    deadline = time.monotonic() + 120
    found = None
    while found is None:
        if proc.poll() is not None:
            raise RuntimeError("relaunched CAD exited before discovery")
        if time.monotonic() > deadline:
            raise RuntimeError("relaunch discovery timeout")
        candidate = F.locate_discovery(native, year)
        if candidate is not None and candidate[1].get("pid") == proc.pid:
            found = candidate
        else:
            time.sleep(0.25)
    disc, data = found
    assert data.get("pid") == proc.pid, (data.get("pid"), proc.pid)
    session["pid"] = proc.pid
    session["started"] = birth
    session["startup"] = "relaunched"
    F.write(work / "session.json", session)
    # The server reads its own copy of the discovery file; refresh it for the new process.
    import shutil
    dest = pathlib.Path(session["server_root"]) / "Dwg" / disc.name
    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.exists():
        dest.unlink()
    shutil.copy2(disc, dest)
    print("[relaunch] pid=", proc.pid, "discovery=", disc.name, "server-copy refreshed")
    return data


def stage_crash(work, plan_path, rid, save_to):
    plan = json.loads(pathlib.Path(plan_path).read_text(encoding="utf-8"))
    session = load_session(work)
    with F.Gateway(work) as g:
        reply = g.apply(plan, rid)
        summary = reply.get("outcome_summary") or {}
        print("[write]", json.dumps({"status": reply.get("status"), "outcome": summary.get("outcome"),
                                     "fence": reply.get("write_fence_released")}, ensure_ascii=False))
        saved = g.persist(save_to)
        saved_json = saved.get("saved") if isinstance(saved.get("saved"), dict) else {}
        print("[save]", json.dumps({"phase": saved.get("job", {}).get("phase"),
                                    "saved_path": saved_json.get("saved_path") if saved_json else None},
                                   ensure_ascii=False))
        # No second query: finalize is deliberately never attempted here.
        print("[window] crash now, before finalize")
    kill_owned_cad(session)


def stage_after(work, rid):
    session = load_session(work)
    ctx = json.loads((work / "requests" / (rid + ".json")).read_text(encoding="utf-8"))["context_id"]
    relaunch(work, session)
    with F.Gateway(work) as g:
        print("[new-context]", g.context, "original-context", ctx)
        try:
            raw = g.client.rpc("tools/call", {"name": "dwg_job_get",
                             "arguments": {"context_id": ctx, "request_id": rid, "wait_ms": 0}}, 120)
            reply = F.E.parse_mcp_tool_result(raw)
            print("[query]", json.dumps(F.E.redact(reply), ensure_ascii=False)[:1500])
        except Exception as ex:
            print("[query-error]", str(ex))
            reply = None
        if reply is None or reply.get("found") is False:
            try:
                resolved = g.call("dwg_job_get", {"context_id": g.context, "request_id": rid, "wait_ms": 0})
                print("[query-new-context]", json.dumps(F.E.redact(resolved), ensure_ascii=False)[:1200])
            except Exception as ex2:
                print("[query-new-context-error]", str(ex2))


if __name__ == "__main__":
    mode = sys.argv[1]
    work = pathlib.Path(sys.argv[2])
    if mode == "crash":
        stage_crash(work, sys.argv[3], sys.argv[4], sys.argv[5])
    else:
        stage_after(work, sys.argv[3])
