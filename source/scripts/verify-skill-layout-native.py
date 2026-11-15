"""Synthetic existing-Skill solver to Foundation apply/readback integration.

The solver is loaded from a separately installed Skill and is never rewritten
or bundled into Foundation. No real road geometry or quantity dataset is used.
"""
from pathlib import Path
import argparse,hashlib,importlib.util,json,math,os,subprocess,sys,tempfile
from shapely.geometry import box,LineString
def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def main():
 p=argparse.ArgumentParser();p.add_argument('--flow',type=Path,required=True);p.add_argument('--package',type=Path);p.add_argument('--resume',type=Path);p.add_argument('--solver',type=Path,default=Path.home()/'.codex/skills/cad-callout-pair-layout/scripts/pair_layout.py');a=p.parse_args();flow=read(a.flow/'summary.json');assert flow['passed'];package=a.package or Path(flow['package']);solver_digest=hashlib.sha256(a.solver.read_bytes()).hexdigest()
 spec=importlib.util.spec_from_file_location('separate_callout_skill_solver',a.solver);solver=importlib.util.module_from_spec(spec);spec.loader.exec_module(solver)
 work=Path(tempfile.mkdtemp(prefix='caf-skill-layout-'));evidence=a.flow/'skill-layout';revision=1
 while evidence.exists():revision+=1;evidence=a.flow/('skill-layout-r'+str(revision))
 if a.resume:
  evidence=a.resume;work=Path(json.loads(read(evidence/'start.json')['stdout'])['drawing']).parents[1]
 else:evidence.mkdir(exist_ok=False)
 env=dict(os.environ,PYTHONIOENCODING='utf-8',PYTHONDONTWRITEBYTECODE='1')
 def write(path,obj):path.write_text(json.dumps(obj,ensure_ascii=False,indent=2),encoding='utf-8')
 def call(label,action,*args):
  c=subprocess.run([sys.executable,str(package/'client/foundation.py'),action,'--workspace',str(work),*map(str,args)],env=env,capture_output=True,text=True,encoding='utf-8',timeout=115);write(evidence/(label+'.json'),{'returncode':c.returncode,'stdout':c.stdout,'stderr':c.stderr});assert c.returncode==0,c.stderr[:500];return json.loads(c.stdout)
 def apply(label,steps):
  path=evidence/(label+'-plan.json');write(path,{'schema':'foundation-drawing-plan-v1','source_role':'test','units':'metres','steps':steps});j=call(label,'apply','--plan',path);assert j['verification']=='passed' and j['effects']=='committed' and j['write_fence_released'];return json.loads(j['result_json'])
 if not a.resume:call('install','install');call('start','start','--source',flow['saved_file'],'--write','--headless')
 starts=[]
 for i,x in enumerate([60,60.5],1):
  starts.append({'id':'P'+str(i),'op':'callout','text':'P'+str(i)+' layout','position':[x+13,64.25],'height':1,'width':40,'vertices':[[x,60],[x+3,64],[x+13,64]],'dot_radius':.125,'layer':'CAF-Test','color':2,'text_color':7,'text_style':'CAF-Text','dimension_style':'CAF-Dim','attachment':'BottomRight','gap':.25})
 if a.resume:
  recorded=json.loads(read(evidence/'create-overlapping.json')['stdout']);assert recorded['verification']=='passed' and recorded['write_fence_released'];created=json.loads(recorded['result_json'])
 else:created=apply('create-overlapping',starts)
 handles=created['created_handles'];objects=created['objects'];native=call('measure-text','read','--handles',','.join(handles))['data']['entities'];rows={r['handle']:r for r in native};assert all(r['ok'] for r in native)
 if not a.resume:
  formats=[]
  for id,group in objects.items():
   lh,th=group['leader'],group['text'];label=rows[th]['entity'];formats.append({'id':'fit_'+id,'op':'set_callout_format','handle':lh,'expected_sha256':rows[lh]['entity_digest'],'text_handle':th,'text_expected_sha256':rows[th]['entity_digest'],'text_style':'CAF-Text','dimension_style':'CAF-Dim','height':1,'width':label['actual_width']+.001,'gap':.25,'text_color':7,'leader_color':2,'attachment':'BottomRight'})
  apply('fit-native-paragraph-boxes',formats);native=call('measure-fitted','read','--handles',','.join(handles))['data']['entities'];rows={r['handle']:r for r in native}
 inputs=[]
 for i,s in enumerate(starts,1):
  text=rows[objects[s['id']]['text']]['entity'];assert text['attachment']=='BottomRight';ext=text['extents'];width=ext['max']['x']-ext['min']['x'];height=ext['max']['y']-ext['min']['y'];assert width>0 and height>0
  inputs.append({'id':s['id'],'anchor':s['vertices'][0],'direction':[.6,.8],'length':5,'width':width,'height':height,'gap':.25,'opening_sign':1,'target':{'kind':'polygon','points':[[50,50],[110,50],[110,110],[50,110]]}})
 data={'bounds':[45,45,120,120],'clearance':.15,'step':5,'max_extensions':5,'max_rounds':3,'check_leader_text':True,'rows':inputs,'obstacles':[]};write(evidence/'solver-input.json',data);proposed=solver.solve(data);write(evidence/'solver-output.json',proposed);assert proposed['status']=='solved' and not proposed['manual_ids'] and not proposed['remaining_conflicts']
 assert any(math.dist(r['points'][0],r['points'][1])>5.001 for r in proposed['rows']),'Synthetic example must exercise actual movement'
 steps=[]
 for r in proposed['rows']:
  group=objects[r['id']];leader=group['leader'];text=group['text'];
  steps.append({'id':'set_'+r['id'],'op':'set_callout','handle':leader,'expected_sha256':rows[leader]['entity_digest'],'text_handle':text,'text_expected_sha256':rows[text]['entity_digest'],'vertices':r['points'],'position':r['insert'],'dot_companions':[{'handle':group[k],'expected_sha256':rows[group[k]]['entity_digest']} for k in ['dot_boundary','dot_fill']]})
 apply('apply-solver-proposals',steps);native=call('independent-native-read','read','--handles',','.join(handles))['data']['entities'];rows={r['handle']:r for r in native};boxes=[];leaders=[]
 for r in proposed['rows']:
  group=objects[r['id']];label=rows[group['text']]['entity'];leader=rows[group['leader']]['entity'];assert label['attachment']=='BottomRight'
  for actual,expected in zip(leader['vertices_wcs'],r['points']):assert math.hypot(actual['x']-expected[0],actual['y']-expected[1])<=.001
  assert math.hypot(label['location']['x']-r['insert'][0],label['location']['y']-r['insert'][1])<=.001
  ext=label['extents'];boxes.append(box(ext['min']['x'],ext['min']['y'],ext['max']['x'],ext['max']['y']));leaders.append(LineString([(v['x'],v['y']) for v in leader['vertices_wcs']]))
 assert boxes[0].distance(boxes[1])>=.149 and leaders[0].distance(boxes[1])>=.149 and leaders[1].distance(boxes[0])>=.149,'Native text/leader conflict remains'
 saved=call('save','save','--handles',','.join(handles));assert saved['verification']=='passed' and saved['write_fence_released'];assert call('stop','stop')['stopped'];assert hashlib.sha256(a.solver.read_bytes()).hexdigest()==solver_digest
 write(evidence/'summary.json',{'passed':True,'callouts':2,'native_objects':len(handles),'solver_unchanged':True,'native_geometry_matches_solver':True,'native_text_and_leader_conflicts':0,'finite_budget':{'rounds':3,'max_extensions':5,'step':5},'saved_and_stopped':True,'workspace':str(work)})
 print('Separate unchanged Skill solver proposals applied and independently verified in native CAD.',flush=True)
if __name__=='__main__':main()
