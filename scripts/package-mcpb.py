"""Wrap an accepted Foundation runtime in a local stdio MCPB, without changing it."""
from pathlib import Path
import argparse,hashlib,json,zipfile

ROOT=Path(__file__).resolve().parents[1]
REGISTRY_NAME='io.github.capskov/cad-agent-foundation'

def sha(data):return hashlib.sha256(data).hexdigest()

def main():
 p=argparse.ArgumentParser();p.add_argument('--runtime-zip',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
 with zipfile.ZipFile(a.runtime_zip) as z:
  names=z.namelist();prefix='cad-agent-foundation-1.1.0-rc.1/';assert all(n.startswith(prefix) and '..' not in Path(n).parts for n in names)
  manifest=json.loads(z.read(prefix+'manifest.json'));assert manifest['version']=='1.1.0-rc.1' and manifest['acceptance']=='scoped-native-acceptance-passed'
  files={n[len(prefix):]:z.read(n) for n in names}
  assert set(files)==set(manifest['files'])|{'manifest.json'}
  for name,digest in manifest['files'].items():assert sha(files[name]).upper()==digest,name
 metadata={
  'manifest_version':'0.3','name':'cad-agent-foundation','display_name':'CAD Agent Foundation — AutoCAD MCP',
  'version':'1.1.0-rc.1','description':'Native AutoCAD MCP server for AI agents with bounded operations and write/save verification.',
  'long_description':'Local Windows x64 stdio integration. Requires separately installed licensed compatible AutoCAD 2026 (.NET 8), .NET 8 Desktop Runtime and Python 3.11+. First install the bundled runtime and explicitly start an owned drawing copy; choose that existing workspace below. Starting the extension only connects to that workspace; it does not start CAD or enable writes. Source drawings, private journals and unresolved requests remain protected. See INSTALL.md for limits and verification scope.',
  'author':{'name':'capskov','url':'https://github.com/capskov'},
  'repository':{'type':'git','url':'https://github.com/capskov/cad-agent-foundation'},
  'homepage':'https://github.com/capskov/cad-agent-foundation','support':'https://github.com/capskov/cad-agent-foundation/issues',
  'license':'Apache-2.0','keywords':['autocad','autocad-mcp','cad-automation','ai-agents','dwg'],
  'server':{'type':'python','entry_point':'runtime/client/foundation.py','mcp_config':{'command':'${user_config.python_command}','args':['${__dirname}/runtime/client/foundation.py','mcp','--workspace','${user_config.workspace}'],'env':{'PYTHONIOENCODING':'utf-8'}}},
  'compatibility':{'platforms':['win32'],'runtimes':{'python':'>=3.11'}},'tools_generated':True,
  'user_config':{'python_command':{'type':'string','title':'Python 3.11+ executable','description':'python on PATH or the absolute path to your installed python.exe.','default':'python','required':True},'workspace':{'type':'directory','title':'Existing owned CAD workspace','description':'Select the private workspace already installed and started with Foundation. Do not select an engineering source folder.','required':True}}
 }
 install='''# Local MCPB installation / 本地安装

Windows x64, licensed compatible AutoCAD 2026 R25.1.74.0.0/.NET 8, .NET 8 Desktop Runtime and Python 3.11+ are prerequisites. This package does not supply Autodesk software, SHX fonts or Python. Full prerequisites and scope: runtime/docs/QUICKSTART.md and runtime/docs/VALIDATION-v1.1.0-rc.1.md.

Install this MCPB in a client that implements the MCPB specification, or extract it as a ZIP. In the extracted runtime directory, run foundation.cmd install --workspace C:\\CAD-work\\demo01, then foundation.cmd start --workspace C:\\CAD-work\\demo01 --source C:\\drawings\\input.dwg. Start defaults to read-only; --write explicitly enables bounded writes on the owned copy. Set the extension's Python executable and select that existing workspace. Never run CLI and MCP concurrently against one workspace. Close the MCP connection before CLI save/status/stop. Save committed changes and stop normally. Unknown writes must be queried under the original request, never replayed or unlocked.

本扩展需先安装运行环境、安装工作目录并显式启动自有图纸副本；配置Python路径与同一工作目录后才可连接。安装扩展不会启动CAD、启用写入或改变全局MCP。原生25项操作的实测来自原运行包；本扩展包装与连接另行验证，不等于所有客户端均兼容。专业判断和排布仍由独立Skill负责。

Runtime bytes, licenses and the original runtime manifest are preserved under runtime/. Only the outer MCPB manifest and these instructions are new. See the repository discovery documentation for the exact packaging/connection validation record. Registry registration and directory submission are different from native CAD acceptance.
'''
 a.output.parent.mkdir(parents=True,exist_ok=True)
 with zipfile.ZipFile(a.output,'w',zipfile.ZIP_DEFLATED) as out:
  for name,data in sorted(files.items()):out.writestr('runtime/'+name,data)
  out.writestr('manifest.json',json.dumps(metadata,ensure_ascii=False,indent=2)+'\n');out.writestr('INSTALL.md',install)
 digest=sha(a.output.read_bytes())
 a.output.with_suffix(a.output.suffix+'.sha256').write_text(digest+'  '+a.output.name+'\n',encoding='ascii')
 server={'$schema':'https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json','name':REGISTRY_NAME,'title':'CAD Agent Foundation','description':metadata['description'],'version':metadata['version'],'websiteUrl':metadata['homepage'],'repository':{'url':metadata['repository']['url'],'source':'github'},'packages':[{'registryType':'mcpb','identifier':'https://github.com/capskov/cad-agent-foundation/releases/download/v1.1.0-rc.1/'+a.output.name,'fileSha256':digest,'transport':{'type':'stdio'}}]}
 (ROOT/'server.json').write_text(json.dumps(server,indent=2)+'\n',encoding='utf-8')
 print(json.dumps({'mcpb':str(a.output),'sha256':digest,'preserved_runtime_files':len(files),'registry_name':REGISTRY_NAME}))

if __name__=='__main__':main()
