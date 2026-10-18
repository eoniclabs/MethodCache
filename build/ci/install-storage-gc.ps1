param([Parameter(Mandatory=$true)][string]$Config,
      [string]$InstallDir = 'C:\mc\storage-gc')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$null = Get-Content -Raw $Config | ConvertFrom-Json
New-Item -ItemType Directory -Force $InstallDir | Out-Null

# Private, portable runtime, independent of the user's SDKs/Python installations.
# SHA-256 published at https://www.python.org/downloads/release/python-3148/
$python = Join-Path $InstallDir 'python\python.exe'
if (-not (Test-Path $python)) {
    $zip = Join-Path $InstallDir 'python.zip'
    Invoke-WebRequest -UseBasicParsing 'https://www.python.org/ftp/python/3.14.8/python-3.14.8-embed-amd64.zip' -OutFile $zip
    if ((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'a93abe456ab01bd96d7a085b3cdb6566b3063f4241360d114142fbdb07f0a310') {
        throw 'Portable Python checksum mismatch'
    }
    Expand-Archive $zip -DestinationPath (Join-Path $InstallDir 'python') -Force
    Remove-Item $zip
}
Copy-Item (Join-Path $PSScriptRoot 'storage_gc.py') (Join-Path $InstallDir 'storage_gc.py.new') -Force
Move-Item (Join-Path $InstallDir 'storage_gc.py.new') (Join-Path $InstallDir 'storage_gc.py') -Force
Copy-Item $Config (Join-Path $InstallDir 'config.json') -Force
# SYSTEM inspects processes across bldr/johan; only administrators can change its code/config.
& icacls.exe $InstallDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot secure collector installation' }
& icacls.exe (Join-Path $InstallDir '*') /reset /T /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot propagate collector permissions' }
$wrapper = @'
$ErrorActionPreference = 'Stop'
$env:PATH = 'C:\Program Files\Git\cmd;' + $env:PATH
$log = Join-Path $PSScriptRoot 'collector.log'
if ((Test-Path $log) -and (Get-Item $log).Length -gt 10MB) {
    Remove-Item "$log.5" -ErrorAction SilentlyContinue
    for ($n=4; $n -ge 1; $n--) { if (Test-Path "$log.$n") { Move-Item "$log.$n" "$log.$($n+1)" -Force } }
    Move-Item $log "$log.1" -Force
}
& (Join-Path $PSScriptRoot 'python\python.exe') (Join-Path $PSScriptRoot 'storage_gc.py') --config (Join-Path $PSScriptRoot 'config.json') --apply >> $log 2>&1
exit $LASTEXITCODE
'@
Set-Content -Encoding UTF8 (Join-Path $InstallDir 'run.ps1') $wrapper
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\run.ps1`""
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 15)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
Register-ScheduledTask -TaskName 'BuildStorageGc' -Action $action -Trigger $trigger -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
Start-ScheduledTask -TaskName 'BuildStorageGc'
Write-Output "Installed automatic collection every 15 minutes. Log: $InstallDir\collector.log"
