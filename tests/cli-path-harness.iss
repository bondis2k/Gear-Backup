// Runs the actual installer PATH routines against an isolated registry key.
#define CliEnvironmentKey PathTestRoot + "\Environment"
#define CliOwnerKey PathTestRoot + "\Owner"
[Setup]
AppName=Gear Backup PATH tests
AppVersion=1.0
CreateAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
PrivilegesRequired=lowest
OutputDir={#TestOutput}
OutputBaseFilename=GearBackup.PathTests
[Code]
#include "..\installer\cli-path.iss"
var
  Assertions: Integer;

procedure Assert(Condition: Boolean; Message: String);
begin
  if not Condition then RaiseException(Message);
  Assertions := Assertions + 1;
end;

function UserPath: String;
begin
  Result := '';
  RegQueryStringValue(HKCU, '{#CliEnvironmentKey}', 'Path', Result);
end;

procedure RunTests;
var
  Original, Entry, Other, Preexisting: String;
begin
  Original := '%USERPROFILE%\tools;C:\Existing;;C:\Tail';
  Entry := 'C:\Gear Backup Test';
  Other := 'D:\Gear Backup Test';
  UpdateCliPath(Entry, False);
  Assert(not RegKeyExists(HKCU, '{#CliEnvironmentKey}'), 'Unchecked task must leave PATH untouched');
  RegWriteExpandStringValue(HKCU, '{#CliEnvironmentKey}', 'Path', Original);
  UpdateCliPath(Entry, True);
  Assert(UserPath = Original + ';' + Entry, 'Add must preserve existing PATH and environment variables');
  Assert(RegValueExists(HKCU, '{#CliOwnerKey}', 'PathEntry'), 'New entry must record ownership');
  UpdateCliPath(Entry, True);
  Assert(UserPath = Original + ';' + Entry, 'Upgrade must not duplicate entry');
  UpdateCliPath(Other, True);
  Assert(UserPath = Original + ';' + Other, 'Relocation must remove previous owned entry');
  UpdateCliPath(Other, False);
  Assert(UserPath = Original, 'Unchecking on upgrade must remove owned entry');
  Preexisting := Original + ';"c:\GEAR BACKUP TEST\"';
  RegWriteExpandStringValue(HKCU, '{#CliEnvironmentKey}', 'Path', Preexisting);
  UpdateCliPath(Entry, True);
  Assert(UserPath = Preexisting, 'Case, quotes and trailing slash must not create duplicates');
  Assert(not RegValueExists(HKCU, '{#CliOwnerKey}', 'PathEntry'), 'Preexisting entry must not become owned');
  RemoveOwnedCliPath;
  Assert(UserPath = Preexisting, 'Uninstall must preserve preexisting entry');
  Preexisting := Original + ';%USERPROFILE%\GearBackupPathTest';
  RegWriteExpandStringValue(HKCU, '{#CliEnvironmentKey}', 'Path', Preexisting);
  UpdateCliPath(GetEnv('USERPROFILE') + '\GearBackupPathTest', True);
  Assert(UserPath = Preexisting, 'Environment variable entry must be recognized');
  RegWriteExpandStringValue(HKCU, '{#CliEnvironmentKey}', 'Path', Original);
  UpdateCliPath(Entry, True);
  RegWriteExpandStringValue(HKCU, '{#CliEnvironmentKey}', 'Path', UserPath + ';C:\LaterAddition');
  RemoveOwnedCliPath;
  Assert(UserPath = Original + ';C:\LaterAddition', 'Uninstall must preserve later PATH additions');
  RegWriteExpandStringValue(HKCU, '{#CliEnvironmentKey}', 'Path', '');
  UpdateCliPath(Entry, True);
  Assert(UserPath = Entry, 'Empty PATH must not gain a leading separator');
  RemoveOwnedCliPath;
  Assert(UserPath = '', 'Uninstall of only owned entry must leave an empty PATH');
end;

function InitializeSetup: Boolean;
begin
  Result := False; // Test-only: no installation takes place.
  try
    RunTests;
    SaveStringToFile('{#TestResult}', 'PASS: ' + IntToStr(Assertions) + ' installer PATH assertions.', False);
  except
    SaveStringToFile('{#TestResult}', 'FAIL: ' + GetExceptionMessage, False);
  end;
  RegDeleteKeyIncludingSubkeys(HKCU, '{#PathTestRoot}');
end;
