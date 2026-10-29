#!/usr/bin/env python3
"""Local first-release entry. Uses the existing MCP client and durable gateway."""
from __future__ import annotations
import argparse, contextlib, ctypes, datetime as dt, hashlib, importlib.util, json, msvcrt, os
from pathlib import Path
import shutil, sqlite3, subprocess, sys, time, uuid

# Machine-readable CLI/MCP output must not depend on a Windows GBK console.
for stream in (sys.stdout,sys.stderr):
    if hasattr(stream,'reconfigure'):stream.reconfigure(encoding='utf-8')

HOME=Path(__file__).resolve().parent.parent
support=HOME/'support/readonly-entry.py'
if not support.exists(): support=HOME/'scripts/readonly-entry.py'
spec=importlib.util.spec_from_file_location('foundation_mcp_support',support)
E=importlib.util.module_from_spec(spec);sys.modules[spec.name]=E;spec.loader.exec_module(E)

def read(p):
    def unique(pairs):
        result={}
        for key,value in pairs:
            require(key not in result,'Duplicate JSON key: '+key);result[key]=value
        return result
    return json.loads(Path(p).read_text(encoding='utf-8-sig'),object_pairs_hook=unique)
def write(p,value):
    p=Path(p);p.parent.mkdir(parents=True,exist_ok=True)
    tmp=p.with_name(p.name+'.tmp');tmp.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8');os.replace(tmp,p)
def sha(p):
    h=hashlib.sha256()
    with Path(p).open('rb') as f:
        for chunk in iter(lambda:f.read(1024*1024),b''):h.update(chunk)
    return h.hexdigest().upper()
def require(ok,message):
    if not ok: raise RuntimeError(message)
def clean_env(root):
    env=dict(os.environ)
    for key in list(env):
        if key.upper().startswith(('BIMWRIGHT_DWG_','BIMWRIGHT_D1_','BIMWRIGHT_H1_','BIMWRIGHT_FOUNDATION_')): del env[key]
    env['BIMWRIGHT_DWG_ROOT']=str(root)
    return env

def held_fences(session):
    counts={}
    for label,path,table in [('native',Path(session['native_root'])/'Dwg/execution/plugin-execution.sqlite','plugin_write_fences'),('server',Path(session['server_root'])/'Dwg/request-ledger.sqlite','write_fences')]:
        require(path.is_file(),'Missing execution journal; cannot approve stop')
        with sqlite3.connect(path.as_uri()+'?mode=ro',uri=True) as db:counts[label]=db.execute('SELECT COUNT(*) FROM '+table).fetchone()[0]
    return counts

def stop(g):
    s=g.session
    fresh=g.call('dwg_bind_active_document',{})
    gate,issues=E.check_binding_identity(fresh['binding_snapshot'],s['pid'],s['started'],s['drawing']);require(gate=='identity_matched',str(issues))
    info=fresh['document_info'];require(info.get('loaded_plugin_mvid')==s['installation']['plugin_mvid'],'Loaded plugin changed before stop')
    counts=held_fences(s)
    require(not any(counts.values()),'Unresolved write fence: '+str(counts))
    require(info.get('document_count')==1 and info.get('dbmod')==0 and info.get('command_active')==0,'Stop requires one idle, saved owned drawing. Save it first.')
    handle=info.get('main_window_handle');require(isinstance(handle,int) and handle>0,'Owned CAD window handle unavailable')
    user=ctypes.WinDLL('user32',use_last_error=True);owner=ctypes.c_uint32()
    user.GetWindowThreadProcessId(ctypes.c_void_p(handle),ctypes.byref(owner));require(owner.value==s['pid'],'CAD window owner differs')
    require(process_birth(s['pid'])==s['started'],'CAD process identity changed')
    require(user.PostMessageW(ctypes.c_void_p(handle),0x0010,0,0),'Native close request failed')
    deadline=time.monotonic()+30
    while time.monotonic()<deadline:
        try:process_birth(s['pid'])
        except RuntimeError:
            s['closed_at_utc']=dt.datetime.now(dt.timezone.utc).isoformat();write(g.work/'closed-session.json',s)
            # Session file is retained as ownership history; reuse is explicit in a new workspace.
            return {'stopped':True,'pid':s['pid'],'drawing':s['drawing'],'fences':counts}
        time.sleep(.25)
    return {'stopped':False,'close_requested':True,'pid':s['pid'],'next_action':'inspect_owned_CAD_window; no force kill'}

