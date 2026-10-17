from pathlib import Path
import hashlib,json,subprocess
root=Path.home()/'methodcache-design-bench-20261015';folder=root/'http-deferred-id-time32-v1';assert (root/'OWNER').read_text().strip()=='methodcache-design-20261015-01a13a93';sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();inputs=json.loads((folder/'inputs-template.json').read_text())
for n,h in inputs['files_sha256'].items():assert sha(folder/n)==h,n
runner=(root/'eviction-hash-experiment-v1/run.py').read_text();inputs['runner_origin_sha256']=sha(root/'eviction-hash-experiment-v1/run.py')
needle="dll=folder/'bin'/side/family/('MethodCache.CostProfile.dll' if family=='main' else 'MethodCache.IntProfile.dll')";assert runner.count(needle)==1;runner=runner.replace(needle,"dll=folder/'bin'/side/family/'MethodCache.WarmupProbe.dll'")
needle="with (destination/f'{side}-{pair}-{case_index}.log').open('x') as log:";assert runner.count(needle)==1;runner=runner.replace(needle,"with (destination/f'{side}-{pair}-{case_index}.log').open('x') as log, output.open('x') as jsonstream:")
needle="child=subprocess.Popen(['dotnet',str(dll),item['case'],'--exact','--time','--out',str(output)],cwd=folder/'tree',env=env,stdout=log,stderr=subprocess.STDOUT)";assert runner.count(needle)==1;runner=runner.replace(needle,"child=subprocess.Popen(['dotnet',str(dll),item['case'],str(inputs['warmups'])],cwd=folder/'tree',env=env,stdout=jsonstream,stderr=log)")
needle="assert record['actualIterations']==len(record['actualMeasurements'])==12";assert runner.count(needle)==1;runner=runner.replace(needle,needle+"\n                    assert record['warmupCount']==len(record['warmupMeasurements'])==inputs['warmups']")
compile(runner,'paired-time32-runner','exec');(folder/'run.py').write_text(runner);(folder/'process_activity.py').write_bytes((root/'eviction-hash-experiment-v1/process_activity.py').read_bytes())
for n in ['run.py','process_activity.py','initialize.py']:inputs['files_sha256'][n]=sha(folder/n)
(folder/'inputs.json').write_text(json.dumps(inputs,indent=2)+'\n')
with (folder/'prepare-dispatcher.log').open('x') as log:child=subprocess.Popen(['python3','-B','-u',str(folder/'prepare.py'),'dispatch'],stdin=subprocess.DEVNULL,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
(folder/'prepare-dispatcher.pid').write_text(str(child.pid)+'\n');print('Time32 candidate preparation queued',child.pid,'inputs',sha(folder/'inputs.json'))
