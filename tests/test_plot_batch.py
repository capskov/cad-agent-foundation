import importlib.util, tempfile, unittest
from pathlib import Path
spec=importlib.util.spec_from_file_location('plot_batch',Path(__file__).resolve().parents[1]/'client/plot_batch.py')
batch=importlib.util.module_from_spec(spec);spec.loader.exec_module(batch)
class Gateway:
    bound={'document_info':{'foundation_catalog':{'layout_digests':{'Model':'A'*64}}}}
    def __init__(self,stop=None):self.stop=stop;self.calls=[];self.requests={}
    def apply(self,plan,request):
        self.calls.append(request)
        if request in self.requests:return self.requests[request]
        reply={'effects':'unknown','verification':'unknown','write_fence_released':False} if request==self.stop else {'effects':'committed','verification':'passed','write_fence_released':True}
        self.requests[request]=reply;return reply
def arguments():return {'schema':'foundation-pdf-batch-v1','batch_id':'demo','units':'raw_drawing_units','expected_insunits':4,'sheets':[{'sheet_id':str(i),'layout':'Model','file_name':f'sheet-{i}.pdf','window_settings':{}} for i in range(3)]}
class BatchTests(unittest.TestCase):
    def test_all_sheets_and_stable_original_plans(self):
        with tempfile.TemporaryDirectory() as folder:
            g=Gateway();a=arguments();result=batch.run(g,folder,a)
            self.assertEqual(result['completed_sheets'],3)
            g.bound={'document_info':{'foundation_catalog':{'layout_digests':{'Model':'B'*64}}}}
            self.assertEqual(batch.run(g,folder,a)['status'],'completed')
            self.assertEqual(len(g.requests),3)
            a['sheets'][0]['file_name']='other.pdf'
            with self.assertRaises(ValueError):batch.run(g,folder,a)
    def test_unknown_stops_before_later_sheets(self):
        with tempfile.TemporaryDirectory() as folder:
            g=Gateway('demo--1');r=batch.run(g,folder,arguments())
            self.assertEqual(r['status'],'stopped');self.assertEqual(g.calls,['demo--0','demo--1'])
            self.assertEqual(batch.run(g,folder,arguments())['status'],'stopped')
            self.assertNotIn('demo--2',g.calls)
    def test_duplicate_output_refused_before_submission(self):
        with tempfile.TemporaryDirectory() as folder:
            g=Gateway();a=arguments();a['sheets'][1]['file_name']=a['sheets'][0]['file_name'].upper()
            with self.assertRaises(ValueError):batch.run(g,folder,a)
            self.assertEqual(g.calls,[])
if __name__=='__main__':unittest.main()
