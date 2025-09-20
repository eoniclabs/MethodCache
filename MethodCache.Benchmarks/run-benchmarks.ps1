#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Runs MethodCache performance benchmarks with various configuration options.

.DESCRIPTION
    This script provides a convenient way to run MethodCache performance benchmarks
    with different configurations, output formats, and filtering options.

.PARAMETER Category
    The benchmark category to run. Valid values: basic, providers, concurrent, memory, realworld, generic, serialization, all

.PARAMETER OutputFormat
    Output format for results. Valid values: console, html, csv, json

.PARAMETER Filter
    Filter benchmarks by method name pattern

.PARAMETER Configuration
    Build configuration. Valid values: Debug, Release

.PARAMETER Warmup
    Number of warmup iterations

.PARAMETER Iterations
    Number of measurement iterations

.PARAMETER Redis
    Enable Redis provider benchmarks (requires Redis server)

.PARAMETER Verbose
    Enable verbose output

.EXAMPLE
    .\run-benchmarks.ps1 -Category basic -OutputFormat html
    Runs basic benchmarks and generates HTML report

.EXAMPLE
    .\run-benchmarks.ps1 -Category all -Configuration Release -Redis
    Runs all benchmarks in Release mode with Redis enabled

.EXAMPLE
    .\run-benchmarks.ps1 -Category concurrent -Filter "*Concurrent*" -Verbose
    Runs concurrent benchmarks with filtering and verbose output
#>

param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("basic", "providers", "concurrent", "memory", "realworld", "generic", "serialization", "all")]
    [string]$Category,
    
    [ValidateSet("console", "html", "csv", "json")]
    [string]$OutputFormat = "console",
    
    [string]$Filter = "",
    
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    
    [int]$Warmup = 3,
    
    [int]$Iterations = 5,
    
    [switch]$Redis,
    
    [switch]$Verbose
)

# Set error action preference
$ErrorActionPreference = "Stop"

# Get script directory
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = $ScriptDir

Write-Host "MethodCache Performance Benchmarks" -ForegroundColor Green
Write-Host "=================================" -ForegroundColor Green
Write-Host ""

# Check prerequisites
Write-Host "Checking prerequisites..." -ForegroundColor Yellow

# Check .NET version
try {
    $dotnetVersion = dotnet --version
    Write-Host "✓ .NET version: $dotnetVersion" -ForegroundColor Green
} catch {
    Write-Error "❌ .NET is not installed or not in PATH"
    exit 1
}

# Check Redis if required
if ($Redis -or $Category -eq "providers" -or $Category -eq "all") {
    Write-Host "Checking Redis connectivity..." -ForegroundColor Yellow
    
    try {
        # Test Redis connection using .NET code
        $testScript = @"
using System;
using StackExchange.Redis;

try {
    var redis = ConnectionMultiplexer.Connect("localhost:6379");
    var db = redis.GetDatabase();
    db.StringSet("test", "value");
    var result = db.StringGet("test");
    redis.Dispose();
    Console.WriteLine("Redis connection successful");
    Environment.Exit(0);
} catch (Exception ex) {
    Console.WriteLine($"Redis connection failed: {ex.Message}");
    Environment.Exit(1);
}
"@
        
        $testFile = "$env:TEMP\redis-test.cs"
        $testScript | Out-File -FilePath $testFile -Encoding UTF8
        
        $testResult = dotnet-script $testFile 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "✓ Redis connection successful" -ForegroundColor Green
        } else {
            Write-Host "⚠ Redis connection failed - Redis benchmarks will be skipped" -ForegroundColor Yellow
            Write-Host "  To enable Redis benchmarks:" -ForegroundColor Gray
            Write-Host "    Docker: docker run -d -p 6379:6379 redis:alpine" -ForegroundColor Gray
            Write-Host "    Windows: Install Redis for Windows" -ForegroundColor Gray
            Write-Host "    macOS: brew install redis && redis-server" -ForegroundColor Gray
            Write-Host "    Linux: sudo apt-get install redis-server && redis-server" -ForegroundColor Gray
        }
        
        Remove-Item $testFile -ErrorAction SilentlyContinue
    } catch {
        Write-Host "⚠ Could not test Redis connection - Redis benchmarks may fail" -ForegroundColor Yellow
    }
}

# Build project
Write-Host ""
Write-Host "Building project..." -ForegroundColor Yellow
try {
    dotnet build -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed"
    }
    Write-Host "✓ Build successful" -ForegroundColor Green
} catch {
    Write-Error "❌ Build failed"
    exit 1
}

# Prepare benchmark arguments
$benchmarkArgs = @($Category)

if ($Filter) {
    $benchmarkArgs += "--filter"
    $benchmarkArgs += $Filter
}

# Set environment variables for configuration
$env:BENCHMARK_OUTPUT_FORMAT = $OutputFormat
$env:BENCHMARK_WARMUP_COUNT = $Warmup
$env:BENCHMARK_ITERATION_COUNT = $Iterations
$env:BENCHMARK_VERBOSE = if ($Verbose) { "true" } else { "false" }

# Create output directory
$outputDir = Join-Path $ProjectDir "BenchmarkResults"
if (!(Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

# Run benchmarks
Write-Host ""
Write-Host "Running benchmarks..." -ForegroundColor Yellow
Write-Host "Category: $Category" -ForegroundColor Gray
Write-Host "Configuration: $Configuration" -ForegroundColor Gray
Write-Host "Output Format: $OutputFormat" -ForegroundColor Gray
if ($Filter) {
    Write-Host "Filter: $Filter" -ForegroundColor Gray
}
Write-Host ""

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"

try {
    if ($Verbose) {
        dotnet run -c $Configuration --no-build -- @benchmarkArgs
    } else {
        dotnet run -c $Configuration --no-build -- @benchmarkArgs | Tee-Object -FilePath "$outputDir\benchmark-$Category-$timestamp.log"
    }
    
    if ($LASTEXITCODE -ne 0) {
        throw "Benchmark execution failed"
    }
    
    Write-Host ""
    Write-Host "✓ Benchmarks completed successfully" -ForegroundColor Green
    
    # Copy artifacts if they exist
    $artifactsDir = Join-Path $ProjectDir "BenchmarkDotNet.Artifacts"
    if (Test-Path $artifactsDir) {
        Write-Host "📊 Benchmark artifacts available in: $artifactsDir" -ForegroundColor Cyan
        
        # Copy results to output directory
        Copy-Item "$artifactsDir\*" "$outputDir\" -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "📁 Results copied to: $outputDir" -ForegroundColor Cyan
    }
    
} catch {
    Write-Error "❌ Benchmark execution failed: $_"
    exit 1
}

# Generate summary
Write-Host ""
Write-Host "Benchmark Summary" -ForegroundColor Green
Write-Host "=================" -ForegroundColor Green
Write-Host "Category: $Category"
Write-Host "Configuration: $Configuration"
Write-Host "Completed: $(Get-Date)"
Write-Host "Results: $outputDir"
Write-Host ""

if ($OutputFormat -eq "html") {
    $htmlFiles = Get-ChildItem "$outputDir\*.html" -ErrorAction SilentlyContinue
    if ($htmlFiles) {
        Write-Host "🌐 HTML Reports generated:" -ForegroundColor Cyan
        foreach ($file in $htmlFiles) {
            Write-Host "  $($file.FullName)" -ForegroundColor Gray
        }
        Write-Host ""
    }
}

Write-Host "✨ Benchmark run completed!" -ForegroundColor Green