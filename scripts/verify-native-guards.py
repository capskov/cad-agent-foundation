"""Meaningful native refusal checks on a previously accepted synthetic copy."""
from pathlib import Path
import argparse,hashlib,json,os,subprocess,sys,tempfile,uuid
def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def main():
 p=argparse.ArgumentParser();p.add_argument('--flow',type=Path,required=True);p.add_argument('--package',type=Path);a=p.parse_args();flow=read(a.flow/'summary.json');assert flow['passed'];package=a.package or Path(flow['package']);work=Path(tempfile.mkdtemp(prefix='caf-native-guards-'));evidence=a.flow/'guards';evidence.mkdir(exist_ok=False);env=dict(os.environ,PYTHONIOENCODING='utf-8',PYTHONDONTWRITEBYTECODE='1')
 def call(label,action,*extra):
  c=subprocess.run([sys.executable,str(package/'client/foundation.py'),action,'--workspace',str(work),*map(str,extra)],env=env,capture_output=True,text=True,encoding='utf-8',timeout=115);(evidence/(label+'.json')).write_text(json.dumps({'code':c.returncode,'stdout':c.stdout,'stderr':c.stderr},ensure_ascii=False),encoding='utf-8');assert c.returncode==0,c.stderr[:500];return json.loads(c.stdout)
 def apply(label,step,refuse=False):
  path=evidence/(label+'-plan.json');path.write_text(json.dumps({'schema':'foundation-drawing-plan-v1','source_role':'test','units':'metres','steps':[step]},ensure_ascii=False),encoding='utf-8');obj=call(label,'apply','--plan',path)
  if refuse:assert obj.get('effects')=='none' and obj.get('write_fence_released') is True and obj.get('status') in {'failed','rejected'},label
  else:assert obj.get('effects')=='committed' and obj.get('verification')=='passed' and obj.get('write_fence_released') is True,label
  return obj
 call('install','install');call('start','start','--source',flow['saved_file'],'--write','--headless')
 line=flow['objects']['line']['entity'];text=flow['objects']['callout']['text']
 rows=call('read-baseline','read','--handles',','.join(flow['handles']))['data']['entities'];baseline={r['handle']:r['entity_digest'] for r in rows};
 apply('stale-old-value',{'id':'stale','op':'rotate','targets':[{'handle':line,'expected_sha256':'0'*64}],'base':[0,0],'angle':15},True)
 apply('missing-associated-leader',{'id':'incomplete','op':'copy','targets':[{'handle':text,'expected_sha256':baseline[text]}],'delta':[1,0]},True)
 def layer_state(label,locked):
  bound=call(label+'-catalog','doctor');cat=bound['document_info']['foundation_catalog'];return apply(label,{'id':'state','op':'set_layer','name':'CAF-Test','expected_sha256':cat['layer_digests']['CAF-Test'],'color':3,'is_off':False,'is_frozen':False,'is_locked':locked,'is_plottable':True})
 layer_state('lock-layer',True);apply('locked-layer',{'id':'locked','op':'rotate','targets':[{'handle':line,'expected_sha256':baseline[line]}],'base':[0,0],'angle':15},True);layer_state('restore-layer',False)
 rows=call('read-after-refusals','read','--handles',','.join(flow['handles']))['data']['entities'];assert {r['handle']:r['entity_digest'] for r in rows}==baseline,'Refused edits changed existing native objects'
 saved=call('save','save','--handles',','.join(flow['handles']));assert saved['verification']=='passed' and saved['write_fence_released'];assert call('stop','stop')['stopped']
 (evidence/'summary.json').write_text(json.dumps({'passed':True,'native_refusals':['stale_digest','missing_associated_leader','locked_layer'],'entities_unchanged':True,'layer_restored':True,'saved_and_stopped':True,'workspace':str(work)},indent=2),encoding='utf-8');print('Three native safety refusals passed; protected entities unchanged; layer restored; CAD stopped.',flush=True)
if __name__=='__main__':main()
