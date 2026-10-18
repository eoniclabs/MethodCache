import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'storage_gc.py'
spec = importlib.util.spec_from_file_location('storage_gc', SCRIPT)
gc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gc)


class StorageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.now = 2000000000

    def project(self, parent=None):
        p = (parent or self.root) / 'project'
        p.mkdir(parents=True)
        subprocess.run(['git', 'init', '-q', str(p)], check=True)
        (p / '.gitignore').write_text('bin/\nobj/\n')
        (p / 'Test.csproj').write_text('<Project />')
        subprocess.run(['git', '-C', str(p), 'add', '.'], check=True)
        subprocess.run(['git', '-C', str(p), '-c', 'user.name=test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'initial'], check=True)
        (p / 'bin').mkdir()
        (p / 'bin/app.dll').write_bytes(b'output')
        return p

    def test_idle_local_build_removed_without_removing_source(self):
        p = self.project()
        gc.clean_builds(p, 0, self.now, set(), apply=True)
        self.assertFalse((p / 'bin').exists())
        self.assertTrue((p / 'Test.csproj').exists())

    def test_open_checkout_preserved(self):
        p = self.project()
        gc.clean_builds(p, 0, self.now, {str(p / 'Test.csproj')}, apply=True)
        self.assertTrue((p / 'bin').exists())

    def test_recent_output_preserved(self):
        p = self.project()
        os.utime(p / 'bin/app.dll', (self.now - 5, self.now - 5))
        gc.clean_builds(p, 60, self.now, set(), apply=True)
        self.assertTrue((p / 'bin').exists())

    def test_database_and_tracked_output_preserved(self):
        p = self.project()
        (p / 'bin/user.db').write_bytes(b'valuable')
        gc.clean_builds(p, 0, self.now, set(), apply=True)
        self.assertTrue((p / 'bin/user.db').exists())
        (p / 'bin/user.db').unlink()
        subprocess.run(['git', '-C', str(p), 'add', '-f', 'bin/app.dll'], check=True)
        gc.clean_builds(p, 0, self.now, set(), apply=True)
        self.assertTrue((p / 'bin/app.dll').exists())

    def test_symlink_output_and_dirty_checkout_preserved(self):
        p = self.project()
        (p / 'Test.csproj').write_text('unsaved')
        gc.clean_builds(p, 0, self.now, set(), apply=True)
        self.assertTrue((p / 'bin').exists())
        outside = self.root / 'outside'; outside.mkdir()
        (outside / 'keep').write_text('valuable')
        (p / 'obj').symlink_to(outside, target_is_directory=True)
        gc.clean_builds(p, 0, self.now, set(), apply=True, snapshot=True)
        self.assertTrue((outside / 'keep').exists())

    def test_staging_and_windows_archives_are_recognized(self):
        for name, kind, marker in [('staging-aaaaaaaaaaaa-fast-20260501T120000Z', 'skribera-source', 'Skribera.sln'),
                                   ('pr-12-aaaaaaaaaaaa-20260501T120000Z', 'skribera-media', 'result.json')]:
            p = self.root / name; p.mkdir()
            (p / marker).write_text('{}')
            self.assertIsNotNone(gc.archive_info(p, {'kind': kind}, self.now))
        p = self.root / 'personal'; p.mkdir()
        self.assertIsNone(gc.archive_info(p, {'kind': 'skribera-source'}, self.now))

    def test_failed_archive_has_finite_diagnostic_window(self):
        p = self.root / 'pr-12-aaaaaaaaaaaa-20260501T120000Z'; p.mkdir()
        (p / 'Skribera.sln').touch()
        (p / '.ci-last-used.json').write_text(json.dumps({'runId': p.name, 'pr': '12', 'shaPrefix': 'a' * 12,
                                                       'outcome': 'failure', 'lastUsed': '2033-05-17T02:00:00+00:00'}))
        info = gc.archive_info(p, {'kind': 'skribera-source'}, self.now)
        self.assertFalse(info['passed'])
        self.assertGreater(info['finished'], 0)

    def test_logs_survive_archive_removal(self):
        p = self.root / 'run'; p.mkdir()
        result = p / 'artifacts/ci'; result.mkdir(parents=True)
        (result / 'build.log').write_text('diagnostic')
        (result / 'build.exit').write_text('1')
        destination = self.root / 'retained'
        gc.preserve_logs(p, destination)
        self.assertEqual('diagnostic', (destination / 'run/artifacts/ci/build.log').read_text())

    def test_build_directory_activity_is_rechecked_before_deletion(self):
        from unittest.mock import patch
        p = self.project()
        with patch.object(gc, 'activity', return_value={str(p / 'bin/app.dll')}):
            gc.clean_builds(p, 0, self.now, set(), apply=True, recheck=True)
        self.assertTrue((p / 'bin').exists())

    @unittest.skipIf(os.name == 'nt', 'Linux runner shell integration')
    def test_successful_linux_runner_collects_output_at_exit(self):
        import shlex
        p = self.project()
        scripts = p / 'build/ci'; scripts.mkdir(parents=True)
        import shutil
        shutil.copy2(SCRIPT, scripts / 'storage_gc.py')
        subprocess.run(['git', '-C', str(p), 'add', 'build'], check=True)
        subprocess.run(['git', '-C', str(p), '-c', 'user.name=test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'collector'], check=True)
        source = (SCRIPT.parent / 'linux.sh').read_text()
        cleanup = source[source.index('cleanup()'):source.index('trap cleanup EXIT')]
        script = 'repo_dir=' + shlex.quote(str(p)) + '\nlabel=test; failed=0; collect_output=true\ndocker() { :; }\n' + cleanup + '\ncleanup\n'
        result = subprocess.run(['/bin/bash', '-c', script], capture_output=True, text=True, timeout=90)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse((p / 'bin').exists())

    @unittest.skipIf(os.name == 'nt', 'Linux runner shell integration')
    def test_partial_linux_invocation_keeps_outputs_for_later_no_build_jobs(self):
        import shlex, shutil
        p = self.project()
        scripts = p / 'build/ci'; scripts.mkdir(parents=True)
        shutil.copy2(SCRIPT, scripts / 'storage_gc.py')
        subprocess.run(['git', '-C', str(p), 'add', 'build'], check=True)
        subprocess.run(['git', '-C', str(p), '-c', 'user.name=test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'collector'], check=True)
        source = (SCRIPT.parent / 'linux.sh').read_text()
        cleanup = source[source.index('cleanup()'):source.index('trap cleanup EXIT')]
        script = 'repo_dir=' + shlex.quote(str(p)) + '\nlabel=test; failed=0; collect_output=false\ndocker() { :; }\n' + cleanup + '\ncleanup\n'
        result = subprocess.run(['/bin/bash', '-c', script], capture_output=True, text=True, timeout=90)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue((p / 'bin/app.dll').exists())

    def archive(self, age=100000):
        import datetime
        p = self.root / 'runs/pr-12-aaaaaaaaaaaa-20260501T120000Z'; p.mkdir(parents=True)
        (p / 'Skribera.sln').touch()
        (p / '.ci-last-used.json').write_text(json.dumps({'runId': p.name, 'pr': '12', 'shaPrefix': 'a' * 12,
            'outcome': 'failure', 'lastUsed': datetime.datetime.fromtimestamp(self.now - age, datetime.timezone.utc).isoformat()}))
        (p / 'external-ci.log').write_text('keep diagnostics')
        cfg = {'root': str(p.parent), 'kind': 'skribera-source', 'retained': str(self.root / 'retained'), 'max_gib': 0}
        return p, cfg

    def test_expired_failure_retired_with_logs_but_recent_failure_protected_even_over_budget(self):
        from unittest.mock import patch
        import time
        p, cfg = self.archive(age=3600)
        with patch.object(gc, 'activity', return_value=set()):
            gc.collect_archives(cfg, {}, self.now, set(), True, time.monotonic() + 30)
        self.assertTrue(p.exists())
        with patch.object(gc, 'activity', return_value=set()):
            gc.collect_archives(cfg, {}, self.now + 86400, set(), True, time.monotonic() + 30)
        self.assertFalse(p.exists())
        self.assertEqual('keep diagnostics', (self.root / 'retained' / p.name / 'external-ci.log').read_text())

    def test_archive_internal_link_removed_without_touching_target(self):
        from unittest.mock import patch
        import time
        p, cfg = self.archive()
        outside = self.root / 'shared-media'; outside.mkdir()
        (outside / 'vm.iso').write_text('shared VM media')
        (p / 'media').symlink_to(outside, target_is_directory=True)
        with patch.object(gc, 'activity', return_value=set()):
            gc.collect_archives(cfg, {}, self.now, set(), True, time.monotonic() + 30)
        self.assertFalse(p.exists())
        self.assertEqual('shared VM media', (outside / 'vm.iso').read_text())

    def test_archive_database_and_new_activity_prevent_retirement(self):
        from unittest.mock import patch
        import time
        p, cfg = self.archive()
        (p / 'user.db').write_text('valuable')
        gc.collect_archives(cfg, {}, self.now, set(), True, time.monotonic() + 30)
        self.assertTrue((p / 'user.db').exists())
        (p / 'user.db').unlink()
        with patch.object(gc, 'activity', return_value={str(p / 'external-ci.log')}):
            gc.collect_archives(cfg, {}, self.now, set(), True, time.monotonic() + 30)
        self.assertTrue(p.exists())

    def test_ci_build_starting_after_inventory_preserves_generated_output(self):
        from unittest.mock import patch
        import time
        p, cfg = self.archive()
        (p / 'Test.csproj').write_text('<Project/>')
        (p / 'bin').mkdir(); (p / 'bin/app.dll').write_text('in use')
        with patch.object(gc, 'activity', return_value={str(p / 'bin/app.dll')}):
            gc.collect_archives(cfg, {}, self.now, set(), True, time.monotonic() + 30)
        self.assertTrue((p / 'bin/app.dll').exists())

    def test_windows_workspace_metadata_proves_last_run_and_pr(self):
        p = self.root / 'v3-pr-12'; p.mkdir()
        (p / 'Skribera.sln').touch()
        m = p / '.ci-last-used.json'
        record = {'runId': 'pr-12-aaaaaaaaaaaa-20260501T120000Z', 'pr': '12', 'sha': 'a' * 40,
                  'outcome': 'success', 'lastUsed': '2026-05-01T12:00:00Z'}
        m.write_text(json.dumps(record))
        self.assertTrue(gc.archive_info(p, {'kind': 'skribera-windows-workspace'}, self.now)['passed'])
        record['pr'] = '13'; m.write_text(json.dumps(record))
        self.assertIsNone(gc.archive_info(p, {'kind': 'skribera-windows-workspace'}, self.now))

    def test_windows_build_process_protects_all_roots(self):
        from unittest.mock import patch
        response = subprocess.CompletedProcess([], 0, stdout=json.dumps([{'ProcessId': 999999, 'Name': 'dotnet.exe',
            'ExecutablePath': 'C:\\mc\\dotnet\\dotnet.exe', 'CommandLine': 'dotnet build'}]))
        with patch.object(gc.subprocess, 'run', return_value=response):
            self.assertTrue(gc.active(self.root, gc.windows_activity()))

    def test_xcode_index_is_generated_but_app_database_and_active_build_are_preserved(self):
        from unittest.mock import patch
        import time
        root = self.root / 'DerivedData'; p = root / 'App-abcdef'; p.mkdir(parents=True)
        (p / 'info.plist').touch()
        index = p / 'Index.noindex'; index.mkdir(); (index / 'index.db').write_text('generated index')
        build = p / 'Build'; build.mkdir(); (build / 'user.db').write_text('valuable app data')
        with patch.object(gc, 'activity', return_value=set()):
            gc.collect_xcode(root, {}, self.now, set(), True, time.monotonic() + 30)
        self.assertFalse(index.exists())
        self.assertTrue((build / 'user.db').exists())
        logs = p / 'Logs'; logs.mkdir(); (logs / 'build.log').touch()
        gc.collect_xcode(root, {}, self.now, {str(build / 'user.db')}, True, time.monotonic() + 30)
        self.assertTrue(logs.exists())

    def test_time_limited_sweeps_rotate_roots_instead_of_starving_later_roots(self):
        from unittest.mock import patch
        roots = [{'root': str(self.root / n), 'kind': 'skribera-source'} for n in ('a', 'b', 'c')]
        calls = []
        with patch.object(gc, 'activity', return_value=set()), patch.object(gc.time, 'monotonic', return_value=0) as clock:
            def collect(cfg, *args):
                calls.append(cfg['root']); clock.return_value = 1000
            with patch.object(gc, 'collect_archives', side_effect=collect):
                config = {'archives': roots, 'max_seconds': 10, '_start': 1}
                gc.run(config, False)
        self.assertEqual([str(self.root / 'b')], calls)
        self.assertEqual(2, config['_next_start'])

    def test_scan_rotation_is_persisted_between_scheduled_invocations(self):
        from unittest.mock import patch
        cfg = self.root / 'config.json'; cfg.write_text('{}')
        starts = []
        def run(config, apply):
            starts.append(config['_start'])
            config['_next_start'] = config['_start'] + 1
        with patch('sys.argv', [str(SCRIPT), '--config', str(cfg), '--apply']), patch.object(gc, 'run', side_effect=run):
            gc.main(); gc.main()
        self.assertEqual([0, 1], starts)
        self.assertEqual({'next': 2}, json.loads(cfg.with_suffix('.progress.json').read_text()))

    def test_git_timeout_and_missing_tracked_inventory_preserve_output(self):
        from unittest.mock import patch
        p = self.project()
        with patch.object(gc.subprocess, 'run', side_effect=subprocess.TimeoutExpired('git', 30)):
            self.assertIsNone(gc.git(p, 'status', '--porcelain'))
        original = gc.git
        def git(root, *args):
            return None if args[0] == 'ls-files' else original(root, *args)
        with patch.object(gc, 'git', side_effect=git):
            gc.clean_builds(p, 0, self.now, set(), apply=True)
        self.assertTrue((p / 'bin/app.dll').exists())

    def test_untracked_source_directory_protects_local_build_output(self):
        p = self.project()
        source = p / 'unsaved'; source.mkdir(); (source / 'New.cs').write_text('new source')
        gc.clean_builds(p, 0, self.now, set(), apply=True)
        self.assertTrue((p / 'bin/app.dll').exists())
        self.assertTrue((source / 'New.cs').exists())


if __name__ == '__main__':
    unittest.main()
