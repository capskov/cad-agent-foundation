"""Bounded local CTB configuration; no DWG mutation or printer submission.

Supports the compressed CTB v2 text format only. Unknown structures fail closed.
"""
import hashlib, json, os, re, struct, zlib
from pathlib import Path

HEADER = b'PIAFILEVERSION_2.0,CTBVER1,compress\r\npmzlibcodec'
ROWS = re.compile(r'(?m)^ (\d+)\{\r?\n(.*?)^ \}', re.S)

def digest(data):
    return hashlib.sha256(data).hexdigest().upper()

def unpack(data):
    if len(data) > 2_000_000 or not data.startswith(HEADER):
        raise ValueError('unsupported CTB header or size')
    checksum, plain_size, compressed_size = struct.unpack('<III', data[48:60])
    if compressed_size != len(data)-60 or checksum != zlib.adler32(data[60:]):
        raise ValueError('CTB compressed size/checksum mismatch')
    dec = zlib.decompressobj()
    plain = dec.decompress(data[60:], 2_000_001)
    if len(plain)>2_000_000 or not dec.eof or dec.unused_data or len(plain)!=plain_size:
        raise ValueError('CTB expanded size/stream mismatch')
    text = plain.decode('utf-8')
    rows = list(ROWS.finditer(text))
    if [int(m[1]) for m in rows] != list(range(255)):
        raise ValueError('exactly 255 ordered color styles required')
    return text, rows

def field(body, key):
    values = re.findall(r'(?m)^  '+re.escape(key)+r'=([^\r\n]+)', body)
    if len(values)!=1:
        raise ValueError('missing or duplicate CTB field: '+key)
    return values[0]

def replace(body, key, value):
    field(body, key)
    return re.sub(r'(?m)^(  '+re.escape(key)+r'=)[^\r\n]+', lambda m:m[1]+str(value), body)

def describe(data):
    text, rows = unpack(data)
    weights_match = re.search(r'custom_lineweight_table\{\s*(.*?)\}', text, re.S)
    if not weights_match:
        raise ValueError('custom lineweight table required')
    weights = {int(k):float(v) for k,v in re.findall(r'(\d+)=([0-9.]+)', weights_match[1])}
    colors=[]
    for m in rows:
        b=m[2]; index=int(field(b,'lineweight'))
        colors.append({'aci':int(m[1])+1,'color':int(field(b,'color')),
                       'mode_color':int(field(b,'mode_color')) if '  mode_color=' in b else None,
                       'color_policy':int(field(b,'color_policy')),
                       'lineweight_index':index,'lineweight_mm':weights.get(index) if index else None})
    return {'sha256':digest(data),'colors':colors,'lineweights':weights}

def configure(data, overrides):
    text, rows=unpack(data); before=describe(data)
    indices={}
    for aci, mm in overrides.items():
        if not isinstance(aci,int) or not 1<=aci<=255 or not isinstance(mm,(int,float)) or not 0<mm<=2.11:
            raise ValueError('ACI 1..255 and positive lineweight <=2.11 mm required')
        matches=[i for i,w in before['lineweights'].items() if abs(w-mm)<1e-6 and i!=0]
        if len(matches)!=1:
            raise ValueError('requested width must already exist uniquely in source CTB')
        indices[aci]=matches[0]
    def change(m):
        b=m[2]
        for key,value in [('color',-1023410176),('color_policy',5)]:
            b=replace(b,key,value)
        if '  mode_color=' in b:b=replace(b,'mode_color',-1023410176)
        if int(m[1])+1 in indices:b=replace(b,'lineweight',indices[int(m[1])+1])
        return ' '+m[1]+'{\n'+b+' }'
    updated=ROWS.sub(change,text)
    raw=updated.encode('utf-8'); packed=zlib.compress(raw)
    result=HEADER+struct.pack('<III',zlib.adler32(packed),len(raw),len(packed))+packed
    after=describe(result)
    for a,b in zip(before['colors'],after['colors']):
        if b['color']!=-1023410176 or b['color_policy']!=5:
            raise ValueError('black color verification failed')
        if a['aci'] in overrides:
            if abs(b['lineweight_mm']-overrides[a['aci']])>1e-6:raise ValueError('width verification failed')
        elif a['lineweight_index']!=b['lineweight_index']:raise ValueError('unrequested width changed')
    return result

def run(workspace, args):
    allowed={'action','path','expected_sha256','all_colors_black','lineweights_mm','request_id'}
    if set(args)-allowed:raise ValueError('unexpected plot-style argument')
    path=Path(args['path']).resolve()
    if path.suffix.lower()!='.ctb' or path.is_symlink():raise ValueError('regular CTB path required')
    data=path.read_bytes()
    if args['action']=='read':return dict(describe(data),path=str(path),effects='none')
    if args['action']!='configure' or args.get('all_colors_black') is not True:
        raise ValueError('configure requires explicit all_colors_black=true')
    request=args['request_id']
    if not re.fullmatch(r'[A-Za-z0-9_-]{1,80}',request):raise ValueError('invalid request ID')
    journal=Path(workspace)/'plot-style-requests'/request
    canonical=json.dumps(args,sort_keys=True,separators=(',',':'))
    if journal.exists():
        if (journal/'request.json').read_text('utf-8')!=canonical:raise ValueError('request ID parameters differ')
        if (journal/'result.json').exists():return json.loads((journal/'result.json').read_text('utf-8'))
        return {'effects':'unknown','next_action':'inspect original request and target; do not replay','path':str(path)}
    if digest(data)!=args['expected_sha256']:raise ValueError('CTB changed since inspection')
    updates={int(k):v for k,v in args['lineweights_mm'].items()}
    output=configure(data,updates)
    journal.mkdir(parents=True)
    (journal/'request.json').write_text(canonical,encoding='utf-8')
    (journal/'before.ctb').write_bytes(data)
    (journal/'after.ctb').write_bytes(output)
    if path.read_bytes()!=data:raise ValueError('CTB changed before write; no write performed')
    temp=path.with_name(path.name+'.'+request+'.tmp')
    with temp.open('xb') as stream:stream.write(output)
    if path.read_bytes()!=data:raise ValueError('CTB changed before replacement; inspect temporary file')
    os.replace(temp,path)
    actual=path.read_bytes()
    if actual!=output:raise ValueError('CTB readback mismatch; query original files, do not replay')
    result={'effects':'committed','verification':'passed','scope':'local_CTB_file_only',
            'path':str(path),'before_sha256':digest(data),'sha256':digest(actual),
            'all_255_colors_black':True,'lineweights_mm':args['lineweights_mm'],
            'other_lineweights_preserved':True,'backup':str(journal/'before.ctb')}
    (journal/'result.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    return result
