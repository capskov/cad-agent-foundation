"""Submission classification regression checks; never connects to CAD."""
import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock

spec=importlib.util.spec_from_file_location('feedback_entry',Path(__file__).resolve().parents[1]/'client/foundation.py')
F=importlib.util.module_from_spec(spec);sys.modules[spec.name]=F;spec.loader.exec_module(F)

class SubmissionClassification(unittest.TestCase):
    def gateway(self,directory):
        g=F.Gateway.__new__(F.Gateway);g.work=Path(directory);g.session={'write_enabled':True};g.context='test-context'
        g.query=Mock(return_value={'status':'unknown'})
        return g

    def test_structured_rejection_is_not_queried_or_resubmitted(self):
        with tempfile.TemporaryDirectory() as d:
            g=self.gateway(d)
            g.call=Mock(return_value={'status':'rejected_before_submission','pending':False,'effects':'none','registered':False,'request_id':'test-request','context_id':'test-context'})
            self.assertEqual(g.apply({'bad':'plan'},'test-request')['status'],'rejected_before_submission')
            self.assertEqual(g.apply({'bad':'plan'},'test-request')['status'],'rejected_before_submission')
            g.call.assert_called_once();g.query.assert_not_called()

    def test_transport_timeout_queries_only_original_request(self):
        with tempfile.TemporaryDirectory() as d:
            g=self.gateway(d);g.call=Mock(side_effect=TimeoutError('lost reply'))
            self.assertEqual(g.apply({'example':1},'test-request')['status'],'unknown')
            g.call.assert_called_once();g.query.assert_called_once()
            self.assertEqual(g.query.call_args.args[0]['request_id'],'test-request')

    def test_unstructured_application_error_remains_unknown(self):
        with tempfile.TemporaryDirectory() as d:
            g=self.gateway(d);g.call=Mock(side_effect=F.E.ApplicationFailure('unexpected fields'))
            g.apply({'example':1},'test-request');g.query.assert_called_once()

    def test_rpc_invalid_params_is_distinct_from_unknown_rpc(self):
        for code in [-32602,-32603]:
            with tempfile.TemporaryDirectory() as d:
                g=self.gateway(d);g.call=Mock(side_effect=F.E.RemoteRpcError('rpc failure',code))
                result=g.apply({'example':1},'test-request')
                if code==-32602:self.assertEqual(result['status'],'rejected_before_submission');g.query.assert_not_called()
                else:g.query.assert_called_once()

if __name__=='__main__':unittest.main()
