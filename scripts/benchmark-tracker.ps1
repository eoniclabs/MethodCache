#!/usr/bin/env pwsh
# Local Benchmark Tracking Script

param(
    [string]$Command = "run",
    [string]$Filter = "*",
    [string]$Baseline = "",
    [switch]$UpdateReadme
)

$ResultsDir = ".benchmark-results"
$HistoryFile = "$ResultsDir/history.json"
$LatestFile = "$ResultsDir/latest.json"

function Initialize-BenchmarkDir {
    if (-not (Test-Path $ResultsDir)) {
        New-Item -ItemType Directory -Path $ResultsDir | Out-Null
        @{
            version = "1.0.0"
            runs = @()
        } | ConvertTo-Json | Set-Content $HistoryFile
    }
}

function Run-Benchmarks {
    param([string]$Filter)

    Write-Host "🚀 Running benchmarks with filter: $Filter" -ForegroundColor Cyan

    $timestamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
    $commitHash = git rev-parse --short HEAD
    $branch = git branch --show-current

    # Run benchmarks
    Push-Location MethodCache.Benchmarks
    try {
        dotnet run -c Release -- `
            --filter "$Filter" `
            --exporters json `
            --artifacts "$ResultsDir/artifacts-$timestamp"

        # Find the JSON result
        $jsonFile = Get-ChildItem "$ResultsDir/artifacts-$timestamp" -Filter "*-report-full.json" -Recurse | Select-Object -First 1

        if ($jsonFile) {
            $results = Get-Content $jsonFile.FullName | ConvertFrom-Json

            # Create summary
            $summary = @{
                timestamp = $timestamp
                commit = $commitHash
                branch = $branch
                benchmarks = @()
            }

            foreach ($benchmark in $results.Benchmarks) {
                $summary.benchmarks += @{
                    name = $benchmark.Method
                    mean = $benchmark.Statistics.Mean
                    error = $benchmark.Statistics.StdErr
                    allocated = $benchmark.Memory.BytesAllocatedPerOperation
                    gen0 = $benchmark.Memory.Gen0CollectionsPerOperation
                }
            }

            # Save as latest
            $summary | ConvertTo-Json -Depth 10 | Set-Content $LatestFile

            # Add to history
            $history = Get-Content $HistoryFile | ConvertFrom-Json
            $history.runs += $summary

            # Keep only last 50 runs
            if ($history.runs.Count -gt 50) {
                $history.runs = $history.runs | Select-Object -Last 50
            }

            $history | ConvertTo-Json -Depth 10 | Set-Content $HistoryFile

            Write-Host "✅ Benchmark complete! Results saved to $ResultsDir" -ForegroundColor Green

            # Show summary
            Show-Summary $summary
        }
    }
    finally {
        Pop-Location
    }
}

function Show-Summary {
    param($Summary)

    Write-Host "`n📊 Performance Summary" -ForegroundColor Yellow
    Write-Host "═══════════════════════════════════════════" -ForegroundColor DarkGray

    $Summary.benchmarks | ForEach-Object {
        $name = $_.name.PadRight(30)
        $mean = "{0,12:N2} ns" -f $_.mean
        $memory = "{0,10:N0} B" -f $_.allocated

        Write-Host "$name $mean $memory"
    }
}

function Compare-WithBaseline {
    param([string]$Baseline)

    if (-not (Test-Path $LatestFile)) {
        Write-Host "❌ No latest results found. Run benchmarks first." -ForegroundColor Red
        return
    }

    $latest = Get-Content $LatestFile | ConvertFrom-Json
    $baseline = $null

    if ($Baseline -eq "previous") {
        # Get previous run from history
        $history = Get-Content $HistoryFile | ConvertFrom-Json
        if ($history.runs.Count -ge 2) {
            $baseline = $history.runs[-2]
        }
    }
    elseif (Test-Path $Baseline) {
        $baseline = Get-Content $Baseline | ConvertFrom-Json
    }

    if (-not $baseline) {
        Write-Host "❌ Baseline not found" -ForegroundColor Red
        return
    }

    Write-Host "`n📈 Performance Comparison" -ForegroundColor Yellow
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor DarkGray
    Write-Host "Benchmark".PadRight(30) + "Baseline".PadRight(15) + "Current".PadRight(15) + "Change"
    Write-Host "─────────────────────────────────────────────────────────────" -ForegroundColor DarkGray

    $regressions = 0
    $improvements = 0

    foreach ($current in $latest.benchmarks) {
        $base = $baseline.benchmarks | Where-Object { $_.name -eq $current.name }

        if ($base) {
            $changePct = (($current.mean - $base.mean) / $base.mean) * 100
            $changeColor = "White"
            $changeSymbol = "→"

            if ($changePct -gt 5) {
                $changeColor = "Red"
                $changeSymbol = "⬆"
                $regressions++
            }
            elseif ($changePct -lt -5) {
                $changeColor = "Green"
                $changeSymbol = "⬇"
                $improvements++
            }

            $name = $current.name.PadRight(30)
            $baseValue = "{0,12:N2} ns" -f $base.mean
            $currentValue = "{0,12:N2} ns" -f $current.mean
            $change = "$changeSymbol {0,+6:N1}%" -f $changePct

            Write-Host "$name $baseValue $currentValue " -NoNewline
            Write-Host $change -ForegroundColor $changeColor
        }
    }

    Write-Host "`n📊 Summary: " -NoNewline
    if ($regressions -gt 0) {
        Write-Host "$regressions regressions " -NoNewline -ForegroundColor Red
    }
    if ($improvements -gt 0) {
        Write-Host "$improvements improvements " -NoNewline -ForegroundColor Green
    }
    if ($regressions -eq 0 -and $improvements -eq 0) {
        Write-Host "No significant changes" -ForegroundColor Gray
    }
    Write-Host ""
}

function Update-Readme {
    $readmePath = "README.md"

    if (-not (Test-Path $LatestFile)) {
        Write-Host "❌ No benchmark results found" -ForegroundColor Red
        return
    }

    $latest = Get-Content $LatestFile | ConvertFrom-Json

    # Generate markdown table
    $table = @"

## 🚀 Performance Benchmarks

Last updated: $($latest.timestamp) (commit: ``$($latest.commit)``)

| Benchmark | Mean | Error | Allocated |
|-----------|------|-------|-----------|
"@

    foreach ($benchmark in $latest.benchmarks | Sort-Object name) {
        $table += "`n| $($benchmark.name) | $("{0:N2}" -f $benchmark.mean) ns | ±$("{0:N2}" -f $benchmark.error) | $("{0:N0}" -f $benchmark.allocated) B |"
    }

    # Read current README
    $readme = Get-Content $readmePath -Raw

    # Replace or append performance section
    $performancePattern = "## 🚀 Performance Benchmarks[\s\S]*?(?=##|$)"

    if ($readme -match $performancePattern) {
        $readme = $readme -replace $performancePattern, ($table + "`n`n")
    }
    else {
        # Find a good place to insert (before ## License or at end)
        if ($readme -match "## License") {
            $readme = $readme -replace "## License", ($table + "`n`n## License")
        }
        else {
            $readme += "`n`n" + $table
        }
    }

    Set-Content $readmePath $readme
    Write-Host "✅ README.md updated with latest benchmark results" -ForegroundColor Green
}

# Main script
Initialize-BenchmarkDir

switch ($Command.ToLower()) {
    "run" {
        Run-Benchmarks -Filter $Filter
        if ($UpdateReadme) {
            Update-Readme
        }
    }
    "compare" {
        Compare-WithBaseline -Baseline $Baseline
    }
    "update-readme" {
        Update-Readme
    }
    "history" {
        $history = Get-Content $HistoryFile | ConvertFrom-Json
        Write-Host "📊 Benchmark History ($($history.runs.Count) runs)" -ForegroundColor Yellow
        $history.runs | Select-Object -Last 10 | ForEach-Object {
            Write-Host "$($_.timestamp) - $($_.commit) ($($_.branch))"
        }
    }
    default {
        Write-Host "Usage: ./benchmark-tracker.ps1 [command] [options]"
        Write-Host "Commands:"
        Write-Host "  run         - Run benchmarks"
        Write-Host "  compare     - Compare with baseline"
        Write-Host "  update-readme - Update README with latest results"
        Write-Host "  history     - Show benchmark history"
    }
}