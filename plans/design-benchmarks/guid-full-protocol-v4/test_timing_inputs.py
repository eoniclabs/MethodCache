"""Guard tests use synthetic records; actual compiled/setup preflights are separate."""
import ast
import hashlib
import json
from pathlib import Path
import tempfile
import types
import unittest

from workload_inventory import normalize_inventory, require_same_inventory


def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()


class TimingInputGuards(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.folder = self.root/'cohort'
        self.folder.mkdir()
        self.head = 'f'*40
        self.dirty = ''
        self.catalog = [{'full_name':'Contract.SyncHit(Id: guid)', 'report_type':'Contract'}]
        self.actual = list(self.catalog)
        self.sources = {}
        self.binaries = {}
        for component in (False,True):
            harness = self.folder/('component-harness' if component else 'harness')/'final'
            binary = harness/'bin/Release/net10.0'/('MethodCache.Benchmarks.dll' if component else 'DesignValidation.dll')
            binary.parent.mkdir(parents=True)
            binary.write_bytes(b'fake compiled fixture, not benchmark evidence')
            dependency = binary.with_name('Dependency.dll')
            dependency.write_bytes(b'fake dependency')
            (harness/'Program.cs').write_text('immutable fixture program')
            (harness/'Contract.cs').write_text('immutable fixture workload')
            source = {'runtime_root':str(self.root/'runtime'),'runtime_sha':self.head,
                'harness_program_sha256':digest(harness/'Program.cs'),
                'source_sha256':{'Comparison/Contract.cs':digest(harness/'Contract.cs')}}
            (harness/'source-manifest.json').write_text(json.dumps(source))
            self.sources[component] = source
            self.binaries[component] = {str(p.relative_to(harness)):digest(p) for p in (binary,dependency)}
        self.prepared = {'sources':{'final':self.sources[False]}, 'inventories':{'local-contract':self.catalog},
            'filters':{'local-contract':['*Contract*']}, 'binaries_sha256':{'final':self.binaries[False]},
            'components':{'final':{'source':self.sources[True],'inventories':{'component-generated':self.catalog},
                'filters':{'component-generated':['*Contract*']},'binaries_sha256':self.binaries[True]}}}
        (self.folder/'prepared-manifest.json').write_text(json.dumps(self.prepared))
        (self.folder/'helper.py').write_text('immutable helper')
        protocol = {'helpers_sha256':{'cohort/helper.py':digest(self.folder/'helper.py')}}
        (self.folder/'timing-protocol.json').write_text(json.dumps(protocol))
        metadata = {'prepared_manifest_sha256':digest(self.folder/'prepared-manifest.json'),
                    'protocol_sha256':digest(self.folder/'timing-protocol.json')}
        self.namespace = {'Path':Path,'hashlib':hashlib,'json':json,'folder':self.folder,'root':self.root,
            'prepared':self.prepared,'metadata':metadata,'protocol':protocol,
            'normalize_inventory':normalize_inventory,'require_same_inventory':require_same_inventory,
            'subprocess':types.SimpleNamespace(check_output=self.command)}
        tree = ast.parse(Path(__file__).with_name('timing-worker.py').read_text())
        functions = [n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name in ('digest','frozen_protocol','inputs')]
        exec(compile(ast.Module(body=functions,type_ignores=[]),'<frozen-worker-functions>','exec'),self.namespace)

    def command(self, command, **kwargs):
        if command[:3] == ['git','rev-parse','HEAD']: return self.head+'\n'
        if command[:3] == ['git','diff','--name-only']: return self.dirty
        self.assertEqual(command[0],'dotnet')
        if not Path(command[1]).is_file(): raise FileNotFoundError(command[1])
        self.assertEqual(command[2],'--workload-inventory')
        return json.dumps(self.actual)

    def validate(self, component=False):
        return self.namespace['inputs']('final','component-generated' if component else 'local-contract')

    def test_frozen_main_family_is_accepted(self):
        _, executable, _, cases = self.validate()
        self.assertEqual(executable.name,'DesignValidation.dll')
        self.assertEqual(cases,self.catalog)

    def test_component_uses_actual_assembly_name(self):
        _, executable, _, cases = self.validate(component=True)
        self.assertEqual(executable.name,'MethodCache.Benchmarks.dll')
        self.assertEqual(cases,self.catalog)

    def test_program_changes_are_rejected_even_after_first_validation(self):
        self.validate()
        (self.folder/'harness/final/Program.cs').write_text('replacement')
        with self.assertRaisesRegex(RuntimeError,'program changed'):self.validate()

    def test_workload_source_changes_are_rejected(self):
        (self.folder/'harness/final/Contract.cs').write_text('replacement')
        with self.assertRaisesRegex(RuntimeError,'Workload bytes changed'):self.validate()

    def test_dependency_dll_changes_are_rejected(self):
        (self.folder/'harness/final/bin/Release/net10.0/Dependency.dll').write_bytes(b'replacement')
        with self.assertRaisesRegex(RuntimeError,'Compiled DLL changed'):self.validate()

    def test_runtime_move_is_rejected(self):
        self.head = 'e'*40
        with self.assertRaisesRegex(RuntimeError,'checkout moved'):self.validate()

    def test_dirty_runtime_is_rejected(self):
        self.dirty = 'src/Changed.cs\n'
        with self.assertRaisesRegex(RuntimeError,'modified sources'):self.validate()

    def test_manifest_tampering_is_rejected(self):
        (self.folder/'harness/final/source-manifest.json').write_text('{}')
        with self.assertRaisesRegex(RuntimeError,'source manifest changed'):self.validate()

    def test_same_count_substituted_case_is_rejected(self):
        self.actual = [{'full_name':'Contract.SyncHit(Id: int)', 'report_type':'Contract'}]
        with self.assertRaisesRegex(ValueError,'inventories differ'):self.validate()

    def test_duplicate_compiled_case_is_rejected(self):
        self.actual = self.catalog+self.catalog
        with self.assertRaisesRegex(ValueError,'Duplicate compiled case'):self.validate()

    def test_helper_change_is_rejected(self):
        (self.folder/'helper.py').write_text('replacement')
        with self.assertRaisesRegex(RuntimeError,'helper changed'):self.validate()

    def test_protocol_change_is_rejected(self):
        (self.folder/'timing-protocol.json').write_text('{}')
        with self.assertRaisesRegex(RuntimeError,'Timing protocol changed'):self.validate()

    def test_prepared_manifest_change_is_rejected(self):
        (self.folder/'prepared-manifest.json').write_text('{}')
        with self.assertRaisesRegex(RuntimeError,'Prepared manifest changed'):self.validate()


if __name__ == '__main__': unittest.main()
