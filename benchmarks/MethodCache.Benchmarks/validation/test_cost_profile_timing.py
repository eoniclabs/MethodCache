"""The paired gate must reject incomplete or imprecise timing evidence."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[2] / "MethodCache.CostProfile" / "paired.py"
CASE = "GeneratedContractBenchmarks.SyncHit()"
# Stands in for dotnet: builds nothing, lists the cases with Windows line endings and times only exact names
FAKE_DOTNET = """#!/usr/bin/env bash
set -euo pipefail
[ "$1" = build ] && exit 0
case "$3" in
  --list) printf 'A.Hit()\\r\\nB.Hit()\\r\\n' ;;
  --exact)
    : > "$6"
    case "$2" in "A.Hit()"|"B.Hit()") printf '{"case":"%s","ns":100}\\n' "$2" > "$6" ;; esac ;;
esac
"""


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


class TimedLaunchTests(unittest.TestCase):
    def test_windows_case_list_reaches_every_exact_launch(self):
        benchmarks = SCRIPT.parents[1]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "repo"
            shutil.copytree(SCRIPT.parent, root / "benchmarks" / "MethodCache.CostProfile",
                            ignore=shutil.ignore_patterns("bin", "obj", "__pycache__"))
            for folder in ("Comparison", "Microbenchmarks"):
                shutil.copytree(benchmarks / "MethodCache.Benchmarks" / folder,
                                root / "benchmarks" / "MethodCache.Benchmarks" / folder,
                                ignore=lambda _, names: [n for n in names if not n.endswith(".cs")])
            git = ["git", "-c", "user.name=test", "-c", "user.email=test@example.com", "-C", str(root)]
            subprocess.run(["git", "init", "-q", str(root)], check=True)
            subprocess.run(git + ["add", "-A"], check=True)
            subprocess.run(git + ["commit", "-q", "-m", "base"], check=True)
            bin_dir = Path(directory) / "bin"
            bin_dir.mkdir()
            (bin_dir / "dotnet").write_text(FAKE_DOTNET)
            (bin_dir / "dotnet").chmod(0o755)
            out = Path(directory) / "out"
            env = dict(os.environ, PATH=f"{bin_dir}{os.pathsep}{os.environ['PATH']}", COST_PROFILE_OUT=str(out))
            script = root / "benchmarks" / "MethodCache.CostProfile" / "cost-profile.sh"
            result = subprocess.run(["bash", str(script), "--time", "2", "HEAD"], cwd=root, env=env,
                                    text=True, capture_output=True)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            for launch in sorted(out.glob("*-[12].jsonl")):
                cases = [json.loads(line)["case"] for line in launch.read_text().splitlines()]
                self.assertEqual(["A.Hit()", "B.Hit()"], cases, launch.name)
            self.assertEqual(4, len(list(out.glob("*-[12].jsonl"))))


if __name__ == "__main__":
    unittest.main()
