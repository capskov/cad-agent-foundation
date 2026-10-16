"""Scoped font/metric regression through packaged Foundation MCP, on an owned copy."""
import argparse,importlib.util,json,hashlib
from pathlib import Path

def put(p,v):p.write_text(json.dumps(v,ensure_ascii=False,indent=2),encoding='utf-8')
def main():
 p=argparse.ArgumentParser();p.add_argument('--package',type=Path,required=True);p.add_argument('--workspace',type=Path,required=True);p.add_argument('--run-id',required=True);a=p.parse_args()
 spec=importlib.util.spec_from_file_location('font_entry',a.package/'client/foundation.py');F=importlib.util.module_from_spec(spec);spec.loader.exec_module(F)
 evidence=a.workspace/'font-regression';evidence.mkdir(exist_ok=True)
 with F.lease(a.workspace),F.Gateway(a.workspace) as g:
  put(evidence/'binding.json',g.bound);unit=g.bound['document_info']['units_code']
  def catalog():return g.call('dwg_bind_active_document',{})['document_info']['foundation_catalog']
  def rows(label,hs):
   r=g.submit_read('dwg_get_entity_properties',{'handles':json.dumps(hs),'include_geometry':True});put(evidence/(label+'.json'),r);rs=r['data']['entities'];assert len(rs)==len(hs) and all(x['ok'] for x in rs);return {x['handle']:x for x in rs}
  def apply(label,steps,expect=True):
   plan={'schema':'foundation-drawing-plan-v1','source_role':'test','units':'raw_drawing_units','expected_insunits':unit,'steps':steps};put(evidence/(label+'-plan.json'),plan)
   result=g.apply(plan,'font-'+a.run_id+'-'+label);put(evidence/(a.run_id+'-'+label+'-result.json'),result);j=result['job'];print(label,j.get('effects'),j.get('verification'),flush=True)
   assert j.get('write_fence_released') is True,str(result)[:2000]
   assert (j.get('effects')=='committed' and j.get('verification')=='passed') if expect else j.get('effects')=='none',str(result)[:2000]
   return json.loads(j['result_json']) if expect else result
  made=apply('seed',[
   {'id':'ttf','op':'text_style','name':'CF_Font_TTF','font_file':'arial.ttf','big_font_file':'','width_factor':.8},
   {'id':'shx','op':'text_style','name':'CF_Font_SHX','font_file':'txt.shx','big_font_file':'hztxt.shx','width_factor':.75},
   {'id':'a','op':'text','text':'字体替换 M7.5 15cm 123','position':[5000,-1500],'height':5,'rotation':0,'text_style':'CF_Font_TTF','layer':'0','color':7},
   {'id':'b','op':'text','text':'基层厚度 18cm 1:1','position':[5000,-1520],'height':5,'rotation':0,'text_style':'CF_Font_SHX','layer':'0','color':7},
   {'id':'m','op':'mtext','text':'多行文字 Abc 2500','position':[5000,-1540],'height':5,'width':150,'rotation':0,'text_style':'CF_Font_TTF','layer':'0','color':7},
   {'id':'ds','op':'dimension_style','name':'CF_Font_Dim','text_style':'CF_Font_TTF','text_height':4,'arrow_size':2,'overall_scale':1,'gap':1,'decimal_places':0,'dimension_color':7,'extension_color':7,'text_color':7},
   {'id':'dim','op':'aligned_dimension','start':[5000,-1580],'end':[5100,-1580],'position':[5050,-1565],'dimension_style':'CF_Font_Dim','layer':'0','color':7}])
  hs=made['created_handles'];original=rows('before',hs+['5A63']);c=catalog()
  def fontstep(name,digest,font='hztxt_e.shx'):
   return {'id':'font'+name.replace('_',''),'op':'set_text_style_font','name':name,'expected_sha256':digest,'font_file':font,'big_font_file':'hztxt.shx'}
  # Use the same canonical native digest algorithm as the governed protocol.
  def digest(v):return hashlib.sha256(json.dumps(v,ensure_ascii=False,sort_keys=True,separators=(',',':')).encode()).hexdigest().upper()
  first=fontstep('CF_Font_TTF',digest(c['text_styles']['CF_Font_TTF']))
  apply('stale-style',[dict(first,expected_sha256='0'*64)],False)
  apply('missing-font',[dict(first,font_file='CF_missing_font_20261016.shx')],False)
  apply('replace-fonts',[first,fontstep('CF_Font_SHX',digest(c['text_styles']['CF_Font_SHX']))])
  changed=rows('after-fonts',hs+['5A63']);c=catalog();put(evidence/'styles-after.json',c['text_styles'])
  for name in ['CF_Font_TTF','CF_Font_SHX']:
   assert c['text_styles'][name]['font_file'].lower()=='hztxt_e.shx' and c['text_styles'][name]['big_font_file'].lower()=='hztxt.shx'
  atext=made['objects']['a']['entity'];btext=made['objects']['b']['entity'];selected=[atext,btext,'5A63']
  for h in hs:
   x=original[h]['entity'];y=changed[h]['entity']
   for key in ['text','position','location','height','text_height','width_factor','alignment_point']:
    if key in x:assert x[key]==y[key],(h,key)
  apply('metrics',[{'id':'metrics','op':'set_text_metrics','targets':[{'handle':h,'expected_sha256':changed[h]['entity_digest']} for h in selected],'height_scale':1.1,'width_scale':1.375}])
  final=rows('final',hs+['5A63']);c=catalog()
  for h in selected:
   x=changed[h]['entity'];y=final[h]['entity'];anchor='position' if x['horizontal_mode']=='TextLeft' and x['vertical_mode']=='TextBase' else 'alignment_point'
   assert x[anchor]==y[anchor] and abs(y['height']-x['height']*1.1)<1e-8 and abs(y['width_factor']-x['width_factor']*1.375)<1e-8
  saved=apply('save',[{'id':'save','op':'save','expected_layouts':c['layout_digests'],'expected':[{'handle':h,'expected_sha256':r['entity_digest']} for h,r in final.items()]}])
  put(evidence/'report.json',{'passed':True,'package':str(a.package.resolve()),'workspace':str(a.workspace.resolve()),'handles':list(final),'digests':{h:r['entity_digest'] for h,r in final.items()},'saved_file_sha256':saved['saved_file_sha256'],'plugin_mvid':g.bound['document_info']['loaded_plugin_mvid'],'scope':'SHX pair replacement from TTF/SHX; DBText baseline/center metric anchors; MText/dimension shared styles; two native rejections; whole-file save with disk readback; visual QA separate'})
 print('Scoped native font replacement passed',flush=True)
if __name__=='__main__':main()
