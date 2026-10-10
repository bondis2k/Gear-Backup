@echo off
setlocal
cd /d "%~dp0"
where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo PowerShell 7 is required to build the installer. Install it from https://aka.ms/powershell
  exit /b 1
)
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\build-installer.ps1" %*
exit /b %ERRORLEVEL%
