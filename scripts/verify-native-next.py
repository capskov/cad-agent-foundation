"""Scoped maintainer native test on owned synthetic copies, never engineering DWGs."""
from pathlib import Path
import argparse,datetime,hashlib,json,os,subprocess,sys,tempfile,time,uuid
ROOT=Path(__file__).resolve().parents[1]
def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def write(p,obj):p=Path(p);p.parent.mkdir(parents=True,exist_ok=True);p.write_text(json.dumps(obj,ensure_ascii=False,indent=2),encoding='utf-8')
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest().upper()
def seed(units=6):
    assert units in (0,4,6), "Synthetic unit fixture only"
    root=Path(tempfile.mkdtemp(prefix='caf-synthetic-source-'));drawing=root/'seed.dwg';script=root/'seed.scr'
    script.write_text('(vl-load-com)\n(setvar "INSUNITS" ' + str(units) + ')\n(vla-saveas (vla-get-activedocument (vlax-get-acad-object)) "'+drawing.as_posix()+'")\n(vla-quit (vlax-get-acad-object))\n',encoding='ascii')
    startup=subprocess.STARTUPINFO();startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW;startup.wShowWindow=0
    process=subprocess.Popen([r'C:\Program Files\Autodesk\AutoCAD 2026\acad.exe','/nologo','/b',str(script)],startupinfo=startup)
    deadline=time.monotonic()+110
    while process.poll() is None and time.monotonic()<deadline:time.sleep(.5)
    assert process.poll() is not None and drawing.is_file(),'Synthetic source startup/save/normal quit not confirmed; preserve seed process, inspect before retry'
    return drawing,{'seed_pid':process.pid,'seed_process_exited':True,'source':str(drawing),'source_sha256':sha(drawing)}
