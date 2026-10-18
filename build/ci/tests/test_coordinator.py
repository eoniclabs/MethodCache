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
    def run_linux_capacity(self, granted):
        import shlex
        source = SCRIPT.read_text()
        functions = source[source.index('run_linux()'):source.index('fetch_linux_results()')]
        with tempfile.TemporaryDirectory() as directory:
            state = pathlib.Path(directory)
            script = 'set -uo pipefail\n' + functions + '\n'
            script += 'nimbus_lock=' + shlex.quote(str(state / 'capacity.lock')) + '\n'
            script += 'fixture_dir=' + shlex.quote(directory) + '\n'
            script += 'flock() { return ' + ('0' if granted else '75') + '; }\n'
            script += r'''
nimbus_root=owned; nimbus=unused; timeout_seconds=5; ssh_options=(-o BatchMode=yes)
checkout_script() { echo 'echo CHECKOUT-EXECUTED; mkdir -p artifacts/ci'; }
with_timeout() { echo "LOCAL_TIMEOUT=$1"; shift; "$@"; }
ssh() { echo "REMOTE_COMMAND=$*"; (cd "$fixture_dir" && /bin/bash -s); }
nice() {
  : >&8 || return 47
  [ -z "${METHODCACHE_CI_SHARED_LOCK:-}" ] || return 48
  echo MOCK-BUILD
}
export -f flock nice
fetch_linux_results() { return 0; }
'''
            script += 'run_linux ' + 'a' * 40 + ' run ' + shlex.quote(directory) + '\n'
            result = subprocess.run(['/bin/bash', '-c', script], text=True, capture_output=True, timeout=10)
            return result, (state / 'console.log').read_text()

    def test_linux_capacity_denial_prevents_remote_checkout(self):
        result, console = self.run_linux_capacity(granted=False)
        self.assertNotIn('CHECKOUT-EXECUTED', console)
        self.assertEqual(75, result.returncode, result.stderr)

    def test_linux_capacity_grant_keeps_descriptor_without_nested_reacquisition(self):
        result, console = self.run_linux_capacity(granted=True)
        self.assertEqual(0, result.returncode, result.stderr + console)
        self.assertIn('CHECKOUT-EXECUTED', console)
        self.assertIn('MOCK-BUILD', console)

    def test_linux_timeout_runs_on_server_with_local_cleanup_grace(self):
        result, console = self.run_linux_capacity(granted=True)
        self.assertEqual(0, result.returncode, result.stderr + console)
        self.assertIn('timeout --signal=TERM --kill-after=30s 5 bash -s', console)
        self.assertIn('LOCAL_TIMEOUT=65', console)

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


def commits(count, tag, parent=None):
    """A git fast-import stream of count commits on main, each changing one of 50 files."""
    stream = []
    for i in range(1, count + 1):
        message, content = f'{tag}{i}', f'{tag}-{i}\n'
        stream.append(f'commit refs/heads/main\ncommitter ci <ci@example.com> {1700000000 + i} +0000\n'
                      f'data {len(message)}\n{message}\n')
        if i == 1 and parent:
            stream.append(f'from {parent}\n')
        stream.append(f'M 644 inline f{i % 50}\ndata {len(content)}\n{content}\n')
    return ''.join(stream)


class CheckoutTests(unittest.TestCase):
    def test_fetch_finishes_its_automatic_gc_before_the_clone(self):
        # A gc that the fetch detaches packs and deletes loose objects while the clone copies them, so the clone
        # can lack objects the mirror had a moment before
        import os
        import shlex
        source = SCRIPT.read_text()
        functions = source[source.index('checkout_script()'):source.index('run_linux()')]
        with tempfile.TemporaryDirectory() as directory:
            home = pathlib.Path(directory)
            origin, mirror = home / 'origin', home / 'ci' / 'mirror.git'

            def git(*args, stream=None):
                return subprocess.run(['git', *args], input=stream, text=True, capture_output=True, check=True).stdout

            git('init', '--quiet', '--initial-branch=main', str(origin))
            git('-C', str(origin), 'fast-import', '--quiet', stream=commits(10, 'a'))
            git('clone', '--quiet', '--mirror', str(origin), str(mirror))
            # A mirror that has been fetched often: an automatic gc is due, and it detaches as by default
            for key, value in (('gc.auto', '100'), ('gc.autoDetach', 'true'), ('fetch.unpackLimit', '1000000')):
                git('-C', str(mirror), 'config', key, value)
            git('-C', str(origin), 'fast-import', '--quiet', stream=commits(3000, 'b', parent='refs/heads/main^0'))
            sha = git('-C', str(origin), 'rev-parse', 'main').strip()

            script = 'set -euo pipefail\n' + functions
            script += f'\nclone_url={shlex.quote(str(origin))}; state=/nonexistent; keep_runs_days=3\n'
            script += f'eval "$(checkout_script ci {sha} run)"\n'
            script += 'if [ -e ../../mirror.git/gc.pid ]; then echo "gc still running after the fetch"; exit 1; fi\n'
            script += 'git fsck --connectivity-only\n'
            result = subprocess.run(['/bin/bash', '-c', script], text=True, capture_output=True, timeout=120,
                                    env={**os.environ, 'HOME': str(home)})
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)


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
