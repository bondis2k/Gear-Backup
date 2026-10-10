// User PATH integration. Ownership records ensure we never remove a pre-existing entry.
#ifndef CliEnvironmentKey
  #define CliEnvironmentKey "Environment"
#endif
#ifndef CliOwnerKey
  #define CliOwnerKey "Software\GearBackup\Installer"
#endif

function ExpandEnvironmentStrings(Source, Destination: String; Size: Cardinal): Cardinal;
  external 'ExpandEnvironmentStringsW@kernel32.dll stdcall';

function NormalizePathEntry(Value: String): String;
var
  Expanded: String;
  Size: Cardinal;
begin
  Value := RemoveQuotes(Trim(Value));
  SetLength(Expanded, 32768);
  Size := ExpandEnvironmentStrings(Value, Expanded, 32768);
  if (Size > 0) and (Size <= 32768) then
  begin
    SetLength(Expanded, Size - 1);
    Value := Expanded;
  end;
  Result := RemoveBackslashUnlessRoot(Value);
end;

function FindPathEntry(Value, Entry: String): Integer;
var
  Start, Separator, Count: Integer;
begin
  Result := 0;
  Start := 1;
  Entry := NormalizePathEntry(Entry);
  while Start <= Length(Value) do
  begin
    Separator := Pos(';', Copy(Value, Start, Length(Value)));
    if Separator = 0 then Count := Length(Value) - Start + 1
    else Count := Separator - 1;
    if CompareText(NormalizePathEntry(Copy(Value, Start, Count)), Entry) = 0 then
      Result := Start;  // Our appended entry is normally the last match.
    Start := Start + Count + 1;
  end;
end;

procedure RemoveOwnedCliPath;
var
  Entry, Value: String;
  Start, Separator: Integer;
begin
  if not RegQueryStringValue(HKCU, '{#CliOwnerKey}', 'PathEntry', Entry) then Exit;
  if RegQueryStringValue(HKCU, '{#CliEnvironmentKey}', 'Path', Value) then
  begin
    Start := FindPathEntry(Value, Entry);
    if Start > 0 then
    begin
      Separator := Pos(';', Copy(Value, Start, Length(Value)));
      if Separator > 0 then Delete(Value, Start, Separator)
      else if Start > 1 then Delete(Value, Start - 1, Length(Value) - Start + 2)
      else Value := '';
      if not RegWriteExpandStringValue(HKCU, '{#CliEnvironmentKey}', 'Path', Value) then
        RaiseException('Could not remove Gear Backup from your user PATH.');
    end;
  end;
  RegDeleteValue(HKCU, '{#CliOwnerKey}', 'PathEntry');
  RegDeleteKeyIfEmpty(HKCU, '{#CliOwnerKey}');
end;

procedure UpdateCliPath(Entry: String; Enabled: Boolean);
var
  Owned, Value: String;
begin
  if RegQueryStringValue(HKCU, '{#CliOwnerKey}', 'PathEntry', Owned) then
    if (not Enabled) or (CompareText(NormalizePathEntry(Owned), NormalizePathEntry(Entry)) <> 0) then
      RemoveOwnedCliPath;
  if not Enabled then Exit;
  Value := '';
  RegQueryStringValue(HKCU, '{#CliEnvironmentKey}', 'Path', Value);
  if FindPathEntry(Value, Entry) > 0 then Exit;
  if not RegWriteStringValue(HKCU, '{#CliOwnerKey}', 'PathEntry', Entry) then
    RaiseException('Could not record the CLI PATH setting.');
  if Value <> '' then Value := Value + ';';
  if not RegWriteExpandStringValue(HKCU, '{#CliEnvironmentKey}', 'Path', Value + Entry) then
  begin
    RegDeleteValue(HKCU, '{#CliOwnerKey}', 'PathEntry');
    RaiseException('Could not add Gear Backup to your user PATH.');
  end;
end;
