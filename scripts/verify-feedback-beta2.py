"""Scoped packaged MCP checks on a separately prepared synthetic view/raster DWG.

Requires an already started owned --write session. Never uses COM or changes
an engineering DWG. Keep failed receipts; do not replay uncertain writes.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import sys

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--package',type=Path,required=True);parser.add_argument('--workspace',type=Path,required=True);parser.add_argument('--fixture',type=Path,required=True)
    a=parser.parse_args();work=a.workspace.resolve()
    spec=importlib.util.spec_from_file_location('feedback_runtime',a.package.resolve()/'client/foundation.py')
    F=importlib.util.module_from_spec(spec);sys.modules[spec.name]=F;spec.loader.exec_module(F)
    fixture=F.read(a.fixture);handles=list(fixture['handles'].values());report={'passed':False,'execution':'packaged MCP, synthetic owned drawing'}
    with F.lease(work),F.Gateway(work) as g:
        F.write(work/'acceptance-bind.json',g.bound)
        def entities(label,geometry):
            r=g.submit_read('dwg_get_entity_properties',{'handles':json.dumps(handles),'include_geometry':geometry});F.write(work/(label+'.json'),r)
            rows=r.get('data',{}).get('entities',[]);assert len(rows)==len(handles) and all(x['ok'] for x in rows),r
            return {x['handle']:x for x in rows}
        def view(label,p):
            r=g.submit_read('dwg_view',{'request_json':json.dumps(p)});F.write(work/(label+'.json'),r)
            assert 'data' in r and r['data'].get('ok') is not False,r
            return r['data']
        before=entities('before-full',True)
        light=entities('before-light',False)
        assert all(light[h]['entity_digest']==before[h]['entity_digest'] for h in handles)
        block=before[fixture['handles']['text_block']]['geometry_read']
        assert any('Fixture visible text' in str(p.get('text','')) for p in block['primitives']),block
        initial=view('view-initial',{'action':'read'})
        window=view('view-window',{'action':'window','min_x':80,'min_y':180,'max_x':170,'max_y':250,'file_name':'view-window.png'})
        assert window['native_regen_completed'] and window['captured_after_native_regen']
        center=view('view-center',{'action':'center','x':120,'y':210,'height':90,'file_name':'view-center.png'})
        assert center['native_regen_completed'] and center['wcs_xy_bounds']['min_y']==165
        check=entities('after-view',True)
        assert all(check[h]['entity_digest']==before[h]['entity_digest'] for h in handles)
        rejected=g.apply({'invalid':'plan'},'beta2-invalid-plan');F.write(work/'invalid-plan.json',rejected)
        assert rejected['status']=='rejected_before_submission' and rejected['registered'] is False,rejected
        # A completed, explicitly read-only rejection may be recorded; no unknown write is retried.
        raster=fixture['handles']['raster']
        def apply(label,path):
            r=g.apply({'schema':'foundation-drawing-plan-v1','source_role':'test','units':'raw_drawing_units','steps':[{'id':'image','op':'set_raster_path','targets':[{'handle':raster,'expected_sha256':check[raster]['entity_digest']}],'path':path}]},label)
            F.write(work/(label+'.json'),r);return r
        missing=apply('beta2-missing-image',str(work/'missing-file.png'))
        assert missing.get('effects')=='none' and missing.get('write_fence_released') is True,missing
        repaired=apply('beta2-repair-image',fixture['replacement_image'])
        assert repaired.get('effects')=='committed' and repaired.get('verification')=='passed' and repaired.get('write_fence_released') is True,repaired
        after=entities('after-repair',True)
        assert after[raster]['entity']['source_file']==fixture['replacement_image'] and after[raster]['reference_read']['native_is_loaded']
        b=dict(before[raster]['entity']);c=dict(after[raster]['entity']);b.pop('source_file');c.pop('source_file');assert b==c,(b,c)
        capture=view('image-restored',{'action':'capture','file_name':'image-restored.png'})
        bound=g.call('dwg_bind_active_document',{});F.write(work/'pre-save-bind.json',bound)
        saved=g.apply({'schema':'foundation-drawing-plan-v1','source_role':'test','units':'raw_drawing_units','steps':[{'id':'save','op':'save','expected_layouts':bound['document_info']['foundation_catalog']['layout_digests'],'expected':[{'handle':h,'expected_sha256':after[h]['entity_digest']} for h in handles]}]},'beta2-save')
        F.write(work/'save.json',saved)
        assert saved.get('effects')=='committed' and saved.get('verification')=='passed' and saved.get('write_fence_released') is True,saved
        report.update(passed=True,digest_without_geometry=True,transient_text=True,view_read=True,window_and_center_readback=True,three_native_captures=True,view_preserved_entity_digests=True,invalid_plan_rejected_before_submission=True,missing_image_rejected_without_effect=True,raster_path_repaired=True,raster_geometry_unchanged=True,save_disk_verified=True,plugin_mvid=g.bound['document_info']['loaded_plugin_mvid'])
        F.write(work/'ACCEPTANCE.json',report)
    print(json.dumps(report,ensure_ascii=False))

if __name__=='__main__':main()
