import importlib.util, json, struct, tempfile, unittest, zlib
from pathlib import Path

spec=importlib.util.spec_from_file_location('plot_style',Path(__file__).resolve().parents[1]/'client/plot_style.py')
ctb=importlib.util.module_from_spec(spec);spec.loader.exec_module(ctb)

def fixture():
    text='plot_style{\n'+''.join(' '+str(i)+'{\n  color=-1006632961\n  mode_color=-1006632961\n  color_policy=1\n  lineweight=2\n  screen=100\n }\n' for i in range(255))+'}\ncustom_lineweight_table{\n 0=0.0\n 1=0.15\n 2=0.18\n}\n\0'
    raw=text.encode();body=zlib.compress(raw)
    return ctb.HEADER+struct.pack('<III',zlib.adler32(body),len(raw),len(body))+body

class PlotStyleTests(unittest.TestCase):
    def test_black_widths_and_preservation(self):
        result=ctb.describe(ctb.configure(fixture(),{2:.15,3:.15}))
        self.assertTrue(all(c['color']==-1023410176 and c['color_policy']==5 for c in result['colors']))
        self.assertEqual([c['lineweight_mm'] for c in result['colors'][:4]],[.18,.15,.15,.18])
        self.assertIn(b'screen=100',zlib.decompress(ctb.configure(fixture(),{2:.15})[60:]))

    def test_corrupt_and_unsupported(self):
        with self.assertRaises(ValueError):ctb.describe(fixture()[:-1])
        with self.assertRaises(ValueError):ctb.configure(fixture(),{2:.16})
        with self.assertRaises(ValueError):ctb.configure(fixture(),{256:.15})

    def test_journal_backup_and_idempotency(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);path=root/'PM.ctb';path.write_bytes(fixture())
            args={'action':'configure','path':str(path),'expected_sha256':ctb.digest(fixture()),'all_colors_black':True,'lineweights_mm':{'2':.15,'3':.15},'request_id':'test-01'}
            first=ctb.run(root,args);self.assertEqual(ctb.run(root,args),first)
            self.assertEqual(Path(first['backup']).read_bytes(),fixture())
            args['request_id']='test-02'
            with self.assertRaises(ValueError):ctb.run(root,args)
            journal=root/'plot-style-requests'/'pending';journal.mkdir()
            args['request_id']='pending';(journal/'request.json').write_text(json.dumps(args,sort_keys=True,separators=(',',':')))
            self.assertEqual(ctb.run(root,args)['effects'],'unknown')

if __name__=='__main__':unittest.main()
