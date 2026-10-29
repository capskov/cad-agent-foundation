"""One representative native capability batch against an owned synthetic mm DWG.

Fixture preparation and install/start are separate; this script never replays an
existing request or releases a fence. Native failures stop the batch.
"""
import argparse, importlib.util, json, sys
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--package',type=Path,required=True);p.add_argument('--workspace',type=Path,required=True);a=p.parse_args()
package=a.package.resolve();work=a.workspace.resolve();sys.path.insert(0,str(package/'client'))
spec=importlib.util.spec_from_file_location('batch1_foundation',package/'client/foundation.py');f=importlib.util.module_from_spec(spec);spec.loader.exec_module(f)
evidence=work/'capability-batch1';evidence.mkdir(exist_ok=True)
def dump(name,value):(evidence/(name+'.json')).write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8')
with f.lease(work),f.Gateway(work) as g:
    dump('binding',g.bound);catalog=g.bound['document_info']['foundation_catalog']
    assert 'set_arc' in catalog['plan_operations'] and 'set_ellipse' in catalog['plan_operations']
    assert 'transient_explicit_window' in catalog['print_feature_status']['M3']
    def apply(label,steps):
        request='capbatch1-'+label;prior=work/'requests'/(request+'.json')
        plan=json.loads(prior.read_text('utf-8'))['plan'] if prior.exists() else {'schema':'foundation-drawing-plan-v1','source_role':'test','units':'raw_drawing_units','expected_insunits':4,'steps':steps}
        reply=g.apply(plan,request);dump(label,reply)
        print(label,reply.get('effects'),reply.get('verification'),reply.get('write_fence_released'),flush=True)
        if reply.get('verification')!='passed' or reply.get('write_fence_released') is not True:
            raise RuntimeError('batch stopped; inspect original request '+request)
        return json.loads(reply['job']['result_json'])
    def read(handles):
        reply=g.submit_read('dwg_get_entity_properties',{'handles':json.dumps(handles),'include_geometry':True});rows=reply['data']['entities'];assert len(rows)==len(handles) and all(r['ok'] for r in rows)
        return {r['handle']:r for r in rows}
    created=apply('create',[
        {'id':'arc','op':'arc','center':[120,140],'radius':40,'start_angle':20,'sweep_angle':130,'layer':'0','color':2},
        {'id':'ellipse','op':'ellipse','center':[260,140],'major_axis':[60,0],'radius_ratio':.5,'layer':'0','color':3},
        {'id':'circle','op':'circle','center':[600,140],'radius':60,'layer':'0','color':2}])
    handles={name:created['objects'][name]['entity'] for name in ['arc','ellipse','circle']};rows=read(list(handles.values()))
    def target(name):return [{'handle':handles[name],'expected_sha256':rows[handles[name]]['entity_digest']}]
    apply('curves',[
        {'id':'arc','op':'set_arc','targets':target('arc'),'center':[120,140],'radius':55,'start_angle':300,'sweep_angle':120},
        {'id':'ellipse','op':'set_ellipse','targets':target('ellipse'),'center':[275,150],'major_axis':[45,25],'radius_ratio':.65}])
    rows=read(list(handles.values()));dump('edited-curves',rows)
    apply('dxf-mm',[{'id':'dxf','op':'export_dxf','file_name':'capbatch1-mm.dxf','expected':[{'handle':h,'expected_sha256':r['entity_digest']} for h,r in rows.items()]}])
    import plot_batch
    base={'device':'AutoCAD PDF (General Documentation).pc3','media':'ISO_full_bleed_A3_(420.00_x_297.00_MM)','plot_style':'monochrome.ctb','rotation':0,'scale_numerator':1,'scale_denominator':1,'center':True}
    args={'schema':'foundation-pdf-batch-v1','batch_id':'capbatch1-pdf','units':'raw_drawing_units','expected_insunits':4,'sheets':[{'sheet_id':str(i+1),'layout':'Model','file_name':f'capbatch1-sheet-{i+1}.pdf','window_settings':dict(base,min=[420*i,0],max=[420*(i+1),297])} for i in range(2)]}
    batch=plot_batch.run(g,work,args);dump('batch-pdf',batch);assert batch['status']=='completed'
    again=plot_batch.run(g,work,args);assert again['status']=='completed';dump('batch-resume-original-requests',again)
    rows=read(list(handles.values()))
    apply('save',[{'id':'save','op':'save','expected_layouts':catalog['layout_digests'],'expected':[{'handle':h,'expected_sha256':r['entity_digest']} for h,r in rows.items()]}])
    dump('acceptance',{'passed':True,'scope':'synthetic_mm_curves_DXF_two_explicit_PDFs_original_request_resume_save','visual_review':'pending','handles':handles})
    print('capability batch1 native operations passed; render PDFs for visual review',flush=True)