@contextlib.contextmanager
def lease(work):
    work.mkdir(parents=True,exist_ok=True)
    with (work/'entry.lock').open('a+b') as f:
        f.seek(0);f.write(b'1');f.flush();f.seek(0)
        try: msvcrt.locking(f.fileno(),msvcrt.LK_NBLCK,1)
        except OSError: raise RuntimeError('This workspace already has a CLI or MCP session. Close it first.')
        try: yield
        finally: f.seek(0);msvcrt.locking(f.fileno(),msvcrt.LK_UNLCK,1)

def installation():
    require((HOME/'manifest.json').exists(),'Run the packaged entry; this development source needs a release manifest.')
    manifest=read(HOME/'manifest.json')
    for name,digest in manifest['files'].items():
        p=HOME/name;require(p.is_file() and sha(p)==digest,'Package file changed: '+name)
    if manifest.get('signing'):
        validator=HOME/'support/verify-runtime-signatures.ps1'
        powershell=shutil.which('pwsh.exe') or shutil.which('powershell.exe')
        require(validator.is_file() and powershell,'Signed package signature verifier unavailable')
        result=subprocess.run([powershell,'-NoProfile','-NonInteractive','-File',str(validator),'-RuntimeRoot',str(HOME)],capture_output=True,text=True,encoding='utf-8-sig')
        require(result.returncode==0,'Package publisher signature validation failed: '+result.stderr[-1200:])
        checked=json.loads(result.stdout);require(checked.get('passed') is True and checked.get('thumbprint')==manifest['signing']['thumbprint'],'Package signing identity differs')
    return manifest

def install(args):
    manifest=installation();cad=Path(args.cad).resolve()
    require((cad/'acad.exe').is_file(),'AutoCAD 2026 install missing')
    target=cad/'Plugins'/('CadFoundation2026-v1-'+manifest['plugin_bundle_sha256'][:12].lower())
    target.mkdir(parents=True,exist_ok=True)
    for source in (HOME/'plugin').rglob('*'):
        if source.is_file():
            dest=target/source.relative_to(HOME/'plugin');dest.parent.mkdir(parents=True,exist_ok=True)
            if dest.exists(): require(sha(dest)==sha(source),'Versioned installation differs; preserve it and use a fresh package.')
            else: shutil.copy2(source,dest)
    record={'cad':str(cad),'plugin':str(target/'Bimwright.Dwg.Plugin.Acad26.dll'),'plugin_mvid':manifest['plugin_mvid'],'plugin_sha256':manifest['plugin_sha256'],'package':str(HOME)}
    write(args.workspace/'installation.json',record);return record

def start(args):
    work=args.workspace;record=read(work/'installation.json');manifest=installation()
    require(record['plugin_sha256']==manifest['plugin_sha256'],'Install this package version first')
    require(sha(record['plugin'])==record['plugin_sha256'],'Installed plugin differs')
    require(not (work/'session.json').exists(),'Existing session preserved. Use doctor/status/stop; a new session needs a new workspace.')
    source=Path(args.source).resolve();require(source.is_file() and source.suffix.lower()=='.dwg','Existing source DWG required')
    drawing=work/'drawings'/'work.dwg';drawing.parent.mkdir(parents=True,exist_ok=True)
    require(not drawing.exists(),'Working drawing already exists; refusing overwrite')
    source_hash=sha(source);shutil.copy2(source,drawing);require(sha(drawing)==source_hash,'Copy differs')
    native=work/'native';server=work/'server';(server/'Dwg').mkdir(parents=True,exist_ok=True);(server/'Fonts').mkdir(exist_ok=True)
    script=work/'start.scr'
    # Trusted versioned directory under the AutoCAD install; keep SECURELOAD enabled.
    commands=['(setvar "PROXYSHOW" 1)','(setvar "PROXYNOTICE" 0)','(command "_NETLOAD" "'+record['plugin'].replace('\\','/')+'")','MCPSTART','']
    script.write_text('\n'.join(commands),encoding='ascii')
    env=clean_env(native);env['BIMWRIGHT_FOUNDATION_WRITE_ENABLED']='1' if args.write else '0'
    startup=subprocess.STARTUPINFO();startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW;startup.wShowWindow=0 if args.headless else 5
    proc=subprocess.Popen([str(Path(record['cad'])/'acad.exe'),str(drawing),'/b',str(script),'/nologo'],env=env,startupinfo=startup)
    birth=process_birth(proc.pid)
    session={'schema':'foundation-local-session-v1','session_id':uuid.uuid4().hex,'pid':proc.pid,'started':birth,'drawing':str(drawing),'source':str(source),'source_sha256':source_hash,'native_root':str(native),'server_root':str(server),'write_enabled':args.write,'installation':record,'created_at_utc':dt.datetime.now(dt.timezone.utc).isoformat()}
    write(work/'session.json',session)
    discovery=native/'Dwg/acad-2026.json';deadline=time.monotonic()+90
    while not discovery.exists():
        require(proc.poll() is None,'Owned CAD exited before plugin discovery; session preserved')
        require(time.monotonic()<deadline,'Plugin startup not confirmed; inspect session, do not start another CAD in this workspace')
        time.sleep(.25)
    shutil.copy2(discovery,server/'Dwg/acad-2026.json')
    session['startup']='discovered';write(work/'session.json',session)
    write(work/'mcp-config.json',{'mcpServers':{'cad-foundation':{'command':sys.executable,'args':[str(Path(__file__).resolve()),'mcp','--workspace',str(work)]}}})
    return session

