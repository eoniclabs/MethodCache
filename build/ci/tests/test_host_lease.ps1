# Real FileShare.None contention and failure propagation, executable on Windows or PowerShell 7/macOS.
param([string] $Wrapper = (Join-Path $PSScriptRoot '..\host-lease.ps1'))
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
New-Item -ItemType Directory $root | Out-Null
$path = Join-Path $root 'capacity.lock'
$marker = Join-Path $root 'started'
$hostShell = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh' } else { 'powershell' }
try {
    # Parse both Windows scripts without executing registration.
    foreach ($file in @($Wrapper, (Join-Path (Split-Path $Wrapper) 'install-actions-windows.ps1'))) {
        $errors = $null
        [Management.Automation.Language.Parser]::ParseFile($file, [ref]$null, [ref]$errors) | Out-Null
        if ($errors) { throw $errors }
    }
    $lease = [IO.File]::Open($path, 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        & $hostShell -NoProfile -File $Wrapper -Locks $path -WaitSeconds 0 -Command "New-Item '$marker'"
        if ($LASTEXITCODE -ne 75 -or (Test-Path $marker)) { throw 'Contended admission started a command' }
    } finally { $lease.Dispose() }
    # This tests the wrapper's child shell, including exit inside the project's script.
    & $hostShell -NoProfile -File $Wrapper -Locks $path -Command 'exit 23'
    if ($LASTEXITCODE -ne 23) { throw "Project failure was lost: $LASTEXITCODE" }
    $command = "try { `$h=[IO.File]::Open('$path','OpenOrCreate','ReadWrite','None'); `$h.Dispose(); exit 99 } catch [IO.IOException] { exit 23 }"
    & $hostShell -NoProfile -File $Wrapper -Locks $path -Command $command
    if ($LASTEXITCODE -ne 23) { throw 'Child did not run under admission' }
    $lease = [IO.File]::Open($path, 'OpenOrCreate', 'ReadWrite', 'None')
    $lease.Dispose()
    Write-Host 'PASS: Windows parser, contention, failure propagation and release'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
