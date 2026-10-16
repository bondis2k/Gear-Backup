using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GearBackup;

[assembly: AssemblyTitle(AppIdentity.Name)]
[assembly: AssemblyProduct(AppIdentity.Name)]
[assembly: AssemblyVersion(AppIdentity.BinaryVersion)]
[assembly: AssemblyFileVersion(AppIdentity.BinaryVersion)]
[assembly: AssemblyInformationalVersion(AppIdentity.Version)]

// The core's types are internal; the programs built on it and the test programs may use them.
[assembly: InternalsVisibleTo("GearBackup")]
[assembly: InternalsVisibleTo("GearBackup.GUI")]
[assembly: InternalsVisibleTo("GearBackup.Tests")]
[assembly: InternalsVisibleTo("GearBackup.GuiTests")]
[assembly: InternalsVisibleTo("GearBackup.Benchmarks")]

// Windows looks for a DLL in the program's own folder first. cfgmgr32.dll isn't one of the "known DLLs"
// Windows always takes from System32, so without this a cfgmgr32.dll planted next to the exe (e.g. in
// Downloads) would be loaded. This makes every DllImport in the program load from System32 only.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
