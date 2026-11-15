"""Scoped regression of raw-coordinate plans and legacy hatch reuse via packaged MCP."""
import argparse,importlib.util,json,sys,subprocess
from pathlib import Path

def put(p,v):p.write_text(json.dumps(v,ensure_ascii=False,indent=2),encoding='utf-8')

def unit_closeout(F,a,evidence):
 # Reuse the existing packaged MCP harness; synthetic fixture setup is separate.
 with F.lease(a.workspace),F.Gateway(a.workspace) as g:
  unit=g.bound['document_info']['units_code'];assert unit==a.expected_unit
  manifest=F.read(a.package/'manifest.json');assert g.bound['document_info']['loaded_plugin_mvid']==manifest['plugin_mvid']
  put(evidence/'binding.json',g.bound);session=F.read(a.workspace/'session.json');source=Path(session['source']);original=F.sha(source)
  def rows(label,hs):
   r=g.submit_read('dwg_get_entity_properties',{'handles':json.dumps(hs),'include_geometry':True});put(evidence/(label+'.json'),r)
   data=r['data']['entities'];assert len(data)==len(hs) and all(x['ok'] for x in data);return {x['handle']:x for x in data}
  def apply(label,steps,mode='raw_drawing_units',expected=None,ok=True):
   plan={'schema':'foundation-drawing-plan-v1','source_role':'test','units':mode,'steps':steps}
   if expected is not None:plan['expected_insunits']=expected
   put(evidence/(label+'-plan.json'),plan);r=g.apply(plan,'unit-closeout-'+label);put(evidence/(label+'-result.json'),r);j=r.get('job',{})
   print(label,j.get('effects'),j.get('verification'),flush=True)
   assert j.get('write_fence_released') is True,str(r)[:2000]
   assert (j.get('effects')=='committed' and j.get('verification')=='passed') if ok else j.get('effects')=='none',str(r)[:2000]
   return json.loads(j['result_json']) if ok else r
  s=1000 if unit==4 else 1;b=1000*s
  def circle(id,x):return {'id':id,'op':'circle','center':[b+x*s,0],'radius':s,'layer':'0','color':7}
  refused=apply('metadata-mismatch',[circle('bad',50)],expected=6 if unit!=6 else 4,ok=False)
  assert refused['job'].get('error_code')=='drawing_unit_metadata_mismatch',str(refused)[:2000]
  made=apply('raw-create',[
   {'id':'ts','op':'text_style','name':'CF_UnitText','font_file':'txt.shx','big_font_file':'','width_factor':1},
   {'id':'ds','op':'dimension_style','name':'CF_UnitDim','text_style':'CF_UnitText','text_height':.003*s,'arrow_size':.001*s,'overall_scale':1,'gap':.001*s,'decimal_places':3,'dimension_color':7,'extension_color':7,'text_color':7},
   {'id':'line','op':'line','start':[b,0],'end':[b+3*s,4*s],'layer':'0','color':7},
   {'id':'text','op':'text','text':'Raw 123','position':[b,8*s],'height':.003*s,'rotation':0,'text_style':'CF_UnitText','layer':'0','color':7},
   {'id':'dim','op':'aligned_dimension','start':[b,0],'end':[b+3*s,4*s],'position':[b+4*s,0],'dimension_style':'CF_UnitDim','layer':'0','color':7},
   {'id':'boundary','op':'polyline','vertices':[[b+10*s,0],[b+13*s,0],[b+13*s,4*s],[b+10*s,4*s]],'closed':True,'layer':'0','color':7},
   {'id':'fill','op':'hatch','boundary':'boundary','pattern':'ANSI31','scale':s,'angle':0,'layer':'0','color':7}])
  hs=made['created_handles'];data=rows('created',hs);obj=made['objects']
  line=obj['line']['entity'];text=obj['text']['entity'];dim=obj['dim']['entity'];boundary=obj['boundary']['entity']
  assert data[line]['entity']['start']['x']==b and abs(data[dim]['entity']['measurement']-5*s)<1e-7
  assert abs(data[boundary]['entity']['area']-12*s*s)<.001
  assert abs(data[text]['entity']['height']-.003*s)<1e-9
  def target(h):return {'handle':h,'expected_sha256':data[h]['entity_digest']}
  apply('raw-edit',[{'id':'move','op':'move','targets':[target(line)],'delta':[.125*s,-.25*s]},
    {'id':'text','op':'set_text','handle':text,'expected_sha256':data[text]['entity_digest'],'text':'Raw changed 456','companions':[]}])
  data=rows('edited',hs);assert abs(data[line]['entity']['start']['x']-(b+.125*s))<1e-7 and data[text]['entity']['text']=='Raw changed 456'
  copy=apply('raw-copy',[{'id':'copy','op':'copy','targets':[target(line)],'delta':[2*s,0]}]);hs+=copy['created_handles']
  matching=apply('metadata-matching',[circle('guarded',60)],expected=unit);hs+=matching['created_handles']
  legacy=apply('legacy-metres',[circle('legacy',70)],mode='metres',ok=unit in (0,6))
  if unit in (0,6):hs+=legacy['created_handles']
  apply('stale-object',[{'id':'stale','op':'move','targets':[{'handle':line,'expected_sha256':'0'*64}],'delta':[1,0]}],ok=False)
  final=rows('final-readback',hs);bound=g.call('dwg_bind_active_document',{});assert bound['document_info']['units_code']==unit
  put(evidence/'report.json',{'passed':False,'units_code':unit,'handles':hs,'digests':{h:r['entity_digest'] for h,r in final.items()},'plugin_mvid':manifest['plugin_mvid'],'source_unchanged':F.sha(source)==original,'scope':'raw create/move/copy/text/dimension/hatch; optional metadata guard; legacy metres; stale-object protection; CLI save below'})
 # Exercise the changed CLI save path as well as its native disk readback.
 saved=subprocess.run([sys.executable,str(a.package/'client/foundation.py'),'save','--workspace',str(a.workspace),'--handles',','.join(hs),'--request-id','unit-closeout-cli-save'],capture_output=True,text=True,encoding='utf-8',timeout=100)
 put(evidence/'cli-save.json',{'exit_code':saved.returncode,'stdout':saved.stdout,'stderr':saved.stderr});assert saved.returncode==0,saved.stderr
 request=F.read(a.workspace/'requests/unit-closeout-cli-save.json');assert request['plan']['units']=='raw_drawing_units' and 'expected_insunits' not in request['plan']
 job=request['last_reply']['job'];assert job['effects']=='committed' and job['verification']=='passed' and job['write_fence_released'] is True
 report=F.read(evidence/'report.json');report.update(passed=True,saved_file_sha256=json.loads(job['result_json'])['saved_file_sha256']);assert report['source_unchanged'];put(evidence/'report.json',report)
 print('Unit closeout and raw CLI save passed',flush=True)
