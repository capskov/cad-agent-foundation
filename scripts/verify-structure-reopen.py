"""Read-only MCP comparison in an independently started CAD process."""
import argparse,importlib.util,json
from pathlib import Path
def main():
 p=argparse.ArgumentParser();p.add_argument('--package',type=Path,required=True);p.add_argument('--workspace',type=Path,required=True);p.add_argument('--report',type=Path,required=True);a=p.parse_args()
 spec=importlib.util.spec_from_file_location('reopen_entry',a.package/'client/foundation.py');F=importlib.util.module_from_spec(spec);spec.loader.exec_module(F);old=json.loads(a.report.read_text(encoding='utf-8'))
 with F.lease(a.workspace),F.Gateway(a.workspace) as g:
  assert not g.bound['document_info']['native_write_enabled']
  result=g.submit_read('dwg_get_entity_properties',{'handles':json.dumps(old['handles']),'include_geometry':True});F.write(a.workspace/'reopen-readback.json',result)
  rows=result['data']['entities'];assert len(rows)==len(old['handles'])
  for r in rows:assert r['ok'] and r['entity_digest']==old['digests'][r['handle']],r['handle']
  F.write(a.workspace/'reopen-report.json',{'passed':True,'read_only':True,'objects':len(rows),'digests_match':True,'plugin_mvid':g.bound['document_info']['loaded_plugin_mvid']})
 print('Independent read-only CAD reopen passed')
if __name__=='__main__':main()
