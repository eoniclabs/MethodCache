from pathlib import Path
import subprocess
root=Path('/Users/johan/.codex/worktrees/http-core-store-v1/MethodCache')
f=root/'src/MethodCache.HttpCaching/Internal/HttpCacheStore.cs'
original=f.read_bytes();needle=b'(targetLength ?? TargetSpan(key).Length)';assert original.count(needle)==1
try:
 f.write_bytes(original.replace(needle,b'(targetLength ?? Target(key).Length)'))
 with Path('/tmp/methodcache-body-budget-probe/mutation-target-copy.log').open('w') as log:
  result=subprocess.run(['dotnet','test','tests/MethodCache.HttpCaching.Tests','-c','Release','-f','net10.0','--filter','FullyQualifiedName~BodyBudget_DoesNotMaterializeTemporaryTargetStrings','--collect:XPlat Code Coverage','--results-directory','/tmp/methodcache-body-budget-probe/mutation-coverage'],cwd=root,stdout=log,stderr=subprocess.STDOUT)
 assert result.returncode!=0,'Mutation was not detected'
 text=Path('/tmp/methodcache-body-budget-probe/mutation-target-copy.log').read_text()
 assert text.count('[FAIL]')==3,text[-2000:]
 print('Temporary target-string regression detected in all three cases')
finally:
 f.write_bytes(original)
 assert f.read_bytes()==original
 print('Production source restored byte for byte')
