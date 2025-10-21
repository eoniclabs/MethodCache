@echo off
setlocal

set SCRIPT_DIR=%~dp0

if not exist "%SCRIPT_DIR%run-benchmarks.ps1" (
    echo Could not find run-benchmarks.ps1 in %SCRIPT_DIR%
    exit /b 1
)

if "%~1"=="" (
    echo Usage: run-benchmarks.cmd ^<category^> [options]
    echo   Categories: basic, providers, concurrent, memory, realworld, generic, serialization, all
    echo   Options match the PowerShell and bash scripts (e.g. --format html --quick)
    exit /b 1
)

pwsh -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%run-benchmarks.ps1" %*
set EXIT_CODE=%ERRORLEVEL%

if %EXIT_CODE% NEQ 0 (
    exit /b %EXIT_CODE%
)

exit /b 0
