"""Fresh-process, read-only verification of the synthetic next-release drawing."""
from pathlib import Path
import argparse,hashlib,json,os,subprocess,sys,tempfile
def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def main():
 p=argparse.ArgumentParser();p.add_argument('--flow',type=Path,required=True);p.add_argument('--package',type=Path);a=p.parse_args()
 old=read(a.flow/'summary.json');assert old['passed'] and old['owned_CAD_stopped']
 package=a.package or Path(old['package']);source=Path(old['saved_file']);digest=hashlib.sha256(source.read_bytes()).hexdigest().upper();assert digest==old['saved_digest']
 workspace=Path(tempfile.mkdtemp(prefix='caf-native-reopen-'));evidence=a.flow/'reopen';evidence.mkdir(exist_ok=False);env=dict(os.environ,PYTHONIOENCODING='utf-8',PYTHONDONTWRITEBYTECODE='1')
 def call(label,action,*extra):
  c=subprocess.run([sys.executable,str(package/'client/foundation.py'),action,'--workspace',str(workspace),*map(str,extra)],env=env,capture_output=True,text=True,encoding='utf-8',timeout=115)
  (evidence/(label+'.json')).write_text(json.dumps({'returncode':c.returncode,'stdout':c.stdout,'stderr':c.stderr},ensure_ascii=False),encoding='utf-8');assert c.returncode==0,c.stderr[:500]
  print(label+': ok',flush=True);return json.loads(c.stdout)
 call('install','install');call('start','start','--source',source,'--headless');bound=call('doctor','doctor');assert bound['document_info']['native_write_enabled'] is False
 rows=call('read','read','--handles',','.join(old['handles']))['data']['entities'];previous=read(a.flow/'final-native-facts.json');assert len(rows)==len(previous)
 for row in rows:assert row['ok'] and row['entity_digest']==previous[row['handle']]['entity_digest'],('reopen digest differs',row['handle'])
 assert call('stop','stop')['stopped'] is True and hashlib.sha256(source.read_bytes()).hexdigest().upper()==digest
 (evidence/'summary.json').write_text(json.dumps({'passed':True,'objects':len(rows),'read_only':True,'native_digest_match':True,'source_unchanged':True,'owned_CAD_stopped':True,'workspace':str(workspace)},indent=2),encoding='utf-8')
 print('Independent saved-DWG reopen passed for every tracked native object.',flush=True)
if __name__=='__main__':main()
