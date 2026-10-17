from pathlib import Path
import json

def prepare(c):
 f,i,sha,run,capture,utc=c['folder'],c['inputs'],c['digest'],c['run'],c['capture'],c['utc'];c['verify_inputs']()
 base=json.loads((f/'baseline-original-prepared.json').read_text());head=json.loads((f/'head-original-prepared.json').read_text())
 assert sha(f/'baseline-original-prepared.json')==i['baseline_original_prepared_sha256']and sha(f/'head-original-prepared.json')==i['head_original_prepared_sha256']
 production={'baseline':base['production_sources']['baseline'],'final':head['production_sources']['final']}
 assert len(production['baseline'])==200 and len(production['final'])==224
 for point,rows in production.items():
  for r in rows:assert sha(f/'trees'/point/r['path'])==r['sha256'],r['path']
  for n,h in i['source_sha256'].items():assert sha(f/'trees'/point/n)==sha(f/'harness'/n)==h,n
 originals={'baseline':Path('/tmp/methodcache-final-local-cpu-v2/bin/baseline/main'),'final':Path('/tmp/methodcache-http-client-index-pilot-v1/shipping/final/main')}
 bins={}
 for point,shipping in originals.items():
  prior=Path('/tmp/methodcache-compact-nonhttp-v1/bin/baseline/main')if point=='baseline'else Path('/tmp/methodcache-http-client-index-pilot-v1/bin/final/main')
  for p in shipping.glob('*.dll'):assert sha(p)==sha(prior/p.name),p.name
  output=f/'bin'/point/'main';run(['dotnet','build',f/'helper/Profile.csproj','--no-incremental','-c','Release','-o',output,'-p:ShippingDir='+str(shipping),'-p:Side='+('base'if point=='baseline'else'head'),'-nologo','-v:q'])
  for p in shipping.glob('*.dll'):assert sha(output/p.name)==sha(p),p.name
  bins[point+'/main']=[{'path':str(p.relative_to(f)),'sha256':sha(p)}for p in sorted(output.iterdir())if p.is_file()]
 assert sum(map(len,bins.values()))==210
 d={'inputs_sha256':sha(f/'inputs.json'),'prepared_utc':utc(),'points':i['points'],'head_kind':i['head_kind'],'head_patch_sha256':sha(f/'production.patch'),'production_sources':production,'source_sha256':{s:i['source_sha256']for s in['baseline','final']},'binaries':bins,'sdk':capture(['dotnet','--info']),'os':capture(['sw_vers']),'cpu':head['cpu'],'protocol':i['scope'],'fixed_growing_set_invocations':8192}
 (f/'prepared.json').write_text(json.dumps(d,indent=2)+'\n');print('Prepared all14fixedHTTPcases/280freshprocesses;no timingstarted',utc(),flush=True)
