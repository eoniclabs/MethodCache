"""Match measured cases to BenchmarkDotNet's compiled, filtered case inventory."""
import json
from pathlib import Path
import subprocess


def compiled_inventory(harness, filters):
    harness = Path(harness)
    command = ['dotnet', str(harness/'bin/Release/net10.0/DesignValidation.dll'),
               '--workload-inventory', *filters]
    return normalize_inventory(json.loads(subprocess.check_output(command, cwd=harness, text=True)))


def normalize_inventory(entries):
    if not isinstance(entries, list) or not entries:
        raise ValueError('The compiled workload inventory must be a nonempty list.')
    normalized = []
    names = set()
    for entry in entries:
        if not isinstance(entry, dict) or any(
            not isinstance(entry.get(key), str) or not entry[key]
            for key in ('full_name', 'report_type')
        ):
            raise ValueError('Each compiled case needs its full name and report type.')
        if entry['full_name'] in names:
            raise ValueError('Duplicate compiled case: ' + entry['full_name'])
        names.add(entry['full_name'])
        normalized.append({key: entry[key] for key in ('full_name', 'report_type')})
    return sorted(normalized, key=lambda entry: entry['full_name'])


def require_same_inventory(actual, expected):
    if normalize_inventory(actual) != normalize_inventory(expected):
        raise ValueError('Compiled workload inventories differ across revisions.')


def verify_reports(reports, expected, iterations=12):
    expected = normalize_inventory(expected)
    reports = list(reports)
    if len(reports) != len({entry['report_type'] for entry in expected}):
        raise ValueError('Unexpected workload report count.')
    measured = []
    for report in reports:
        data = json.loads(Path(report).read_text())
        cases = data.get('Benchmarks')
        if not isinstance(cases, list) or not cases:
            raise ValueError('A workload report has no benchmark cases.')
        for case in cases:
            name = case.get('FullName')
            if not isinstance(name, str) or not name or not case.get('Statistics'):
                raise ValueError('A benchmark failed or lacks its name/statistics.')
            samples = case.get('Measurements', [])
            actual = [sample for sample in samples if sample.get('IterationMode') == 'Workload'
                      and sample.get('IterationStage') == 'Actual']
            if len(actual) != iterations:
                raise ValueError('Unexpected actual workload iteration count: ' + name)
            measured.append(name)
    if len(set(measured)) != len(measured):
        raise ValueError('Duplicate measured benchmark case.')
    if set(measured) != {entry['full_name'] for entry in expected}:
        raise ValueError('Measured benchmark cases do not match the compiled inventory.')
    return len(measured)