def process_birth(pid):
    # Native process identity, independent of active COM document selection.
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.OpenProcess.restype=ctypes.c_void_p
    handle=kernel.OpenProcess(0x1000,False,pid);require(bool(handle),'CAD process missing or inaccessible')
    try:
        exit_code=ctypes.c_uint32()
        require(kernel.GetExitCodeProcess(ctypes.c_void_p(handle),ctypes.byref(exit_code)) and exit_code.value==259,'Owned CAD process has exited')
        times=[ctypes.c_uint64() for _ in range(4)]
        require(kernel.GetProcessTimes(ctypes.c_void_p(handle),*[ctypes.byref(t) for t in times]),'Cannot identify CAD process birth')
        ticks=times[0].value;seconds,fraction=divmod(ticks,10_000_000)
        base=dt.datetime(1601,1,1,tzinfo=dt.timezone.utc)+dt.timedelta(seconds=seconds)
        return base.strftime('%Y-%m-%dT%H:%M:%S')+'.'+str(fraction).zfill(7)+'Z'
    finally:kernel.CloseHandle(ctypes.c_void_p(handle))

def sync_owned_discovery(work,session):
    source=Path(session['native_root'])/'Dwg/acad-2026.json'
    destination=Path(session['server_root'])/'Dwg/acad-2026.json'
    if destination.exists(): return
    require(source.is_file(),'Owned CAD discovery is not ready; inspect its startup window, do not start another session')
    discovery=read(source)
    require(discovery.get('pid')==session['pid'] and discovery.get('acad_year')==2026 and discovery.get('process_name')=='acad','Delayed native discovery identity differs')
    require(dt.datetime.fromisoformat(discovery['started_at_utc'].replace('Z','+00:00'))>=dt.datetime.fromisoformat(session['started'].replace('Z','+00:00')),'Delayed discovery predates owned CAD process')
    destination.parent.mkdir(parents=True,exist_ok=True)
    shutil.copy2(source,destination)
    write(Path(work)/'startup-discovery-sync.json',{'schema':'foundation-delayed-startup-sync-v1','session_id':session['session_id'],'pid':session['pid'],'copied_matching_native_discovery':True,'native_journal_unchanged':True,'binding_identity_still_required':True})

