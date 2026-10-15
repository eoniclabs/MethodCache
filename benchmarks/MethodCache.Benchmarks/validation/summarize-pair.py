"""Report independent run means (three, six or nine launches); never replace them with within-run BDN samples.

No automatic acceptance: feature equivalence and pressure scenarios still require review.
"""
from pathlib import Path
import argparse,json,math,statistics
# Two-sided Student-t 0.975 quantiles, df = independent launches - 1.
# NIST table: https://www.itl.nist.gov/div898/handbook/eda/section3/eda3672.htm
T95_BY_LAUNCHES={3:4.302652729696142,6:2.570581835636305,9:2.306004135204166}

def interval(values):
    if len(values) not in T95_BY_LAUNCHES or any(not math.isfinite(v) or v<=0 for v in values):
        raise ValueError('Expected three, six or nine positive finite independent run means')
    mean=statistics.mean(values)
    half=T95_BY_LAUNCHES[len(values)]*statistics.stdev(values)/math.sqrt(len(values))
    return {'means_ns':values,'mean_ns':mean,'lower_ns':mean-half,'upper_ns':mean+half,
            'relative_half_width':half/mean,'launch_count':len(values),'degrees_of_freedom':len(values)-1}

def collect(root,revision,group,rounds=3,method=None):
    if rounds not in T95_BY_LAUNCHES:raise ValueError('Unsupported independent launch count')
    runs=[]
    for rnd in range(1,rounds+1):
        stem=f'round-{rnd}-{revision}-{group}'
        interference=root/(stem+'-interference.json')
        if not interference.is_file() or json.loads(interference.read_text()):
            raise ValueError('Missing/positive interference guard: '+str(interference))
        reports=list((root/stem/'results').glob('*-report-full*.json'))
        if not reports:raise ValueError('Missing reports: '+stem)
        cases={}
        for report in reports:
            for case in json.loads(report.read_text())['Benchmarks']:
                if method is not None and case['Method']!=method:continue
                key=(case['Namespace'],case['Type'],case['Method'],case['Parameters'])
                if key in cases:raise ValueError('Duplicate case: '+str(key))
                actual=[m for m in case['Measurements'] if m['IterationMode']=='Workload' and m['IterationStage']=='Actual']
                if len(actual)!=12 or any(m['Operations']<=0 or m['Nanoseconds']<=0 for m in actual):
                    raise ValueError('Expected twelve actual measured iterations: '+str(key))
                allocation=case['Memory']['BytesAllocatedPerOperation']
                if not math.isfinite(allocation) or allocation<0:raise ValueError('Invalid allocation sample: '+str(key))
                cases[key]=(case['Statistics']['Mean'],case['Memory']['BytesAllocatedPerOperation'])
        if not cases:raise ValueError('No matching measured cases: '+stem)
        runs.append(cases)
    if any(r.keys()!=runs[0].keys() for r in runs):raise ValueError('Incomplete independent case matrix')
    return {k:([r[k][0] for r in runs],[r[k][1] for r in runs]) for k in runs[0]}

def compare(base,change):
    if not base or base.keys()!=change.keys():raise ValueError('Missing/different base/change cases')
    rows=[]
    for key in sorted(base):
        if len(base[key][0])!=len(change[key][0]) or len(base[key][1])!=len(base[key][0]) or len(change[key][1])!=len(change[key][0]):
            raise ValueError('Base/change means and allocations require matching launch counts')
        b=interval(base[key][0]);c=interval(change[key][0]);delta=c['mean_ns']/b['mean_ns']-1
        # The specified decisive regression rule. Other results never imply acceptance by this tool.
        regression=c['lower_ns']>b['upper_ns'] and delta>0.03
        wide=max(b['relative_half_width'],c['relative_half_width'])>0.03
        rows.append({'case':list(key),'base':b,'change':c,'relative_change':delta,
            'base_allocated_bytes':base[key][1],'change_allocated_bytes':change[key][1],
            'allocation_increase_observed':max(change[key][1])>min(base[key][1]),
            'review':'regression' if regression else 'insufficient_precision' if wide else 'review_required'})
    return rows

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('root',type=Path)
    p.add_argument('base');p.add_argument('change');p.add_argument('group')
    p.add_argument('--rounds',type=int,choices=sorted(T95_BY_LAUNCHES),default=3,help='Complete independent launches per revision; retain every launch in the chosen fixed series')
    p.add_argument('--method',help='Exact method name for a targeted supplementary series; unselected cases are not assessed')
    args=p.parse_args()
    manifest=json.loads((args.root/'metadata.json').read_text())
    if manifest['sources'][args.base]['source_sha256']!=manifest['sources'][args.change]['source_sha256']:
        raise ValueError('Different workload sources')
    rows=compare(collect(args.root,args.base,args.group,args.rounds,args.method),collect(args.root,args.change,args.group,args.rounds,args.method))
    print(json.dumps({'confidence':f'95%, Student t with df={args.rounds-1} over {args.rounds} independent run means',
        'method_filter':args.method,
        'base_source':manifest['sources'][args.base],'change_source':manifest['sources'][args.change],
        'acceptance':'not determined; review feature equivalence and matching hit allocations',
        'rows':rows},indent=2))

if __name__=='__main__':main()
