# Run as bldr; pipe a short-lived organization registration token on stdin. Does not require administrator.
param([string] $Root = "$env:USERPROFILE\actions-runner")
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (Test-Path "$Root\.runner") { throw 'Runner already configured; inspect instead of overwriting' }
foreach ($path in @('C:\ProgramData\bldr\ci', 'C:\ProgramData\bldr\ci1', 'C:\mc')) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing reviewed CI root: $path" }
}
$env:DOTNET_ROOT = 'C:\mc\dotnet'
$env:PATH = "C:\mc\dotnet;C:\Program Files\Git\cmd;C:\Program Files\Git\bin;$env:PATH"
& dotnet --version
if ($LASTEXITCODE -ne 0) { throw '.NET is required' }
New-Item -ItemType Directory -Force $Root | Out-Null
$archive = Join-Path $env:TEMP 'eonic-actions-runner-2.338.0.zip'
try {
    Invoke-WebRequest -UseBasicParsing 'https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-win-x64-2.338.0.zip' -OutFile $archive
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne 'f48e0750a21812bca5f82de5f7f5aeae71abee647fab5a582f1742d07eba455f') {
        throw 'Runner checksum mismatch'
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $Root
} finally { Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'host-lease.ps1') -Destination "$Root\host-lease.ps1"
@("EONIC_CI_HOST_LEASE=$Root\host-lease.ps1", 'EONIC_CI_CAPACITY_LOCKS=C:\ProgramData\bldr\ci\.lock;C:\ProgramData\bldr\ci1\.lock;C:\mc\actions-capacity.lock',
  'DOTNET_ROOT=C:\mc\dotnet') | Set-Content -LiteralPath "$Root\.env" -Encoding ascii
$env:ACTIONS_RUNNER_INPUT_TOKEN = [Console]::ReadLine()
if (-not $env:ACTIONS_RUNNER_INPUT_TOKEN) { throw 'Expected registration token on stdin' }
try {
    Push-Location $Root
    & .\config.cmd --unattended --url https://github.com/eoniclabs --name baloo-org-windows-1 --runnergroup ci-public-reviewed --labels windows-build,baloo --work _work
    if ($LASTEXITCODE -ne 0) { throw 'Runner registration failed' }
} finally {
    Pop-Location
    Remove-Item Env:ACTIONS_RUNNER_INPUT_TOKEN -ErrorAction SilentlyContinue
}
@'
$env:DOTNET_ROOT = 'C:\mc\dotnet'
$env:PATH = "C:\mc\dotnet;C:\Program Files\Git\cmd;C:\Program Files\Git\bin;$env:PATH"
Set-Location $PSScriptRoot
& .\run.cmd
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath "$Root\start-runner.ps1" -Encoding ascii
Write-Host "Configured runner at $Root; start through the reviewed SSH supervisor."