def main():
    p=argparse.ArgumentParser();p.add_argument('--package',type=Path);p.add_argument('--workspace',type=Path);p.add_argument('--evidence',type=Path);p.add_argument('--seed',type=Path);args=p.parse_args()
    package=args.package or Path(read(ROOT/'artifacts/latest-package.json')['package'])
    evidence=args.evidence or ROOT/'artifacts/native-next'/datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S');evidence.mkdir(parents=True,exist_ok=False)
    workspace=args.workspace or Path(tempfile.mkdtemp(prefix='caf-native-next-'))
    drawing,seedinfo=(args.seed,{'source':str(args.seed),'source_sha256':sha(args.seed)}) if args.seed else seed()
    write(evidence/'seed.json',seedinfo);write(ROOT/'artifacts/latest-native-run.json',{'evidence':str(evidence),'workspace':str(workspace),'package':str(package),'source':str(drawing)})
    entry=package/'client/foundation.py';env=dict(os.environ,PYTHONIOENCODING='utf-8',PYTHONDONTWRITEBYTECODE='1')
    def call(label,action,*extra):
        result=subprocess.run([sys.executable,str(entry),action,'--workspace',str(workspace),*map(str,extra)],env=env,capture_output=True,text=True,encoding='utf-8',timeout=115)
        write(evidence/(label+'.json'),{'exit_code':result.returncode,'stdout':result.stdout,'stderr':result.stderr})
        assert result.returncode==0,label+': '+result.stderr[:600]
        obj=json.loads(result.stdout);print(label+': '+str(obj.get('status',obj.get('startup',obj.get('stopped','ok')))),flush=True);return obj
    def plan(label,steps):
        path=evidence/(label+'-plan.json');write(path,{'schema':'foundation-drawing-plan-v1','source_role':'test','units':'metres','steps':steps});obj=call(label,'apply','--plan',path)
        assert obj.get('status')=='completed' and obj.get('effects')=='committed' and obj.get('verification')=='passed' and obj.get('write_fence_released') is True,label+': '+str(obj)[:900]
        return json.loads(obj['result_json'])
    call('install','install');call('start','start','--source',drawing,'--write','--headless');doctor=call('doctor','doctor');write(evidence/'bound.json',doctor)
    assert doctor['document_info']['units_code']==6 and doctor['document_info']['secureload']>=1
    creation=[{'id':'layer','op':'layer','name':'CAF-Test','color':2},{'id':'style','op':'text_style','name':'CAF-Text','font_file':'gbenor.shx','big_font_file':'gbcbig.shx','width_factor':1},
     {'id':'dimstyle','op':'dimension_style','name':'CAF-Dim','text_style':'CAF-Text','text_height':1.5,'arrow_size':.375,'overall_scale':1,'gap':.375,'decimal_places':3,'dimension_color':2,'extension_color':2,'text_color':7},
     {'id':'boundary','op':'polyline','vertices':[[0,0],[8,0],[8,4],[0,4]],'closed':True,'layer':'CAF-Test','color':2},
     {'id':'hatch','op':'hatch','boundary':'boundary','pattern':'ANSI31','scale':1,'angle':0,'layer':'CAF-Test','color':2},
     {'id':'line','op':'line','start':[15,0],'end':[19,0],'layer':'CAF-Test','color':2},
     {'id':'text','op':'text','text':'中文原生验收','position':[15,4],'height':1.5,'rotation':0,'layer':'CAF-Test','color':7,'text_style':'CAF-Text'},
     {'id':'callout','op':'callout','text':'测试面 A=32m²','position':[-8,7],'height':1.5,'width':12,'vertices':[[2,2],[-2,7],[-8,7]],'dot_radius':.1875,'layer':'CAF-Test','color':2,'text_color':7,'text_style':'CAF-Text','dimension_style':'CAF-Dim'},
     {'id':'dimlinear','op':'linear_dimension','start':[0,-4],'end':[8,-4],'position':[4,-7],'rotation':0,'dimension_style':'CAF-Dim','layer':'CAF-Test','color':2},
     {'id':'dimaligned','op':'aligned_dimension','start':[12,-4],'end':[15,0],'position':[16,-5],'dimension_style':'CAF-Dim','layer':'CAF-Test','color':2},
     {'id':'dimradius','op':'radial_dimension','center':[22,8],'chord':[25,8],'text_offset':2,'dimension_style':'CAF-Dim','layer':'CAF-Test','color':2},
     {'id':'dimdiameter','op':'diameter_dimension','center':[30,8],'chord':[33,8],'text_offset':2,'dimension_style':'CAF-Dim','layer':'CAF-Test','color':2}]
    result=plan('create',creation);handles=list(result['created_handles']);objects=result['objects'];write(evidence/'created.json',result)
    def rows(label,hs=handles):
        obj=call(label,'read','--handles',','.join(hs));entities=obj['data']['entities'];assert len(entities)==len(hs) and all(r['ok'] for r in entities);return {r['handle']:r for r in entities}
    data=rows('read-created');write(evidence/'created-readback.json',data)
    expected_measurements={'dimlinear':8,'dimaligned':5,'dimradius':3,'dimdiameter':6}
    for id,value in expected_measurements.items():assert abs(data[objects[id]['entity']]['entity']['measurement']-value)<.001
    assert data[objects['text']['entity']]['entity']['text']=='中文原生验收'
    def target(h):return {'handle':h,'expected_sha256':data[h]['entity_digest']}
    boundary=objects['boundary']['entity'];fill=objects['hatch']['entity']
    plan('reshape-boundary',[{'id':'reshape','op':'set_polyline','handle':boundary,'expected_sha256':data[boundary]['entity_digest'],'vertices':[[0,0],[9,0],[9,4],[0,4]],'companions':[target(fill)]}]);data=rows('read-reshaped')
    assert abs(data[boundary]['entity']['area']-36)<.001 and abs(data[fill]['entity']['area']-36)<.001
    line=objects['line']['entity'];plan('rotate-line',[{'id':'rotate','op':'rotate','targets':[target(line)],'base':[15,0],'angle':90}]);data=rows('read-rotated')
    plan('scale-line',[{'id':'scale','op':'scale','targets':[target(line)],'base':[15,0],'factor':2}]);data=rows('read-scaled');assert abs(data[line]['entity']['length']-8)<.001
    copied=plan('copy-line',[{'id':'copy','op':'copy','targets':[target(line)],'delta':[8,0]}]);new=copied['created_handles'];handles+=new;data=rows('read-copied',handles)
    group=list(objects['callout'].values());leader=objects['callout']['leader'];text=objects['callout']['text'];dot=objects['callout']['dot_boundary'];dotfill=objects['callout']['dot_fill']
    plan('reposition-callout',[{'id':'callout_pose','op':'set_callout','handle':leader,'expected_sha256':data[leader]['entity_digest'],'text_handle':text,'text_expected_sha256':data[text]['entity_digest'],'vertices':[[3,2],[-3,8],[-9,8]],'position':[-9,8],'dot_companions':[target(dot),target(dotfill)]}]);data=rows('read-callout-pose',handles)
    plan('format-callout',[{'id':'callout_format','op':'set_callout_format','handle':leader,'expected_sha256':data[leader]['entity_digest'],'text_handle':text,'text_expected_sha256':data[text]['entity_digest'],'text_style':'CAF-Text','dimension_style':'CAF-Dim','height':1.7,'width':14,'gap':.425,'text_color':7,'leader_color':2}]);data=rows('read-callout-format',handles)
    textupdate=[target(h) for h in group if h!=text]
    plan('update-callout-content',[{'id':'content','op':'set_text','handle':text,'expected_sha256':data[text]['entity_digest'],'text':'修改后的中文标注 A=36m²','companions':textupdate}]);data=rows('read-callout-content',handles)
    cg=plan('copy-callout-group',[{'id':'copygroup','op':'copy','targets':[target(h) for h in group],'delta':[0,20]}]);handles+=cg['created_handles'];data=rows('read-group-copy',handles)
    plan('scale-callout-group',[{'id':'scalegroup','op':'scale','targets':[target(h) for h in group],'base':[3,2],'factor':1.2}]);data=rows('read-group-scale',handles)
    plan('archive-closed-surface',[{'id':'archive_surface','op':'archive','targets':[target(boundary),target(fill)],'layer':'CAF-Archived'}]);data=rows('read-surface-archive',handles)
    # Layer state is checked against a fresh catalog; affects all members of that layer.
    doctor=call('fresh-catalog','doctor');catalog=doctor['document_info']['foundation_catalog'];plan('layer-state',[{'id':'state','op':'set_layer','name':'CAF-Test','expected_sha256':catalog['layer_digests']['CAF-Test'],'color':3,'is_off':False,'is_frozen':False,'is_locked':False,'is_plottable':True}])
    readlayers=call('query-test','query','--arguments',write_query(evidence));assert readlayers.get('data',{}).get('count',0)>0
    data=rows('read-final',handles);saved=call('save','save','--handles',','.join(handles));assert saved['verification']=='passed' and saved['write_fence_released'] is True
    write(evidence/'saved.json',saved);write(evidence/'final-native-facts.json',data)
    assert call('stop','stop')['stopped'] is True
    write(evidence/'summary.json',{'passed':True,'package':str(package),'workspace':str(workspace),'source_unchanged':sha(drawing)==seedinfo['source_sha256'],'handles':handles,'objects':objects,'dimensions':expected_measurements,'saved_file':str(workspace/'drawings/work.dwg'),'saved_digest':json.loads(saved['result_json'])['saved_file_sha256'],'owned_CAD_stopped':True})
    print('Native core operation workflow passed; source protected; owned CAD stopped.',flush=True)
def write_query(evidence):
    path=evidence/'query.json';write(path,{'layer':'CAF-Test','limit':50,'include_geometry':False});return path
if __name__=='__main__':main()
