using System.IO;
using System.Text;
using static PathSafety;

// Reading the files the program keeps beside backups and settings: status and check records, device
// records, checksum lists, snapshot manifests, the old-version record, statistics and settings files.
// Every read goes through here so each is checked the same way, without callers having to remember:
// the file and the folders above it must not be links (which could redirect the read anywhere), and
// the file must be within a size limit far above what the program writes, so a damaged or planted
// file is reported instead of being read whole into memory. Writes go through AtomicFile.AtomicText,
// which checks links the same way.
static class Records
{
    internal const long DeviceLimit = 128, SmallLimit = 4 * 1024, SettingsLimit = 1024 * 1024,
        OldVersionLimit = 16L * 1024 * 1024, ListLimit = 128L * 1024 * 1024;

    internal static string ReadText(string path, long limit, string what)
    {
        Check(path, limit, what);
        return File.ReadAllText(path, Encoding.UTF8);
    }

    internal static string[] ReadLines(string path, long limit, string what)
    {
        Check(path, limit, what);
        return File.ReadAllLines(path, Encoding.UTF8);
    }

    internal static byte[] ReadBytes(string path, long limit, string what)
    {
        Check(path, limit, what);
        return File.ReadAllBytes(path);
    }

    static void Check(string path, long limit, string what)
    {
        RejectRootLinks(path);
        if (new FileInfo(path).Length > limit) throw new IOException(what + " is too large: " + path);
    }
}
