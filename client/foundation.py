#!/usr/bin/env python3
"""Local first-release entry. Uses the existing MCP client and durable gateway."""
from __future__ import annotations
import argparse, contextlib, ctypes, datetime as dt, hashlib, importlib.util, json, msvcrt, ntpath, os
from pathlib import Path, PureWindowsPath
import shutil, sqlite3, subprocess, sys, time, uuid

# Machine-readable CLI/MCP output must not depend on a Windows GBK console.
for stream in (sys.stdout,sys.stderr):
    if hasattr(stream,'reconfigure'):stream.reconfigure(encoding='utf-8')

HOME=Path(__file__).resolve().parent.parent
DEFAULT_ACAD_YEAR=2026

def discovery_name(year): return 'acad-'+str(int(year))+'.json'
def plugin_dll_name(year): return 'Bimwright.Dwg.Plugin.Acad'+str(int(year)-2000)+'.dll'
def default_cad_root(year): return r'C:\Program Files\Autodesk\AutoCAD '+str(int(year))

def locate_discovery(root,year):
    '''Return (path, data) for the discovery file of the requested AutoCAD year, or None.

    The plugin names the file after its own version, and older helpers guessed it
    by name. Scan every acad-*.json and match on acad_year instead, so a 2022
    plugin never needs a 2026-shaped filename.'''
    directory=Path(root)/'Dwg'
    exact=directory/discovery_name(year)
    if exact.is_file():
        try: return exact,read(exact)
        except Exception: pass
    for candidate in sorted(directory.glob('acad-*.json')):
        try: data=read(candidate)
        except Exception: continue
        if data.get('acad_year')==int(year): return candidate,data
    return None

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
    manifest=installation();cad=Path(args.cad).resolve();year=int(args.acad_year)
    require((cad/'acad.exe').is_file(),'AutoCAD '+str(year)+' install missing')
    dll=plugin_dll_name(year)
    target=cad/'Plugins'/('CadFoundation'+str(year)+'-v1-'+manifest['plugin_bundle_sha256'][:12].lower())
    target.mkdir(parents=True,exist_ok=True)
    for source in (HOME/'plugin').rglob('*'):
        if source.is_file():
            dest=target/source.relative_to(HOME/'plugin');dest.parent.mkdir(parents=True,exist_ok=True)
            if dest.exists(): require(sha(dest)==sha(source),'Versioned installation differs; preserve it and use a fresh package.')
            else: shutil.copy2(source,dest)
    plugin_sha=manifest.get('files',{}).get('plugin/'+dll) or manifest.get('plugin_sha256')
    record={'cad':str(cad),'acad_year':year,'plugin':str(target/dll),'plugin_mvid':manifest['plugin_mvid'],'plugin_sha256':plugin_sha,'package':str(HOME)}
    write(args.workspace/'installation.json',record);return record