def main():
 p=argparse.ArgumentParser();p.add_argument('--package',type=Path,required=True);p.add_argument('--workspace',type=Path,required=True);p.add_argument('--unit-closeout',action='store_true');p.add_argument('--expected-unit',type=int,choices=[0,4,6]);a=p.parse_args()
 spec=importlib.util.spec_from_file_location('structure_entry',a.package/'client/foundation.py');F=importlib.util.module_from_spec(spec);spec.loader.exec_module(F)
 evidence=a.workspace/'regression';evidence.mkdir(exist_ok=True)
 if a.unit_closeout:
  assert a.expected_unit is not None,'--expected-unit required for closeout';unit_closeout(F,a,evidence);return
 with F.lease(a.workspace),F.Gateway(a.workspace) as g:
  put(evidence/'binding.json',g.bound);assert g.bound['document_info']['units_code']==4
  def rows(label,hs):
   result=g.submit_read('dwg_get_entity_properties',{'handles':json.dumps(hs),'include_geometry':True});put(evidence/(label+'.json'),result)
   data=result['data']['entities'];assert len(data)==len(hs) and all(r['ok'] for r in data);return {r['handle']:r for r in data}
  def apply(label,steps,unit=4,expect=True):
   plan={'schema':'foundation-drawing-plan-v1','source_role':'test','units':'raw_drawing_units','expected_insunits':unit,'steps':steps};put(evidence/(label+'-plan.json'),plan)
   result=g.apply(plan,'structure-regression-'+label);put(evidence/(label+'-result.json'),result);job=result['job'];print(label,job.get('effects'),job.get('verification'),flush=True)
   assert job.get('write_fence_released') is True,str(result)[:2000]
   if expect:assert job.get('verification')=='passed' and job.get('effects')=='committed',str(result)[:2000]
   else:assert job.get('effects')=='none',str(result)[:2000];return result
   return json.loads(job['result_json'])
  apply('wrong-units',[{'id':'bad','op':'line','start':[2000,0],'end':[2010,10],'layer':'0','color':7}],unit=5,expect=False)
  old=rows('source-before',['4285F','49C0C']);source=old['4285F'];put(evidence/'source-row.json',source)
  template={'handle':'4285F','expected_sha256':source['entity_digest']}
  made=apply('clone-custom-fill',[{'id':'boundary','op':'polyline','vertices':[[2000,0],[2030,0],[2048,-18],[2000,-18]],'closed':True,'layer':'0','color':7},{'id':'fill','op':'hatch','boundary':'boundary','pattern':'石灰粉煤灰碎石_WENDYCAD','scale':1.4,'angle':0,'layer':'0','color':4,'template':template}])
  hs=made['created_handles'];after=rows('created-readback',hs+['4285F']);assert after['4285F']['entity_digest']==source['entity_digest']
  # Preserve the legacy non-associative hatch without altering its geometry.
  apply('archive-legacy',[{'id':'archive','op':'archive','targets':[template,{'handle':'49C0C','expected_sha256':old['49C0C']['entity_digest']}],'layer':'CF_REGRESSION_ARCHIVE'}])
  final=rows('final-readback',hs+['4285F','49C0C']);catalog=g.call('dwg_bind_active_document',{})['document_info']['foundation_catalog']
  saved=apply('save',[{'id':'save','op':'save','expected_layouts':catalog['layout_digests'],'expected':[{'handle':h,'expected_sha256':r['entity_digest']} for h,r in final.items()]}])
  put(evidence/'report.json',{'passed':True,'handles':list(final),'digests':{h:r['entity_digest'] for h,r in final.items()},'saved_file_sha256':saved['saved_file_sha256'],'loaded_mvid':g.bound['document_info']['loaded_plugin_mvid'],'units_code':4})
 print('Scoped raw drawing-unit/custom hatch regression passed',flush=True)
if __name__=='__main__':main()
