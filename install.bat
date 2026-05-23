@echo off
REM PalLLM one-click installer for end users.
REM Double-click from a released zip to auto-detect Palworld, install the mod,
REM and verify the sidecar. For developers working from the repo, see
REM scripts\install-dev-mod.ps1 instead.

setlocal enableextensions

set "SCRIPT_DIR=%~dp0"
set "TOOLING=%SCRIPT_DIR%scripts\install-mod.ps1"

if not exist "%TOOLING%" (
    echo.
    echo [PalLLM] ERROR: install-mod.ps1 was not found next to install.bat.
    echo [PalLLM] Make sure you extracted the full release zip, not just install.bat.
    echo [PalLLM] Expected: %TOOLING%
    echo.
    pause
    exit /b 1
)

echo.
echo =====================================================
echo  PalLLM installer
echo  Auto-detecting your Palworld install...
echo =====================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%TOOLING%" %*
set "INSTALL_EXIT=%ERRORLEVEL%"

if %INSTALL_EXIT% neq 0 (
    echo.
    echo [PalLLM] Install failed with exit code %INSTALL_EXIT%.
    echo [PalLLM] Common causes:
    echo [PalLLM]   - Palworld not detected. Re-run with the game path:
    echo [PalLLM]     install.bat -PalworldPath "D:\SteamLibrary\steamapps\common\Palworld"
    echo [PalLLM]   - UE4SS is not installed. See docs\QUICKSTART.md.
    echo [PalLLM]   - The game is running. Close Palworld and try again.
    echo.
    pause
    exit /b %INSTALL_EXIT%
)

echo.
echo =====================================================
echo  PalLLM installed.
echo  Easiest next step:      double-click play.bat
echo  Manual path:            scripts\start-sidecar.ps1
echo  Verify only:            scripts\doctor.ps1
echo =====================================================
echo.

pause
exit /b 0