def start(args):
    work=args.workspace;record=read(work/'installation.json');manifest=installation()
    require(record['plugin_sha256']==manifest['plugin_sha256'],'Install this package version first')
    require(sha(record['plugin'])==record['plugin_sha256'],'Installed plugin differs')
    session_path=work/'session.json';closed=work/'closed-session.json'
    reopen=bool(getattr(args,'reopen',False))
    if session_path.exists():
        require(reopen,'Existing session preserved. Use doctor/status/stop; a new session needs a new workspace.')
        prior=read(session_path)
        require(closed.exists() and read(closed).get('pid')==prior.get('pid'),'Reopen requires a session that was explicitly stopped (closed-session.json)')
        try:
            process_birth(prior['pid']);raise RuntimeError('Owned CAD process is still running; close it before reopening')
        except RuntimeError as ex:
            require('still running' not in str(ex),str(ex))
        held=held_fences(prior);require(not any(held.values()),'Reopen refused: unresolved write fence '+str(held))
        drawing=Path(prior['drawing']);require(drawing.is_file(),'Owned working drawing is missing; reopen needs the file that session wrote')
        source=Path(prior['source']);source_hash=prior['source_sha256']
        history=work/'sessions';history.mkdir(exist_ok=True)
        shutil.move(str(session_path),str(history/(prior.get('session_id','session')+'.json')))
    else:
        require(not reopen,'Reopen needs an existing session')
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
    # AutoCAD 2022 has no upstream plugin-side save. Persisting to disk is an explicit
    # opt-in on that host only: it enables the save job AND the finalize-before-save guard.
    save_enabled=bool(getattr(args,'allow_save',False)) and int(record.get('acad_year') or args.acad_year)==2022
    require(not save_enabled or args.write,'Save opt-in requires a write session')
    if save_enabled: env['BIMWRIGHT_FOUNDATION_SAVE_ENABLED']='1'
    startup=subprocess.STARTUPINFO();startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW;startup.wShowWindow=0 if args.headless else 5
    proc=subprocess.Popen([str(Path(record['cad'])/'acad.exe'),str(drawing),'/b',str(script),'/nologo'],env=env,startupinfo=startup)
    birth=process_birth(proc.pid)
    year=int(record.get('acad_year') or args.acad_year)
    session={'schema':'foundation-local-session-v1','session_id':uuid.uuid4().hex,'pid':proc.pid,'started':birth,'drawing':str(drawing),'source':str(source),'source_sha256':source_hash,'native_root':str(native),'server_root':str(server),'write_enabled':args.write,'save_enabled':save_enabled,'acad_year':year,'installation':record,'created_at_utc':dt.datetime.now(dt.timezone.utc).isoformat()}
    write(work/'session.json',session)
    deadline=time.monotonic()+90
    found=None
    while found is None:
        require(proc.poll() is None,'Owned CAD exited before plugin discovery; session preserved')
        require(time.monotonic()<deadline,'Plugin startup not confirmed; inspect session, do not start another CAD in this workspace')
        found=locate_discovery(native,year)
        if found is None: time.sleep(.25)
    discovery,discovery_data=found
    require(discovery_data.get('pid')==proc.pid,'Native discovery belongs to a different CAD process')
    shutil.copy2(discovery,server/'Dwg'/discovery_name(year))
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
    year=int(session.get('acad_year') or DEFAULT_ACAD_YEAR)
    source=Path(session['native_root'])/'Dwg'/discovery_name(year)
    destination=Path(session['server_root'])/'Dwg'/discovery_name(year)
    if destination.exists(): return
    require(source.is_file(),'Owned CAD discovery is not ready; inspect its startup window, do not start another session')
    discovery=read(source)
    require(discovery.get('pid')==session['pid'] and discovery.get('acad_year')==year and discovery.get('process_name')=='acad','Delayed native discovery identity differs')
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
        env=clean_env(root);command=E.build_server_command(HOME/'server/Bimwright.Dwg.Server.exe',root)
        if s['write_enabled']:
            with sqlite3.connect((native/'Dwg/execution/plugin-execution.sqlite').as_uri()+'?mode=ro',uri=True) as db:
                scope,digest=db.execute('select scope_id,path_digest from plugin_journal_identity where singleton=1').fetchone()
            env.update(BIMWRIGHT_DWG_ENABLE_FOUNDATION_WRITE='true',BIMWRIGHT_DWG_FOUNDATION_EXPECTED_DRAWING_PATH=s['drawing'],BIMWRIGHT_DWG_FOUNDATION_EXPECTED_JOURNAL_SCOPE_ID=scope,BIMWRIGHT_DWG_FOUNDATION_EXPECTED_JOURNAL_PATH_DIGEST=digest)
            env['BIMWRIGHT_DWG_FOUNDATION_JOURNAL_EVIDENCE_PATH']=str(native/'Dwg/execution/plugin-execution.sqlite')
            command=[x for x in command if x!='--read-only']+['--enable-foundation-write','--disable-h1-write-pilot','--disable-h1-preflight-pilot']
            if s.get('save_enabled'):
                env['BIMWRIGHT_DWG_FOUNDATION_SAVE_TOOL_ENABLED']='true'
        self.command,self.env=command,env;self.client=None;self.log=None
    def __enter__(self):
        self.log=(self.work/'server.log').open('ab')
        self.proc=subprocess.Popen(self.command,cwd=HOME/'server',env=self.env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=self.log,text=True,encoding='utf-8',bufsize=1)
        self.client=E.McpStdioClient(self.proc)
        try:
            self.client.initialize(30);names={t['name'] for t in self.client.tools_list(30)['tools']}
            expected=set(E.CATALOG_ALLOWLIST)
            if self.session['write_enabled']:expected.remove('dwg_inspect_lisp');expected.add('dwg_apply_plan');expected.add('dwg_view')
            if self.session.get('save_enabled'):expected.add('dwg_foundation_save_drawing')
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
        # Accept a stored request ID as well as a request record: callers often only
        # keep the ID, and indexing a string here used to crash instead of failing clean.
        if isinstance(request,str):
            stored=self.work/'requests'/(request+'.json')
            request=read(stored) if stored.exists() else None
        require(isinstance(request,dict) and isinstance(request.get('request_id'),str),'Unknown request record; pass a request ID that this workspace submitted')
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
            if isinstance(reply,dict) and 'write_owner_process_changed' in str(reply.get('error') or ''):
                reply['next_action']='operator_recovery_required: the save may be complete but the fence is still held; review the drawing and saved file, then resolve the original request explicitly (never replay it)'
                reply['persisted']=False
                return reply
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
    def journal_has(self,request_id):
        # Read-only look at the plugin journal: the only authority on whether a
        # submission actually reached the queue.
        path=Path(self.session['native_root'])/'Dwg/execution/plugin-execution.sqlite'
        if not path.exists():return None
        try:
            with sqlite3.connect(path.as_uri()+'?mode=ro',uri=True) as db:
                return db.execute('select count(*) from plugin_jobs where request_id=?',(request_id,)).fetchone()[0]>0
        except Exception:return None
    def persist(self ,save_to,request_id=None,overwrite=False):
        # 2022 save route: one separate save job of the bound owned drawing. It never
        # edits geometry; the write's fence stays held until this save exists.
        require(self.session.get('save_enabled'),'This session did not opt into plugin-side save')
        # Reject traversal before resolving: resolve() would silently collapse ".." and
        # turn an escaping path into an accepted absolute one.
        raw_parts=PureWindowsPath(str(save_to)).parts
        require('..' not in raw_parts,'Save target must not contain parent-directory segments')
        target=Path(save_to).resolve();require(target.suffix.lower()=='.dwg','Save target must be a .dwg file')
        # An existing DWG may be a real drawing. Replacing it needs an explicit opt-in,
        # so a mistyped path can never silently overwrite someone else's file.
        if target.exists() and not overwrite:
            raise RuntimeError('Save target already exists; rerun with --overwrite to replace it: '+str(target))
        payload={'output_path':str(target),'confirm':True,'overwrite_existing':bool(overwrite),'allow_repo_output':False}
        canonical=json.dumps(payload,ensure_ascii=False,sort_keys=True,separators=(',',':'))
        rid=request_id or 'save-'+uuid.uuid4().hex
        require(all(c.isalnum() or c in '-_' for c in rid) and 1<=len(rid)<=80,'Invalid request ID')
        request={'request_id':rid,'context_id':self.context,'save':payload,'save_digest':hashlib.sha256(canonical.encode('utf-8')).hexdigest()}
        path=self.work/'requests'/(rid+'.json')
        write(path,request) # Once submitted, an uncertain save is reconciled, never repeated blindly.
        try:request['submit_reply']=self.call('dwg_foundation_save_drawing',{'context_id':self.context,'request_id':rid,'request_json':canonical})
        except Exception as ex:
            request['submission']='unknown';request['submit_error']=str(ex);write(path,request)
            return self.query(request)
        write(path,request);reply=self.query(request)
        raw=reply.get('job',{}).get('result_json')
        if isinstance(raw,str):
            try:reply['saved']=json.loads(raw)
            except Exception:pass
        # Remember the exact bytes this session last verified on disk. Publishing needs that
        # digest, because AutoCAD keeps DBMOD set after an API-level SaveAs even when the file
        # on disk is current.
        saved=reply.get('saved') if isinstance(reply.get('saved'),dict) else None
        saved_path=saved.get('saved_path') if saved else None
        if reply.get('job',{}).get('phase')=='completed' and saved_path and Path(saved_path).is_file():
            self.session['last_saved_sha256']=sha(Path(saved_path))
            self.session['last_saved_at_utc']=dt.datetime.now(dt.timezone.utc).isoformat()
            self.session['last_saved_request_id']=rid
            write(self.work/'session.json',self.session)
        return reply
    def apply(self,plan,request_id=None,save_to=None):
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
            text=str(ex)
            if 'fence conflicts' in text or 'request ID' in text:
                # A pre-submission conflict is only called terminal after the journal
                # is read back: no row for this request means nothing was queued.
                if not self.journal_has(request_id):
                    reply={'status':'rejected_before_submission','pending':False,'effects':'none','registered':False,'request_id':request_id,'context_id':self.context,'error':text,'reason':'fence_held_by_earlier_unpersisted_write' if 'fence conflicts' in text else 'request_id_conflict'}
                    request['terminal_rejection']=reply;write(path,request);return reply
            request['submission']='unknown';request['submit_error']=str(ex);write(path,request)
            # Like the proven read entry: a refused or lost reply is reconciled only
            # through the same request, never by a second submission.
            return self.query(request)
        reply=request['submit_reply']
        if reply.get('status')=='rejected_before_submission' and reply.get('registered') is False and reply.get('effects')=='none' and reply.get('request_id')==request_id and reply.get('context_id')==self.context:
            request['terminal_rejection']=reply;write(path,request);return reply
        write(path,request);return self.query(request)

    def apply_with_optional_save(self,plan,request_id=None,save_to=None,overwrite=False):
        """Write, then optionally persist and re-query so the server can finalize.

        Without save_to the reply must be reported as verified-but-not-persisted:
        the write is committed in memory and verified, its fence is still held, and
        the caller decides when to persist. With save_to the client saves the bound
        owned drawing to that path and re-queries the original write request, which
        lets the server release the fence only after a real save exists.
        """
        write_reply=self.apply(plan,request_id)
        # apply() generates the canonical request id when the caller did not pass one.
        # Finalization must query that same id; using the original None/auto id
        # produces a false "Unknown request record" after a successful save.
        write_id=write_reply.get('request_id') or request_id
        require(write_id,'write reply carries no request id')
        summary=write_reply.get('outcome_summary') if isinstance(write_reply,dict) else None
        if not isinstance(summary,dict):
            summary={}
        released=write_reply.get('write_fence_released') is True and summary.get('next_action')=='none'
        if released:
            return write_reply
        if summary.get('effects')!='committed':
            if write_reply.get('status')=='rejected_before_submission':
                write_reply['persisted']=False
                write_reply['next_action']='no_new_write_queued: the held fence belongs to an earlier verified-but-unpersisted write; persist or resolve that request first'
            return write_reply
        if not self.session.get('save_enabled'):
            result=dict(write_reply)
            result['persisted']=False
            result['next_action']='memory_write_only: this session has no plugin-side save; the drawing is not on disk and its fence is still held'
            return result
        if not save_to:
            result=dict(write_reply)
            result['persisted']=False
            result['next_action']='verified_not_persisted: rerun with --save-to <bound owned drawing> to persist and release the fence'
            return result
        # Only the bound owned drawing may be saved by this route. Saving anywhere else is a
        # SaveAs that changes the active document path while the plugin journal scope and the
        # server-side drawing identity still describe the old file, so it is refused here,
        # before any save runs, rather than reported as a failure after the identity moved.
        target=Path(save_to).resolve()
        owned=Path(self.session['drawing'])
        require(target==owned,'apply --save-to must target the bound owned drawing: '+str(owned)+' (use publish-dwg for a delivery copy, got '+str(target)+')')
        saved=self.persist(target,overwrite=overwrite)
        saved_json=saved.get('saved') if isinstance(saved.get('saved'),dict) else None
        saved_ok=saved_json.get('saved_path') if isinstance(saved_json,dict) else None
        # A completed save of the bound drawing keeps its path; the session identity is never
        # rewritten here. A save reply naming any other file means the host changed something
        # this client does not own, so the write stays unpersisted and nothing is rebound.
        if saved.get('job',{}).get('phase')=='completed' and saved_ok and str(Path(saved_ok))!=str(Path(self.session['drawing'])):
            result=dict(write_reply)
            result['persisted']=False
            result['save_reply']=saved
            result['next_action']='save_target_differs_from_bound_drawing: identity left unchanged; use publish-dwg for a delivery copy'
            return result
        if saved.get('job',{}).get('phase')!='completed' or not saved_ok:
            result=dict(write_reply)
            result['persisted']=False
            result['save_reply']=saved
            result['next_action']='save_failed: the write stays verified but unpersisted; its fence remains held and nothing may be replayed'
            return result
        final=self.query({'request_id':write_id,'context_id':write_reply.get('context_id') or self.context},wait=120)
        final_summary=final.get('outcome_summary') if isinstance(final,dict) else None
        result=dict(final)
        result['persisted']=bool(final.get('write_fence_released')) and isinstance(final_summary,dict) and final_summary.get('next_action')=='none'
        result['save_reply']={'request_id':saved.get('request_id'),'saved_path':saved_ok,'phase':saved.get('job',{}).get('phase')}
        if not result['persisted']:
            result['next_action']='save_succeeded_but_finalize_pending: query the original write request; never replay the write'
        return result

