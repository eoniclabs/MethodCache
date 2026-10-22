"""Exercise real OS admission and termination, without starting project builds."""
import fcntl
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'host-lease.py'


class HostLeaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.lock = self.root / 'capacity.lock'

    def run_lease(self, source, *options):
        return subprocess.run([sys.executable, str(SCRIPT), '--lock', str(self.lock),
                               *options, '--', sys.executable, '-c', source],
                              capture_output=True, text=True, timeout=8)

    def test_held_for_child_and_failure_preserved(self):
        source = f'''import fcntl,sys
with open({str(self.lock)!r}, 'a+b') as lease:
    try: fcntl.lockf(lease, fcntl.LOCK_EX|fcntl.LOCK_NB)
    except BlockingIOError: sys.exit(23)
    sys.exit(99)
'''
        self.assertEqual(23, self.run_lease(source).returncode)
        with self.lock.open('a+b') as lease:
            fcntl.lockf(lease, fcntl.LOCK_EX | fcntl.LOCK_NB)

    def test_busy_does_not_start_child(self):
        marker = self.root / 'started'
        with self.lock.open('a+b') as lease:
            fcntl.lockf(lease, fcntl.LOCK_EX)
            result = self.run_lease(f'open({str(marker)!r},"w").close()', '--wait-seconds', '0.05')
        self.assertEqual(75, result.returncode)
        self.assertFalse(marker.exists())

    def test_run_timeout_terminates_descendant_before_admission_released(self):
        marker = self.root / 'terminated'
        child = f'import signal,time; signal.signal(signal.SIGTERM, lambda *_: (open({str(marker)!r},"w").close(),exit(0))); time.sleep(60)'
        source = f'import subprocess,sys,time; subprocess.Popen([sys.executable,"-c",{child!r}]); time.sleep(60)'
        result = self.run_lease(source, '--run-seconds', '0.5', '--kill-seconds', '0.2')
        self.assertEqual(124, result.returncode, result.stderr)
        self.assertTrue(marker.exists())
        with self.lock.open('a+b') as lease:
            fcntl.lockf(lease, fcntl.LOCK_EX | fcntl.LOCK_NB)

    def test_signal_during_wait_leaves_holder_and_no_child(self):
        marker = self.root / 'started'
        with self.lock.open('a+b') as lease:
            fcntl.lockf(lease, fcntl.LOCK_EX)
            process = subprocess.Popen([sys.executable, str(SCRIPT), '--lock', str(self.lock),
                '--', sys.executable, '-c', f'open({str(marker)!r},"w").close()'], stdout=subprocess.PIPE, text=True)
            self.addCleanup(process.stdout.close)
            process.stdout.readline()  # actual acquisition attempt; do not guess startup delay
            process.send_signal(signal.SIGTERM)
            self.assertEqual(143, process.wait(timeout=3))
        self.assertFalse(marker.exists())

    def test_success_retires_owned_background_children(self):
        ready = self.root / 'ready'
        marker = self.root / 'retired'
        child = f'import signal,time; signal.signal(signal.SIGTERM, lambda *_: (open({str(marker)!r},"w").close(),exit(0))); open({str(ready)!r},"w").close(); time.sleep(60)'
        source = f'import subprocess,sys,time,pathlib; subprocess.Popen([sys.executable,"-c",{child!r}]); p=pathlib.Path({str(ready)!r}); exec("while not p.exists(): time.sleep(0.01)")'
        result = self.run_lease(source, '--kill-seconds', '0.2')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(marker.exists())

    def test_requires_configured_locks(self):
        env = dict(os.environ, EONIC_CI_CAPACITY_LOCKS='')
        result = subprocess.run([sys.executable, str(SCRIPT), '--', 'true'], env=env, capture_output=True)
        self.assertEqual(2, result.returncode)


if __name__ == '__main__':
    unittest.main()
