import json
import os
import pathlib
import subprocess
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / 'publish.sh'
PACKAGES = ('MethodCache', 'MethodCache.Redis', 'MethodCache.SqlServer',
            'MethodCache.Serialization.MessagePack', 'MethodCache.AspNetCore',
            'MethodCache.OpenTelemetry', 'MethodCache.HttpCaching')

FAKE_TOOL = '''#!/usr/bin/env python3
import json, os, pathlib, sys
name = pathlib.Path(sys.argv[0]).name
args = sys.argv[1:]
with open(os.environ['TEST_LOG'], 'a') as log:
    log.write(json.dumps({'tool': name, 'args': args,
        'nuget_key': bool(os.environ.get('NUGET_API_KEY')),
        'symbol_key': bool(os.environ.get('NUGET_SYMBOL_API_KEY')),
        'bws_token': bool(os.environ.get('BWS_ACCESS_TOKEN'))}) + '\\n')
if name == 'gh':
    if args[:2] == ['auth', 'token']: print('github-test-key')
    elif '--include' in args: print('X-OAuth-Scopes: repo, write:packages')
    elif '/status' in args[1]: print(os.environ.get('TEST_STATUS', 'success'))
    elif '--method' not in args: print('a' * 40)
elif name == 'git' and args[0] == 'clone':
    pathlib.Path(args[-1]).mkdir()
elif name == 'dotnet':
    if args == ['--version']: print(os.environ.get('TEST_SDK', '10.0.402'))
    elif args[0] == 'pack':
        dest = pathlib.Path(args[args.index('--output') + 1]); dest.mkdir()
        for package in os.environ['TEST_PACKAGES'].split(','):
            (dest / (package + '.1.0.0.nupkg')).touch()
            if os.environ.get('TEST_MISSING_SYMBOL') != package:
                (dest / (package + '.1.0.0.snupkg')).touch()
    elif args[:2] == ['nuget', 'push'] and 'api.nuget.org' in ' '.join(args):
        assert os.environ['NUGET_API_KEY'] == 'nuget-test-key'
        assert 'BWS_ACCESS_TOKEN' not in os.environ
        assert '--api-key' not in args
        if pathlib.Path(args[2]).with_suffix('.snupkg').exists():
            assert os.environ.get('NUGET_SYMBOL_API_KEY') == os.environ['NUGET_API_KEY']
            assert pathlib.Path(args[2]).with_suffix('.snupkg').exists()
            assert '--symbol-api-key' not in args
elif name == 'bws':
    assert os.environ['BWS_ACCESS_TOKEN'] == 'bws-test-token'
    if os.environ.get('TEST_BWS_FAIL'): sys.exit(1)
    print(json.dumps({'key': os.environ.get('TEST_BWS_KEY', 'METHODCACHE_NUGET_ORG_API_KEY'),
                     'value': os.environ.get('TEST_BWS_VALUE', 'nuget-test-key')}))
'''


