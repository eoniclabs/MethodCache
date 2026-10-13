import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'coordinator-state.py'
spec = importlib.util.spec_from_file_location('coordinator_state', SCRIPT)
state_tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(state_tools)


class StateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.state = Path(self.temp.name)
        self.sha = 'a' * 40
        self.run = '20261013-120000-' + self.sha[:12]
        self.report = self.state / 'runs' / self.run
        self.report.mkdir(parents=True)
        (self.report / 'commit').write_text(self.sha)

    def results(self, platform='macos', passed=True, report=None):
        p = (report or self.report) / platform
        p.mkdir(parents=True, exist_ok=True)
        (p / 'finished').touch()
        for job in state_tools.JOBS[platform]:
            (p / (job + '.exit')).write_text('0' if passed else '1')
            (p / (job + '.log')).write_text('diagnostics')
        return p

    def checkout(self):
        mirror = self.state / 'macos' / 'mirror.git'
        mirror.parent.mkdir(parents=True)
        subprocess.run(['git', 'init', '--bare', '-q', str(mirror)], check=True)
        seed = self.state / 'seed'; seed.mkdir()
        subprocess.run(['git', 'init', '-q', str(seed)], check=True)
        (seed / 'Test.csproj').write_text('<Project />')
        (seed / '.gitignore').write_text('bin/\nobj/\nartifacts/\n')
        for args in [('add', '.'), ('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'fixture')]:
            subprocess.run(['git', '-C', str(seed), *args], check=True)
        subprocess.run(['git', '-C', str(seed), 'push', '-q', str(mirror), 'HEAD:refs/heads/main'], check=True)
        self.sha = state_tools.git(seed, 'rev-parse', 'HEAD')
        self.run = '20261013-120000-' + self.sha[:12]
        self.report = self.state / 'runs' / self.run; self.report.mkdir()
        (self.report / 'commit').write_text(self.sha)
        checkout = self.state / 'macos' / 'runs' / self.run
        subprocess.run(['git', 'clone', '-q', '-b', 'main', str(mirror), str(checkout)], check=True)
        return checkout

    def test_finished_marker_with_missing_job_is_incomplete(self):
        p = self.results('linux'); (p / 'tests.exit').unlink()
        self.assertFalse(state_tools.complete(p))

    def test_failure_is_complete_but_not_reusable_success(self):
        p = self.results(passed=False)
        self.assertTrue(state_tools.complete(p))
        self.assertFalse(state_tools.complete(p, passed=True))
        dest = self.state / 'runs' / ('20261013-130000-' + self.sha[:12]) / 'macos'
        self.assertFalse(state_tools.reuse(self.state, self.sha, 'macos', dest))

    def test_invalid_job_exit_is_incomplete(self):
        p = self.results(); (p / 'unit.exit').write_text('corrupt')
        self.assertFalse(state_tools.complete(p))

    def test_terminal_failure_is_not_a_pass_even_with_zero_job_exits(self):
        p = self.results(); (p / 'finished').write_text('1')
        self.assertTrue(state_tools.complete(p))
        self.assertFalse(state_tools.complete(p, passed=True))

    def test_symlinked_exit_file_is_not_a_result(self):
        p = self.results(); (p / 'unit.exit').unlink()
        outside = self.state / 'not-a-job'; outside.write_text('0')
        (p / 'unit.exit').symlink_to(outside)
        self.assertFalse(state_tools.complete(p))

    def test_successful_platform_reused_with_logs_without_build(self):
        self.results()
        dest = self.state / 'runs' / ('20261013-130000-' + self.sha[:12]) / 'macos'
        self.assertTrue(state_tools.reuse(self.state, self.sha, 'macos', dest))
        self.assertEqual('diagnostics', (dest / 'unit.log').read_text())
        self.assertEqual(self.run, (dest / 'reused-from').read_text().strip())

    def test_same_prefix_different_full_sha_is_not_reused(self):
        self.results(); wrong = self.sha[:12] + 'b' * 28
        dest = self.state / 'runs' / ('20261013-130000-' + wrong[:12]) / 'macos'
        self.assertFalse(state_tools.reuse(self.state, wrong, 'macos', dest))

    def test_result_destination_outside_reports_refused(self):
        self.results()
        with self.assertRaises(ValueError):
            state_tools.reuse(self.state, self.sha, 'macos', self.state / 'outside' / 'macos')

    def test_passed_build_removed_and_reports_kept(self):
        checkout = self.checkout(); self.results()
        (checkout / 'bin').mkdir(); (checkout / 'bin' / 'build.dll').write_bytes(b'built')
        actions = state_tools.collect(self.state, 3, 24, 10240, paths=set())
        self.assertTrue(any(x['action'] == 'remove-build' for x in actions))
        self.assertFalse((checkout / 'bin').exists())
        self.assertTrue((self.report / 'macos' / 'unit.log').exists())
        self.assertTrue((checkout / 'Test.csproj').exists())

    def test_active_checkout_keeps_build(self):
        checkout = self.checkout(); self.results(); (checkout / 'bin').mkdir()
        state_tools.collect(self.state, 3, 24, 1, paths={str(checkout / 'bin' / 'open.dll')})
        self.assertTrue(checkout.exists()); self.assertTrue((checkout / 'bin').exists())

    def test_failed_checkout_kept_for_diagnostic_window(self):
        checkout = self.checkout(); self.results(passed=False); (checkout / 'bin').mkdir()
        state_tools.collect(self.state, 3, 24, 1, paths=set())
        self.assertTrue(checkout.exists()); self.assertTrue((checkout / 'bin').exists())

    def test_expired_failed_checkout_removed_without_losing_reports(self):
        checkout = self.checkout(); p = self.results(passed=False)
        marker = p / 'finished'; old = marker.stat().st_mtime
        state_tools.collect(self.state, 3, 24, 10240, now=old + 86401, paths=set())
        self.assertFalse(checkout.exists()); self.assertTrue((p / 'unit.log').exists())

    def test_budget_removes_completed_clone_but_not_report(self):
        checkout = self.checkout(); p = self.results()
        (checkout / 'artifacts').mkdir(); (checkout / 'artifacts' / 'large').write_bytes(b'x' * (2 * 1024 * 1024))
        state_tools.collect(self.state, 3, 24, 1, paths=set())
        self.assertFalse(checkout.exists()); self.assertTrue((p / 'unit.log').exists())

    def test_redirected_macos_root_is_preserved(self):
        checkout = self.checkout(); self.results(); (checkout / 'bin').mkdir()
        outside = self.state / 'outside'; (self.state / 'macos').rename(outside)
        (self.state / 'macos').symlink_to(outside, target_is_directory=True)
        self.assertEqual([], state_tools.collect(self.state, 3, 24, 1, now=9999999999, paths=set()))
        self.assertTrue(checkout.exists()); self.assertTrue((checkout / 'bin').exists())

    def test_unknown_checkout_and_symlink_preserved(self):
        checkout = self.checkout(); self.results()
        outside = self.state / 'valuable'; outside.mkdir(); (outside / 'data').write_text('keep')
        link = checkout.parent / '20261013-130000-aaaaaaaaaaaa'; link.symlink_to(outside, target_is_directory=True)
        state_tools.collect(self.state, 3, 24, 10240, paths=set())
        self.assertTrue((outside / 'data').exists()); self.assertTrue(link.is_symlink())

    def test_dirty_checkout_preserved(self):
        checkout = self.checkout(); self.results(); (checkout / 'Test.csproj').write_text('uncommitted')
        state_tools.collect(self.state, 3, 24, 1, now=9999999999, paths=set())
        self.assertTrue(checkout.exists())

    def test_build_with_database_preserved(self):
        checkout = self.checkout(); self.results(); (checkout / 'bin').mkdir()
        (checkout / 'bin' / 'user.db').write_bytes(b'valuable')
        state_tools.collect(self.state, 3, 24, 10240, paths=set())
        self.assertTrue((checkout / 'bin' / 'user.db').exists())

    def test_retry_backoff_and_limit(self):
        record = state_tools.schedule_retry(self.state, self.sha, 3, now=1000)
        self.assertEqual(1, record['attempts'])
        self.assertFalse(state_tools.retry_due(self.state, self.sha, now=2799))
        self.assertTrue(state_tools.retry_due(self.state, self.sha, now=2800))
        state_tools.schedule_retry(self.state, self.sha, 3, now=2800)
        record = state_tools.schedule_retry(self.state, self.sha, 3, now=6400)
        self.assertEqual(3, record['attempts'])
        self.assertFalse(state_tools.retry_due(self.state, self.sha, now=9999999))


if __name__ == '__main__':
    unittest.main()
