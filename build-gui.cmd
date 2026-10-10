@echo off
rem Build the Windows GUI alongside the existing CLI.
setlocal
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WINMD=%WINDIR%\System32\WinMetadata
set OUT=M8Backup.GUI.exe
if not "%~2"=="" set OUT=%~2
"%FW%\csc.exe" /nologo /target:winexe /main:M8Gui /out:"%OUT%" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:System.Management.dll /r:Microsoft.VisualBasic.dll ^
  /r:"%WINMD%\Windows.Data.winmd" /r:"%WINMD%\Windows.UI.winmd" /r:"%WINMD%\Windows.Foundation.winmd" ^
  /r:"%FW%\System.Runtime.WindowsRuntime.dll" /r:"%FW%\System.Runtime.dll" ^
  /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
  src\*.cs gui\*.cs gui\Dialogs\*.cs
set RESULT=%ERRORLEVEL%
if %RESULT%==0 (echo Built %OUT%) else (echo. & echo Build FAILED.)
if not "%~1"=="--no-pause" echo %CMDCMDLINE% | find /i "/c" >nul && pause
exit /b %RESULT%
