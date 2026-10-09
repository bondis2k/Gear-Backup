@echo off
rem Builds M8Backup.exe from src\*.cs with the C# compiler that comes with Windows (.NET Framework 4).
setlocal
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WINMD=%WINDIR%\System32\WinMetadata

"%FW%\csc.exe" /nologo /out:M8Backup.exe ^
  /r:System.Management.dll /r:Microsoft.VisualBasic.dll ^
  /r:"%WINMD%\Windows.Data.winmd" /r:"%WINMD%\Windows.UI.winmd" /r:"%WINMD%\Windows.Foundation.winmd" ^
  /r:"%FW%\System.Runtime.WindowsRuntime.dll" /r:"%FW%\System.Runtime.dll" ^
  /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
  src\*.cs
set RESULT=%ERRORLEVEL%

if %RESULT%==0 (echo Built M8Backup.exe) else (echo. & echo Build FAILED.)

rem Keep the window open when started by double-click (pass --no-pause to skip)
if not "%~1"=="--no-pause" echo %CMDCMDLINE% | find /i "/c" >nul && pause
exit /b %RESULT%
