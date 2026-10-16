"""Independent read-only CAD process, packaged MCP, saved entities and page setup."""
from pathlib import Path
import argparse,hashlib,json,os,subprocess,sys,tempfile
ROOT=Path(__file__).resolve().parents[1]
def read(p):return json.loads(Path(p).read_text(encoding='utf-8'))
def main():
 p=argparse.ArgumentParser();p.add_argument('--flow',type=Path);a=p.parse_args();flow=a.flow or Path(read(ROOT/'artifacts/phase2-current.json')['evidence']);old=read(flow/'report.json');assert old['passed'] and old['owned_CAD_stopped']
 work=Path(tempfile.mkdtemp(prefix='caf-phase2-reopen-'));evidence=flow/'reopen';evidence.mkdir(exist_ok=False);entry=Path(old['package'])/'client/foundation.py';env=dict(os.environ,PYTHONIOENCODING='utf-8',PYTHONDONTWRITEBYTECODE='1');source=Path(old['saved_file']);digest=hashlib.sha256(source.read_bytes()).hexdigest().upper();assert digest==old['saved_file_sha256']
 def cli(label,action,*args):
  c=subprocess.run([sys.executable,str(entry),action,'--workspace',str(work),*map(str,args)],env=env,capture_output=True,text=True,encoding='utf-8',timeout=120);(evidence/(label+'.json')).write_text(json.dumps({'exit_code':c.returncode,'stdout':c.stdout,'stderr':c.stderr},ensure_ascii=False,indent=2),encoding='utf-8');assert c.returncode==0,c.stderr;return json.loads(c.stdout)
 cli('install','install');cli('start','start','--source',source,'--headless');bound=cli('doctor','doctor');assert bound['document_info']['native_write_enabled'] is False
 rows=cli('read','read','--handles',','.join(old['handles']))['data']['entities'];previous=read(flow/'final-facts.json');assert len(rows)==len(previous)
 for row in rows:assert row['ok'] and row['entity_digest']==previous[row['handle']]['entity_digest'],row['handle']
 if 'layout_settings' in old:assert bound['document_info']['foundation_catalog']['layout_settings']==old['layout_settings'],'page setup did not persist'
 assert cli('stop','stop')['stopped'] and hashlib.sha256(source.read_bytes()).hexdigest().upper()==digest
 (evidence/'summary.json').write_text(json.dumps({'passed':True,'objects':len(rows),'read_only':True,'entity_digests_match':True,'layout_settings_match':'layout_settings' in old,'source_unchanged':True,'owned_CAD_stopped':True,'workspace':str(work)},indent=2),encoding='utf-8');print('Fresh CAD read-only reopen: entities and page settings passed',flush=True)
if __name__=='__main__':main()
