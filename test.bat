@echo off
REM Run all tests with coverage. Real logic in test.ps1.
REM Usage:  test.bat            (summary only)
REM         test.bat --html     (also open HTML coverage report)
REM         test.bat --stt      (also run Whisper STT integration tests; needs build.bat + conda)

setlocal
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0test.ps1" %*
endlocal
exit /b %ERRORLEVEL%
