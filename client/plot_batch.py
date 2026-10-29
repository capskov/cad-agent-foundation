"""Explicit sheet sequencing over existing guarded single-PDF requests.

This is a resumable client coordinator, not an atomic native batch/print queue.
"""
import hashlib, json, re
from pathlib import Path

def run(gateway, workspace, arguments):
    required={'schema','batch_id','units','sheets'}
    if not required<=set(arguments) or set(arguments)-required-{'expected_insunits'}:
        raise ValueError('unexpected or missing batch fields')
    if arguments['schema']!='foundation-pdf-batch-v1' or arguments['units'] not in ('raw_drawing_units','metres'):
        raise ValueError('explicit PDF batch schema and units required')
    batch=arguments['batch_id']
    if not isinstance(batch,str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,40}',batch):
        raise ValueError('batch ID requires 1..40 ASCII identifier characters')
    sheets=arguments['sheets']
    if not isinstance(sheets,list) or not 1<=len(sheets)<=128:
        raise ValueError('1..128 explicitly specified sheets required')
    ids=set();files=set()
    for sheet in sheets:
        if not isinstance(sheet,dict) or set(sheet)!={'sheet_id','layout','file_name','window_settings'}:
            raise ValueError('each sheet requires sheet_id, layout, file_name, window_settings')
        identifier=sheet['sheet_id'];name=sheet['file_name']
        if not isinstance(identifier,str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,20}',identifier) or identifier in ids:
            raise ValueError('unique 1..20 character sheet ID required')
        if not isinstance(name,str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,79}\.pdf',name) or name.lower() in files:
            raise ValueError('unique simple PDF basename required')
        if not isinstance(sheet['layout'],str) or not sheet['layout'] or not isinstance(sheet['window_settings'],dict):
            raise ValueError('explicit layout and window settings required')
        ids.add(identifier);files.add(name.lower())
    canonical=json.dumps(arguments,ensure_ascii=False,sort_keys=True,separators=(',',':'))
    if len(canonical.encode('utf-8'))>131072:raise ValueError('batch semantic size exceeds 128KiB')
    folder=Path(workspace)/'plot-batches'/batch;manifest=folder/'batch.json'
    if folder.exists():
        if not manifest.is_file():raise ValueError('incomplete batch preparation; inspect original folder')
        saved=json.loads(manifest.read_text('utf-8'))
        if saved['arguments_sha256']!=hashlib.sha256(canonical.encode()).hexdigest():
            raise ValueError('batch ID belongs to different parameters')
        plans=saved['plans']
    else:
        layouts=gateway.bound['document_info']['foundation_catalog']['layout_digests'];plans=[]
        for sheet in sheets:
            layout=sheet['layout']
            if layout not in layouts:raise ValueError('layout missing: '+layout)
            plan={'schema':'foundation-drawing-plan-v1','source_role':'annotation','units':arguments['units'],
                  'steps':[{'id':'pdf','op':'plot_pdf','layout':layout,'expected_sha256':layouts[layout],
                            'file_name':sheet['file_name'],'window_settings':sheet['window_settings']}]}
            if 'expected_insunits' in arguments:plan['expected_insunits']=arguments['expected_insunits']
            plans.append({'sheet_id':sheet['sheet_id'],'request_id':batch+'--'+sheet['sheet_id'],'plan':plan})
        folder.mkdir(parents=True)
        manifest.write_text(json.dumps({'schema':'foundation-pdf-batch-journal-v1',
                            'arguments_sha256':hashlib.sha256(canonical.encode()).hexdigest(),
                            'arguments':arguments,'plans':plans},ensure_ascii=False,indent=2),encoding='utf-8')
    results=[]
    for item in plans:
        # Gateway.apply submits a new request once; existing requests are only queried.
        reply=gateway.apply(item['plan'],item['request_id'])
        passed=reply.get('verification')=='passed' and reply.get('write_fence_released') is True
        results.append({'sheet_id':item['sheet_id'],'request_id':item['request_id'],
                        'effects':reply.get('effects'),'verification':reply.get('verification'),
                        'write_fence_released':reply.get('write_fence_released'),'passed':passed})
        progress={'batch_id':batch,'scope':'client_sequential_single_PDF_requests_not_atomic_native_batch',
                  'status':'completed' if passed and len(results)==len(plans) else 'in_progress' if passed else 'stopped',
                  'completed_sheets':sum(r['passed'] for r in results),'total_sheets':len(plans),
                  'sheets':results,'next_action':None if passed else 'inspect_or_query_original_request; do_not_replay_or_force_unlock'}
        (folder/'progress.json').write_text(json.dumps(progress,ensure_ascii=False,indent=2),encoding='utf-8')
        if not passed:return progress
    return progress
