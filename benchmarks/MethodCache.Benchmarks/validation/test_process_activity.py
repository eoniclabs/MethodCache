import unittest
from process_activity import external_activity, parse_processes


class ProcessActivityTests(unittest.TestCase):
    def test_flutter_test_workers_are_competing_work(self):
        processes = [(10, 1, '/opt/flutter/bin/cache/dart-sdk/bin/dartvm flutter_tools.snapshot test'),
                     (11, 10, '/opt/flutter/bin/cache/artifacts/engine/darwin-x64/flutter_tester --test')]
        self.assertEqual({10, 11}, {item['pid'] for item in external_activity(processes)})

    def test_benchmark_build_descendants_are_excluded_but_other_tests_are_not(self):
        processes = [(20, 1, 'benchmark-host'), (21, 20, 'dotnet build generated.csproj'),
                     (22, 21, 'dotnet testhost.dll'), (23, 1, 'dotnet test other.csproj')]
        self.assertEqual([23], [item['pid'] for item in external_activity(processes, owned_pid=20)])

    def test_normal_apps_and_backends_are_not_build_or_test_work(self):
        processes = [(30, 1, '/Applications/Browser.app/Contents/MacOS/Browser'),
                     (31, 1, 'sqlservr'), (32, 1, 'redis-server')]
        self.assertEqual([], external_activity(processes))

    def test_javascript_and_python_test_runners_are_detected(self):
        processes = [(40, 1, 'node /project/node_modules/vitest/vitest.mjs'),
                     (41, 1, 'python3 -m pytest tests')]
        self.assertEqual({40, 41}, {item['pid'] for item in external_activity(processes)})

    def test_multiline_arguments_are_not_parsed_as_process_ids(self):
        rows = parse_processes('42 1 app --message first\nsecond line here\n43 1 flutter_tester --test\n')
        self.assertEqual([42, 43], [row[0] for row in rows])


if __name__ == '__main__':
    unittest.main()
