"""Scoped AutoCAD2026 packaged-MCP verification; private real fixture plus synthetic source.

Prepare synthetic.dwg/fixture.json with tests/GeometryNativeFixture separately.
This runner never creates/modifies engineering entities or saves either drawing.
"""
from pathlib import Path
import argparse,importlib.util,sys,json,subprocess,math,collections

def main():
    p=argparse.ArgumentParser();p.add_argument('--package',type=Path,required=True);p.add_argument('--source',type=Path,required=True);p.add_argument('--fixture',type=Path,required=True);p.add_argument('--evidence',type=Path,required=True);a=p.parse_args()
    entry=a.package.resolve()/'client/foundation.py';spec=importlib.util.spec_from_file_location('geometry_entry',entry);F=importlib.util.module_from_spec(spec);sys.modules[spec.name]=F;spec.loader.exec_module(F)
    out=a.evidence.resolve();out.mkdir(parents=True,exist_ok=False);report={'passed':False,'package':str(a.package.resolve()),'plugin_mvid':F.read(a.package/'manifest.json')['plugin_mvid']}
    def cli(work,action,*args):
        r=subprocess.run([sys.executable,str(entry),action,'--workspace',str(work),*map(str,args)],capture_output=True,text=True,encoding='utf-8',timeout=115)
        F.write(work/(action+'-cli.json'),{'exit_code':r.returncode,'stdout':r.stdout,'stderr':r.stderr});assert r.returncode==0,r.stderr;return json.loads(r.stdout)
    def run(name,source,body):
        work=out/name;original=F.sha(source);cli(work,'install');session=cli(work,'start','--source',source,'--headless')
        with F.lease(work),F.Gateway(work) as g:
            F.write(work/'bind-before.json',g.bound);assert g.bound['document_info']['dbmod']==0
            def read(handle):
                offset=0;all_primitives=[];identity=None;paths=[];pages=[]
                while True:
                    result=g.submit_read('dwg_get_entity_properties',{'handles':json.dumps([handle]),'include_geometry':True,'geometry_offset':offset});F.write(work/'reads'/(handle+'-'+str(offset)+'.json'),result)
                    rows=result.get('data',{}).get('entities',[]);assert len(rows)==1 and rows[0]['ok'],str(result)[:400];row=rows[0];geo=row['geometry_read']
                    current=(row['entity_digest'],geo['document_session_id'],geo['drawing_revision']);assert identity is None or current==identity,'Changed snapshot';identity=current
                    all_primitives+=geo['primitives'];paths.extend(x['path'] for x in geo['primitives']);pages.append(geo)
                    assert geo['offset']==offset
                    if not geo['has_more']:break
                    assert offset<geo['next_offset']<=4096;offset=geo['next_offset']
                assert len(paths)==len(set(paths));row['geometry_read']={**geo,'primitives':all_primitives,'pages':len(pages),'issues':pages[0]['issues']}
                F.write(work/'assembled'/(handle+'.json'),row);return row
            result=body(g,read,work)
            after=g.call('dwg_bind_active_document',{});F.write(work/'bind-after.json',after);assert after['document_info']['dbmod']==0
        assert F.sha(source)==original and F.sha(Path(session['drawing']))==original
        closed=cli(work,'stop');assert closed['stopped'] and not any(closed['fences'].values())
        return {**result,'source_unchanged':True,'working_file_unchanged':True,'dbmod':0,'owned_session_stopped':True,'fences':closed['fences']}
    def real(g,read,work):
        query=g.submit_read('dwg_query_entities',{'entity_type':'ProxyEntity','include_geometry':False,'limit':5000});F.write(work/'inventory.json',query);rows=query['data']['entities'];assert len(rows)==39 and not query['data']['has_more']
        states=collections.Counter();types=collections.Counter();axes=[]
        for row in rows:
            value=read(row['handle']);geo=value['geometry_read'];states[geo['status']]+=1;types.update(x['type'] for x in geo['primitives'])
            if row['handle'] in {'165D','165E','2AA4'}:
                primitives=geo['primitives'];expected={'165D':366,'165E':5,'2AA4':93};assert len(primitives)==expected[row['handle']]
                # Explicit Agent interpretation for this fixture: native arc path 0/92 in 165D runs backwards.
                chain=[]
                for primitive in primitives:
                    reverse=row['handle']=='165D' and primitive['path']=='0/92'
                    chain.append({'primitive':primitive,'reverse':reverse})
                gaps=[math.dist(a['primitive']['start'] if a['reverse'] else a['primitive']['end'],b['primitive']['end'] if b['reverse'] else b['primitive']['start']) for a,b in zip(chain,chain[1:])]
                assert max(gaps,default=0)<1e-6
                axis={'handle':row['handle'],'segments':len(chain),'length_raw':sum(x['length'] for x in primitives),'max_join_gap_raw':max(gaps,default=0),'reversed_paths':[x['primitive']['path'] for x in chain if x['reverse']],'chain':chain}
                F.write(work/('continuous-axis-'+row['handle']+'.json'),axis);axes.append({k:v for k,v in axis.items() if k!='chain'})
            print('real',row['handle'],geo['status'],len(geo['primitives']),flush=True)
        assert states=={'partial':6,'complete':4,'unavailable':29},states
        frame=read('4EC4')['geometry_read']['primitives'][0];assert frame['closed'] and len(frame['vertices'])==4
        return {'proxy_objects':len(rows),'states':dict(states),'primitive_types':dict(types),'axes':axes,'reference_rectangle_complete':True,'actual_formal_frame_and_subframes':'not_identified_or_verified'}
    def synthetic(g,read,work):
        fixture=F.read(a.fixture/'fixture.json');values={name:read(handle)['geometry_read'] for name,handle in fixture['handles'].items()}
        for name,geo in values.items():assert geo['status']=='complete' and not geo['issues'],(name,geo['issues'])
        prim=lambda name:values[name]['primitives'][0]
        assert prim('line')['start']==[10,20,3] and prim('line')['end']==[13,24,3] and prim('line')['length']==5
        assert prim('circle')['center']==[15,25,4] and prim('circle')['radius']==7
        arc=prim('arc_negative_normal');assert arc['normal']==[0,0,-1] and arc['radius']==10 and abs(arc['length']-18)<1e-9
        ellipse=prim('ellipse');assert ellipse['major_axis']==[8,0,0] and ellipse['minor_axis']==[0,4,0]
        poly=prim('tilted_polyline');assert poly['closed'] and poly['vertices'][0]['bulge']==.25
        n=math.sqrt(.5);assert math.dist(poly['vertices'][0]['point'],[-1,5*n,9*n])<1e-9
        assert prim('polyline3d')['vertices']==[[1,2,3],[4,5,6],[7,8,9]]
        spline=prim('spline');assert spline['degree']==3 and spline['rational'] and spline['weights']==[1,2,2,1] and spline['knots']==[0,0,0,0,1,1,1,1] and spline['control_points']==[[0,0,1],[2,4,1],[4,4,1],[6,0,1]]
        frames=values['transformed_frame']['primitives'];assert len(frames)==2
        for frame,inset in zip(frames,[0,10]):
            expected=[[inset,inset],[420-inset,inset],[420-inset,297-inset],[inset,297-inset]]
            assert frame['closed'] and len(frame['vertices'])==4
            for point,(x,y) in zip(frame['vertices'],expected):assert math.dist(point['point'],[1000+2*x*math.cos(math.pi/6)-y,2000+x+2*y*math.cos(math.pi/6),0])<1e-9
        large=values['paged_block'];assert len(large['primitives'])==200 and large['pages']==4
        assert large['primitives'][0]['start']==[3000,4000,0] and large['primitives'][-1]['end']==[3199,4010,0]
        mixed=g.submit_read('dwg_get_entity_properties',{'handles':json.dumps(['FFFFFFFFFFFF',fixture['handles']['line']]),'include_geometry':True});F.write(work/'mixed-handles.json',mixed);rows=mixed['data']['entities'];assert not rows[0]['ok'] and rows[1]['ok']
        plain=g.submit_read('dwg_get_entity_properties',{'handles':json.dumps([fixture['handles']['line']]),'include_geometry':False});F.write(work/'plain-read.json',plain);assert plain['data']['entities'][0]['geometry_read'] is None
        return {'known_coordinate_cases':len(values),'native_frame_outer_inner_complete':True,'paged_block_segments':200,'pages':4,'mixed_bad_handle_isolated':True,'geometry_opt_out':True}
    try:
        report['real']=run('real',a.source.resolve(),real);report['synthetic']=run('synthetic',(a.fixture/'synthetic.dwg').resolve(),synthetic);report['passed']=True
    finally:F.write(out/'report.json',report)
    print(json.dumps(report,ensure_ascii=False,indent=2))
if __name__=='__main__':main()
