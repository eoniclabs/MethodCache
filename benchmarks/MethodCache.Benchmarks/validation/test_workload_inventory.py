import json
from pathlib import Path
import tempfile
import unittest

from workload_inventory import normalize_inventory, require_same_inventory, verify_reports


class WorkloadInventoryTests(unittest.TestCase):
    def setUp(self):
        self.inventory = [
            {'full_name': 'Generated.GuidHit', 'report_type': 'Generated'},
            {'full_name': 'Generated.StringHit', 'report_type': 'Generated'},
        ]

    def report(self, directory, names, count=12, stage='Actual'):
        path = Path(directory) / 'report.json'
        path.write_text(json.dumps({'Benchmarks': [
            {'FullName': name, 'Statistics': {'Mean': 1}, 'Measurements': [
                {'IterationMode': 'Workload', 'IterationStage': stage} for _ in range(count)
            ]} for name in names
        ]}))
        return path

    def test_inventory_order_does_not_change_workloads(self):
        require_same_inventory(list(reversed(self.inventory)), self.inventory)

    def test_same_size_different_key_shape_is_rejected(self):
        changed = [self.inventory[0], {'full_name': 'Generated.IntHit', 'report_type': 'Generated'}]
        with self.assertRaisesRegex(ValueError, 'differ across revisions'):
            require_same_inventory(changed, self.inventory)

    def test_empty_or_duplicate_compiled_inventory_is_rejected(self):
        for invalid in ([], {}, [self.inventory[0], self.inventory[0]]):
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                normalize_inventory(invalid)

    def test_same_names_different_report_type_is_rejected(self):
        changed = [dict(entry, report_type='Different') for entry in self.inventory]
        with self.assertRaisesRegex(ValueError, 'differ across revisions'):
            require_same_inventory(changed, self.inventory)

    def test_complete_actual_samples_match_the_compiled_inventory(self):
        with tempfile.TemporaryDirectory() as directory:
            report = self.report(directory, ['Generated.StringHit', 'Generated.GuidHit'])
            self.assertEqual(2, verify_reports([report], self.inventory))

    def test_duplicate_replacing_a_required_case_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            report = self.report(directory, ['Generated.GuidHit', 'Generated.GuidHit'])
            with self.assertRaisesRegex(ValueError, 'Duplicate measured'):
                verify_reports([report], self.inventory)

    def test_same_count_wrong_case_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            report = self.report(directory, ['Generated.GuidHit', 'Generated.IntHit'])
            with self.assertRaisesRegex(ValueError, 'do not match'):
                verify_reports([report], self.inventory)

    def test_warmup_only_and_incomplete_actual_measurements_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            for count, stage in ((12, 'Warmup'), (11, 'Actual')):
                with self.subTest(count=count, stage=stage):
                    report = self.report(directory, ['Generated.GuidHit', 'Generated.StringHit'], count, stage)
                    with self.assertRaisesRegex(ValueError, 'iteration count'):
                        verify_reports([report], self.inventory)

    def test_missing_report_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'report count'):
            verify_reports([], self.inventory)


if __name__ == '__main__':
    unittest.main()
