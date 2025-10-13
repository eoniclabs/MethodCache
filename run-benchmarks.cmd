@echo off
REM Wrapper script to run run-benchmarks.sh on Windows using bash

REM Try to find Git Bash first (most reliable on Windows)
set "GIT_BASH=%ProgramFiles%\Git\usr\bin\bash.exe"
if exist "%GIT_BASH%" (
    "%GIT_BASH%" "%~dp0run-benchmarks.sh" %*
    exit /b %ERRORLEVEL%
)

REM Try 32-bit Program Files location
set "GIT_BASH=%ProgramFiles(x86)%\Git\usr\bin\bash.exe"
if exist "%GIT_BASH%" (
    "%GIT_BASH%" "%~dp0run-benchmarks.sh" %*
    exit /b %ERRORLEVEL%
)

REM Fallback to system bash (might be WSL or other)
where bash >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    bash "%~dp0run-benchmarks.sh" %*
    exit /b %ERRORLEVEL%
)

REM No bash found
echo Error: bash is not available on this system.
echo Please install Git for Windows from https://git-scm.com/download/win
exit /b 1