class Gateway:
    def __init__(self,work):
        manifest=installation();self.work=work;self.session=read(work/'session.json');s=self.session
        require(process_birth(s['pid'])==s['started'],'Owned CAD process identity changed')
        require(sha(s['installation']['plugin'])==s['installation']['plugin_sha256'],'Installed plugin changed')
        require(manifest['plugin_mvid']==s['installation']['plugin_mvid'],'This entry package does not match the installed native version')
        installed=Path(s['installation']['plugin']).parent
        for name,digest in manifest['files'].items():
            if name.startswith('plugin/'):
                destination=installed/name[len('plugin/'):];require(destination.is_file() and sha(destination)==digest,'Installed dependency differs: '+name)
        sync_owned_discovery(work,s)
        native=Path(s['native_root']);root=Path(s['server_root'])
        env=clean_env(root);command=E.build_server_command(HOME/'server/Bimwright.Dwg.Server.exe')
        if s['write_enabled']:
            with sqlite3.connect((native/'Dwg/execution/plugin-execution.sqlite').as_uri()+'?mode=ro',uri=True) as db:
                scope,digest=db.execute('select scope_id,path_digest from plugin_journal_identity where singleton=1').fetchone()
            env.update(BIMWRIGHT_DWG_ENABLE_FOUNDATION_WRITE='true',BIMWRIGHT_DWG_FOUNDATION_EXPECTED_DRAWING_PATH=s['drawing'],BIMWRIGHT_DWG_FOUNDATION_EXPECTED_JOURNAL_SCOPE_ID=scope,BIMWRIGHT_DWG_FOUNDATION_EXPECTED_JOURNAL_PATH_DIGEST=digest)
            env['BIMWRIGHT_DWG_FOUNDATION_JOURNAL_EVIDENCE_PATH']=str(native/'Dwg/execution/plugin-execution.sqlite')
            command=[x for x in command if x!='--read-only']+['--enable-foundation-write','--disable-h1-write-pilot','--disable-h1-preflight-pilot']
        self.command,self.env=command,env;self.client=None;self.log=None
    def __enter__(self):
        self.log=(self.work/'server.log').open('ab')
        self.proc=subprocess.Popen(self.command,cwd=HOME/'server',env=self.env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=self.log,text=True,encoding='utf-8',bufsize=1)
        self.client=E.McpStdioClient(self.proc)
        try:
            self.client.initialize(30);names={t['name'] for t in self.client.tools_list(30)['tools']}
            expected=set(E.CATALOG_ALLOWLIST)
            if self.session['write_enabled']:expected.remove('dwg_inspect_lisp');expected.add('dwg_apply_plan');expected.add('dwg_view')
            require(names==expected,'Unexpected tool catalog')
            self.bound=self.call('dwg_bind_active_document',{})
            gate,issues=E.check_binding_identity(self.bound['binding_snapshot'],self.session['pid'],self.session['started'],self.session['drawing']);require(gate=='identity_matched',str(issues))
            info=self.bound.get('document_info',{});require(info.get('loaded_plugin_mvid')==self.session['installation']['plugin_mvid'],'Loaded plugin version unknown or differs')
            self.context=self.bound['context_id'];return self
        except BaseException:self.__exit__(None,None,None);raise
    def __exit__(self,*exc):
        if self.client:self.client.close()
        if self.log:self.log.close()
    def call(self,name,arguments):return E.unwrap_application_result(E.parse_mcp_tool_result(self.client.rpc('tools/call',{'name':name,'arguments':arguments},30)))
    def query(self,request,wait=60):
        deadline=time.monotonic()+wait
        while True:
            try:
                reply=self.call('dwg_job_get',{'context_id':request['context_id'],'request_id':request['request_id'],'wait_ms':0})
            except Exception as ex:
                # A transient/inconsistent read is not evidence that the write failed.
                # Reconcile only this original identity; never re-enter apply/submit.
                request['last_query_error']=str(ex);request['query_failures']=request.get('query_failures',0)+1
                write(self.work/'requests'/(request['request_id']+'.json'),request)
                if time.monotonic()>=deadline:
                    return {'request_id':request['request_id'],'context_id':request['context_id'],'status':'unknown','pending':True,'effects':'unknown','verification':'unknown','write_fence_released':False,'query_error':str(ex),'next_action':'query_original_request; do_not_replay_or_force_unlock'}
                time.sleep(min(2,.25*request['query_failures']));continue
            request['last_reply']=reply;write(self.work/'requests'/(''+request['request_id']+'.json'),request)
            if reply.get('pending') is False or (reply.get('found') is True and reply.get('job',{}).get('phase') in ('completed','failed','cancelled','rejected','unknown')) or time.monotonic()>=deadline:return reply
            time.sleep(.2)
    def submit_read(self,name,args):
        request={'request_id':'read-'+uuid.uuid4().hex,'context_id':self.context}
        reply=self.call(name,dict(args,**request))
        if reply.get('status')=='rejected_before_submission' and reply.get('registered') is False and reply.get('effects')=='none' and reply.get('request_id')==request['request_id'] and reply.get('context_id')==self.context:
            request['terminal_rejection']=reply;write(self.work/'requests'/(request['request_id']+'.json'),request);return reply
        result=self.query(request)
        raw=result.get('job',{}).get('result_json')
        if isinstance(raw,str):result['data']=json.loads(raw)
        return result
    def apply(self,plan,request_id=None):
        require(self.session['write_enabled'],'This session is read-only')
        canonical=json.dumps(plan,ensure_ascii=False,sort_keys=True,separators=(',',':'));digest=hashlib.sha256(canonical.encode('utf-8')).hexdigest()
        request_id=request_id or 'plan-'+digest[:32]
        require(all(c.isalnum() or c in '-_' for c in request_id) and 1<=len(request_id)<=80,'Invalid request ID')
        path=self.work/'requests'/(request_id+'.json')
        if path.exists():
            previous=read(path);require(previous.get('plan_digest')==digest,'Request ID already belongs to different parameters')
            return previous['terminal_rejection'] if 'terminal_rejection' in previous else self.query(previous)
        request={'request_id':request_id,'context_id':self.context,'plan_digest':digest,'plan':plan,'submit_attempts':1}
        write(path,request) # Before transport: uncertain submission is never automatically retried.
        try:request['submit_reply']=self.call('dwg_apply_plan',{'context_id':self.context,'request_id':request_id,'plan_json':canonical})
        except Exception as ex:
            if isinstance(ex,E.RemoteRpcError) and ex.code == -32602:
                reply={'status':'rejected_before_submission','pending':False,'effects':'none','registered':False,'request_id':request_id,'context_id':self.context,'error':str(ex)}
                request['terminal_rejection']=reply;write(path,request);return reply
            request['submission']='unknown';request['submit_error']=str(ex);write(path,request)
            # Like the proven read entry: a refused or lost reply is reconciled only
            # through the same request, never by a second submission.
            return self.query(request)
        reply=request['submit_reply']
        if reply.get('status')=='rejected_before_submission' and reply.get('registered') is False and reply.get('effects')=='none' and reply.get('request_id')==request_id and reply.get('context_id')==self.context:
            request['terminal_rejection']=reply;write(path,request);return reply
        write(path,request);return self.query(request)

