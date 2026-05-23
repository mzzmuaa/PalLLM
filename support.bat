@echo off
REM PalLLM one-click support bundle exporter for release zips and local repo runs.
REM Captures the latest launch evidence, proof surfaces, and release-readiness
REM artifacts into one zip under the runtime root.

setlocal enableextensions

set "SCRIPT_DIR=%~dp0"
set "EXPORTER=%SCRIPT_DIR%scripts\export-support-bundle.ps1"

if not exist "%EXPORTER%" (
    echo.
    echo [PalLLM] ERROR: export-support-bundle.ps1 was not found next to support.bat.
    echo [PalLLM] Make sure you extracted the full release zip.
    echo [PalLLM] Expected: %EXPORTER%
    echo.
    pause
    exit /b 1
)

echo.
echo =====================================================
echo  PalLLM support bundle exporter
echo =====================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%EXPORTER%" %*
set "SUPPORT_EXIT=%ERRORLEVEL%"

if %SUPPORT_EXIT% neq 0 (
    echo.
    echo [PalLLM] Support bundle export failed with exit code %SUPPORT_EXIT%.
    echo [PalLLM] Re-run from a terminal if you need to inspect the error.
    echo.
    pause
    exit /b %SUPPORT_EXIT%
)

exit /b 0