def publish(g,target,overwrite=False):
    """Publish a verified, saved working DWG as a delivery copy.

    This is a file operation on the client: the bound document, the session identity, the
    plugin journal scope and the revision are all left untouched. The copy is written to a
    unique temporary file in the target directory, digest-checked against the source and only
    then atomically renamed, so a failed publish never leaves a plausible-looking delivery DWG.
    """
    s=g.session;owned=Path(s['drawing'])
    require(owned.is_file(),'Owned working drawing is missing; nothing to publish')
    raw=str(target)
    require('..' not in PureWindowsPath(raw).parts,'Delivery target must not contain parent-directory segments')
    dst=Path(target).resolve()
    require(dst.suffix.lower()=='.dwg','Delivery target must be a .dwg file')
    require(dst!=owned,'Delivery target must differ from the bound working drawing')
    if dst.exists() and not overwrite:
        raise RuntimeError('Delivery target already exists; rerun with --overwrite to replace it: '+str(dst))
    # Re-read the live document state: the bind-time snapshot still shows the unsaved flag
    # from before the last save, so publishing has to check the drawing as it is now.
    fresh=g.call('dwg_bind_active_document',{})
    gate,issues=E.check_binding_identity(fresh['binding_snapshot'],s['pid'],s['started'],s['drawing'])
    require(gate=='identity_matched',str(issues))
    info=(fresh.get('document_info') or {}) or {}
    require(info.get('document_count')==1,'Publish requires exactly one open owned drawing')
    # The drawing must match the bytes this session last verified on disk. DBMOD is not used
    # as the gate: an API-level SaveAs leaves it set even when the file is current.
    saved_digest=s.get('last_saved_sha256')
    require(bool(saved_digest),'Publish requires a verified save in this session; run save first')
    disk_digest=sha(owned)
    require(disk_digest==saved_digest,'Publish refused: the working DWG on disk differs from the last verified save; save again')
    require(info.get('command_active')==0,'Publish requires an idle drawing (no active command)')
    require(info.get('loaded_plugin_mvid')==s['installation']['plugin_mvid'],'Loaded plugin changed since start')
    counts=held_fences(s);require(not any(counts.values()),'Publish refused: unresolved write fence '+str(counts))
    xrefs=(info.get('foundation_catalog') or {}).get('xref_definitions') or info.get('xref_definitions') or []
    xref_note=[]
    if isinstance(xrefs,list):
        for row in xrefs:
            path=row.get('path') if isinstance(row,dict) else None
            if path: xref_note.append(str(path))
    source_sha=sha(owned);dst.parent.mkdir(parents=True,exist_ok=True)
    temp=dst.with_name(dst.stem+'.publish-'+uuid.uuid4().hex[:8]+'.dwg')
    try:
        shutil.copy2(owned,temp)
        require(temp.is_file() and sha(temp)==source_sha,'Temporary copy differs from the source drawing; publish aborted')
        os.replace(str(temp),str(dst))
    finally:
        if temp.exists():
            try:temp.unlink()
            except OSError:pass
    target_sha=sha(dst);require(target_sha==source_sha,'Published file digest does not match the source drawing')
    record={'schema':'foundation-publish-v1','published_at_utc':dt.datetime.now(dt.timezone.utc).isoformat(),
            'source_path':str(owned),'source_sha256':source_sha,'target_path':str(dst),'target_sha256':target_sha,
            'bytes':dst.stat().st_size,'overwrote_existing':bool(overwrite),'drawing_identity_changed':False,
            'journal_scope_changed':False,'external_references':xref_note,'complete_engineering_package':not xref_note}
    outdir=g.work/'publishes';outdir.mkdir(exist_ok=True)
    name=dt.datetime.now(dt.timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'-'+dst.stem+'.json'
    write(outdir/name,record)
    return {'published':True,'target_path':str(dst),'source_sha256':source_sha,'target_sha256':target_sha,
            'bytes':record['bytes'],'external_references':xref_note,
            'warning':None if not xref_note else 'The drawing references external files; a single DWG is not a complete package',
            'record':str(outdir/name)}

def main():
    p=argparse.ArgumentParser(description='CAD foundation first release; all drawings are owned copies')
    p.add_argument('action',choices=['install','start','doctor','read','query','apply','save','publish-dwg','stop','status','mcp','view','plot-style'])
    p.add_argument('--workspace',type=Path,required=True);p.add_argument('--cad',default=None)
    p.add_argument('--acad-year',type=int,default=DEFAULT_ACAD_YEAR,help='Target AutoCAD release year (2022 or 2026)')
    p.add_argument('--source');p.add_argument('--write',action='store_true');p.add_argument('--headless',action='store_true')
    p.add_argument('--allow-save',action='store_true',help='AutoCAD 2022 only: enable plugin-side save and the finalize-before-save guard')
    p.add_argument('--handles');p.add_argument('--plan',type=Path);p.add_argument('--request-id');p.add_argument('--arguments',type=Path)
    p.add_argument('--save-to',type=Path,help='apply only: persist the bound owned drawing to this absolute .dwg after the write is verified')
    p.add_argument('--overwrite',action='store_true',help='allow --save-to to replace an existing .dwg (default refuses)')
    p.add_argument('--reopen',action='store_true',help='start only: relaunch a stopped session on its existing owned drawing')
    p.add_argument('--target',help='publish-dwg: absolute .dwg path for the delivery copy')
    args=p.parse_args();args.workspace=args.workspace.resolve()
    if not args.cad: args.cad=default_cad_root(args.acad_year)
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
                elif args.action=='apply':result=g.apply_with_optional_save(read(args.plan),args.request_id,args.save_to,overwrite=args.overwrite)
                elif args.action=='save':
                    # Save means one thing only: write the current bound DWG to its own file.
                    # It is not a publish and it does not decide anything about a new path, so
                    # the drawing identity cannot move here. Pending verified writes are
                    # finalized by re-querying them; none of them is ever replayed.
                    saved=g.persist(g.session['drawing'],overwrite=True)
                    job=saved.get('job',{}) or {}
                    result={'save':{'request_id':saved.get('request_id'),'phase':job.get('phase'),
                                    'saved_path':(saved.get('saved') or {}).get('saved_path'),
                                    'error':job.get('error_message')}}
                    pending=[]
                    for f in sorted((args.workspace/'requests').glob('*.json')):
                        r=read(f)
                        if r.get('save') or r.get('terminal_rejection'):continue
                        try:snap=g.query(r)
                        except Exception:continue
                        summary=snap.get('outcome_summary') or {}
                        if summary.get('effects')=='committed' and snap.get('write_fence_released') is not True:
                            pending.append(snap.get('request_id'))
                    result['pending_write_requests']=pending
                    result['drawing_identity_changed']=False
                    result['next_action']='saved_current_drawing_and_released_pending_writes' if not pending else 'pending writes still fenced; query each request id, never replay'
                elif args.action=='publish-dwg':result=publish(g,args.target or '',overwrite=args.overwrite)
                elif args.action=='stop':result=stop(g)
                elif args.action=='status':
                    # Minimal audit surface: session facts plus, when asked, the stored
                   # request record of a write that is committed but not yet released.
                    snap={'workspace':str(args.workspace),'drawing':g.session['drawing'],'phase':g.session.get('phase'),
                          'pid':g.session.get('pid'),'save_enabled':bool(g.session.get('save_enabled')),
                          'last_saved_sha256':g.session.get('last_saved_sha256'),
                          'last_saved_at_utc':g.session.get('last_saved_at_utc'),
                          'fences':held_fences(g.session),
                          'resolve_tools':None,
                          'note':'No resolve/discard tool exists in this build; a held write is finalized by saving its drawing'}
                    if args.request_id:snap['request']=g.query(args.request_id)
                    result=snap
                else:result=g.query(args.request_id)
        print(json.dumps(E.redact(result),ensure_ascii=False,indent=2));return 0
if __name__=='__main__':
    try:sys.exit(main())
    except Exception as ex:print(json.dumps({'error':str(ex)},ensure_ascii=False),file=sys.stderr);sys.exit(1)
