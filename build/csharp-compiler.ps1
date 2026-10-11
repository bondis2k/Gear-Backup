# Finds the C# compiler for builds and tests: the modern compiler (Roslyn) from Visual Studio or its
# Build Tools. It still builds .NET Framework 4.x programs, so users need nothing new. GEARBACKUP_CSC
# overrides the search. Dot-source this file, then call Get-CSharpCompiler.

# C# 7.3 is the newest language version Microsoft supports for .NET Framework programs; later versions
# rely on runtime features .NET Framework doesn't have.
$CSharpLanguageVersion = '7.3'

function Get-CSharpCompiler {
    if ($env:GEARBACKUP_CSC) {
        if (-not (Test-Path -LiteralPath $env:GEARBACKUP_CSC)) { throw "GEARBACKUP_CSC points to a missing file: $env:GEARBACKUP_CSC" }
        return $env:GEARBACKUP_CSC
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
        if ($found -and (Test-Path -LiteralPath $found)) { return $found }
    }
    throw 'The C# compiler was not found. Install Visual Studio Build Tools (https://aka.ms/vs/17/release/vs_BuildTools.exe) with the ".NET desktop build tools" workload, or set GEARBACKUP_CSC to a csc.exe.'
}
