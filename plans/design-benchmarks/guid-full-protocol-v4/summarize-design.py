"""Summarize complete frozen families using actual PR edges, never flat neighbor pairs."""
import argparse
import importlib.util
import json
from pathlib import Path

spec = importlib.util.spec_from_file_location('pair',Path(__file__).with_name('summarize-pair.py'))
pair = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pair)


def complete_runs(manifest):
    primary = set(manifest['sources'])
    components = set(manifest['components'])
    expected = {(round_number,name,group) for round_number in range(1,4) for name in primary
                for group in ('local-contract','local-pressure','native')}
    expected |= {(round_number,name,group) for round_number in range(1,4) for name in components
                 for group in ('component-lookup','component-facade','component-generated')}
    actual = [(run['round'],run['revision'],run['group']) for run in manifest['runs'] if run['exit']==0]
    if len(actual)!=len(set(actual)) or set(actual)!=expected or len(actual)!=180 or len(manifest['runs'])!=180:
        raise ValueError('Incomplete, repeated, failed or substituted fixed series; all 180 launches are required')


def new_work(base, change, row):
    _, type_name, _, parameters = row['case']
    before_roundtrip = {'base','pr1-base','pr1','pr2','pr3'}
    before_http = before_roundtrip | {'pr4','pr5','prefix'}
    if type_name == 'DesignL2WorkloadBenchmarks' and 'RoundTrip=True' in parameters and base in before_roundtrip and change not in before_roundtrip:
        return 'New serialization round-trip validation, absent at baseline'
    if type_name == 'DesignHttpWorkloadBenchmarks' and 'KeepCookies=True' in parameters and base in before_http and change not in before_http:
        return 'New KeepCookies policy; baseline used an explicitly uncached client'
    return None


def hit_path(row):
    _, type_name, method, parameters = row['case']
    if method.endswith('Hit') or method in ('ContendedHits','ReadyStringLookup','FullSpanLookup','PrefixLookup','ReadyKeySync','BuiltKeySync','ReadyKeyTask','BuiltKeyTask'):
        return True
    if type_name == 'DesignL2WorkloadBenchmarks':return method == 'Read'
    if type_name == 'DesignHttpWorkloadBenchmarks':return method == 'Fresh' and 'KeepCookies=False' in parameters
    return False


def summarize(root, manifest):
    complete_runs(manifest)
    edges = manifest['comparisons']
    expected_prs = {123,124,125,126,127,128,129,130,132,133,'cumulative'}
    if edges != manifest['protocol']['comparisons'] or len(edges)!=11 or {edge['pr'] for edge in edges}!=expected_prs:
        raise ValueError('Missing, repeated or changed actual PR comparison edges')
    results = []
    for edge in edges:
        base, change = edge['base'],edge['change']
        if manifest['sources'][base]['runtime_sha']!=edge['base_sha'] or manifest['sources'][change]['runtime_sha']!=edge['change_sha']:
            raise ValueError('A comparison does not match its actual frozen PR base/head')
        groups = ['local-contract','local-pressure','native']
        if base in manifest['components'] and change in manifest['components']:
            groups += ['component-lookup','component-facade','component-generated']
        for group in groups:
            component = group.startswith('component-')
            a = manifest['components'][base]['source'] if component else manifest['sources'][base]
            b = manifest['components'][change]['source'] if component else manifest['sources'][change]
            if a['source_sha256']!=b['source_sha256'] or a['harness_program_sha256']!=b['harness_program_sha256']:
                raise ValueError('Different workload or program bytes in a comparison')
            rows = pair.compare(pair.collect(root,base,group),pair.collect(root,change,group))
            for row in rows:
                row['new_work'] = new_work(base,change,row)
                row['comparable_hit_allocation_increase'] = hit_path(row) and row['new_work'] is None and row['allocation_increase_observed']
            results.append({'pr':edge['pr'],'base':base,'change':change,'group':group,
                'base_source':a,'change_source':b,'rows':rows})
    return {'scope':'All exact PR edges and cumulative baseline, three independent process means per point/family',
        'confidence':'95% Student t, df=2; within-process iterations are not independent launches',
        'acceptance':'Not automatic: inspect feature equivalence, decisive regressions, uncertain intervals and corresponding hit allocations',
        'comparisons':results}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root',type=Path)
    parser.add_argument('--output',type=Path,required=True)
    args = parser.parse_args()
    manifest = json.loads((args.root/'metadata.json').read_text())
    args.output.write_text(json.dumps(summarize(args.root,manifest),indent=2)+'\n')


if __name__=='__main__':main()
