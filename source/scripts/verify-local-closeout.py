"""Read-only integrity/evidence audit; does not claim a fresh native CAD run."""
import datetime, hashlib, json, sqlite3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/local-1.2.0-20261015'
def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))
def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()

delivery = read(OUT / 'DELIVERY.json')
package = Path(delivery['package'])
manifest = read(package / 'manifest.json')
assert manifest['version'] == '1.2.0-local.1'
assert sha(Path(delivery['archive'])) == delivery['sha256']
for name, digest in manifest['files'].items():
    assert sha(package / name) == digest, name
installed = Path(delivery['installed_plugin']).parent
for name, digest in manifest['files'].items():
    if name.startswith('plugin/'):
        assert sha(installed / name[len('plugin/'):]) == digest, name
record = read(Path(delivery['ready_workspace']) / 'installation.json')
assert Path(record['package']) == package
assert record['plugin_mvid'] == manifest['plugin_mvid']
assert not (Path(delivery['ready_workspace']) / 'session.json').exists()
evidence = []
for relative in ['phase2/20261015-121929/report.json',
                 'phase2/20261015-121929/reopen/summary.json',
                 'native-next/20261015-122146/summary.json',
                 'native-next/20261015-122146/reopen/summary.json',
                 'native-next/20261015-122146/guards/summary.json']:
    path = ROOT / 'artifacts' / relative
    assert read(path)['passed'] is True, relative
    evidence.append({'path': relative, 'sha256': sha(path)})
incident = read(ROOT / 'artifacts/phase2/20261015-115921/original-request-status.json')
assert incident['effects'] == 'committed' and incident['verification'] == 'passed'
assert incident['write_fence_released'] is True
fences = []
for work in [read(ROOT / 'artifacts/phase2/20261015-121929/report.json')['workspace'],
             read(ROOT / 'artifacts/native-next/20261015-122146/summary.json')['workspace'],
             str(Path.home() / 'AppData/Local/Temp/caf-phase2-m77_cbde')]:
    for suffix, table in [('native/Dwg/execution/plugin-execution.sqlite', 'plugin_write_fences'),
                          ('server/Dwg/request-ledger.sqlite', 'write_fences')]:
        path = Path(work) / suffix
        with sqlite3.connect(path.as_uri() + '?mode=ro', uri=True) as db:
            count = db.execute('SELECT COUNT(*) FROM ' + table).fetchone()[0]
        assert count == 0, str(path)
        fences.append({'journal': str(path), 'held': count})
report = {'passed': True, 'checked_at_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
          'validation_kind': 'read-only package, installed dependencies and prior native evidence audit',
          'version': manifest['version'], 'package_files_checked': len(manifest['files']),
          'archive_sha256': delivery['sha256'], 'plugin_mvid': manifest['plugin_mvid'],
          'evidence': evidence, 'fences': fences, 'native_CAD_rerun': False,
          'incident_root_cause_fixed': False, 'entry': str(ROOT.parent / 'foundation-local.cmd')}
(OUT / 'closeout-20261016.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'passed': True, 'files_checked': len(manifest['files']), 'held_fences': 0}))
