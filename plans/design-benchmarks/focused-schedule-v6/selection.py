"""Build a source-scoped schedule; never inspect benchmark timing values."""
import hashlib
import json
from pathlib import Path

folder = Path(__file__).resolve().parent
previous = folder.parent / 'guid-full-protocol-v5'
edges = json.loads((previous / 'comparison-edges.json').read_text())['comparisons']
protocol = json.loads((previous / 'timing-protocol.json').read_text())
primary = ['local-contract', 'local-pressure', 'native']
components = ['component-lookup', 'component-facade', 'component-generated']
counts = dict(zip(primary + components, [27, 20, 16, 18, 36, 48]))
comparisons = []
points = {name: set() for name in protocol['revisions']}
for edge in edges:
    groups = list(primary)
    if edge['pr'] == 128:
        groups += components
    elif edge['pr'] == 132:
        groups = ['local-contract', 'component-generated']
    elif edge['pr'] == 133:
        groups += ['component-lookup', 'component-generated']
    comparisons.append({**edge, 'groups': groups})
    for endpoint in (edge['base'], edge['change']):
        points[endpoint].update(groups)

selected = {name: [g for g in counts if g in groups] for name, groups in points.items()}
assert len(comparisons) == 11
assert {e['pr'] for e in comparisons} == {123,124,125,126,127,128,129,130,132,133,'cumulative'}
assert all(selected.values())
assert next(e['groups'] for e in comparisons if e['pr'] == 'cumulative') == primary
launches = 3 * sum(map(len, selected.values()))
cases = 3 * sum(counts[g] for groups in selected.values() for g in groups)
assert (launches, cases) == (153, 3690)
result = {
    'purpose': 'Source-scoped family selection before any V5 timing means are reviewed',
    'measurement_protocol_sha256': hashlib.sha256((previous/'timing-protocol.json').read_bytes()).hexdigest(),
    'measurement_settings_unchanged': True,
    'independent_rounds': 3,
    'revisions': protocol['revisions'],
    'counts': counts,
    'comparisons': comparisons,
    'selected_families': selected,
    'family_launches': launches,
    'individual_case_launches': cases,
    'previous_family_launches': 180,
    'previous_individual_case_launches': 4482,
    'priority': 'Cumulative three alternating final/base pairs before remaining PR comparisons',
    'means_reviewed_before_selection': False,
}
target = folder/'selection.json'
content = json.dumps(result, indent=2)+'\n'
if target.exists() and target.read_text() != content:
    raise SystemExit('Existing selection differs; preserve it and use a new schedule version')
target.write_text(content)
print(f'Validated {len(comparisons)} original comparisons; {launches} family launches, {cases} case launches')
