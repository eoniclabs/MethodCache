"""The paired gate must reject incomplete or imprecise timing evidence."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[2] / "MethodCache.CostProfile" / "paired.py"
CASE = "GeneratedContractBenchmarks.SyncHit()"


class PairedCostProfileTests(unittest.TestCase):
    def compare(self, ratios, accepted=None, extra_base=False, duplicate=False):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for index, ratio in enumerate(ratios, 1):
                for side, ns in (("base", 100), ("head", 100 * ratio)):
                    line = json.dumps({"case": CASE, "ns": ns}) + "\n"
                    (root / f"{side}-{index}.jsonl").write_text(line * (2 if duplicate else 1))
            if extra_base:
                (root / f"base-{len(ratios)+1}.jsonl").write_text(json.dumps({"case": CASE, "ns": 100}) + "\n")
            args = [sys.executable, str(SCRIPT), directory]
            if accepted:
                path = root / "accepted.json"
                path.write_text(json.dumps({CASE: accepted}))
                args += ["--accepted", str(path)]
            return subprocess.run(args, text=True, capture_output=True)

    def test_precise_equal_measurements_pass(self):
        self.assertEqual(0, self.compare([1] * 10).returncode)

    def test_precise_deliberate_slowdown_requires_reason_and_bound(self):
        self.assertNotEqual(0, self.compare([1.1] * 10).returncode)
        result = self.compare([1.1] * 10, {"reason": "Measured SipHash cost", "time": 1.12})
        self.assertEqual(0, result.returncode, result.stderr)

    def test_wide_faster_interval_still_fails(self):
        result = self.compare([0.6, 0.8] * 5)
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn("too noisy", result.stdout)

    def test_wide_accepted_slowdown_still_fails(self):
        result = self.compare([1.3, 1.5] * 5, {"reason": "Deliberate cost", "time": 1.6})
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn("too noisy", result.stdout)

    def test_unpaired_launch_is_not_silently_discarded(self):
        self.assertNotEqual(0, self.compare([1] * 10, extra_base=True).returncode)

    def test_duplicate_case_is_not_silently_overwritten(self):
        self.assertNotEqual(0, self.compare([1] * 10, duplicate=True).returncode)

    def test_nonfinite_times_fail(self):
        for value in (float("nan"), float("inf")):
            with self.subTest(value=value):
                self.assertNotEqual(0, self.compare([value] * 10).returncode)


if __name__ == "__main__":
    unittest.main()
