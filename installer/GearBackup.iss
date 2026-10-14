; Build through build-installer.cmd so both executables and the offline manual are fresh.
#ifndef AppVersion
  #error AppVersion must come from the compiled application through the installer build script.
#endif
#ifndef PayloadDir
  #error PayloadDir must be supplied by the installer build script.
#endif
#ifndef BinaryVersion
  #error BinaryVersion must come from the compiled application through the installer build script.
#endif
#ifndef OutputPath
  #error OutputPath must be supplied by the installer build script.
#endif

[Setup]
#ifdef PortableBuild
AppId=GearBackup.Portable
AppName=Gear Backup Portable
DefaultDirName={src}\Gear Backup
OutputBaseFilename=GearBackup-Portable-{#AppVersion}
Uninstallable=no
CreateUninstallRegKey=no
UsePreviousAppDir=no
UsePreviousTasks=no
UsePreviousGroup=no
UsePreviousLanguage=no
UsePreviousSetupType=no
UsePreviousUserInfo=no
#else
; Keep this ID fixed so subsequent installers upgrade the same installation.
AppId={{1B17F726-12B1-4C2A-B26A-68E307B7EC49}
AppName=Gear Backup
DefaultDirName={localappdata}\Programs\Gear Backup
OutputBaseFilename=GearBackup-Setup-{#AppVersion}
UninstallDisplayIcon={app}\GearBackup.GUI.exe
UsePreviousAppDir=yes
UsePreviousTasks=yes
ChangesEnvironment=yes
#endif
AppVersion={#AppVersion}
VersionInfoVersion={#BinaryVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoProductVersion={#BinaryVersion}
VersionInfoProductTextVersion={#AppVersion}
AppPublisher=bondis2k
AppPublisherURL=https://github.com/bondis2k/Gear-Backup
AppSupportURL=https://github.com/bondis2k/Gear-Backup/issues
DefaultGroupName=Gear Backup
PrivilegesRequired=lowest
MinVersion=10.0
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir={#OutputPath}
SetupIconFile={#PayloadDir}\Backup.ico
WizardStyle=modern
WizardSmallImageFile={#PayloadDir}\wizard-small.bmp
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

#ifdef PortableBuild
[Messages]
WelcomeLabel1=Extract Gear Backup Portable
WelcomeLabel2=This wizard extracts Gear Backup into a folder you choose.%n%nNo shortcuts or uninstall entry are created. Open GearBackup.GUI.exe from that folder to run the app. Settings are saved beside the executables.
#else
[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked
Name: clipath; Description: "Add CLI to PATH"; Flags: unchecked
#endif

[Files]
Source: "{#PayloadDir}\GearBackup.GUI.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\GearBackup.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\m8_reader.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Manual.html"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Manual-*.html"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\MANUAL.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\assets\manual\*.png"; DestDir: "{app}\assets\manual"; Flags: ignoreversion
; GearBackup.ini is created by the app. Never package personal settings or delete them.

#ifdef PortableBuild
[InstallDelete]
Type: files; Name: "{app}\GearBackup.installed"
#else
[UninstallDelete]
Type: files; Name: "{app}\GearBackup.installed"
#endif

#ifndef PortableBuild
[Icons]
Name: "{group}\Gear Backup"; Filename: "{app}\GearBackup.GUI.exe"; WorkingDir: "{app}"
Name: "{group}\Gear Backup (CLI)"; Filename: "{app}\GearBackup.exe"; WorkingDir: "{app}"
Name: "{group}\Gear Backup Manual"; Filename: "{app}\Manual.html"
Name: "{group}\Uninstall Gear Backup"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Gear Backup"; Filename: "{app}\GearBackup.GUI.exe"; WorkingDir: "{app}"; Tasks: desktopicon
#endif

[Run]
Filename: "{app}\GearBackup.GUI.exe"; Description: "Open Gear Backup"; Flags: nowait postinstall skipifsilent

[Code]
#ifndef PortableBuild
#include "cli-path.iss"

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not SaveStringToFile(ExpandConstant('{app}\GearBackup.installed'), 'Installed application: settings in APPDATA\GearBackup', False) then
      RaiseException('Could not initialize the installed settings location.');
    UpdateCliPath(ExpandConstant('{app}'), WizardIsTaskSelected('clipath'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then RemoveOwnedCliPath;
end;
#endif

function InitializeSetup: Boolean;
begin
  Result := IsDotNetInstalled(net46, 0);
  if not Result then
    SuppressibleMsgBox('Gear Backup requires .NET Framework 4.6 or newer. Enable or install it, then run Setup again.', mbCriticalError, MB_OK, IDOK);
end;
