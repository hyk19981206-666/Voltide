@echo off
rem ===========================================================================
rem  Voltide user entry point.
rem
rem  Why this file exists: the workspace (D:\workspace) carries a Low
rem  integrity label, so an exe started from there can never register a tray
rem  icon (UIPI blocks Low -> Medium). The supported launch path is the
rem  automatically deployed copy under %LOCALAPPDATA%\Voltide\app.
rem
rem  Behaviour:
rem    - already deployed  -> start %LOCALAPPDATA%\Voltide\app\Voltide.exe --show
rem    - not deployed yet   -> run deploy.ps1 next to this file (copies files,
rem                           verifies hashes and integrity labels), then start
rem  No UAC prompt is used or needed.
rem ===========================================================================
setlocal
set "R=%LOCALAPPDATA%\Voltide\app"

if exist "%R%\Voltide.exe" goto launch

rem --- first run: deploy from the script that sits next to this file ---
set "PS=powershell"
where pwsh >nul 2>nul
if not errorlevel 1 set "PS=pwsh"

if exist "%~dp0Install-Voltide.ps1" (
    echo [Voltide] deploying from "%~dp0Install-Voltide.ps1" ...
    "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Voltide.ps1"
) else if exist "%~dp0deploy.ps1" (
    echo [Voltide] deploying from "%~dp0deploy.ps1" ...
    "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy.ps1"
) else (
    echo [Voltide] ERROR: neither Install-Voltide.ps1 nor deploy.ps1 found in "%~dp0"
    exit /b 11
)

if errorlevel 1 goto failed

:launch
echo [Voltide] starting "%R%\Voltide.exe" ...
start "" "%R%\Voltide.exe" --show
exit /b 0

:failed
echo [Voltide] deployment failed. Nothing was started. See messages above.
exit /b 1
