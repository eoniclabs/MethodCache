import importlib.util,json,math,tempfile,unittest
from pathlib import Path
s=importlib.util.spec_from_file_location('report',Path(__file__).with_name('summarize-pair.py'))
m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
class IndependentMeansTests(unittest.TestCase):
 def test_exact_three_independent_means(self):
  r=m.interval([10,11,12]);self.assertEqual(11,r['mean_ns']);self.assertAlmostEqual(4.302652729696142/math.sqrt(3),r['upper_ns']-11)
 def test_reject_missing_or_invalid_samples(self):
  for values in [[10,11], [10,11,12,13], [10,float('nan'),12], [10,0,12]]:
   with self.assertRaises(ValueError):m.interval(values)
 def test_wide_overlapping_intervals_do_not_pass(self):
  key=('N','T','Hit','');r=m.compare({key:([80,100,120],[0,0,0])},{key:([81,101,121],[0,0,0])})[0]
  self.assertEqual('insufficient_precision',r['review'])
 def test_narrow_disjoint_regression_stops(self):
  key=('N','T','Hit','');r=m.compare({key:([99.9,100,100.1],[0,0,0])},{key:([109.9,110,110.1],[0,0,0])})[0]
  self.assertEqual('regression',r['review'])
 def test_narrow_is_review_not_automatic_acceptance(self):
  key=('N','T','Hit','');r=m.compare({key:([100,100,100],[0,0,0])},{key:([100,100,100],[0,0,0])})[0]
  self.assertEqual('review_required',r['review'])
 def test_allocation_increase_is_preserved(self):
  key=('N','T','Hit','');r=m.compare({key:([100,100,100],[0,0,0])},{key:([100,100,100],[8,8,8])})[0]
  self.assertTrue(r['allocation_increase_observed'])
 def test_mismatched_matrix_is_an_error(self):
  with self.assertRaises(ValueError):m.compare({('Hit',):([1,1,1],[0,0,0])},{})
class ReportIntegrityTests(unittest.TestCase):
 def setUp(self):
  self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
  self.case={'Namespace':'N','Type':'T','Method':'Hit','Parameters':'',
   'Measurements':[{'IterationMode':'Workload','IterationStage':'Actual','Operations':1000,'Nanoseconds':100000} for _ in range(12)],
   'Statistics':{'Mean':100},'Memory':{'BytesAllocatedPerOperation':0}}
  for rnd in range(1,4):
   stem=f'round-{rnd}-base-local'
   (self.root/(stem+'-interference.json')).write_text('[]')
   folder=self.root/stem/'results';folder.mkdir(parents=True)
   (folder/'T-report-full.json').write_text(json.dumps({'Benchmarks':[self.case]}))
 def test_three_complete_reports(self):
  row=m.collect(self.root,'base','local')[('N','T','Hit','')]
  self.assertEqual(([100,100,100],[0,0,0]),row)
 def test_missing_guard_is_not_quiet(self):
  (self.root/'round-2-base-local-interference.json').unlink()
  with self.assertRaises(ValueError):m.collect(self.root,'base','local')
 def test_overlap_is_rejected(self):
  (self.root/'round-2-base-local-interference.json').write_text('[{"processes":["other build"]}]')
  with self.assertRaises(ValueError):m.collect(self.root,'base','local')
 def test_warmup_only_is_not_measured_evidence(self):
  for sample in self.case['Measurements']:sample['IterationStage']='Warmup'
  (self.root/'round-2-base-local/results/T-report-full.json').write_text(json.dumps({'Benchmarks':[self.case]}))
  with self.assertRaises(ValueError):m.collect(self.root,'base','local')
 def test_duplicate_report_is_rejected(self):
  (self.root/'round-2-base-local/results/duplicate-report-full.json').write_text(json.dumps({'Benchmarks':[self.case]}))
  with self.assertRaises(ValueError):m.collect(self.root,'base','local')

if __name__=='__main__':unittest.main()