def main():
    p=argparse.ArgumentParser(description='CAD foundation first release; all drawings are owned copies')
    p.add_argument('action',choices=['install','start','doctor','read','query','apply','save','stop','status','mcp','view','plot-style'])
    p.add_argument('--workspace',type=Path,required=True);p.add_argument('--cad',default=r'C:\Program Files\Autodesk\AutoCAD 2026')
    p.add_argument('--source');p.add_argument('--write',action='store_true');p.add_argument('--headless',action='store_true')
    p.add_argument('--handles');p.add_argument('--plan',type=Path);p.add_argument('--request-id');p.add_argument('--arguments',type=Path)
    args=p.parse_args();args.workspace=args.workspace.resolve()
    with lease(args.workspace):
        if args.action=='plot-style':
            import plot_style
            result=plot_style.run(args.workspace,read(args.arguments))
        elif args.action=='install':result=install(args)
        elif args.action=='start':result=start(args)
        elif args.action=='mcp':
            with Gateway(args.workspace) as g:command,env=g.command,g.env
            proc=subprocess.Popen(command,cwd=HOME/'server',env=env)
            try:return proc.wait()
            finally:
                if proc.poll() is None:proc.terminate();proc.wait(timeout=10)
        else:
            with Gateway(args.workspace) as g:
                if args.action=='doctor':result=g.bound
                elif args.action=='read':result=g.submit_read('dwg_get_entity_properties',{'handles':json.dumps(E.parse_handles(args.handles)),'include_geometry':True})
                elif args.action=='view':result=g.submit_read('dwg_view',{'request_json':json.dumps(read(args.arguments),ensure_ascii=False)})
                elif args.action=='query':result=g.submit_read('dwg_query_entities',read(args.arguments))
                elif args.action=='apply':result=g.apply(read(args.plan),args.request_id)
                elif args.action=='save':
                    rows=g.submit_read('dwg_get_entity_properties',{'handles':json.dumps(E.parse_handles(args.handles)),'include_geometry':True}).get('data',{}).get('entities',[])
                    handles=E.parse_handles(args.handles);require(len(rows)==len(handles) and {r.get('handle') for r in rows}==set(handles) and all(r.get('ok') is True and isinstance(r.get('entity_digest'),str) for r in rows),'Fresh expected objects not all readable')
                    plan={'schema':'foundation-drawing-plan-v1','source_role':'annotation','units':'raw_drawing_units','steps':[{'id':'save','op':'save','expected_layouts':g.bound['document_info']['foundation_catalog']['layout_digests'],'expected':[{'handle':r['handle'],'expected_sha256':r['entity_digest']} for r in rows]}]}
                    result=g.apply(plan,args.request_id)
                elif args.action=='stop':result=stop(g)
                else:result=g.query(read(args.workspace/'requests'/(args.request_id+'.json')))
        print(json.dumps(E.redact(result),ensure_ascii=False,indent=2));return 0
if __name__=='__main__':
    try:sys.exit(main())
    except Exception as ex:print(json.dumps({'error':str(ex)},ensure_ascii=False),file=sys.stderr);sys.exit(1)
