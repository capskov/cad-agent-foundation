"""Package the built Windows runtime with hashes/MVID and dependency notices.

No CAD assemblies, SHX files, state, engineering inputs or PDBs are included.
Native acceptance is recorded separately and never inferred from this builder.
"""
from pathlib import Path
import argparse,datetime,hashlib,json,shutil,subprocess,zipfile,re,urllib.request,xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[1]
FORK=ROOT/'third_party/bimwright-dwg-mcp/fork'
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def put(p,text):p.parent.mkdir(parents=True,exist_ok=True);p.write_text(text,encoding='utf-8',newline='\n')
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--version',default='1.1.0-rc.1');parser.add_argument('--output',type=Path);a=parser.parse_args()
    output=a.output or ROOT/'artifacts/packages'/datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S')
    output=output.resolve();output.mkdir(parents=True,exist_ok=False)
    for label,target in [('server','net8.0'),('plugin','net8.0-windows')]:
        source=FORK/'src'/('server' if label=='server' else 'plugin-acad26')/'bin/Release'/target
        assert source.is_dir(),f'Build missing: {label}'
        for f in source.rglob('*'):
            if not f.is_file():continue
            rel=f.relative_to(source)
            allowed=len(rel.parts)==1 and f.suffix.lower() in {'.dll','.exe','.json'} or rel.parts[:3]==('runtimes','win-x64','native') or rel.as_posix()=='Fonts/OpenSans-CondensedLight.ttf'
            if not allowed:continue
            assert f.name.lower() not in {'acmgd.dll','accoremgd.dll','acdbmgd.dll','acwindows.dll'},'Autodesk assembly must not be redistributed'
            dest=output/label/rel;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(f,dest)
    for source,name in [(ROOT/'client/foundation.py','client/foundation.py'),(ROOT/'scripts/readonly-entry.py','support/readonly-entry.py'),(ROOT/'foundation.cmd','foundation.cmd'),(ROOT/'LICENSE','LICENSE'),(ROOT/'NOTICE.md','NOTICE.md'),(ROOT/'THIRD_PARTY_NOTICES.md','THIRD_PARTY_NOTICES.md')]:
        dest=output/name;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(source,dest)
    (output/'docs').mkdir()
    for f in (ROOT/'docs').glob('*.md'):shutil.copy2(f,output/'docs'/f.name)
    # Runtime ZIP is not a source checkout. Link missing source/example assets
    # back to the exact tagged repository instead of shipping broken links.
    for f in (output/'docs').glob('*.md'):
        text=f.read_text(encoding='utf-8-sig')
        def link(match):
            target=match.group(2)
            if target.startswith(('http:','https:','#','mailto:')):return match.group(0)
            path=target.split('#')[0].strip('<>')
            if not path or (f.parent/path).is_file():return match.group(0)
            source=((ROOT/f.relative_to(output)).parent/path).resolve()
            assert source.is_relative_to(ROOT) and source.is_file(),(f.name,path)
            fragment='#'+target.split('#',1)[1] if '#' in target else ''
            return match.group(1)+'https://github.com/capskov/cad-agent-foundation/blob/v'+a.version+'/'+source.relative_to(ROOT).as_posix()+fragment+')'
        put(f,re.sub(r'(\[[^\]\n]*\]\()([^)\n]+)\)',link,text))
    shutil.copy2(FORK/'src/shared/assets/fonts/OFL.txt',output/'plugin/Fonts/OFL.txt')
    dependencies={}
    for label in ['server','plugin']:
        for dep in (output/label).glob('*.deps.json'):
            j=json.loads(dep.read_text(encoding='utf-8'))
            for name,record in j['libraries'].items():
                if record['type']=='package':dependencies[name]=record['path']
    packagefolders=[]
    for project in ['server','plugin-acad26']:
        assets=json.loads((FORK/'src'/project/'obj/project.assets.json').read_text(encoding='utf-8'));packagefolders+=list(assets['packageFolders'])
    notices=[];spdx=ROOT/'artifacts/license-cache';spdx.mkdir(parents=True,exist_ok=True)
    for name,path in sorted(dependencies.items()):
        folder=next((Path(base)/path for base in packagefolders if (Path(base)/path).is_dir()),None);assert folder is not None,name
        nuspec=next(folder.glob('*.nuspec'));xml=ET.parse(nuspec);license=xml.find('.//{*}license');expression=license.text if license is not None else None
        d=output/'licenses'/name.replace('/','-');d.mkdir(parents=True)
        shipped=[]
        for f in folder.rglob('*'):
            if f.is_file() and any(word in f.name.lower() for word in ['license','copyright','thirdpartynotice','third-party-notice','notice.']):
                if f.suffix.lower() not in {'.dll','.xml','.pdb','.png','.nupkg','.json'}:
                    filename=f.relative_to(folder).as_posix().replace('/','__');shutil.copy2(f,d/filename);shipped.append(filename)
        if expression and license.attrib.get('type')=='expression':
            ids=[x for x in re.findall(r'[A-Za-z0-9][A-Za-z0-9.+-]*',expression) if x not in {'AND','OR','WITH'}]
            for identifier in ids:
                f=spdx/(identifier+'.txt')
                if not f.exists():
                    url='https://raw.githubusercontent.com/spdx/license-list-data/main/text/'+identifier+'.txt'
                    with urllib.request.urlopen(url,timeout=30) as response:f.write_bytes(response.read())
                shutil.copy2(f,d/(identifier+'.txt'));shipped.append(identifier+'.txt')
        assert shipped,'No license text resolved: '+name
        notices.append({'package':name,'license':expression,'included_license_files':shipped,'source':'NuGet package notices; SPDX text for declared expressions'})
    put(output/'DEPENDENCIES.json',json.dumps(notices,indent=2)+'\n')
    put(output/'README.md', '# CAD Agent Foundation '+a.version+' runtime candidate\n\nWindows x64, compatible licensed AutoCAD 2026/.NET 8 and Python 3.11+ required.\n\nInstall with foundation.cmd install --workspace C:\\CAD-work\\demo01, then start an owned drawing copy. Default is read-only; use --write for bounded plans. See docs/QUICKSTART.md and docs/NEXT-DEVELOPMENT-20261015.md. This build is a candidate until its scoped native acceptance record is published. No global MCP changes or Autodesk/SHX redistribution.\n')
    put(output/'README.zh-CN.md','# CAD Agent Foundation '+a.version+' 运行包\n\n需合法兼容的Windows x64 AutoCAD2026／.NET8桌面运行时及Python3.11+。完整解压后执行foundation.cmd install --workspace C:\\CAD-work\\demo01，再用start启动来源图的自有副本。默认只读，--write开启有界计划。见docs/QUICKSTART.zh-CN.md及版本验收；不修改全局MCP，不再分发Autodesk或SHX。\n')
    plugin=output/'plugin/Bimwright.Dwg.Plugin.Acad26.dll'
    info=json.loads(subprocess.check_output(['dotnet',str(ROOT/'tools/assembly-info/bin/Release/net8.0/assembly-info.dll'),str(plugin)],text=True,encoding='utf-8'))
    files={f.relative_to(output).as_posix():sha(f) for f in sorted(output.rglob('*')) if f.is_file()}
    bundle=hashlib.sha256(json.dumps({k:v for k,v in files.items() if k.startswith('plugin/')},sort_keys=True,separators=(',',':')).encode()).hexdigest().upper()
    manifest={'schema':'foundation-release-manifest-v1','version':a.version,'acceptance':'candidate-native-acceptance-pending','plugin_sha256':sha(plugin),'plugin_bundle_sha256':bundle,'plugin_mvid':info['mvid'],'files':files,'limits':['recorded AutoCAD2026/.NET8 host only','2D WCS metres','bounded operations; no arbitrary code','unknown writes never replayed','owned working copies only','dependency notices in licenses/']}
    put(output/'manifest.json',json.dumps(manifest,indent=2)+'\n')
    archive=output.with_suffix('.zip')
    with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
        for f in sorted(output.rglob('*')):
            if f.is_file():z.write(f,'cad-agent-foundation-'+a.version+'/'+f.relative_to(output).as_posix())
    with zipfile.ZipFile(archive) as z:
        assert z.testzip() is None and len(z.namelist())==len(files)+1
        for name,digest in files.items():assert hashlib.sha256(z.read('cad-agent-foundation-'+a.version+'/'+name)).hexdigest().upper()==digest
    record={'package':str(output),'archive':str(archive),'sha256':sha(archive),'plugin_mvid':info['mvid'],'files':len(files)+1,'dependency_packages':len(notices)}
    put(ROOT/'artifacts/latest-package.json',json.dumps(record,indent=2)+'\n');print(json.dumps(record))
if __name__=='__main__':main()
