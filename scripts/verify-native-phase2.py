"""Scoped phase-two tests via packaged Foundation MCP; no direct COM/Lisp writes."""
from pathlib import Path
import argparse,datetime,hashlib,importlib.util,json,os,subprocess,sys,tempfile,uuid
ROOT=Path(__file__).resolve().parents[1]
sys.dont_write_bytecode=True
def write(p,v):p=Path(p);p.parent.mkdir(parents=True,exist_ok=True);p.write_text(json.dumps(v,ensure_ascii=False,indent=2),encoding='utf-8')
def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest().upper()
def main():
 p=argparse.ArgumentParser();p.add_argument('--package',type=Path);p.add_argument('--source',type=Path,required=True);a=p.parse_args()
 package=a.package or Path(read(ROOT/'artifacts/latest-package.json')['package']);stamp=datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S');evidence=ROOT/'artifacts/phase2'/stamp;work=Path(tempfile.mkdtemp(prefix='caf-phase2-'));evidence.mkdir(parents=True)
 write(ROOT/'artifacts/phase2-current.json',{'evidence':str(evidence),'workspace':str(work),'package':str(package)})
 entry=package/'client/foundation.py';env=dict(os.environ,PYTHONIOENCODING='utf-8',PYTHONDONTWRITEBYTECODE='1');original=sha(a.source)
 def cli(action,*args):
  c=subprocess.run([sys.executable,str(entry),action,'--workspace',str(work),*map(str,args)],env=env,capture_output=True,text=True,encoding='utf-8',timeout=120);write(evidence/(action+'-cli.json'),{'exit_code':c.returncode,'stdout':c.stdout,'stderr':c.stderr});assert c.returncode==0,c.stderr;return json.loads(c.stdout)
 cli('install');session=cli('start','--source',a.source,'--write','--headless');write(evidence/'session.json',session)
 spec=importlib.util.spec_from_file_location('phase2_package_entry',entry);F=importlib.util.module_from_spec(spec);spec.loader.exec_module(F)
 report={'passed':False,'workspace':str(work),'package':str(package),'source':str(a.source),'calls':[]}
 try:
  with F.lease(work),F.Gateway(work) as g:
   def plan(label,steps,expect=True):
    payload={'schema':'foundation-drawing-plan-v1','source_role':'test','units':'metres','steps':steps};write(evidence/(label+'-plan.json'),payload)
    result=g.apply(payload,'p2-'+label+'-'+uuid.uuid4().hex);write(evidence/(label+'-result.json'),result);report['calls'].append(label)
    job=result.get('job',{});print(label+': '+str(job.get('phase'))+'/'+str(job.get('verification')),flush=True)
    if expect:assert job.get('phase')=='completed' and job.get('verification')=='passed' and job.get('write_fence_released') is True,str(result)[:2000]
    else:assert job.get('effects')=='none' and job.get('write_fence_released') is True,str(result)[:2000];return result
    return json.loads(job['result_json'])
   def rows(label,hs):
    result=g.submit_read('dwg_get_entity_properties',{'handles':json.dumps(hs),'include_geometry':True});write(evidence/(label+'.json'),result);data=result.get('data',{}).get('entities',[]);assert len(data)==len(hs) and all(r['ok'] for r in data);return {r['handle']:r for r in data}
   catalog=g.bound['document_info']['foundation_catalog'];write(evidence/'catalog.json',catalog);assert len(catalog['plan_operations'])>=33
   assert catalog['printing']['complete'] and catalog['printing']['devices'] and catalog['printing']['plot_styles'];report['print_catalog']=catalog['printing'];report['plan_operations']=catalog['plan_operations']
   created=plan('create',[{'id':'layer','op':'layer','name':'P2-Test','color':2},{'id':'dest','op':'layer','name':'P2-Dest','color':4},
    {'id':'style','op':'text_style','name':'P2-Text','font_file':'gbenor.shx','big_font_file':'gbcbig.shx','width_factor':1},
    {'id':'dims','op':'dimension_style','name':'P2-Dim','text_style':'P2-Text','text_height':1,'arrow_size':.2,'overall_scale':1,'gap':.1,'decimal_places':3,'dimension_color':2,'extension_color':2,'text_color':7},
    {'id':'pt','op':'point','position':[0,0],'layer':'P2-Test','color':2},
    {'id':'arc','op':'arc','center':[5,0],'radius':2,'start_angle':300,'sweep_angle':120,'layer':'P2-Test','color':2},
    {'id':'ellipse','op':'ellipse','center':[12,0],'major_axis':[3,4],'radius_ratio':.5,'layer':'P2-Test','color':2},
    {'id':'rect','op':'rectangle','min':[20,0],'max':[26,4],'layer':'P2-Test','color':2},
    {'id':'open','op':'polyline','vertices':[[0,10],[4,10],[6,12]],'closed':False,'layer':'P2-Test','color':2},
    {'id':'text','op':'text','text':'底座命令测试','position':[10,10],'height':1,'rotation':0,'text_style':'P2-Text','layer':'P2-Test','color':7},
    {'id':'mtext','op':'mtext','text':'独立多行文字','position':[10,15],'height':1,'width':12,'rotation':0,'text_style':'P2-Text','layer':'P2-Test','color':7},
    {'id':'dimension','op':'aligned_dimension','start':[0,20],'end':[3,24],'position':[5,20],'dimension_style':'P2-Dim','layer':'P2-Test','color':2},
    {'id':'table','op':'table','position':[30,10],'cells':[['名称','数值'],['测试','12']],'row_height':6,'column_width':8,'text_height':1,'text_style':'P2-Text','layer':'P2-Test','color':7},
    {'id':'line','op':'line','start':[30,20],'end':[34,20],'layer':'P2-Test','color':2},
    {'id':'circle','op':'circle','center':[40,20],'radius':2,'layer':'P2-Test','color':2},
    {'id':'hatch','op':'hatch','boundary':'rect','pattern':'ANSI31','scale':1,'angle':0,'layer':'P2-Test','color':2}])
   handles=created['created_handles'];objects=created['objects'];data=rows('created-readback',handles)
   def h(name):return objects[name]['entity']
   def target(name):handle=h(name);return {'handle':handle,'expected_sha256':data[handle]['entity_digest']}
   plan('block-define',[{'id':'block','op':'block_define','name':'P2_Symbol','members':[{'kind':'line','start':[0,0],'end':[2,0]},{'kind':'circle','center':[1,1],'radius':.5},{'kind':'attribute','tag':'CODE','text':'DEFAULT','position':[0,2],'height':1}]}])
   catalog=g.call('dwg_bind_active_document',{})['document_info']['foundation_catalog'];definition=catalog['block_definitions']['P2_Symbol']
   # Hash the native JSON canonically with the shared C# verifier's number spelling;
   # catalog exposes that digest explicitly to avoid Python/.NET numeric ambiguity.
   definition_digest=catalog['block_definition_digests']['P2_Symbol']
   inserted=plan('block-insert',[{'id':'reference','op':'block_insert','name':'P2_Symbol','definition_sha256':definition_digest,'position':[35,15],'scale':1,'rotation':0,'attributes':{'CODE':'A01'},'layer':'P2-Test','color':2}]);handles+=inserted['created_handles'];objects['block']={'entity':inserted['created_handles'][0]};data=rows('block-readback',handles)
   plan('block-attributes',[{'id':'attributes','op':'set_block_attributes','targets':[target('block')],'attributes':{'CODE':'B02'}}]);data=rows('block-attributes-readback',handles)
   plan('properties',[{'id':'properties','op':'set_properties','targets':[target('pt'),target('arc'),target('ellipse'),target('rect'),target('hatch')],'layer':'P2-Dest','color':4,'linetype':'Continuous','lineweight':25}]);data=rows('property-readback',handles)
   plan('line-circle-edit',[{'id':'line','op':'set_line','targets':[target('line')],'start':[30,20],'end':[35,20]},{'id':'circle','op':'set_circle','targets':[target('circle')],'center':[40,20],'radius':2.5}]);data=rows('primitive-readback',handles)
   plan('hatch-pattern',[{'id':'pattern','op':'set_hatch_pattern','targets':[target('rect'),target('hatch')],'hatch_handle':h('hatch'),'pattern':'ANSI32','scale':.5,'angle':.25}]);data=rows('hatch-readback',handles)
   angle=plan('angular-dimension',[{'id':'angle','op':'angular_dimension','center':[30,30],'ray1':[34,30],'ray2':[30,34],'position':[33,33],'dimension_style':'P2-Dim','layer':'P2-Test','color':2}]);handles+=angle['created_handles'];data=rows('angular-readback',handles)
   plan('open-polyline',[{'id':'open','op':'set_open_polyline','targets':[target('open')],'vertices':[[0,10],[5,10,.25],[8,13],[10,13]]}]);data=rows('open-readback',handles)
   plan('text-format',[{'id':'text','op':'set_text_format','targets':[target('text')],'position':[12,10],'height':1.5,'rotation':30,'text_style':'P2-Text'},
    {'id':'mtext','op':'set_text_format','targets':[target('mtext')],'position':[12,15],'height':1.5,'rotation':15,'width':15,'text_style':'P2-Text'}]);data=rows('text-readback',handles)
   plan('dimension-text',[{'id':'dim','op':'set_dimension_text','targets':[target('dimension')],'text':'L=<>','position':[6,23],'height':1.8}]);data=rows('dimension-readback',handles)
   plan('table-cell',[{'id':'cell','op':'set_table_cell','targets':[target('table')],'row':1,'column':1,'text':'25.5'}]);data=rows('table-readback',handles)
   for label,step in [('offset-line',{'id':'offset','op':'offset','targets':[target('line')],'distance':2}),('offset-circle',{'id':'offset','op':'offset','targets':[target('circle')],'distance':1}),('mirror-line',{'id':'mirror','op':'mirror_copy','targets':[target('line')],'axis_start':[35,0],'axis_end':[35,1]})]:
    made=plan(label,[step]);handles+=made['created_handles'];data=rows(label+'-readback',handles)
   stale=target('arc');stale['expected_sha256']='0'*64
   plan('unknown-block-tag-refusal',[{'id':'badtag','op':'set_block_attributes','targets':[target('block')],'attributes':{'UNKNOWN':'bad'}}],False)
   plan('stale-refusal',[{'id':'stale','op':'set_properties','targets':[stale],'layer':'P2-Test','color':2}],False)
   plan('transaction-rollback',[{'id':'temporary','op':'line','start':[100,100],'end':[101,101],'layer':'P2-Test','color':2},{'id':'badlayer','op':'set_properties','targets':[target('arc')],'layer':'P2-DoesNotExist','color':2}],False)
   protected=rows('after-refusals',handles);assert {k:v['entity_digest'] for k,v in protected.items()}=={k:v['entity_digest'] for k,v in data.items()}
   exported=plan('export-dxf',[{'id':'dxf','op':'export_dxf','file_name':'phase2-native-exchange.dxf','expected':[target('line'),target('circle')]}]);report['dxf_output']=exported['output_file']
   if 'page_setup' in catalog['plan_operations']:
    catalog=g.call('dwg_bind_active_document',{})['document_info']['foundation_catalog'];write(evidence/'before-print-catalog.json',catalog)
    device=next(d for d in catalog['printing']['devices'] if d=='DWG To PDF.pc3')
    media=next(m for m in catalog['printing']['pc3_media'][device]['media'] if 'ISO_full_bleed_A3_(420.00_x_297.00_MM)'==m)
    plan('page-setup',[{'id':'setup','op':'page_setup','layout':'Model','expected_sha256':catalog['layout_digests']['Model'],'device':device,'media':media,'plot_style':'monochrome.ctb','rotation':0,'plot_area':'extents','scale_numerator':5,'scale_denominator':1,'center':True}])
    catalog=g.call('dwg_bind_active_document',{})['document_info']['foundation_catalog'];write(evidence/'after-print-catalog.json',catalog);assert next(l for l in catalog['printing']['layouts'] if l['name']=='Model')['preflight']['passed']
    printed=plan('plot-pdf',[{'id':'pdf','op':'plot_pdf','layout':'Model','expected_sha256':catalog['layout_digests']['Model'],'file_name':'phase2-native-sheet.pdf'}]);report['output_file']=printed['output_file']
    plan('existing-pdf-refusal',[{'id':'pdf','op':'plot_pdf','layout':'Model','expected_sha256':catalog['layout_digests']['Model'],'file_name':'phase2-native-sheet.pdf'}],False)
    assert sha(printed['output_file']['path'])==printed['output_file']['sha256'];report['layout_settings']=catalog['layout_settings']
   latest=g.call('dwg_bind_active_document',{})['document_info']['foundation_catalog']
   saved=plan('save',[{'id':'save','op':'save','expected_layouts':latest['layout_digests'],'expected':[{'handle':x,'expected_sha256':data[x]['entity_digest']} for x in handles]}]);write(evidence/'final-facts.json',data);report.update(handles=handles,objects=objects,saved_file=str(work/'drawings/work.dwg'),saved_file_sha256=saved['saved_file_sha256'],passed=True)
  assert cli('stop')['stopped'];report['owned_CAD_stopped']=True;assert sha(a.source)==original;report['source_unchanged']=True
 finally:write(evidence/'report.json',report)
 print('Phase2 MCP native test passed: '+str(evidence),flush=True)
if __name__=='__main__':main()
