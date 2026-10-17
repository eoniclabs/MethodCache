"""Prepare a fresh bounded HTTP-only cohort; never reuse earlier timing samples."""
from pathlib import Path
import hashlib
import json
import subprocess
import sys

folder = Path(__file__).resolve().parent
root = folder.parent
sha = 'f0dd6c204cd3eefb8fb08ef0509340caf242da92'
helper = root/'net10-followup/checkouts/net10/benchmarks/MethodCache.Benchmarks/validation/create-harness.py'
expected_helper = '993d2e2f4d88d7523dc345c869f85612578b212f2f395f5b538ce02b9ca06d3d'

VERIFY_HTTP = '''
if (args.Contains("--verify-http"))
{
    foreach (var listening in new[] { false, true })
    foreach (var invalidated in new[] { false, true })
    {
        var bench = new HttpResponsePathBenchmarks { Listening = listening, AfterInvalidation = invalidated };
        try
        {
            await bench.Setup();
            var initial = invalidated ? 7 : 3;
            if (bench.OriginLoads != initial) throw new Exception("Incorrect initial HTTP origin count.");
            for (var i = 0; i < 100; i++)
            {
                var before = bench.OriginLoads;
                if (await bench.FreshHit() != 200 || await bench.VariantHit() != 200 || bench.OriginLoads != before)
                    throw new Exception("HTTP resident path bypassed.");
                if (await bench.Revalidated() != 200 || bench.OriginLoads != before + 1)
                    throw new Exception("HTTP conditional revalidation bypassed.");
            }
            Console.WriteLine($"Verified 100 resident/304 operations, listener={listening}, invalidated={invalidated}");
        }
        finally { bench.Cleanup(); }
    }
    return;
}
'''

def run(*args, cwd=None):
    subprocess.run([str(a) for a in args], cwd=cwd, check=True)

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

phase = sys.argv[1]
if phase == 'checkout':
    run('git', 'fetch', folder/'source.bundle',
        'refs/heads/codex/http-core-store-v1:refs/heads/design-304-header-v1', cwd=root/'source')
    assert not (folder/'checkout').exists(), 'Fresh cohort only'
    run('git', 'worktree', 'add', '--detach', folder/'checkout', sha, cwd=root/'source')
    assert subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=folder/'checkout', text=True).strip() == sha
elif phase == 'build':
    name = sys.argv[2]
    assert name in ('prefix', 'candidate')
    runtime = root/'checkouts/prefix' if name == 'prefix' else folder/'checkout'
    expected_sha = '850cef8f3bc709eaf464b3c25e5ec898217fcc73' if name == 'prefix' else sha
    assert subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=runtime, text=True).strip() == expected_sha
    assert digest(helper) == expected_helper
    harness = folder/'harness'/name
    assert not harness.exists(), 'Preserve earlier preparation attempts'
    run('python3', helper, '--runtime-root', runtime, '--output', harness)
    baseline = json.loads((root/'harness/prefix/source-manifest.json').read_text())
    manifest_path = harness/'source-manifest.json'
    manifest = json.loads(manifest_path.read_text())
    assert manifest['source_sha256'] == baseline['source_sha256']
    program = harness/'Program.cs'
    anchor = 'BenchmarkSwitcher.FromTypes('
    content = program.read_text()
    assert content.count(anchor) == 1
    manifest['canonical_program_sha256'] = digest(program)
    program.write_text(content.replace(anchor, VERIFY_HTTP + '\n' + anchor))
    manifest['harness_program_sha256'] = digest(program)
    manifest['http_verification_extension_sha256'] = hashlib.sha256(VERIFY_HTTP.encode()).hexdigest()
    manifest_path.write_text(json.dumps(manifest, indent=2) + '\n')
    run('dotnet', 'build', harness/'DesignValidation.csproj', '-c', 'Release', '--nologo', '-v', 'minimal')
    run('dotnet', harness/'bin/Release/net10.0/DesignValidation.dll', '--verify-http', cwd=harness)
    sdk = subprocess.check_output(['dotnet', '--info'], text=True)
    sdk_file = folder/'sdk-info.txt'
    if sdk_file.exists(): assert sdk_file.read_text() == sdk
    else: sdk_file.write_text(sdk)
else:
    raise SystemExit('Unknown preparation phase')
