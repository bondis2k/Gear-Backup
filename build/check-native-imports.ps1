# Fail the native build if a new dependency would require a non-system DLL search directory.
param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = 'Stop'
$image = [IO.File]::ReadAllBytes($Path)
function Require-Range([long]$Offset, [long]$Count) {
    if ($Offset -lt 0 -or $Count -lt 0 -or $Offset -gt $image.Length - $Count) { throw 'Invalid PE import table.' }
}
function Read-U16([long]$Offset) { Require-Range $Offset 2; [BitConverter]::ToUInt16($image, [int]$Offset) }
function Read-U32([long]$Offset) { Require-Range $Offset 4; [BitConverter]::ToUInt32($image, [int]$Offset) }
$pe = Read-U32 0x3c
if ((Read-U32 $pe) -ne 0x4550) { throw 'Native reader is not a PE image.' }
$sectionCount = Read-U16 ($pe + 6)
$optionalSize = Read-U16 ($pe + 20)
$optional = $pe + 24
if ((Read-U16 $optional) -ne 0x20b -or $optionalSize -lt 240) { throw 'Expected a PE32+ native reader.' }
$sections = $optional + $optionalSize
Require-Range $sections ($sectionCount * 40)
function Resolve-Rva([uint32]$Rva, [int]$Count) {
    for ($s = 0; $s -lt $sectionCount; $s++) {
        $section = $sections + $s * 40
        $address = Read-U32 ($section + 12)
        $rawSize = Read-U32 ($section + 16)
        if ($Rva -ge $address -and [long]$Rva + $Count -le [long]$address + $rawSize) {
            $offset = [long](Read-U32 ($section + 20)) + $Rva - $address
            Require-Range $offset $Count
            return $offset
        }
    }
    throw 'PE import RVA escapes the file-backed sections.'
}
# Delay-loaded imports need a separate loader policy; this reader does not use them.
if ((Read-U32 ($optional + 112 + 13 * 8)) -ne 0 -or (Read-U32 ($optional + 116 + 13 * 8)) -ne 0) {
    throw 'Unexpected delay-loaded dependency in m8_reader.dll.'
}
$importRva = Read-U32 ($optional + 112 + 8)
$importSize = Read-U32 ($optional + 116 + 8)
if ($importRva -eq 0 -or $importSize -lt 20 -or $importSize -gt 65536) { throw 'Invalid native import directory.' }
$imports = Resolve-Rva $importRva $importSize
$allowed = @('advapi32.dll', 'bcrypt.dll', 'kernel32.dll', 'ntdll.dll', 'userenv.dll', 'ws2_32.dll')
$terminated = $false
for ($entry = 0; $entry + 20 -le $importSize; $entry += 20) {
    $descriptor = $imports + $entry
    $allZero = $true
    for ($field = 0; $field -lt 20; $field += 4) { if ((Read-U32 ($descriptor + $field)) -ne 0) { $allZero = $false } }
    if ($allZero) { $terminated = $true; break }
    $nameRva = Read-U32 ($descriptor + 12)
    $nameBytes = [Collections.Generic.List[byte]]::new()
    for ($character = 0; $character -lt 260; $character++) {
        $nameOffset = Resolve-Rva ($nameRva + $character) 1
        $value = $image[$nameOffset]
        if ($value -eq 0) { break }
        $nameBytes.Add($value)
    }
    if ($character -eq 260) { throw 'Unterminated native import name.' }
    $name = [Text.Encoding]::ASCII.GetString($nameBytes.ToArray()).ToLowerInvariant()
    if ($allowed -notcontains $name -and $name -notmatch '^api-ms-win-[a-z0-9-]+\.dll$') { throw "Unexpected native dependency '$name'. Review its System32-only loading before allowing it." }
    Write-Output $name
}
if (-not $terminated) { throw 'Unterminated native import directory.' }
