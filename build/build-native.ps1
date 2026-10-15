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

Push-Location $crate
try {
    # --locked builds exactly the dependency versions in Cargo.lock.
    if ($Test) {
        & $cargo test --locked --quiet
        if ($LASTEXITCODE -ne 0) { throw 'The M8 reader tests failed.' }
    }
    & $cargo build --release --locked --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Building the M8 song reader failed.' }
} finally { Pop-Location }

$dll = Join-Path $crate 'target\release\m8_reader.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw "Expected $dll after the build." }
if ($OutputDirectory) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $dll = (Copy-Item -LiteralPath $dll -Destination $OutputDirectory -PassThru -Force).FullName
}
$dll
