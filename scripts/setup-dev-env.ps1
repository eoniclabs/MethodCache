# MethodCache Development Environment Setup Script for Windows
# This script sets up the optimal development environment for MethodCache integration tests

param(
    [switch]$Interactive = $true,
    [switch]$DockerOnly = $false,
    [switch]$ExternalOnly = $false,
    [string]$ConfigPath = ""
)

$ErrorActionPreference = "Stop"

Write-Host "*** MethodCache Development Environment Setup (Windows) ***" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host

# Function to check if running as administrator
function Test-Administrator {
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentUser)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Function to check if a command exists
function Test-Command {
    param($Command)
    $null -ne (Get-Command $Command -ErrorAction SilentlyContinue)
}

# Function to check if a service is running
function Test-Service {
    param($ServiceName)
    try {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        return $service -and $service.Status -eq 'Running'
    }
    catch {
        return $false
    }
}

# Function to install via Chocolatey
function Install-ChocolateyPackage {
    param($PackageName, $DisplayName)

    Write-Host "Installing $DisplayName via Chocolatey..." -ForegroundColor Yellow
    try {
        choco install $PackageName -y
        Write-Host "[SUCCESS] $DisplayName installed successfully" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "[ERROR] Failed to install $DisplayName`: $_" -ForegroundColor Red
        return $false
    }
}

# Function to check SQL Server installations
function Get-SqlServerInstances {
    $instances = @()

    # Check for SQL Server services
    $services = Get-Service -Name "MSSQL*" -ErrorAction SilentlyContinue
    foreach ($service in $services) {
        if ($service.Status -eq 'Running') {
            $instanceName = $service.Name -replace "MSSQL\$", ""
            if ($instanceName -eq "MSSQLSERVER") {
                $instances += "."
            } else {
                $instances += ".\$instanceName"
            }
        }
    }

    # Check for LocalDB
    if (Test-Command "SqlLocalDB") {
        try {
            $localDbInstances = & SqlLocalDB info
            if ($localDbInstances) {
                $instances += "(localdb)\MSSQLLocalDB"
            }
        }
        catch {
            # Ignore LocalDB errors
        }
    }

    return $instances
}

# Function to test SQL Server connection
function Test-SqlServerConnection {
    param($ConnectionString)

    try {
        $connection = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
        $connection.Open()
        $connection.Close()
        return $true
    }
    catch {
        return $false
    }
}

# Function to test Redis connection
function Test-RedisConnection {
    param($ConnectionString)

    try {
        # Simple TCP connection test
        $parts = $ConnectionString -split ':'
        $host = if ($parts.Length -gt 0) { $parts[0] } else { "localhost" }
        $port = if ($parts.Length -gt 1) { [int]$parts[1] } else { 6379 }

        $tcpClient = New-Object System.Net.Sockets.TcpClient
        $tcpClient.Connect($host, $port)
        $tcpClient.Close()
        return $true
    }
    catch {
        return $false
    }
}

# Main setup logic
Write-Host "Detecting current environment..." -ForegroundColor Yellow

# Check if running as admin
$isAdmin = Test-Administrator
if (-not $isAdmin) {
    Write-Host "[INFO] Running as regular user (some features may require admin privileges)" -ForegroundColor Blue
}

# Check Docker
$dockerInstalled = Test-Command "docker"
$dockerRunning = $false

if ($dockerInstalled) {
    try {
        docker version | Out-Null
        $dockerRunning = $true
        $dockerVersion = (docker --version) -replace "Docker version ", "" -replace ",.*", ""
        Write-Host "[SUCCESS] Docker installed and running (v$dockerVersion)" -ForegroundColor Green
    }
    catch {
        Write-Host "[WARNING] Docker installed but not running" -ForegroundColor Yellow
    }
} else {
    Write-Host "[ERROR] Docker not found" -ForegroundColor Red
}

# Check Chocolatey (for package management)
$chocoInstalled = Test-Command "choco"
if (-not $chocoInstalled) {
    Write-Host "[WARNING] Chocolatey not found - some automated installations won't be available" -ForegroundColor Yellow
}

# Check SQL Server
$sqlInstances = Get-SqlServerInstances
$sqlServerAvailable = $sqlInstances.Count -gt 0

