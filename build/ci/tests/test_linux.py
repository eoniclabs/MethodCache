import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / 'linux.sh'


class LinuxCleanupTests(unittest.TestCase):
    def test_remote_termination_cleans_only_run_owned_containers(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            docker = root / 'docker'
            docker.write_text('#!' + sys.executable + '\n' + '''
import os, pathlib, sys, time
root = pathlib.Path(os.environ['FIXTURE_ROOT'])
args = sys.argv[1:]
if args[:2] == ['image', 'inspect']:
    pass
elif args[0] == 'run':
    (root/'ready').touch()
    time.sleep(30)
elif args[0] == 'ps':
    assert args[-1] == 'label=methodcache.ci.run=fixture-run'
    print('owned-sdk')
elif args[0] == 'rm':
    assert args[1:] == ['--force', 'owned-sdk']
    (root/'cleaned').touch()
else:
    raise RuntimeError('Unexpected fake Docker command')
''')
            docker.chmod(0o755)
            env = dict(os.environ, PATH=directory + os.pathsep + os.environ['PATH'],
                       FIXTURE_ROOT=directory, METHODCACHE_CI_RUN_ID='fixture-run',
                       METHODCACHE_CI_RESULTS=str(root/'results'), METHODCACHE_CI_CACHE=str(root/'cache'),
                       METHODCACHE_CI_SHARED_LOCK='', METHODCACHE_CI_DOCKER_ARGS='')
            # macOS lacks GNU stat/sha256sum; only their fixture metadata is needed here.
            for name, output in [('sha256sum', 'fixture-image'), ('stat', '1000')]:
                helper = root/name
                helper.write_text('#!/bin/sh\necho ' + output + '\n')
                helper.chmod(0o755)
            with (root/'output').open('w') as log:
                child = subprocess.Popen(['/bin/bash', str(SCRIPT), 'tests'], env=env,
                                         stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
                try:
                    deadline = time.monotonic() + 5
                    while not (root/'ready').exists() and child.poll() is None and time.monotonic() < deadline:
                        time.sleep(0.01)
                    self.assertTrue((root/'ready').exists(), (root/'output').read_text())
                    os.killpg(child.pid, signal.SIGTERM)
                    self.assertNotEqual(0, child.wait(timeout=5))
                    self.assertTrue((root/'cleaned').exists(), 'Terminated remote shell left its Docker job alive')
                finally:
                    if child.poll() is None:
                        os.killpg(child.pid, signal.SIGKILL)
                        child.wait()


if __name__ == '__main__':
    unittest.main()
