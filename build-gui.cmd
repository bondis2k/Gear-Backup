@echo off
rem Build the Windows GUI alongside the CLI in dist\app.
setlocal
cd /d "%~dp0"
set OUT=dist\app\GearBackup.GUI.exe
if not "%~2"=="" set OUT=%~2
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\build-app.ps1" -Target GUI -Output "%OUT%"
set RESULT=%ERRORLEVEL%
if not "%~1"=="--no-pause" echo %CMDCMDLINE% | find /i "/c" >nul && pause
exit /b %RESULT%
