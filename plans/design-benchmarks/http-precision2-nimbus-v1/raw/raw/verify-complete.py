from pathlib import Path
import datetime,hashlib,json,math,os,subprocess,tarfile
f=Path(__file__).resolve().parent;root=f.parent;sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
pid=int((f/'dispatcher.pid').read_text());live=subprocess.run(['ps','-p',str(pid),'-o','args='],capture_output=True,text=True).stdout
assert str(f/'run.py')+' run' not in live,'Exact dispatcher still live; no partial review'
assert (f/'exit').read_text().strip()=='0'and(f/'results/group-00/exit').read_text().strip()=='0'
i=json.loads((f/'inputs.json').read_text());d=json.loads((f/'prepared.json').read_text());intent=json.loads((f/'dispatch-intent.json').read_text());s=json.loads((f/'source-verification.json').read_text());origin=root/'http-client-index-candidate-v1'
assert sha(f/'inputs.json')==d['inputs_sha256']==intent['inputs_sha256']==s['inputs_sha256']
assert sha(f/'prepared.json')==intent['prepared_sha256']==s['prepared_sha256']and sha(f/'source-verification.json')==intent['source_verification_sha256']
assert sha(origin/'prepared.json')==sha(f/'origin-prepared.json')==i['origin_prepared_sha256']
assert i['pairs']==10 and i['warmups']==32 and len(i['cases'])==2 and len(d['groups'])==1
for p,h in i['files_sha256'].items():assert sha(f/p)==h,p
assert sha(f/'accepted.json')=='5f1358997312fcd369488172a4a224b0582607b8bd74f492c411a2c93dc141b6'
assert sha(root/'leased-batch.py')=='4a83e0b505eaef6d91599479c8fa6c78ddc2958e698882740cca7ecad57a6830'
for files in d['binaries'].values():
 for p,h in files.items():assert sha(f/p)==h,p
assert sum(map(len,d['binaries'].values()))==220
for row in d['production_sources']['head']:assert sha(f/'tree'/row['path'])==row['sha256'],row['path']
assert len(d['production_sources']['head'])==224 and len(d['production_sources']['base'])==200
assert d['source_hashes']['head']==d['source_hashes']['base']
for p,h in d['source_hashes']['head'].items():assert sha(f/'tree'/p)==h,p
assert len(d['source_hashes']['head'])==8
for side in ['base','head']:assert sha(f/f'bin/{side}/main/MethodCache.WarmupProbe.dll')==d['helper_original_sha256']
g=f/'results/group-00';launches=[json.loads(l)for l in(g/'launches.jsonl').read_text().splitlines()];expected=[(side,n,c)for n in range(1,11)for c in i['cases']for side in(['base','head']if n%2 else['head','base'])];assert len(launches)==40 and[(r['side'],r['pair'],r['case'])for r in launches]==expected
observed=json.loads((g/'observations.json').read_text());assert observed and all(not r['external']for r in observed)
for o in observed:
 for affinity in o['thread_affinities'].values():assert affinity and set(affinity)<=set(range(16))
records={}
for row in launches:
 p=g/row['output'];assert p.resolve().is_relative_to(g.resolve())and sha(p)==row['sha256'];lines=p.read_text().splitlines();assert len(lines)==1;x=json.loads(lines[0]);assert x['case']==row['case']
 assert x['warmupCount']==len(x['warmupMeasurements'])==32 and x['actualIterations']==len(x['actualMeasurements'])==12
 assert x['runtime']=='.NET 10.0.12'and not x['serverGC']and x['gcLatencyMode']=='Interactive'and x['affinityMask']==65535
 assert all(m['Operations']==8192 and math.isfinite(m['Nanoseconds'])and m['Nanoseconds']>0 for m in x['actualMeasurements'])
 assert isinstance(x['ns'],(int,float))and math.isfinite(x['ns'])and x['ns']>0
 assert any(row['started_utc']<=o['utc']<=row['finished_utc']for o in observed)
 catalog={(f/p).resolve():h for p,h in d['binaries'][row['side']+'/main'].items()}
 for lib in x['libraries']:
  path=Path(lib['location']).resolve();assert path in catalog and sha(path)==catalog[path]==lib['sha256'].lower(),lib['name']
 k=(row['side'],row['pair'],row['case']);assert k not in records;records[k]=x
label='methodcache-design-20261015-01a13a93-'+f.name+'-group-00';containers=subprocess.check_output(['docker','ps','-aq','--filter','label=methodcache-design-bench='+label],text=True).strip();assert not containers,'Owned labeled container cleanup incomplete'
raw=[]
for p in sorted(f.rglob('*')):
 if p.is_symlink()or not p.is_file()or p.name in ['completion-verification.json','complete-text-only.tar.gz']:continue
 assert p.suffix not in ['.dll','.pdb','.exe','.so','.dylib','.pyc'];p.read_bytes().decode();raw.append({'path':str(p.relative_to(f)),'sha256':sha(p)})
report={'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'scope':'Completefixed2cold-afterinvalidationprecisionconfirmation;40freshprocesses/10alternatingpairs/32warm12actual8192op;no changedruntime/settings/pooling/omissions/waivers. All220binaryfiles,224actualcandidatefiles,8commonharnesssources,sourceprovenanceandactualDLLhashes/order/runtimeGC/observedaffinityverify. OriginalfullMacfailedcohortandthreepreciseregressionsremainseparate. No timing verdict inferred by integrity verification.','cases':i['cases'],'launches':40,'binary_files':220,'candidate_source_files':224,'baseline_source_files':200,'runtime':'.NET 10.0.12','server_gc':False,'observed_cpuset':'0-15','own_labeled_containers_remaining':0,'files':raw}
assert not(f/'completion-verification.json').exists();(f/'completion-verification.json').write_text(json.dumps(report,indent=2)+'\n')
arc=f/'complete-text-only.tar.gz';assert not arc.exists()
with tarfile.open(arc,'w:gz')as tar:
 for row in raw:tar.add(f/row['path'],arcname=row['path'],recursive=False)
 tar.add(f/'completion-verification.json',arcname='completion-verification.json',recursive=False)
print(json.dumps({'scope':report['scope'],'archive':str(arc),'archive_sha256':sha(arc),'archive_bytes':arc.stat().st_size,'launches':40},indent=2))
