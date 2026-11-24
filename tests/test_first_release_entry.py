"""Focused protection tests for the new local entry; no CAD acceptance claims."""
import importlib.util,json,tempfile,unittest
from pathlib import Path
from unittest.mock import patch
spec=importlib.util.spec_from_file_location('first_release_entry',Path(__file__).resolve().parents[1]/'client/foundation.py')
F=importlib.util.module_from_spec(spec);spec.loader.exec_module(F)
class EntrySafetyTests(unittest.TestCase):
    def setUp(self):self.temp=tempfile.TemporaryDirectory();self.work=Path(self.temp.name)
    def tearDown(self):self.temp.cleanup()
    def gateway(self):
        g=F.Gateway.__new__(F.Gateway);g.work=self.work;g.context='bound-context';g.session={'write_enabled':True};return g
    def test_uncertain_submission_only_queries_original(self):
        g=self.gateway();calls=[]
        def call(name,args):calls.append((name,args));raise RuntimeError('reply lost')
        g.call=call
        def query(request):
            self.assertEqual(request['context_id'],'bound-context');self.assertTrue((self.work/'requests'/('r1.json')).exists());return {'request':request['request_id'],'state':'unknown'}
        g.query=query;self.assertEqual(g.apply({'example':'fixed'},'r1')['request'],'r1');self.assertEqual(len(calls),1)
        g.apply({'example':'fixed'},'r1');self.assertEqual(len(calls),1)
    def test_request_id_cannot_change_plan(self):
        g=self.gateway();g.call=lambda *x:{};g.query=lambda r:{};g.apply({'value':1},'r1')
        with self.assertRaises(RuntimeError):g.apply({'value':2},'r1')
    def test_duplicate_json_fails_before_submission(self):
        path=self.work/'plan.json';path.write_text('{"steps":[],"steps":[1]}',encoding='utf-8')
        with self.assertRaises(RuntimeError):F.read(path)
    def test_read_only_cannot_submit(self):
        g=self.gateway();g.session['write_enabled']=False
        with self.assertRaises(RuntimeError):g.apply({'anything':1})
        self.assertFalse((self.work/'requests').exists())
    def test_query_batch_stops_on_first_unfinished_read(self):
        g=self.gateway()
        with patch.object(g,'submit_read',side_effect=[
            {'job':{'phase':'completed'}},{'pending':True}
        ]) as submit:
            output=g.query_batch([{'limit':1},{'limit':2},{'limit':3}])
        self.assertEqual((output['requested'],output['completed'],submit.call_count),(3,1,2))
        with self.assertRaises(RuntimeError):
            g.query_batch([{}]*17)

    def test_startup_status_detects_mismatched_discovery(self):
        native=self.work/'native'
        F.write(self.work/'session.json',{'pid':123,'started':'birth','native_root':str(native)})
        endpoint=native/'Dwg/acad-2026.json'
        F.write(endpoint,{'pid':456,'acad_year':2026,'process_name':'acad'})
        with patch.object(F,'process_birth',return_value='birth'):
            self.assertEqual(F.startup_status(self.work)['state'],'discovery_identity_mismatch')
            F.write(endpoint,{'pid':123,'acad_year':2026,'process_name':'acad'})
            self.assertEqual(F.startup_status(self.work)['state'],'ready')
    def test_workspace_lease_excludes_other_session(self):
        with F.lease(self.work):
            with self.assertRaises(RuntimeError):
                with F.lease(self.work):pass
        with F.lease(self.work):pass
    def test_transient_snapshot_failure_only_requeries_same_request(self):
        g=self.gateway();calls=[]
        def call(name,args):
            calls.append((name,args))
            if name=='dwg_apply_plan':return {'queued':True}
            if len(calls)==2:raise RuntimeError('inconsistent snapshot')
            return {'pending':False,'found':True,'job':{'phase':'completed','verification':'passed','write_fence_released':True}}
        g.call=call
        result=g.apply({'test':'snapshot'},'snapshot-original')
        self.assertEqual(result['job']['phase'],'completed')
        self.assertEqual([name for name,_ in calls],['dwg_apply_plan','dwg_job_get','dwg_job_get'])
        self.assertTrue(all(args['request_id']=='snapshot-original' for _,args in calls))
        self.assertEqual(F.read(self.work/'requests/snapshot-original.json')['query_failures'],1)
    def test_query_failure_deadline_keeps_unknown_and_original_identity(self):
        g=self.gateway();calls=[]
        def call(name,args):calls.append((name,args));raise RuntimeError('query unavailable')
        g.call=call
        result=g.query({'request_id':'original','context_id':'original-context'},wait=0)
        self.assertEqual(result['status'],'unknown');self.assertFalse(result['write_fence_released'])
        self.assertEqual(calls[0][0],'dwg_job_get');self.assertEqual(calls[0][1]['context_id'],'original-context')
        self.assertEqual(len(calls),1)
if __name__=='__main__':unittest.main()