if ($sqlServerAvailable) {
    Write-Host "[SUCCESS] SQL Server instances found: $($sqlInstances -join ', ')" -ForegroundColor Green

    # Test connections
    foreach ($instance in $sqlInstances) {
        $connString = "Server=$instance;Database=master;Trusted_Connection=true;Connection Timeout=5;"
        if (Test-SqlServerConnection $connString) {
            Write-Host "  [SUCCESS] Connection successful: $instance" -ForegroundColor Green
        } else {
            Write-Host "  [WARNING] Connection failed: $instance" -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "[ERROR] No SQL Server instances found" -ForegroundColor Red
}

# Check Redis
$redisRunning = Test-Service "Redis"
if ($redisRunning) {
    Write-Host "[SUCCESS] Redis service running" -ForegroundColor Green

    if (Test-RedisConnection "localhost:6379") {
        Write-Host "  [SUCCESS] Redis connection successful" -ForegroundColor Green
    } else {
        Write-Host "  [WARNING] Redis connection failed" -ForegroundColor Yellow
    }
} else {
    Write-Host "[ERROR] Redis service not found or not running" -ForegroundColor Red
}

Write-Host

# Determine recommended setup
$recommendation = "Unknown"
$estimatedTime = "Unknown"

if ($sqlServerAvailable -and $redisRunning) {
    $recommendation = "External Services (Optimal)"
    $estimatedTime = "~30 seconds"
    Write-Host "[OPTIMAL] Setup detected! Using external services for fastest test execution." -ForegroundColor Green
} elseif ($dockerInstalled -and $dockerRunning) {
    $recommendation = "Docker Containers"
    $estimatedTime = "~5 minutes first run, ~1 minute subsequent"
    Write-Host "[DOCKER] Available - will use containers for missing services." -ForegroundColor Blue
} else {
    $recommendation = "Setup Required"
    $estimatedTime = "Setup needed"
    Write-Host "[WARNING] Additional setup required for optimal development experience." -ForegroundColor Yellow
}

Write-Host "Current Setup: $recommendation" -ForegroundColor Cyan
Write-Host "Estimated Test Time: $estimatedTime" -ForegroundColor Cyan
Write-Host

# Interactive setup
if ($Interactive -and -not ($DockerOnly -or $ExternalOnly)) {
    Write-Host "Setup Options:" -ForegroundColor Cyan
    Write-Host "1. Set up external services (fastest - SQL Server + Redis locally)"
    Write-Host "2. Use Docker containers only (good - automatic but slower)"
    Write-Host "3. Manual configuration"
    Write-Host "4. Skip setup (use current configuration)"
    Write-Host

    do {
        $choice = Read-Host "Choose setup option (1-4)"
    } while ($choice -notin @('1', '2', '3', '4'))

    switch ($choice) {
        '1' { $ExternalOnly = $true }
        '2' { $DockerOnly = $true }
        '3' {
            Write-Host "Manual configuration mode - you'll be prompted for each step" -ForegroundColor Blue
        }
        '4' {
            Write-Host "Skipping setup - using current configuration" -ForegroundColor Blue
            exit 0
        }
    }
}

# External Services Setup
if ($ExternalOnly -or (-not $DockerOnly -and $Interactive)) {
    Write-Host
    Write-Host "SQL Server Setup" -ForegroundColor Cyan
    Write-Host "==================" -ForegroundColor Cyan

    if (-not $sqlServerAvailable) {
        Write-Host "SQL Server not found. Installation options:" -ForegroundColor Yellow
        Write-Host "1. SQL Server Express (free, full features)"
        Write-Host "2. SQL Server LocalDB (lightweight, developer focused)"
        Write-Host "3. Skip (will use Docker)"

        if ($Interactive) {
            do {
                $sqlChoice = Read-Host "Choose SQL Server option (1-3)"
            } while ($sqlChoice -notin @('1', '2', '3'))

            switch ($sqlChoice) {
                '1' {
                    if ($chocoInstalled) {
                        Install-ChocolateyPackage "sql-server-express" "SQL Server Express"
                    } else {
                        Write-Host "Please install SQL Server Express manually from: https://www.microsoft.com/en-us/sql-server/sql-server-downloads" -ForegroundColor Yellow
                    }
                }
                '2' {
                    if ($chocoInstalled) {
                        Install-ChocolateyPackage "sql-server-2019-localdb" "SQL Server LocalDB"
                    } else {
                        Write-Host "Please install SQL Server LocalDB manually from: https://docs.microsoft.com/en-us/sql/database-engine/configure-windows/sql-server-express-localdb" -ForegroundColor Yellow
                    }
                }
                '3' {
                    Write-Host "Skipping SQL Server installation - will use Docker" -ForegroundColor Blue
                }
            }
        }
    }

    Write-Host
    Write-Host "Redis Setup" -ForegroundColor Cyan
    Write-Host "==============" -ForegroundColor Cyan

    if (-not $redisRunning) {
        Write-Host "Redis not found. Installation options:" -ForegroundColor Yellow
        Write-Host "1. Install Redis for Windows"
        Write-Host "2. Skip (will use Docker)"

        if ($Interactive) {
            do {
                $redisChoice = Read-Host "Choose Redis option (1-2)"
            } while ($redisChoice -notin @('1', '2'))

            switch ($redisChoice) {
                '1' {
                    if ($chocoInstalled) {
                        Install-ChocolateyPackage "redis-64" "Redis for Windows"

                        # Start Redis service
                        try {
                            Start-Service -Name "Redis" -ErrorAction SilentlyContinue
                            Write-Host "[SUCCESS] Redis service started" -ForegroundColor Green
                        }
                        catch {
                            Write-Host "[WARNING] Please start Redis service manually" -ForegroundColor Yellow
                        }
                    } else {
                        Write-Host "Please install Redis manually from: https://github.com/microsoftarchive/redis/releases" -ForegroundColor Yellow
                    }
                }
                '2' {
                    Write-Host "Skipping Redis installation - will use Docker" -ForegroundColor Blue
                }
            }
        }
    }
}

# Docker Setup
if ($DockerOnly -or (-not $ExternalOnly -and -not $dockerRunning)) {
    Write-Host
    Write-Host "Docker Setup" -ForegroundColor Cyan
    Write-Host "===============" -ForegroundColor Cyan

    if (-not $dockerInstalled) {
        Write-Host "Docker not found. Please install Docker Desktop:" -ForegroundColor Yellow
        Write-Host "  https://desktop.docker.com/win/main/amd64/Docker%20Desktop%20Installer.exe" -ForegroundColor Blue

        if ($chocoInstalled) {
            Write-Host "Or install via Chocolatey: choco install docker-desktop" -ForegroundColor Blue
        }

        if ($Interactive) {
            Read-Host "Press Enter after installing Docker Desktop"
        }
    }

    if (-not $dockerRunning) {
        Write-Host "Docker is not running. Please start Docker Desktop." -ForegroundColor Yellow

        if ($Interactive) {
            Read-Host "Press Enter after starting Docker Desktop"
        }
    }
}

# Configuration Setup
Write-Host
Write-Host "Configuration Setup" -ForegroundColor Cyan
Write-Host "======================" -ForegroundColor Cyan

# Run the configuration tool
$configTool = Join-Path $PSScriptRoot "..\Tests\MethodCache.Tests.Infrastructure\bin\Release\net9.0\MethodCache.Tests.Infrastructure.exe"

if (Test-Path $configTool) {
    Write-Host "Running configuration tool..." -ForegroundColor Yellow

    $args = @("setup")
    if (-not $Interactive) { $args += "--non-interactive" }
    if ($ConfigPath) { $args += "--config-path", $ConfigPath }

    & $configTool $args
} else {
    Write-Host "Configuration tool not found. Building..." -ForegroundColor Yellow

    try {
        Push-Location (Join-Path $PSScriptRoot "..\Tests\MethodCache.Tests.Infrastructure")
        dotnet build -c Release

        if (Test-Path $configTool) {
            & $configTool setup
        } else {
            Write-Host "[WARNING] Could not build configuration tool. Manual setup required." -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host "[ERROR] Failed to build configuration tool: $_" -ForegroundColor Red
    }
    finally {
        Pop-Location
    }
}

# Environment Variables
Write-Host
Write-Host "Environment Variables" -ForegroundColor Cyan
Write-Host "========================" -ForegroundColor Cyan

$envVars = @{}

# SQL Server
if ($sqlServerAvailable) {
    $bestInstance = $sqlInstances[0]
    $sqlConnString = "Server=$bestInstance;Database=MethodCacheTests;Trusted_Connection=true;Connection Timeout=30;"
    $envVars["METHODCACHE_SQLSERVER_URL"] = $sqlConnString
    Write-Host "METHODCACHE_SQLSERVER_URL=$sqlConnString" -ForegroundColor Green
}

# Redis
if ($redisRunning) {
    $envVars["METHODCACHE_REDIS_URL"] = "localhost:6379"
    Write-Host "METHODCACHE_REDIS_URL=localhost:6379" -ForegroundColor Green
}

# Set environment variables for current session
foreach ($key in $envVars.Keys) {
    [Environment]::SetEnvironmentVariable($key, $envVars[$key], "Process")
}

# Final Summary
Write-Host
Write-Host "Setup Complete!" -ForegroundColor Green
Write-Host "=================" -ForegroundColor Green

# Re-evaluate setup after changes
$finalSqlAvailable = (Get-SqlServerInstances).Count -gt 0
$finalRedisAvailable = Test-Service "Redis"
$finalDockerAvailable = $dockerInstalled -and $dockerRunning

if ($finalSqlAvailable -and $finalRedisAvailable) {
    Write-Host "[SUCCESS] Optimal setup: External SQL Server + Redis" -ForegroundColor Green
    Write-Host "[FAST] Estimated test time: ~30 seconds" -ForegroundColor Green
} elseif ($finalDockerAvailable) {
    Write-Host "[SUCCESS] Good setup: Docker containers available" -ForegroundColor Green
    Write-Host "[DOCKER] Estimated test time: ~5 minutes first run, ~1 minute subsequent" -ForegroundColor Green
} else {
    Write-Host "[WARNING] Partial setup: Additional configuration may be needed" -ForegroundColor Yellow
}

Write-Host
Write-Host "To run integration tests:" -ForegroundColor Cyan
Write-Host "   dotnet test MethodCache.Providers.SqlServer.IntegrationTests" -ForegroundColor Blue
Write-Host "   dotnet test MethodCache.Providers.Redis.IntegrationTests" -ForegroundColor Blue
Write-Host
Write-Host "For more information, see:" -ForegroundColor Cyan
Write-Host "   - INTEGRATION_TEST_STRATEGY.md" -ForegroundColor Blue
Write-Host "   - Tests/MethodCache.Tests.Infrastructure/README.md" -ForegroundColor Blue