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

    def test_roundtrip_is_new_only_when_crossing_feature_introduction(self):
        row={'case':['ns','DesignL2WorkloadBenchmarks','Read','Backend=Redis&RoundTrip=True']}
        self.assertIsNotNone(summary.new_work('pr3','pr4',row))
        self.assertIsNone(summary.new_work('constructor','siphash',row))

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
