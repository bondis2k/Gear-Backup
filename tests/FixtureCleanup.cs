using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Test-only deletion: stay below .test-runs and unlink reparse points without visiting their targets.
internal static class FixtureCleanup
{
    internal static void Delete(string root)
    {
        string boundary = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".test-runs"));
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!root.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fixture cleanup must stay below " + boundary);
        // Check every ancestor before walking. The leaf itself may be a link, which is removed only.
        for (string parent = Path.GetDirectoryName(root); parent != null; parent = Path.GetDirectoryName(parent))
        {
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Fixture cleanup cannot traverse a linked parent: " + parent);
            if (string.Equals(parent, boundary, StringComparison.OrdinalIgnoreCase)) break;
        }
        DeleteEntry(root);
    }

    static void DeleteEntry(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; } // Already removed by the test.
        catch (DirectoryNotFoundException) { return; } // Already removed by the test.
        bool directory = (attributes & FileAttributes.Directory) != 0;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if (directory) Directory.Delete(path); else File.Delete(path);
            return;
        }
        if (directory)
        {
            foreach (string child in Directory.GetFileSystemEntries(path)) DeleteEntry(child);
        }
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            // Attributes belong to the shared file, not to a hard-link name. Refuse to change
            // another name's attributes; writable hard links can simply be unlinked below.
            if (!directory && LinkCount(path) != 1) throw new IOException("Cannot clear read-only attributes on a shared fixture file: " + path);
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        if (directory) Directory.Delete(path); else File.Delete(path);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    static uint LinkCount(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            FileInformation information;
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out information))
                throw new IOException("Cannot inspect fixture file links.", new Win32Exception(Marshal.GetLastWin32Error()));
            return information.Links;
        }
    }
}
