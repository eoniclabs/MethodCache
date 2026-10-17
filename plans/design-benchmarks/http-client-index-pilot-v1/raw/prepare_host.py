from pathlib import Path
import hashlib,json,os,shutil,subprocess

def prepare(c):
    folder,inputs,repo=c['folder'],c['inputs'],c['repo']
    digest,run,capture,utc=c['digest'],c['run'],c['capture'],c['utc']
    c['verify_inputs']()
    old=Path('/tmp/methodcache-compact-nonhttp-v1');prior=json.loads((old/'prepared.json').read_text())
    assert digest(old/'prepared.json')==inputs['prior_prepared_sha256']
    assert inputs['points']['baseline']==inputs['points']['final']=='5257fb63fb72393c817d74292e989299ede374d2'
    overlay=json.loads((folder/'candidate-overlay.json').read_text());changes={r['path']:r['sha256']for r in overlay};assert len(changes)==2
    expected={r['path']:r['sha256']for r in prior['production_sources']['final']};assert len(expected)==224
    tree=folder/'trees/final';tree.mkdir(parents=True)
    with(folder/'source.tar').open('xb')as output:run(['git','-C',repo,'archive',inputs['points']['final']],stdout=output)
    run(['tar','-xf',folder/'source.tar','-C',tree])
    (folder/'trees/baseline').symlink_to(old/'trees/final',target_is_directory=True)
    for n,h in expected.items():assert digest(tree/n)==digest(folder/'trees/baseline'/n)==h,n
    for n,h in changes.items():
        assert digest(folder/'candidate-src'/n)==h and expected[n]!=h
        (tree/n).write_bytes((folder/'candidate-src'/n).read_bytes())
    for n,h in inputs['source_sha256'].items():
        p=tree/n;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes((folder/'harness'/n).read_bytes())
        assert digest(p)==digest(folder/'trees/baseline'/n)==h,n
    shipping=folder/'shipping/final/main'
    run(['dotnet','build',tree/'benchmarks/MethodCache.CostProfile/MethodCache.CostProfile.csproj','-c','Release','-o',shipping,'-p:NBGV_GitEngine=Disabled','-nologo','-v:q','-clp:ErrorsOnly'])
    baseline=folder/'bin/baseline/main';baseline.parent.mkdir(parents=True)
    for r in prior['binaries']['final/main']:assert digest(old/r['path'])==r['sha256']
    shutil.copytree(old/'bin/final/main',baseline)
    for r in prior['binaries']['final/main']:assert digest(baseline/Path(r['path']).name)==r['sha256']
    output=folder/'bin/final/main'
    run(['dotnet','build',folder/'helper/Profile.csproj','--no-incremental','-c','Release','-o',output,'-p:ShippingDir='+str(shipping),'-p:Side=head','-nologo','-v:q'])
    for p in shipping.glob('*.dll'):assert digest(output/p.name)==digest(p)
    bins={point+'/main':[{'path':str(p.relative_to(folder)),'sha256':digest(p)}for p in sorted((folder/'bin'/point/'main').iterdir())if p.is_file()]for point in ['baseline','final']}
    inventories={}
    for point in ['baseline','final']:
        names=capture(['dotnet',folder/'bin'/point/'main/MethodCache.CostProfile.dll','--list'],env=dict(os.environ,METHODCACHE_REDIS_URL='localhost:1',METHODCACHE_SQLSERVER_CONNECTION='inventory-only')).splitlines()
        assert len(names)==len(set(names))==67 and set(inputs['cases']['main']).issubset(set(names));inventories[point]=names
    assert inventories['baseline']==inventories['final'] and sum(map(len,bins.values()))==210
    head={n:changes.get(n,h)for n,h in expected.items()}
    for n,h in head.items():assert digest(tree/n)==h
    prepared={'inputs_sha256':digest(folder/'inputs.json'),'prepared_utc':utc(),'points':inputs['points'],'head_kind':inputs['head_kind'],'head_patch_sha256':inputs['production_patch_sha256'],'source_sha256':{s:inputs['source_sha256']for s in ['baseline','final']},'production_sources':{s:[{'path':n,'sha256':h}for n,h in values.items()]for s,values in [('baseline',expected),('final',head)]},'binaries':bins,'inventories':inventories,'sdk':capture(['dotnet','--info']),'os':capture(['sw_vers']),'cpu':prior['cpu'],'protocol':inputs['scope']}
    (folder/'prepared.json').write_text(json.dumps(prepared,indent=2)+'\n')
    print('Prepared direct current-vs-client-index trial,2cases/40freshprocesses;notcumulativeacceptance',utc(),flush=True)
