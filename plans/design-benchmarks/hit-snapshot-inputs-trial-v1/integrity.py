"""Validate retained launch evidence and assemble complete paired inputs without selecting runs."""
from pathlib import Path
import hashlib
import json


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def checked_group(study, index):
    study = Path(study)
    prepared = json.loads((study / 'prepared.json').read_text())
    if prepared['inputs_sha256'] != sha(study / 'inputs.json'):
        raise ValueError('Changed preparation inputs')
    intent = json.loads((study / 'launch-intent.json').read_text())
    if intent['prepared_sha256'] != sha(study / 'prepared.json') or intent['inputs_sha256'] != sha(study / 'inputs.json'):
        raise ValueError('Changed launch inputs')
    group = study / 'results' / f'group-{index:02d}'
    if not (group / 'exit').exists() or (group / 'exit').read_text().strip() != '0':
        raise ValueError('Incomplete or failed group')
    names = [item['case'] for item in prepared['groups'][index]]
    expected = {(side, pair, case) for side in ('base', 'head') for pair in range(1, 11) for case in names}
    launches = [json.loads(line) for line in (group / 'launches.jsonl').read_text().splitlines()]
    keys = [(row['side'], row['pair'], row['case']) for row in launches]
    if len(keys) != len(expected) or len(keys) != len(set(keys)) or set(keys) != expected:
        raise ValueError('Missing, duplicate or substituted launch')
    observations = json.loads((group / 'observations.json').read_text())
    if not observations or any(row['external'] for row in observations):
        raise ValueError('Missing quiet guard or competing work')
    if any(not cpus or not set(cpus).issubset(set(range(16))) for row in observations for cpus in row['thread_affinities'].values()):
        raise ValueError('Unexpected observed CPU affinity')
    records = {}
    for row, key in zip(launches, keys):
        path = group / row['output']
        if sha(path) != row['sha256']:
            raise ValueError('Changed launch output')
        record = json.loads(path.read_text())
        if record['case'] != row['case'] or record['actualIterations'] != 12 or len(record['actualMeasurements']) != 12:
            raise ValueError('Wrong case or incomplete actual workload')
        if any(m['Operations'] <= 0 or m['Nanoseconds'] <= 0 for m in record['actualMeasurements']):
            raise ValueError('Invalid raw measurement')
        if record['serverGC'] or record['runtime'] != '.NET 10.0.12' or record['gcLatencyMode'] != 'Interactive':
            raise ValueError('Changed runtime or GC mode')
        during = [o for o in observations if row['started_utc'] <= o['utc'] <= row['finished_utc']]
        if not any(o['thread_affinities'] for o in during):
            raise ValueError('No actual .NET thread affinity evidence for launch')
        records[key] = record
    return records


def write_pairs(records, output, expected_cases):
    output = Path(output)
    expected = {(side, pair, case) for side in ('base', 'head') for pair in range(1, 11) for case in expected_cases}
    if set(records) != expected:
        raise ValueError('Final comparison does not contain exactly ten complete pairs for every declared case')
    output.mkdir()
    for side in ('base', 'head'):
        for pair in range(1, 11):
            lines = [json.dumps(records[side, pair, case]) for case in expected_cases]
            (output / f'{side}-{pair}.jsonl').write_text('\n'.join(lines) + '\n')
