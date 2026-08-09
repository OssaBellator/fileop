@echo off
setlocal

where powershell.exe >nul 2>&1
if errorlevel 1 (
    echo ERROR: Windows PowerShell is required to run the focused Windows Copy gate.
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0test-windows-copy-local.ps1" %*
exit /b %ERRORLEVEL%
