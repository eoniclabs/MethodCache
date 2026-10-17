from pathlib import Path
import datetime, hashlib, json, os, secrets, subprocess, sys, time

root = Path.home() / 'methodcache-design-bench-20261015'
folder = root / 'cost-profile-137-v1'
assert (root / 'OWNER').read_text().strip() == 'methodcache-design-20261015-01a13a93'
sha = '5d9c829a857bd377049b3bd18703e97cdb254547'
label = 'methodcache-design-20261015-01a13a93-cost-profile-137-v1'
image = 'sha256:800f1bd715743c32891eb73ba59d22a028d9d6a221234be54b440391a6c793e3'
redis_image = 'redis@sha256:858f009f9709ce576febc734aa78b8f6d624b82571f9ddb6bda4377c833b3499'
sql_image = 'mcr.microsoft.com/mssql/server@sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090'
utc = lambda: datetime.datetime.now(datetime.timezone.utc).isoformat()

def run(args, **kw):
    return subprocess.run(args, check=True, text=True, **kw)

def captured(args, **kw):
    return subprocess.check_output(args, text=True, **kw).strip()

def worker():
    tree = folder / 'tree'
    assert captured(['git','-C',str(tree),'rev-parse','HEAD']) == sha
    password = 'Cost137_' + secrets.token_hex(16) + '!'
    child_env = dict(os.environ, MSSQL_SA_PASSWORD=password, SQLCMDPASSWORD=password)
    redis_name, sql_name = label+'-redis', label+'-sql'
    run(['docker','run','--detach','--label','methodcache-design-bench='+label,'--name',redis_name,
         '--publish','127.0.0.1::6379',redis_image], stdout=subprocess.DEVNULL)
    run(['docker','run','--detach','--label','methodcache-design-bench='+label,'--name',sql_name,
         '--publish','127.0.0.1::1433','--env','ACCEPT_EULA=Y','--env','MSSQL_SA_PASSWORD',sql_image],
        env=child_env, stdout=subprocess.DEVNULL)
    for _ in range(120):
        ready = subprocess.run(['docker','exec','--env','SQLCMDPASSWORD',sql_name,
            '/opt/mssql-tools18/bin/sqlcmd','-S','localhost','-U','sa','-C','-b','-Q','SELECT 1'],
            env=child_env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        if ready.returncode == 0: break
        time.sleep(1)
    else: raise RuntimeError('Owned SQL backend not ready')
    run(['docker','exec',redis_name,'redis-cli','ping'],stdout=subprocess.DEVNULL)
    redis_port = captured(['docker','port',redis_name,'6379/tcp']).rsplit(':',1)[1]
    sql_port = captured(['docker','port',sql_name,'1433/tcp']).rsplit(':',1)[1]
    profile_env = dict(os.environ, METHODCACHE_REDIS_URL='localhost:'+redis_port,
        METHODCACHE_SQLSERVER_CONNECTION=f'Server=localhost,{sql_port};User Id=sa;Password={password};TrustServerCertificate=True;Encrypt=False',
        COST_PROFILE_OUT=str(folder/'results'))
    receipt = {'head':sha,'base':captured(['git','-C',str(tree),'rev-parse','f43854d']),
        'started_utc':utc(),'build_image':image,'redis_image':redis_image,'sql_image':sql_image,
        'cpu':captured(['lscpu']),'host_affinity':sorted(os.sched_getaffinity(0)),
        'bundle_sha256':hashlib.sha256((folder/'source.bundle').read_bytes()).hexdigest(),
        'controller_sha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        'backend_scope':'Only newly created exact-label containers; no shared services',
        'gate':'PR137 cost profile, unchanged accepted.json; no paired timing in this phase'}
    (folder/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
    (folder/'results').mkdir(exist_ok=True)
    uid,gid,docker_gid = map(str,[os.getuid(),os.getgid(),os.stat('/var/run/docker.sock').st_gid])
    args = ['docker','run','--rm','--init','--network','host','--cpuset-cpus','0-15',
        '--user',uid+':'+gid,'--group-add',docker_gid,'--label','methodcache-design-bench='+label,
        '--volume',str(root)+':'+str(root),'--volume',str(root/'nuget')+':/nuget',
        '--volume',str(root/'home')+':/tmp/home','--workdir',str(tree),
        '--env','METHODCACHE_REDIS_URL','--env','METHODCACHE_SQLSERVER_CONNECTION','--env','COST_PROFILE_OUT',
        image,'bash','-lc','dotnet --info > "$COST_PROFILE_OUT/sdk-info.txt" && benchmarks/MethodCache.CostProfile/cost-profile.sh f43854d']
    code = subprocess.run(args,env=profile_env).returncode
    receipt.update(finished_utc=utc(),profile_exit=code)
    (folder/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
    raise SystemExit(code)

def main():
    if len(sys.argv)>1 and sys.argv[1]=='worker': worker(); return
    assert not (folder/'results').exists()
    tree=folder/'tree'
    assert not tree.exists()
    run(['git','clone','--no-checkout',str(folder/'source.bundle'),str(tree)])
    run(['git','-C',str(tree),'config','gc.auto','0'])
    run(['git','-C',str(tree),'checkout','--detach',sha])
    leased=['flock','--exclusive','--nonblock','--conflict-exit-code','75',
        '/srv/skribera-ci/ci-build-capacity.lock','python3',str(root/'leased-batch.py'),label,
        'taskset','-c','0-15','python3','-B','-u',str(Path(__file__)),'worker']
    print('Waiting for exclusive capacity',utc(),flush=True)
    while True:
        code=subprocess.run(leased).returncode
        if code != 75: break
        time.sleep(1)
    (folder/'exit').write_text(str(code)+'\n')
    print('Completed cost profile',code,utc(),flush=True)
    raise SystemExit(code)

if __name__=='__main__': main()
