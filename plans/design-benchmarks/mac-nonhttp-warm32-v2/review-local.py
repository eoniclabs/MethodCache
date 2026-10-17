"""Review the complete fixed F05 post-correction follow-up, preserving the prior full study."""
from pathlib import Path
import datetime
import hashlib
import json
import math
import os
import shutil
import subprocess

source = Path('/tmp/methodcache-nonhttp-warm32-v2')
out = Path('/tmp/methodcache-nonhttp-warm32-evidence-v2')
repo = Path('/Users/johan/.codex/worktrees/design-combined-gates-v1/MethodCache')
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
assert (source / 'sequence.exit').read_text().strip() == '0'
try:
    os.kill(int((source / 'sequence.pid').read_text()), 0)
except ProcessLookupError:
    pass
else:
    raise RuntimeError('Wait for the actual fixed local-CPU sequence handle to terminate')
inputs = json.loads((source / 'inputs.json').read_text())
prepared = json.loads((source / 'prepared.json').read_text())
assert sha(source / 'inputs.json') == prepared['inputs_sha256']
assert sha(source / 'run.py') == inputs['controller_sha256']
assert len(inputs['comparisons']) == 5
cases = inputs['cases']['main']
assert len(cases) == len(set(cases)) == 9
assert sha(source / 'queue.py') == json.loads((source / 'sequence-intent.json').read_text())['queue_sha256']
for relative, expected in inputs['source_sha256'].items():
    assert sha(source / 'harness' / relative) == expected
    for point in ['baseline','final']: assert sha(source / 'trees' / point / relative) == expected
for relative, expected in inputs['frozen_reviewers'].items(): assert sha(source / relative) == expected
for entries in prepared['binaries'].values():
    for entry in entries: assert sha(source / entry['path']) == entry['sha256']
equivalence = json.loads((source / 'source-equivalence-intent.json').read_text())
current_differences = []
for point, entries in prepared['production_sources'].items():
    for entry in entries:
        relative, expected = entry['path'], entry['sha256']
        assert sha(source / 'trees' / point / relative) == expected
        if point == 'final' and sha(repo / relative) != expected:
            assert equivalence['original_sources'].get(relative) == expected
            assert equivalence['candidate_sources'].get(relative) == sha(repo / relative)
            current_differences.append(relative)
assert not current_differences or set(current_differences) == set(equivalence['candidate_sources'])
records = {}
for group in inputs['comparisons']:
    study = source / ('pr-' + group['pr'])
    assert (study / 'exit').read_text().strip() == '0'
    intent = json.loads((study / 'intent.json').read_text())
    assert intent['inputs_sha256'] == sha(source / 'inputs.json')
    assert intent['prepared_sha256'] == sha(source / 'prepared.json')
    assert intent['comparison'] == group
    local = [c for family in ['main', 'int'] for c in group['cases'][family]]
    expected = {(s, n, c) for s in ['base', 'head'] for n in range(1, inputs['pairs']+1) for c in local}
    launches = [json.loads(l) for l in (study / 'launches.jsonl').read_text().splitlines()]
    keys = [(l['side'], l['pair'], l['case']) for l in launches]
    assert len(keys) == len(set(keys)) == len(expected) and set(keys) == expected
    ordered = []
    for pair in range(1, inputs['pairs']+1):
        for family in ['main', 'int']:
            for case in group['cases'][family]:
                for side in (['base', 'head'] if pair % 2 else ['head', 'base']):
                    ordered.append((side, pair, case))
    assert keys == ordered, 'Predeclared alternating order changed'
    assert not (records.keys() & expected)
    observations = json.loads((study / 'observations.json').read_text())
    confirmed = json.loads((study / 'quiet-confirmed.json').read_text())
    readiness = [o for o in observations if o['phase'] == 'preflight']
    assert confirmed['consecutive_quiet_samples'] == 60 and len(readiness) >= 60
    assert all(not o['external'] and o['other_process_cpu_percent'] <= confirmed['other_process_cpu_limit'] for o in readiness[-60:])
    assert all(not o['external'] for o in observations if o['phase'] == 'measurement')
    for row, key in zip(launches, keys):
        path = study / row['output']
        assert path.resolve().is_relative_to(study.resolve()) and sha(path) == row['sha256']
        record = json.loads(path.read_text())
        assert record['case'] == row['case'] and row['point'] == group[row['side']]
        assert record['actualIterations'] == len(record['actualMeasurements']) == 12
        assert record['warmupCount'] == len(record['warmupMeasurements']) == 32
        assert record['runtime'] == '.NET 10.0.3' and not record['serverGC'] and record['gcLatencyMode'] == 'Interactive'
        assert not isinstance(record['ns'], bool) and math.isfinite(record['ns']) and record['ns'] > 0
        assert all(m['Operations'] > 0 and math.isfinite(m['Nanoseconds']) and m['Nanoseconds'] > 0 for m in record['actualMeasurements'])
        assert any(o['phase'] == 'measurement' and row['started_utc'] <= o['utc'] <= row['finished_utc'] for o in observations)
        records[key] = record
assert len(records) == 360
out.mkdir()
raw = out / 'raw'
raw.mkdir()
for path in source.iterdir():
    if path.is_file():
        shutil.copyfile(path, raw / path.name)
shutil.copytree(source / 'harness', raw / 'harness')
shutil.copytree(source / 'helper', raw / 'helper', ignore=shutil.ignore_patterns('bin','obj'))
for group in inputs['comparisons']:
    name = 'pr-' + group['pr']
    shutil.copytree(source / name, raw / name)

paired = out / 'paired-input'
paired.mkdir()
for side in ['base', 'head']:
    for pair in range(1, inputs['pairs']+1):
        (paired / f'{side}-{pair}.jsonl').write_text('\n'.join(json.dumps(records[side, pair, c]) for c in cases) + '\n')
results = {}
for label, rules in [('original', source / 'accepted.json'), ('accepted', repo / 'benchmarks/MethodCache.CostProfile/accepted.json')]:
    (out / (label + '-rules.json')).write_bytes(rules.read_bytes())
    result = subprocess.run(['python3', str(repo / 'benchmarks/MethodCache.CostProfile/paired.py'), str(paired),
                             '--accepted', str(rules), '--markdown', str(out / (label + '-timing.md'))], text=True, capture_output=True)
    (out / (label + '-review.log')).write_text(result.stdout + result.stderr)
    (out / (label + '-review.exit')).write_text(str(result.returncode) + '\n')
    results[label] = result.returncode
    print(label, result.stdout, 'review exit', result.returncode)
(out / 'validation.json').write_text(json.dumps({
    'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'scope': 'All9predeclaredHTTP-independentcases/20pairs/360freshprocesses/32warmups. Actual A2 binary provenance retained. No launch omitted or pooled with other studies.',
    'input_sha256': sha(source / 'inputs.json'), 'prepared_sha256': sha(source / 'prepared.json'),
    'launches_verified': 360, 'cases': 9, 'pairs': 20, 'warmups': 32, 'actual_iterations': 12,
    'reviews': results, 'actual_measured_source_point': 'A2; original source/binary receipts retained',
    'published_http_only_source_differences': current_differences,
    'source_equivalence_scope': 'These nine GeneratedContract/DesignMemory cases do not register or call HTTP. Only the two exact predeclared HTTP source hashes may differ; no fields/layout or non-HTTP path changes.', 'files': [{'path': str(p.relative_to(out)), 'sha256': sha(p)} for p in sorted(raw.rglob('*')) if p.is_file()],
}, indent=2) + '\n')
print('Complete nine-case non-HTTP confirmation verified; actual gate status is each separate reviewer exit')
