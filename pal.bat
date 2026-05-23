@echo off
REM PalLLM verb-driven task runner -- Windows shortcut.
REM
REM Lets you type "pal status" instead of "pwsh ./pal.ps1 status".
REM Forwards all arguments verbatim to pal.ps1, including extra flags
REM that the underlying script consumes (e.g. -PalworldPath, -DryRun).
REM
REM See pal.ps1 for the full verb table, or run:  pal help

setlocal enableextensions

set "SCRIPT_DIR=%~dp0"
set "RUNNER=%SCRIPT_DIR%pal.ps1"

if not exist "%RUNNER%" (
    echo [PalLLM] ERROR: pal.ps1 was not found next to pal.bat.
    echo [PalLLM] Expected: %RUNNER%
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%RUNNER%" %*
exit /b %ERRORLEVEL%
