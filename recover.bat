@echo off
REM PalLLM one-click recovery. Use this when the sidecar looks stuck,
REM the dashboard shows Critical, or envelopes appear to be piling up.
REM Stops the sidecar cleanly, archives stuck bridge envelopes (so nothing
REM is lost — they go under the runtime root's Bridge\RecoveryArchive\),
REM prunes durable evidence older than 14 days, and starts the sidecar
REM back up. Reports the operator happiness score when done.

setlocal enableextensions

set "SCRIPT_DIR=%~dp0"
set "RECOVERY=%SCRIPT_DIR%scripts\recover-palllm.ps1"

if not exist "%RECOVERY%" (
    echo.
    echo [PalLLM] ERROR: recover-palllm.ps1 was not found next to recover.bat.
    echo [PalLLM] Make sure you extracted the full release zip.
    echo [PalLLM] Expected: %RECOVERY%
    echo.
    pause
    exit /b 1
)

echo.
echo =====================================================
echo  PalLLM one-click recovery
echo =====================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%RECOVERY%" %*
set "RECOVER_EXIT=%ERRORLEVEL%"

if %RECOVER_EXIT% neq 0 (
    echo.
    echo [PalLLM] Recovery reported issues (exit code %RECOVER_EXIT%).
    echo [PalLLM] The sidecar may still be starting; run scripts\doctor.ps1
    echo [PalLLM] to inspect what's still missing.
    echo.
    pause
    exit /b %RECOVER_EXIT%
)

echo.
echo Recovery complete. Open the dashboard or run play.bat to continue.
echo.
exit /b 0
