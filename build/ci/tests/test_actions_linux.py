"""The Actions adapter must respect other projects' admission and preserve real failures."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / 'actions-linux.sh'


@unittest.skipUnless(os.name == 'posix', 'The Linux Actions adapter needs POSIX descriptor locks')
class ActionsLinuxTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        (self.root / 'build/ci').mkdir(parents=True)
        shutil.copy2(SCRIPT, self.root / 'build/ci/actions-linux.sh')
        self.lock = self.root / 'capacity.lock'
        self.lock.touch()
        self.env = dict(os.environ, PATH=str(self.root) + os.pathsep + os.environ['PATH'],
                        METHODCACHE_ACTIONS_CAPACITY_LOCK=str(self.lock),
                        METHODCACHE_CI_SHARED_LOCK='must-be-unset', GITHUB_RUN_ID='42',
                        GITHUB_RUN_ATTEMPT='2', GITHUB_STEP_SUMMARY=str(self.root / 'summary'))
        self.env.pop('METHODCACHE_ACTIONS_SUITE', None)
        self.helper('flock', '''
import fcntl, sys
try:
    fcntl.flock(int(sys.argv[-1]), fcntl.LOCK_EX | fcntl.LOCK_NB)
except BlockingIOError:
    sys.exit(75)
''')
        self.helper('timeout', '''
import os, sys
if os.environ.get('FIXTURE_TIMEOUT'):
    sys.exit(124)
os.execvp(sys.argv[4], sys.argv[4:])
''')
        (self.root / 'build/ci/linux.sh').write_text('''#!/bin/bash
exec python3 - "$@" <<'PY'
import fcntl, json, os, pathlib, sys
# Admission must still be held while the build runs, not only while flock was executing.
with open(os.environ['METHODCACHE_ACTIONS_CAPACITY_LOCK']) as lock:
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        pass
    else:
        raise AssertionError('Build started without owning host capacity')
assert 'METHODCACHE_CI_SHARED_LOCK' not in os.environ, 'Nested shared admission would deadlock'
pathlib.Path('invocation.json').write_text(json.dumps({
    'jobs': sys.argv[1:], 'run': os.environ['METHODCACHE_CI_RUN_ID'],
    'limits': os.environ['METHODCACHE_CI_DOCKER_ARGS']}))
pathlib.Path('artifacts/ci').mkdir(parents=True)
pathlib.Path('artifacts/ci/build.exit').write_text(os.environ.get('FIXTURE_EXIT', '0'))
sys.exit(int(os.environ.get('FIXTURE_EXIT', '0')))
PY
''')

    def helper(self, name, source):
        path = self.root / name
        path.write_text('#!' + sys.executable + '\n' + source)
        path.chmod(0o755)

    def run_adapter(self, suite='smoke'):
        return subprocess.run(['/bin/bash', 'build/ci/actions-linux.sh', suite],
                              cwd=self.root, env=self.env, capture_output=True, text=True, timeout=10)

    def test_smoke_holds_exclusive_capacity_for_build_and_unit(self):
        result = self.run_adapter()
        self.assertEqual(0, result.returncode, result.stderr)
        invocation = json.loads((self.root / 'invocation.json').read_text())
        self.assertEqual(['build', 'unit'], invocation['jobs'])
        self.assertEqual('actions-42-attempt-2', invocation['run'])
        self.assertIn('--memory=16g', invocation['limits'])

    def test_full_delegates_to_existing_default_suite(self):
        result = self.run_adapter('full')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], json.loads((self.root / 'invocation.json').read_text())['jobs'])

    def test_another_project_holding_capacity_prevents_execution(self):
        import fcntl
        with self.lock.open() as held:
            fcntl.flock(held, fcntl.LOCK_SH)
            result = self.run_adapter()
        self.assertEqual(75, result.returncode)
        self.assertFalse((self.root / 'invocation.json').exists())

    def test_missing_capacity_lock_fails_closed(self):
        self.lock.unlink()
        self.assertNotEqual(0, self.run_adapter().returncode)
        self.assertFalse((self.root / 'invocation.json').exists())

    def test_failure_is_preserved_in_exit_and_summary(self):
        self.env['FIXTURE_EXIT'] = '7'
        self.assertEqual(7, self.run_adapter().returncode)
        self.assertIn('| build | 7 |', (self.root / 'summary').read_text())

    def test_timeout_is_failure(self):
        self.env['FIXTURE_TIMEOUT'] = '1'
        self.assertEqual(124, self.run_adapter().returncode)
        self.assertFalse((self.root / 'invocation.json').exists())

    def test_unknown_suite_never_executes(self):
        self.assertEqual(64, self.run_adapter('anything-else').returncode)
        self.assertFalse((self.root / 'invocation.json').exists())
