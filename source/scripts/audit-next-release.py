"""Review the exact intended source set and a runtime ZIP before publication."""
from pathlib import Path
import ast,json,re,hashlib,subprocess,zipfile,sys
ROOT=Path(__file__).resolve().parents[1]
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest().upper()
def main():
 paths=subprocess.check_output(['git','-C',str(ROOT),'ls-files','--cached','--others','--exclude-standard','-z']).decode('utf-8').split('\0');paths=sorted(set(x for x in paths if x))
 links=0
 for rel in paths:
  f=ROOT/rel;assert f.is_file(),rel
  assert not any(part in {'artifacts','private','bin','obj','__pycache__','.venv'} for part in Path(rel).parts),rel
  assert f.suffix.lower() not in {'.dwg','.dxf','.sqlite','.db','.exe','.dll','.pdb','.pyc','.zip','.shx'},rel
  if f.suffix.lower() in {'.md','.json','.py','.cs','.csproj','.txt','.cmd','.yaml'}:
   t=f.read_text(encoding='utf-8-sig')
   for pattern in [r'[A-Z]:[\\/]Users[\\/]',r'gh[pousr]_[A-Za-z0-9]{20,}',r'github_pat_[A-Za-z0-9_]{30,}',r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----']:
    assert not re.search(pattern,t,re.I),(rel,pattern)
   if f.suffix=='.py':ast.parse(t)
   if f.suffix=='.json':json.loads(t)
   if f.suffix=='.md':
    for target in re.findall(r'\[[^\]\n]*\]\(([^)\n]+)\)',t):
     if target.startswith(('http:','https:','#','mailto:')):continue
     target=target.split('#')[0].strip('<>')
     if target:assert (f.parent/target).resolve().is_relative_to(ROOT) and (f.parent/target).is_file(),(rel,target);links+=1
 # Keep line endings/EOF clean without changing executable semantics.
 for rel in paths:
  if rel.endswith('.md'):
   f=ROOT/rel;t=f.read_text(encoding='utf-8-sig');f.write_text(t.rstrip()+'\n',encoding='utf-8',newline='\n')
 packageinfo=json.loads((ROOT/'artifacts/latest-package.json').read_text());package=Path(packageinfo['package']);manifest=json.loads((package/'manifest.json').read_text())
 for rel,digest in manifest['files'].items():assert sha(package/rel)==digest,rel
 archive=Path(packageinfo['archive'])
 with zipfile.ZipFile(archive) as z:
  assert z.testzip() is None and len(z.namelist())==len(manifest['files'])+1
  for rel,digest in manifest['files'].items():assert hashlib.sha256(z.read('cad-agent-foundation-'+manifest['version']+'/'+rel)).hexdigest().upper()==digest
  for entry in z.namelist():assert not entry.lower().endswith(('.pdb','.shx','.dwg','.sqlite')) and Path(entry).name.lower() not in {'acmgd.dll','accoremgd.dll','acdbmgd.dll','acwindows.dll'}
 # Embedded source paths must not occur in the native/runtime payloads.
 for folder in ['plugin','server']:
  for f in (package/folder).rglob('*'):
   if f.is_file() and f.suffix.lower() in {'.dll','.exe','.json'}:
    data=f.read_bytes()
    for pattern in [str(Path.home()).encode('utf-8'),str(Path.home()).replace('\\','/').encode('utf-8'),str(Path.home()).encode('utf-16le')]:assert pattern not in data,(folder,f.name,'private path')
 report={'source_files':len(paths),'local_links_checked':links,'privacy_and_asset_checks':True,'runtime_files':len(manifest['files'])+1,'runtime_sha256':sha(archive),'runtime_mvid':manifest['plugin_mvid'],'dependency_package_notices':packageinfo['dependency_packages'],'native_compatibility':'recorded AutoCAD2026/.NET8 only','source_paths':paths}
 (ROOT/'artifacts/release-audit.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print(json.dumps({k:v for k,v in report.items() if k!='source_paths'},ensure_ascii=False))
if __name__=='__main__':main()
