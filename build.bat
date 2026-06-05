@echo off
REM Build Talktastic (say.exe) into dist\ if source has changed.
REM Uses pwsh if available; falls back to Windows PowerShell.

setlocal

where pwsh >nul 2>&1
if %ERRORLEVEL%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
)

endlocal
exit /b %ERRORLEVEL%
