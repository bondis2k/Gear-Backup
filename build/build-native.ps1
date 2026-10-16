# Builds the Rust M8 song reader (native/m8-reader) as m8_reader.dll. Needs the Rust toolchain
# (https://rustup.rs) with the MSVC target, which the Visual Studio Build Tools already provide.
# With -OutputDirectory the DLL is copied there (next to the application); otherwise it stays in
# native\m8-reader\target\release. The DLL's full path is written to the output.
param(
    [string]$OutputDirectory,
    # Run the Rust unit tests too.
    [switch]$Test,
    # Return nothing instead of failing when Rust isn't installed (the tests then skip the native reader).
    [switch]$Optional
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$crate = Join-Path $projectRoot 'native\m8-reader'

$cargo = (Get-Command cargo.exe -ErrorAction SilentlyContinue).Source
if (-not $cargo) {
    $candidate = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
    if (Test-Path -LiteralPath $candidate) { $cargo = $candidate }
}
if (-not $cargo) {
    if ($Optional) { Write-Warning 'Rust (cargo) was not found; the M8 song reader was not built.'; return }
    throw 'Rust is required to build the M8 song reader. Install it from https://rustup.rs (default settings), then build again.'
}

# Panic messages embed source paths, which would put the builder's user name and folders into the shipped
# DLL. These remaps replace them (later flags win, so the more specific ones come last). The variable
# replaces the crate's .cargo\config.toml rustflags, so crt-static is repeated here.
$cargoHome = if ($env:CARGO_HOME) { $env:CARGO_HOME } else { Join-Path $env:USERPROFILE '.cargo' }
$flags = @('-Ctarget-feature=+crt-static', "--remap-path-prefix=$env:USERPROFILE=~",
    "--remap-path-prefix=$cargoHome=~\.cargo", "--remap-path-prefix=$projectRoot=gearbackup")
$previousFlags = $env:CARGO_ENCODED_RUSTFLAGS
$env:CARGO_ENCODED_RUSTFLAGS = $flags -join [string][char]0x1f
Push-Location $crate
try {
    # --locked builds exactly the dependency versions in Cargo.lock.
    if ($Test) {
        & $cargo test --locked --quiet
        if ($LASTEXITCODE -ne 0) { throw 'The M8 reader tests failed.' }
    }
    & $cargo build --release --locked --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Building the M8 song reader failed.' }
} finally {
    Pop-Location
    $env:CARGO_ENCODED_RUSTFLAGS = $previousFlags
}

$dll = Join-Path $crate 'target\release\m8_reader.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw "Expected $dll after the build." }

# Refuse to ship a DLL that still carries the builder's folders.
$text = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($dll))
foreach ($private in @($env:USERPROFILE, $projectRoot)) {
    if ($private -and $text.IndexOf($private, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "m8_reader.dll contains the build path '$private'. Check the path remapping in build-native.ps1."
    }
}
if ($OutputDirectory) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $dll = (Copy-Item -LiteralPath $dll -Destination $OutputDirectory -PassThru -Force).FullName
}
$dll
