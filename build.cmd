@echo off
rem Builds the CLI using the shared compiler setup.
setlocal
cd /d "%~dp0"
set OUT=dist\app\GearBackup.exe
rem Optional second argument lets a build finish while the usual exe is running.
if not "%~2"=="" set OUT=%~2

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\build-app.ps1" -Target CLI -Output "%OUT%"
set RESULT=%ERRORLEVEL%

rem Keep the window open when started by double-click (pass --no-pause to skip)
if not "%~1"=="--no-pause" echo %CMDCMDLINE% | find /i "/c" >nul && pause
exit /b %RESULT%
