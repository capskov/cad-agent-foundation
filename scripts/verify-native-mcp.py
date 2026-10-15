"""Verify the packaged MCP wrapper and a new native operation on an owned copy."""
from pathlib import Path
import argparse,importlib.util,json,os,subprocess,sys,tempfile,time,uuid
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('next_mcp_support',ROOT/'scripts/readonly-entry.py');E=importlib.util.module_from_spec(spec);sys.modules[spec.name]=E;spec.loader.exec_module(E)
def main():
 p=argparse.ArgumentParser();p.add_argument('--flow',type=Path,required=True);p.add_argument('--package',type=Path);p.add_argument('--mcpb-manifest',type=Path);a=p.parse_args()
 flow=json.loads((a.flow/'summary.json').read_text(encoding='utf-8'));assert flow['passed'];package=a.package or Path(flow['package']);entry=package/'client/foundation.py';work=Path(tempfile.mkdtemp(prefix='caf-mcp-next-'));evidence=a.flow/'mcp';evidence.mkdir(exist_ok=False);env=dict(os.environ,PYTHONIOENCODING='utf-8',PYTHONDONTWRITEBYTECODE='1')
 def cli(action,*extra):
  c=subprocess.run([sys.executable,str(entry),action,'--workspace',str(work),*map(str,extra)],env=env,capture_output=True,text=True,encoding='utf-8',timeout=115);assert c.returncode==0,c.stderr[:500];return json.loads(c.stdout)
 cli('install');session=cli('start','--source',flow['saved_file'],'--write','--headless');report={'passed':False,'workspace':str(work),'pid':session['pid'],'calls':[]}
 command=[sys.executable,str(entry),'mcp','--workspace',str(work)]
 if a.mcpb_manifest:
  metadata=json.loads(a.mcpb_manifest.read_text(encoding='utf-8'));config=metadata['server']['mcp_config'];variables={'${__dirname}':str(a.mcpb_manifest.resolve().parent),'${user_config.python_command}':sys.executable,'${user_config.workspace}':str(work)}
  def expand(value):
   for key,text in variables.items():value=value.replace(key,text)
   assert '${' not in value,'Unresolved MCPB variable';return value
  command=[expand(config['command']),*[expand(v) for v in config['args']]];env.update({k:expand(v) for k,v in config.get('env',{}).items()});report['mcpb_manifest_resolved']=True
 log=(evidence/'wrapper.log').open('wb');process=subprocess.Popen(command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=log,text=True,encoding='utf-8',env=env,bufsize=1);client=E.McpStdioClient(process)
 def call(name,args):report['calls'].append(name);return E.unwrap_application_result(E.parse_mcp_tool_result(client.rpc('tools/call',{'name':name,'arguments':args},30)))
 def poll(context,request,write=False):
  deadline=time.monotonic()+35
  while True:
   result=call('dwg_job_get',{'context_id':context,'request_id':request,'wait_ms':0});job=result.get('job',{})
   if job.get('phase') in {'failed','rejected','cancelled','unknown'}:raise RuntimeError('MCP original job did not pass: '+str(E.redact(job))[:700])
   if job.get('phase')=='completed' and (not write or job.get('verification')=='passed' and job.get('write_fence_released') is True):return result
   assert time.monotonic()<deadline,'Original request remains pending; never replay';time.sleep(.2)
 try:
  client.initialize(30);names={t['name'] for t in client.tools_list(30)['tools']};assert names==(set(E.CATALOG_ALLOWLIST)-{'dwg_inspect_lisp'})|{'dwg_apply_plan'};report['catalog']=sorted(names)
  bound=call('dwg_bind_active_document',{});assert E.check_binding_identity(bound['binding_snapshot'],session['pid'],session['started'],session['drawing'])[0]=='identity_matched';assert bound['document_info']['loaded_plugin_mvid']==session['installation']['plugin_mvid'];assert len(bound['document_info']['foundation_catalog']['plan_operations'])==25
  context=bound['context_id'];request='mcp-read-'+uuid.uuid4().hex;call('dwg_get_entity_properties',{'context_id':context,'request_id':request,'handles':json.dumps(flow['handles']),'include_geometry':True});result=poll(context,request);rows=json.loads(result['job']['result_json'])['entities'];assert len(rows)==len(flow['handles']) and all(r['ok'] for r in rows);report['original_objects_read']=len(rows)
  layer_request='mcp-layers-'+uuid.uuid4().hex;call('dwg_list_layers',{'context_id':context,'request_id':layer_request});layer_result=poll(context,layer_request);assert json.loads(layer_result['job']['result_json']);report['layer_list_passed']=True
  plan={'schema':'foundation-drawing-plan-v1','source_role':'test','units':'metres','steps':[{'id':'mcp_dimension','op':'aligned_dimension','start':[50,0],'end':[53,4],'position':[56,0],'dimension_style':'CAF-Dim','layer':'CAF-Test','color':2}]};request='mcp-plan-'+uuid.uuid4().hex
  call('dwg_apply_plan',{'context_id':context,'request_id':request,'plan_json':json.dumps(plan,ensure_ascii=False)});result=poll(context,request,True);receipt=json.loads(result['job']['result_json']);report['native_write_receipt']=receipt;report['new_handles']=receipt['created_handles'];assert len(report['new_handles'])==1;report['passed']=True
 finally:
  report['shutdown']=client.close();log.close();(evidence/'report.json').write_text(json.dumps(E.redact(report),ensure_ascii=False,indent=2),encoding='utf-8')
 saved=cli('save','--handles',','.join(flow['handles']+report['new_handles']));assert saved['verification']=='passed' and saved['write_fence_released'];assert cli('stop')['stopped'];(evidence/'summary.json').write_text(json.dumps({'passed':True,'catalog_tools':len(names),'plan_operations':25,'objects_read':len(rows),'new_native_dimension':True,'layer_list':True,'saved_and_stopped':True,'workspace':str(work)},indent=2),encoding='utf-8')
 print('Packaged MCP: eight-tool profile, native read/layer list/new dimension/save/stop passed.',flush=True)
if __name__=='__main__':main()
