"""Review only complete preregistered comparisons, using unchanged V5 statistics."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


folder = Path(__file__).resolve().parent
controller = load('focused_controller', folder/'run.py')
review = load('v5_review', controller.previous/'summarize-design.py')
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--comparison', default='all', choices=['all','123','124','125','126','127','128','129','130','132','133','cumulative'])
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
selection = controller.selection
metadata = json.loads((folder/'metadata.json').read_text())
assert metadata['selection'] == selection
assert metadata['selection_sha256'] == hashlib.sha256((folder/'selection.json').read_bytes()).hexdigest()
assert metadata['controller_sha256'] == hashlib.sha256((folder/'run.py').read_bytes()).hexdigest()
controller.frozen()
rows = metadata['runs']
assert all(row['exit'] == 0 for row in rows)
assert len(rows) == len({controller.key(row) for row in rows})
expected_all = {controller.key(row) for row in controller.schedule()}
assert {controller.key(row) for row in rows}.issubset(expected_all)
edges = [e for e in selection['comparisons'] if args.comparison == 'all' or str(e['pr']) == args.comparison]
assert edges
required = {(r,n,g) for edge in edges for r in range(1,4) for n in (edge['base'],edge['change']) for g in edge['groups']}
assert required.issubset({controller.key(row) for row in rows}), 'Comparison does not yet have three complete independent rounds'
if args.comparison == 'all':
    assert len(rows) == selection['family_launches'] and {controller.key(row) for row in rows} == expected_all
for row in rows:
    if controller.key(row) in required:
        controller.verify(row)

results = []
for edge in edges:
    base, change = edge['base'], edge['change']
    assert metadata['sources'][base]['runtime_sha'] == edge['base_sha']
    assert metadata['sources'][change]['runtime_sha'] == edge['change_sha']
    for group in edge['groups']:
        component = group.startswith('component-')
        a = metadata['components'][base]['source'] if component else metadata['sources'][base]
        b = metadata['components'][change]['source'] if component else metadata['sources'][change]
        assert a['source_sha256'] == b['source_sha256']
        assert a['harness_program_sha256'] == b['harness_program_sha256']
        measured = review.pair.compare(review.pair.collect(controller.output,base,group),review.pair.collect(controller.output,change,group))
        for row in measured:
            row['new_work'] = review.new_work(base,change,row)
            row['comparable_hit_allocation_increase'] = review.hit_path(row) and row['new_work'] is None and row['allocation_increase_observed']
        results.append({'pr':edge['pr'],'base':base,'change':change,'group':group,
            'base_source':a,'change_source':b,'rows':measured})
assert not args.output.exists(), 'Preserve previous analyses'
args.output.write_text(json.dumps({
    'selection_sha256': metadata['selection_sha256'],
    'scope': 'Complete selected comparisons only: '+args.comparison,
    'confidence': '95% Student t, df=2 over three independent process means; unchanged V5 calculation',
    'acceptance': 'Not automatic. Inspect new work, decisive regressions, insufficient precision and corresponding hit allocations. Unselected comparisons are not assessed.',
    'comparisons': results,
},indent=2)+'\n')
print('Wrote complete-comparison analysis:',args.output)
