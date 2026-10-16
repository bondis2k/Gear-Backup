using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Reads an M8 song. Callers depend on IM8SongReader only, so the native DLL can later be swapped for another reader
// (or a fake in tests) without touching the window.
internal interface IM8SongReader
{
    M8SongSummary Read(string path);
}

static class M8SongReaders
{
    // Replaced by tests; the window uses whatever is set here.
    internal static IM8SongReader Current = new NativeM8SongReader();
}

// Calls m8_reader.dll, the Rust reader built from native/m8-reader. The DLL is loaded by full path from the
// application folder, and its own dependencies only from System32 (never the current folder or PATH), so a missing or
// mismatched DLL gives a clear message, not a crash. There is deliberately no environment variable to point elsewhere:
// tests pass the path to the constructor.
sealed class NativeM8SongReader : IM8SongReader
{
    internal const string LibraryName = "m8_reader.dll";
    const uint AbiVersion = 2;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint AbiVersionFunction();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ReadSongFunction([MarshalAs(UnmanagedType.LPWStr)] string path, UIntPtr pathLength, out UIntPtr length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FreeFunction(IntPtr buffer, UIntPtr length);

    const uint SearchDllLoadDir = 0x100, SearchSystem32 = 0x800; // LOAD_LIBRARY_SEARCH_*
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);

    readonly string explicitPath;
    readonly object gate = new object();
    ReadSongFunction readSong;
    FreeFunction free;
    string loadError;

    internal NativeM8SongReader(string libraryPath = null) { explicitPath = libraryPath; }

    internal string LibraryPath
    {
        get
        {
            return Path.GetFullPath(string.IsNullOrEmpty(explicitPath) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LibraryName) : explicitPath);
        }
    }

    public M8SongSummary Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new M8SongException("Choose an M8 song file first.");
        Load();
        UIntPtr length;
        IntPtr buffer = readSong(path, (UIntPtr)path.Length, out length);
        if (buffer == IntPtr.Zero) throw new M8SongException("The M8 reader returned no result.");
        string json;
        try
        {
            var bytes = new byte[checked((int)length)];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            json = Encoding.UTF8.GetString(bytes);
        }
        finally { free(buffer, length); }
        return M8SongJson.Parse(json);
    }

    void Load()
    {
        lock (gate)
        {
            if (readSong != null) return;
            if (loadError != null) throw new M8SongException(loadError);
            try { LoadLibrary(); }
            catch (M8SongException ex) { loadError = ex.Message; throw; }
        }
    }

    void LoadLibrary()
    {
        string path = LibraryPath;
        if (!File.Exists(path)) throw new M8SongException("Reading M8 songs needs " + LibraryName + " next to the application, and it was not found at " + path + ".");
        if (!Environment.Is64BitProcess) throw new M8SongException("Reading M8 songs needs a 64-bit Windows session.");
        IntPtr module = LoadLibraryExW(path, IntPtr.Zero, SearchDllLoadDir | SearchSystem32);
        if (module == IntPtr.Zero)
            throw new M8SongException(LibraryName + " could not be loaded (" + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message + ").");
        var version = Function<AbiVersionFunction>(module, "m8_abi_version");
        if (version() != AbiVersion) throw new M8SongException(LibraryName + " is a different version than this application expects. Reinstall Gear Backup.");
        free = Function<FreeFunction>(module, "m8_free");
        readSong = Function<ReadSongFunction>(module, "m8_read_song");
    }

    static T Function<T>(IntPtr module, string name) where T : class
    {
        IntPtr address = GetProcAddress(module, name);
        if (address == IntPtr.Zero) throw new M8SongException(LibraryName + " is missing " + name + ". Reinstall Gear Backup.");
        return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
    }
}