class PublishTests(unittest.TestCase):
    def run_publish(self, target='--nuget', **settings):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            tools = root / 'bin'; tools.mkdir()
            fake = tools / 'fake'; fake.write_text(FAKE_TOOL); fake.chmod(0o755)
            for name in ('dotnet', 'gh', 'git', 'bws'):
                (tools / name).symlink_to(fake)
            script = root / 'publish.sh'
            # Keep real host gh/dotnet out of this simulated full-script test.
            script.write_text(SCRIPT.read_text().replace(
                'export PATH="/opt/homebrew/bin:/usr/local/share/dotnet:/usr/local/bin:$PATH"',
                'export PATH="$TEST_BIN:$PATH"'))
            if settings.get('TEST_MISSING_TOOL'):
                script.write_text(script.read_text().replace(
                    'export PATH="$TEST_BIN:$PATH"',
                    'export PATH="$TEST_BIN:$PATH"\n'
                    'command() { if [ "$1" = -v ] && [ "$2" = "$TEST_MISSING_TOOL" ]; '
                    'then return 1; fi; builtin command "$@"; }'))
            log = root / 'calls.jsonl'
            env = dict(os.environ, HOME=directory, TEST_BIN=str(tools), TEST_LOG=str(log),
                       TEST_PACKAGES=','.join(PACKAGES), BWS_ACCESS_TOKEN='bws-test-token',
                       NUGET_API_KEY='inherited-key-must-not-reach-build',
                       METHODCACHE_PUBLISH_DOTNET=str(tools / 'dotnet'),
                       METHODCACHE_NUGET_SECRET_ID='11111111-1111-1111-1111-111111111111')
            env.update(settings)
            if env.pop('TEST_LOCAL_REFERENCE', ''):
                config = root / '.config' / 'methodcache-publish'; config.mkdir(parents=True)
                (config / 'nuget-secret-id').write_text(env.pop('METHODCACHE_NUGET_SECRET_ID') + '\n')
            if env.pop('TEST_TOKEN_FILE', ''):
                (root / '.bws_token').write_text(env.pop('BWS_ACCESS_TOKEN'))
            args = ['/bin/bash', '-x', str(script), 'v1.0.0']
            if target: args.append(target)
            result = subprocess.run(args, env=env, text=True, capture_output=True, timeout=15)
            calls = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
            self.assertNotIn('bws-test-token', result.stdout + result.stderr)
            self.assertNotIn('nuget-test-key', result.stdout + result.stderr)
            return result, calls

    def test_nuget_fetches_one_secret_after_pack_and_pushes_seven_without_key_arguments(self):
        result, calls = self.run_publish()
        self.assertEqual(0, result.returncode, result.stderr)
        pack = next(c for c in calls if c['tool'] == 'dotnet' and c['args'][0] == 'pack')
        self.assertFalse(pack['nuget_key'])
        self.assertFalse(pack['bws_token'])
        secrets = [c for c in calls if c['tool'] == 'bws']
        self.assertEqual(1, len(secrets))
        self.assertGreater(calls.index(secrets[0]), calls.index(pack))
        pushes = [c for c in calls if c['args'][:2] == ['nuget', 'push']]
        self.assertEqual(7, len(pushes))
        for push in pushes:
            self.assertTrue(push['nuget_key'])
            self.assertFalse(push['bws_token'])
            self.assertNotIn('--api-key', push['args'])
            self.assertIn('--skip-duplicate', push['args'])
        self.assertFalse(any(c['args'][:2] == ['auth', 'token'] for c in calls))

    def test_default_is_nuget(self):
        result, calls = self.run_publish(target=None)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, len([c for c in calls if c['tool'] == 'bws']))
        pushes = [c for c in calls if c['args'][:2] == ['nuget', 'push']]
        self.assertTrue(all('https://api.nuget.org/v3/index.json' in c['args'] for c in pushes))

    def test_symbol_companions_receive_the_same_environment_key(self):
        result, calls = self.run_publish()
        self.assertEqual(0, result.returncode, result.stderr)
        pack = next(c for c in calls if c['tool'] == 'dotnet' and c['args'][0] == 'pack')
        self.assertFalse(pack['symbol_key'])
        pushes = [c for c in calls if c['args'][:2] == ['nuget', 'push']]
        self.assertEqual(7, len(pushes))
        self.assertTrue(all(c['symbol_key'] for c in pushes))
        self.assertTrue(all('--symbol-api-key' not in c['args'] for c in pushes))

    def test_missing_dependencies_report_the_tool_and_never_build(self):
        for tool in ('bws', 'jq'):
            with self.subTest(tool=tool):
                result, calls = self.run_publish(TEST_MISSING_TOOL=tool)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(tool + ' is required', result.stderr)
                self.assertFalse(any(c['tool'] in ('git', 'bws', 'dotnet') for c in calls))

    def test_failed_ci_never_builds_or_fetches_secret(self):
        result, calls = self.run_publish(TEST_STATUS='failure')
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(any(c['tool'] in ('git', 'bws', 'dotnet') for c in calls))

    def test_local_secret_reference_and_existing_host_token_file(self):
        result, calls = self.run_publish(TEST_LOCAL_REFERENCE='1', TEST_TOKEN_FILE='1')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, len([c for c in calls if c['tool'] == 'bws']))

    def test_missing_secret_reference_stops_before_build(self):
        result, calls = self.run_publish(METHODCACHE_NUGET_SECRET_ID='')
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(any(c['tool'] in ('git', 'bws', 'dotnet') for c in calls))

    def test_old_or_prerelease_sdk_fails_before_build_or_secret_read(self):
        for version in ('10.0.103', '9.0.308', '10.0.300-preview.1', 'unknown'):
            with self.subTest(version=version):
                result, calls = self.run_publish(TEST_SDK=version)
                self.assertNotEqual(0, result.returncode)
                self.assertFalse(any(c['tool'] in ('git', 'bws') for c in calls))

    def test_bws_failure_or_empty_value_never_pushes(self):
        for setting in ({'TEST_BWS_FAIL': '1'}, {'TEST_BWS_VALUE': ''}, {'TEST_BWS_KEY': 'WRONG_SECRET'}):
            result, calls = self.run_publish(**setting)
            self.assertNotEqual(0, result.returncode)
            self.assertFalse(any(c['args'][:2] == ['nuget', 'push'] for c in calls))

    def test_missing_package_never_fetches_secret(self):
        result, calls = self.run_publish(TEST_PACKAGES=','.join(PACKAGES[:-1]))
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(any(c['tool'] == 'bws' for c in calls))

    def test_missing_symbol_companion_never_fetches_secret(self):
        result, calls = self.run_publish(TEST_MISSING_SYMBOL=PACKAGES[-1])
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Missing ' + PACKAGES[-1] + ' 1.0.0 symbol package', result.stderr)
        self.assertFalse(any(c['tool'] == 'bws' for c in calls))

    def test_invalid_target_never_contacts_services(self):
        result, calls = self.run_publish(target='--typo')
        self.assertEqual(2, result.returncode)
        self.assertEqual([], calls)

    def test_explicit_github_packages(self):
        result, calls = self.run_publish(target='--github')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(any(c['tool'] == 'bws' for c in calls))
        pushes = [c for c in calls if c['args'][:2] == ['nuget', 'push']]
        self.assertEqual(7, len(pushes))
        self.assertTrue(all('https://nuget.pkg.github.com/eoniclabs/index.json' in c['args'] for c in pushes))


if __name__ == '__main__':
    unittest.main()
