@echo off
REM PalLLM one-click uninstaller for end users.
REM Double-click from the install directory to remove the PalLLM mod from
REM Palworld using the install manifest written by install.bat.
REM
REM Default behavior: removes the mod files, keeps your chat history and
REM custom packs. Pass /full to wipe the runtime root too.
REM Pass /preview (or /dry-run) to see what would happen without changing
REM anything.

setlocal enableextensions

set "SCRIPT_DIR=%~dp0"
set "TOOLING=%SCRIPT_DIR%scripts\uninstall-mod.ps1"

if not exist "%TOOLING%" (
    echo.
    echo [PalLLM] ERROR: uninstall-mod.ps1 was not found next to uninstall.bat.
    echo [PalLLM] Make sure you extracted the full release zip, not just uninstall.bat.
    echo [PalLLM] Expected: %TOOLING%
    echo.
    pause
    exit /b 1
)

set "PS_ARGS="
set "PREVIEW=0"

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="/full"     ( set "PS_ARGS=%PS_ARGS% -Full" & shift & goto parse_args )
if /i "%~1"=="--full"    ( set "PS_ARGS=%PS_ARGS% -Full" & shift & goto parse_args )
if /i "%~1"=="/preview"  ( set "PS_ARGS=%PS_ARGS% -DryRun" & set "PREVIEW=1" & shift & goto parse_args )
if /i "%~1"=="/dry-run"  ( set "PS_ARGS=%PS_ARGS% -DryRun" & set "PREVIEW=1" & shift & goto parse_args )
if /i "%~1"=="--dry-run" ( set "PS_ARGS=%PS_ARGS% -DryRun" & set "PREVIEW=1" & shift & goto parse_args )
REM Forward anything else (e.g. -PalworldPath "...") straight through.
set "PS_ARGS=%PS_ARGS% %~1"
shift
goto parse_args
:args_done

echo.
echo =====================================================
echo  PalLLM uninstaller
if "%PREVIEW%"=="1" (
    echo  PREVIEW mode - no files will be changed.
) else (
    echo  Removing the PalLLM mod from Palworld...
    echo  Personal data ^(chat history, packs^) is preserved
    echo  unless you passed /full.
)
echo =====================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%TOOLING%" %PS_ARGS%
set "UNINSTALL_EXIT=%ERRORLEVEL%"

if %UNINSTALL_EXIT% neq 0 (
    echo.
    echo [PalLLM] Uninstall exited with code %UNINSTALL_EXIT%.
    echo [PalLLM] Common causes:
    echo [PalLLM]   - Palworld is still running. Close it and try again.
    echo [PalLLM]   - The mod folder was already removed manually.
    echo [PalLLM]   - The install manifest is missing; pass -PalworldPath to
    echo [PalLLM]     uninstall-mod.ps1 directly to retry by convention.
    echo.
    pause
    exit /b %UNINSTALL_EXIT%
)

echo.
if "%PREVIEW%"=="1" (
    echo =====================================================
    echo  Preview complete. Re-run without /preview to apply.
    echo =====================================================
) else (
    echo =====================================================
    echo  PalLLM uninstalled.
    echo  Re-install:    double-click install.bat
    echo  Backup data:   %%LOCALAPPDATA%%\Pal\Saved\PalLLM
    echo =====================================================
)
echo.

pause
exit /b 0
