using System;
using System.IO;
using static GearBackup.ConsoleUi;
using static GearBackup.Formatting;

namespace GearBackup
{
    // One place for "is there room?", so every operation words the shortage the same way.
    static class DiskSpace
    {
        // Null when `needed` bytes fit on the drive holding `path`; otherwise what to tell the user.
        internal static string Shortage(string path, long needed, string purpose)
        {
            long free;
            try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace; }
            catch (Exception ex) { throw new IOException("Cannot determine free space for " + purpose + ": " + path, ex); }
            return needed > free ? "Not enough space for " + purpose + " on " + Path.GetPathRoot(path) + ": needs " + FormatSize(needed) + ", but only " + FormatSize(free) + " is free." : null;
        }
    }

}
