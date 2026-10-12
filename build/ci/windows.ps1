# The Windows CI jobs for MethodCache: paths, line endings and the Windows runtime. The Docker-backed
# Redis and SQL Server tests run on Linux only (build/ci/linux.sh).
#
#   powershell -File build/ci/windows.ps1              build, then every test except the Docker-backed ones
#   powershell -File build/ci/windows.ps1 build unit   only these jobs
#
# Each job's log and exit code go to $env:METHODCACHE_CI_RESULTS (default artifacts\ci): <job>.log and <job>.exit.
param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Jobs = @('build', 'unit'))

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $repo

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:CI = 'true'
# High and critical NuGet advisories fail restore (see Directory.Build.props and README.md)
$env:MethodCacheEnforceAudit = 'true'

$results = if ($env:METHODCACHE_CI_RESULTS) { $env:METHODCACHE_CI_RESULTS } else { Join-Path $repo 'artifacts\ci' }
New-Item -ItemType Directory -Force -Path $results | Out-Null

$withoutDocker = 'FullyQualifiedName!~MethodCache.Redis.IntegrationTests&FullyQualifiedName!~MethodCache.SqlServer.IntegrationTests'
$steps = @{
    build = @(
        @('restore', 'MethodCache.sln'),
        @('build', 'MethodCache.sln', '--configuration', 'Release', '--no-restore')
    )
    # The leading comma keeps a one-command job a list of commands; PowerShell would flatten it otherwise
    unit = @(
        , @('test', 'MethodCache.sln', '--configuration', 'Release', '--no-build', '--filter', $withoutDocker)
    )
}

$failed = $false
foreach ($job in $Jobs) {
    if (-not $steps.ContainsKey($job)) { throw "Unknown job: $job" }
    $log = Join-Path $results "$job.log"
    $exitFile = Join-Path $results "$job.exit"
    Remove-Item -Force -ErrorAction SilentlyContinue $exitFile

    if ($job -ne 'build' -and (Test-Path (Join-Path $results 'build.exit')) -and
        (Get-Content (Join-Path $results 'build.exit')).Trim() -ne '0') {
        'skipped: the build failed' | Out-File -Encoding ascii $log
        'skipped' | Out-File -Encoding ascii $exitFile
        # A job that could not run is not a pass, also when the failed build was an earlier invocation's
        $failed = $true
        continue
    }

    Write-Host "=== $job (log: $log)"
    $code = 0
    '' | Out-File -Encoding utf8 $log
    foreach ($arguments in $steps[$job]) {
        # Windows PowerShell turns a native command's stderr into errors; keep them as log lines instead
        $ErrorActionPreference = 'Continue'
        & dotnet @arguments 2>&1 | Out-File -Append -Encoding utf8 $log
        $code = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($code -ne 0) { break }
    }
    "$code" | Out-File -Encoding ascii $exitFile
    Write-Host "    exit $code"
    if ($code -ne 0) {
        $failed = $true
        # The failed tests and errors first, then the end of the log
        Select-String -Path $log -Pattern '\[FAIL\]', ': error ', 'Error Message:' -Context 0, 6 |
            Select-Object -First 20 | ForEach-Object { $_.ToString() }
        Get-Content $log -Tail 15
    }
}
if ($failed) { exit 1 }
