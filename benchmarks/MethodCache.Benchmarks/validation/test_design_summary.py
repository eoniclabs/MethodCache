import importlib.util
from pathlib import Path
import unittest

spec=importlib.util.spec_from_file_location('summary',Path(__file__).with_name('summarize-design.py'))
summary=importlib.util.module_from_spec(spec)
spec.loader.exec_module(summary)


class DesignSummaryGuards(unittest.TestCase):
    def setUp(self):
        primary=['base','pr1-base','pr1','pr2','pr3','pr4','pr5','prefix','http','net10','constructor-base','constructor','siphash','final']
        components=['pr5','prefix','constructor-base','constructor','siphash','final']
        self.manifest={'sources':dict.fromkeys(primary,{}),'components':dict.fromkeys(components,{})}
        self.manifest['runs']=[{'round':r,'revision':n,'group':g,'exit':0} for r in range(1,4) for n in primary for g in ('local-contract','local-pressure','native')]
        self.manifest['runs'] += [{'round':r,'revision':n,'group':g,'exit':0} for r in range(1,4) for n in components for g in ('component-lookup','component-facade','component-generated')]

    def test_complete_180_launches_are_accepted_as_complete_not_as_timing_pass(self):
        summary.complete_runs(self.manifest)

    def test_missing_launch_is_rejected(self):
        self.manifest['runs'].pop()
        with self.assertRaises(ValueError):summary.complete_runs(self.manifest)

    def test_same_count_duplicate_substitution_is_rejected(self):
        self.manifest['runs'][-1]=self.manifest['runs'][0]
        with self.assertRaises(ValueError):summary.complete_runs(self.manifest)

    def test_failed_launch_is_rejected(self):
        self.manifest['runs'][0]['exit']=1
        with self.assertRaises(ValueError):summary.complete_runs(self.manifest)

    def test_missing_pr_edges_are_rejected_after_complete_measurements(self):
        self.manifest.update(comparisons=[],protocol={'comparisons':[]})
        with self.assertRaisesRegex(ValueError,'comparison edges'):summary.summarize(Path('.'),self.manifest)

    def test_roundtrip_enabled_preserves_existing_write_validation(self):
        row={'case':['ns','DesignL2WorkloadBenchmarks','Write','Backend=Redis&RoundTrip=True']}
        for edge in [('pr3','pr4'),('base','final')]:
            with self.subTest(edge=edge):
                self.assertIsNone(summary.new_work(*edge,row))

    def test_roundtrip_disabled_is_a_new_write_opt_out(self):
        row={'case':['ns','DesignL2WorkloadBenchmarks','Write','Backend=Redis&RoundTrip=False']}
        for edge in [('pr3','pr4'),('base','final')]:
            with self.subTest(edge=edge):
                self.assertIn('opt-out',summary.new_work(*edge,row) or '')

    def test_roundtrip_toggle_does_not_change_measured_reads(self):
        for backend in ('Redis','SqlServer'):
            for enabled in ('False','True'):
                row={'case':['ns','DesignL2WorkloadBenchmarks','Read',f'Backend={backend}&RoundTrip={enabled}']}
                with self.subTest(backend=backend,enabled=enabled):
                    self.assertIsNone(summary.new_work('pr3','pr4',row))
                    self.assertTrue(summary.hit_path(row))

    def test_roundtrip_disabled_is_comparable_after_option_exists(self):
        row={'case':['ns','DesignL2WorkloadBenchmarks','Write','Backend=SqlServer&RoundTrip=False']}
        for edge in [('pr4','pr5'),('constructor','siphash')]:
            with self.subTest(edge=edge):
                self.assertIsNone(summary.new_work(*edge,row))

    def test_roundtrip_is_comparable_between_historical_revisions(self):
        for enabled in ('False','True'):
            row={'case':['ns','DesignL2WorkloadBenchmarks','Write',f'Backend=Redis&RoundTrip={enabled}']}
            with self.subTest(enabled=enabled):
                self.assertIsNone(summary.new_work('pr2','pr3',row))

    def test_cookie_policy_addition_is_separate_from_ordinary_cached_hit(self):
        row={'case':['ns','DesignHttpWorkloadBenchmarks','Fresh','Backend=Redis&KeepCookies=True']}
        self.assertIsNotNone(summary.new_work('prefix','http',row))
        self.assertFalse(summary.hit_path(row))
        row['case'][-1]='Backend=Redis&KeepCookies=False'
        self.assertIsNone(summary.new_work('prefix','http',row))
        self.assertTrue(summary.hit_path(row))

    def test_generated_hits_and_unique_misses_have_distinct_allocation_roles(self):
        row={'case':['ns','GeneratedPrefixHitPathBenchmarks','SyncHit','ArgumentKind=Guid']}
        self.assertTrue(summary.hit_path(row))
        row['case'][2]='SyncMiss'
        self.assertFalse(summary.hit_path(row))


if __name__=='__main__':unittest.main()
