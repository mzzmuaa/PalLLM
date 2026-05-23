@echo off
REM PalLLM one-click player launcher for release zips and local repo runs.
REM Installs or refreshes the mod, starts or reuses the sidecar, verifies the
REM setup, opens the dashboard, and launches Palworld.

setlocal enableextensions

set "SCRIPT_DIR=%~dp0"
set "LAUNCHER=%SCRIPT_DIR%scripts\play-palllm.ps1"

if not exist "%LAUNCHER%" (
    echo.
    echo [PalLLM] ERROR: play-palllm.ps1 was not found next to play.bat.
    echo [PalLLM] Make sure you extracted the full release zip.
    echo [PalLLM] Expected: %LAUNCHER%
    echo.
    pause
    exit /b 1
)

echo.
echo =====================================================
echo  PalLLM one-click player launcher
echo =====================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%LAUNCHER%" %*
set "PLAY_EXIT=%ERRORLEVEL%"

if %PLAY_EXIT% neq 0 (
    echo.
    echo [PalLLM] Launch failed with exit code %PLAY_EXIT%.
    echo [PalLLM] Re-run with a manual game path if auto-detect failed:
    echo [PalLLM]   play.bat -PalworldPath "D:\SteamLibrary\steamapps\common\Palworld"
    echo.
    pause
    exit /b %PLAY_EXIT%
)

exit /b 0
