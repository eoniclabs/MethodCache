"""Mac adapter result files must describe the real build/test outcomes."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'actions-macos.sh'


class ActionsMacOSTests(unittest.TestCase):
    def run_adapter(self, build=0, unit=0):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        (root / 'build/ci').mkdir(parents=True)
        shutil.copy2(SCRIPT, root / 'build/ci/actions-macos.sh')
        (root / 'build/ci/jobs.sh').write_text('echo "running $1"; if [ "$1" = build ]; then exit "$BUILD_EXIT"; else exit "$UNIT_EXIT"; fi\n')
        result = subprocess.run(['bash', 'build/ci/actions-macos.sh'], cwd=root,
            env=dict(os.environ, BUILD_EXIT=str(build), UNIT_EXIT=str(unit), METHODCACHE_CI_RESULTS='artifacts/ci'),
            text=True, capture_output=True, timeout=10)
        return result, root / 'artifacts/ci'

    def test_success_records_both_jobs_and_logs(self):
        result, results = self.run_adapter()
        self.assertEqual(0, result.returncode, result.stderr)
        for job in ('build', 'unit'):
            self.assertEqual('0', (results / (job + '.exit')).read_text().strip())
            self.assertIn('running ' + job, (results / (job + '.log')).read_text())

    def test_build_failure_skips_unit_and_preserves_exit(self):
        result, results = self.run_adapter(build=23)
        self.assertEqual(23, result.returncode)
        self.assertEqual('23', (results / 'build.exit').read_text().strip())
        self.assertEqual('skipped', (results / 'unit.exit').read_text().strip())
        self.assertNotIn('running unit', result.stdout)

    def test_unit_failure_is_not_hidden_by_successful_tee(self):
        result, results = self.run_adapter(unit=42)
        self.assertEqual(42, result.returncode)
        self.assertEqual('42', (results / 'unit.exit').read_text().strip())
