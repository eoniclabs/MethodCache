# Host admission for reviewed Windows Actions commands. Compatible with Skribera's FileShare.None leases.
param(
    [Parameter(Mandatory = $true)][string] $Command,
    [string[]] $Locks = @(),
    [int] $WaitSeconds = 1800
)
$ErrorActionPreference = 'Stop'
if (-not $Locks.Count) { $Locks = @($env:EONIC_CI_CAPACITY_LOCKS -split ';') }
if (-not $Locks.Count -or @($Locks | Where-Object { -not $_ }).Count) {
    throw 'EONIC_CI_CAPACITY_LOCKS must name the host capacity locks'
}
$leases = @()
$deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
try {
    foreach ($path in ($Locks | Sort-Object -Unique)) {
        # Do not invent missing legacy slot roots: a changed host needs inventory first.
        if (-not (Test-Path -LiteralPath (Split-Path $path))) { throw "Missing capacity lock directory: $path" }
        Write-Host "Waiting for host capacity: $path"
        while ($true) {
            try {
                $leases += [IO.File]::Open($path, 'OpenOrCreate', 'ReadWrite', 'None')
                break
            } catch [IO.IOException] {
                if ([DateTime]::UtcNow -ge $deadline) { exit 75 }
                Start-Sleep -Milliseconds 200
            }
        }
    }
    foreach ($path in $Locks) {
        $marker = Join-Path (Split-Path $path) '.active-run'
        if ((Split-Path $path -Leaf) -eq '.lock' -and (Test-Path -LiteralPath $marker)) {
            throw "Unresolved legacy Windows run at $marker; inspect it before admitting another build"
        }
    }
    # A child shell keeps exit in the project's script from skipping this wrapper's finally.
    & powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command $Command
    $code = $LASTEXITCODE
} finally {
    foreach ($lease in $leases) { $lease.Dispose() }
}
exit $code
