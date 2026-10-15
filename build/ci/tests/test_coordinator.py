import pathlib
import subprocess
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / 'coordinator.sh'


def timeout_functions(script=SCRIPT):
    source = script.read_text()
    return source[source.index('kill_tree()'):source.index('# The checkout steps')]


class TimeoutTests(unittest.TestCase):
    def test_background_command_receives_script_on_stdin(self):
        # Bash 3.2 redirects an asynchronous command's stdin unless explicitly inherited.
        script = 'set -uo pipefail\nlog() { echo "$*"; }\n' + timeout_functions()
        script += '\nprintf "echo script-received\\n" | with_timeout 5 /bin/bash -s\n'
        result = subprocess.run(['/bin/bash', '-c', script], text=True, capture_output=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('script-received\n', result.stdout)

    def test_watchdog_does_not_hold_archive_stdout_open(self):
        script = 'set -uo pipefail\nlog() { echo "$*"; }\n' + timeout_functions()
        script += '\nwith_timeout 8 /bin/echo archive | /bin/cat\n'
        result = subprocess.run(['/bin/bash', '-c', script], text=True, capture_output=True, timeout=3)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('archive\n', result.stdout)


class RetryIntegrationTests(unittest.TestCase):
    def run_retry(self, recover=False):
        import shlex
        source = SCRIPT.read_text()
        functions = source[source.index('run_commit()'):source.index('comment_on()')]
        with tempfile.TemporaryDirectory() as directory:
            state = pathlib.Path(directory)
            sha = 'a' * 40
            previous = state / 'runs' / ('20261013-120000-' + sha[:12])
            previous.mkdir(parents=True)
            (previous / 'commit').write_text(sha)
            for platform in ('windows', 'macos'):
                result = previous / platform; result.mkdir()
                (result / 'finished').touch()
                for job in ('build', 'unit'):
                    (result / (job + '.exit')).write_text('0')
                    (result / (job + '.log')).write_text('passed before retry')
            (state / 'done').mkdir()
            script = 'set -uo pipefail\n' + functions + '\n'
            script += 'state=' + shlex.quote(directory) + '\n'
            script += 'here=' + shlex.quote(str(SCRIPT.parent)) + '\n'
            script += '''
contexts=(linux windows macos); aggregate=aggregate; force_run=0; retry_limit=5
log() { echo "$*"; }
date() { echo 20261013-130000; }
post_status() { echo "STATUS $2 $3"; }
comment_on() { :; }
collect_macos() { :; }
finished() { python3 "$here/coordinator-state.py" complete "$1"; }
summary_of() { echo jobs; }
write_linux() {
  mkdir -p "$1"
  for job in build demo tests diffcoverage properties soak cultures aot packages docs; do echo 0 > "$1/$job.exit"; done
  echo 0 > "$1/finished"
}
run_linux() { echo BUILD-linux; write_linux "$3"; }
run_windows() { echo UNEXPECTED-BUILD-windows; return 1; }
run_macos() { echo UNEXPECTED-BUILD-macos; return 1; }
'''
            script += ('fetch_linux_results() { write_linux "$2"; }\n' if recover else 'fetch_linux_results() { return 1; }\n')
            script += 'run_commit ' + sha + '\n'
            result = subprocess.run(['/bin/bash', '-c', script], text=True, capture_output=True, timeout=10)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertNotIn('UNEXPECTED-BUILD', result.stdout)
            self.assertIn('STATUS aggregate success', result.stdout)
            self.assertEqual('done', (state / 'done' / sha).read_text().strip())
            return result.stdout

    def test_retry_builds_only_missing_platform(self):
        output = self.run_retry()
        self.assertIn('BUILD-linux', output)
        self.assertIn('reused completed windows', output)
        self.assertIn('reused completed macos', output)

    def test_remote_linux_results_recovered_without_any_build(self):
        output = self.run_retry(recover=True)
        self.assertNotIn('BUILD-linux', output)
        self.assertIn('recovered Linux results', output)


class InstallTests(unittest.TestCase):
    def test_sdk_check_accepts_only_net10_and_rejects_older_only_hosts(self):
        installer = (SCRIPT.parent / 'install-coordinator.sh').read_text()
        functions = installer[installer.index('sdks_ok()'):installer.index('echo "This Mac')]
        for versions, expected in (("10.0.103 [/sdk]\n", 0), ("8.0.203 [/sdk]\n9.0.308 [/sdk]\n", 1)):
            with self.subTest(versions=versions):
                result = subprocess.run(['/bin/bash', '-c', functions + '\nsdks_ok\n'],
                                        input=versions, text=True, capture_output=True, timeout=5)
                self.assertEqual(expected, result.returncode, result.stderr)

    def test_update_atomically_preserves_open_old_script_and_versions_helper(self):
        import shlex
        installer = (SCRIPT.parent / 'install-coordinator.sh').read_text()
        functions = installer[installer.index('install_scripts()'):installer.index('if [ "${1:-}" = --update-only ]')]
        with tempfile.TemporaryDirectory() as directory:
            installed = pathlib.Path(directory) / 'coordinator.sh'
            installed.write_text('old running coordinator\n')
            with installed.open() as running:
                script = 'set -euo pipefail\n' + functions
                script += '\ninstall_dir=' + shlex.quote(directory)
                script += '\nhere=' + shlex.quote(str(SCRIPT.parent)) + '\ninstall_scripts\n'
                result = subprocess.run(['/bin/bash', '-c', script], text=True, capture_output=True, timeout=10)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual('old running coordinator\n', running.read())
            self.assertIn('coordinator-state-', installed.read_text())
            helpers = list(pathlib.Path(directory).glob('coordinator-state-*.py'))
            self.assertEqual(1, len(helpers))
            self.assertEqual((SCRIPT.parent / 'coordinator-state.py').read_bytes(), helpers[0].read_bytes())


if __name__ == '__main__':
    unittest.main()
