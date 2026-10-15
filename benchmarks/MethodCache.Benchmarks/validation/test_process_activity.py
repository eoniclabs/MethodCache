import unittest
from process_activity import external_activity, parse_processes, ActivityMonitor, parse_snapshot


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

    def test_dormant_old_orphan_is_not_active_but_its_cpu_work_is_detected(self):
        monitor = ActivityMonitor()
        first = '50 1 S 0.0 0:12.00 09-14:37:05 /opt/flutter_tester --test\n'
        self.assertEqual([], monitor.observe(first))
        self.assertEqual([], monitor.observe(first))
        self.assertEqual([50], [item['pid'] for item in monitor.observe(first.replace('0:12.00', '0:12.20'))])

    def test_new_worker_and_busy_old_orphan_remain_active(self):
        snapshot = ('51 1 S 0.0 0:00.00 00:05 flutter_tester --test\n'
                    '52 1 R 80.0 12:00.00 09-14:37:05 flutter_tester --test\n')
        self.assertEqual([51, 52], [item['pid'] for item in ActivityMonitor().observe(snapshot)])

    def test_linux_and_mac_cpu_time_formats_are_supported(self):
        rows = parse_snapshot('53 1 S 0.0 01:02:03 01:30 flutter_tester\n'
                              '54 1 S 0.0 12:03.45 02-01:02:03 flutter_tester\n')
        self.assertEqual([3723, 723.45], [row.cpu_seconds for row in rows])
        self.assertEqual([90, 176523], [row.age_seconds for row in rows])


if __name__ == '__main__':
    unittest.main()
