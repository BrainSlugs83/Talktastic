@echo off
REM Unconditionally rebuild Talktastic (say.exe) into dist\.
REM Thin wrapper -- calls rebuild.ps1 which delegates to build.ps1 -Force.
REM Uses pwsh if available; falls back to Windows PowerShell.

setlocal

where pwsh >nul 2>&1
if %ERRORLEVEL%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0rebuild.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0rebuild.ps1" %*
)

endlocal
exit /b %ERRORLEVEL%
