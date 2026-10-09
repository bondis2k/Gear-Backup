using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

partial class M8Backup
{
    static bool PreviewOnly;
    static string ExpandPath(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
    }

    static string Rooted(string path)
    {
        return Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
    }

    static bool Within(string path, string parent)
    {
        return Rooted(path).StartsWith(Rooted(parent), StringComparison.OrdinalIgnoreCase);
    }

    // Never allow manifest/checksum entries to escape the selected backup or target.
    static string SafePath(string root, string relative)
    {
        RejectRootLinks(root);
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
            throw new IOException("Invalid relative path: " + relative);
        foreach (string part in relative.Replace('/', '\\').Split('\\'))
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" "))
                throw new IOException("Non-canonical relative path: " + relative);
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(full, root) || Rooted(full) == Rooted(root))
            throw new IOException("Path escapes its folder: " + relative);
        RejectLinks(root, full);
        return full;
    }

    static void CheckParents(string root, string path)
    {
        string parent = Path.GetDirectoryName(path);
        while (parent != null && Within(parent, root))
        {
            if (File.Exists(parent)) throw new IOException("A file blocks the destination folder: " + parent);
            if (Rooted(parent) == Rooted(root)) break;
            parent = Path.GetDirectoryName(parent);
        }
    }

    static void RejectLinks(string root, string path)
    {
        string current = path;
        while (Within(current, root))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked paths are not supported: " + current);
            if (Rooted(current) == Rooted(root)) break;
            current = Path.GetDirectoryName(current.TrimEnd('\\'));
            if (current == null) break;
        }
    }

    static void RejectRootLinks(string path)
    {
        string current = Path.GetFullPath(path).TrimEnd('\\', '/');
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked paths are not supported: " + current);
            current = Path.GetDirectoryName(current);
        }
    }

    static void ValidateProfile(string name)
    {
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9 _-]{0,63}$") || name.EndsWith(" ") ||
            Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
            throw new ArgumentException("Profile must be 1-64 letters, digits, spaces, - or _, and not a Windows reserved name.");
    }

    static string ProfileId(string source, string requested)
    {
        if (requested.Length > 0) { ValidateProfile(requested); return requested; }
        string normalized = Rooted(source);
        if (normalized == Rooted(Path.GetPathRoot(normalized)))
        {
            try
            {
                using (var volume = new ManagementObject("Win32_LogicalDisk.DeviceID='" + normalized.Substring(0, 2) + "'"))
                {
                    volume.Get();
                    string serial = (volume["VolumeSerialNumber"] ?? "").ToString();
                    if (serial.Length > 0) return "card-" + serial;
                }
            }
            catch (Exception) { }
        }
        using (var sha = SHA256.Create())
            return "folder-" + ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized.ToUpperInvariant()))).Substring(0, 16);
    }

    static string ChooseHistory(string root, string preferred)
    {
        var roots = new List<string>();
        string profiles = Path.Combine(root, "profiles");
        if (Directory.Exists(profiles))
            foreach (string dir in Directory.GetDirectories(profiles))
                if (ListBackups(dir).Count > 0) roots.Add(dir);
        if (ListBackups(root).Count > 0) roots.Add(root);
        roots.Sort(StringComparer.OrdinalIgnoreCase);
        if (roots.Count == 0) return preferred ?? root;
        Console.WriteLine("  Backup histories:");
        for (int i = 0; i < roots.Count; i++)
            Console.WriteLine("    {0}) {1}{2}", i + 1, roots[i] == root ? "Legacy backups" : Path.GetFileName(roots[i]),
                string.Equals(roots[i], preferred, StringComparison.OrdinalIgnoreCase) ? " (current source)" : "");
        int pick = AskNumber("  Choose history, Enter = cancel: ", roots.Count);
        return pick == 0 ? null : roots[pick - 1];
    }

    static bool HasHistory(string root)
    {
        if (ListBackups(root).Count > 0) return true;
        string profiles = Path.Combine(root, "profiles");
        if (Directory.Exists(profiles))
            foreach (string dir in Directory.GetDirectories(profiles)) if (ListBackups(dir).Count > 0) return true;
        return false;
    }

    static string SelectSource(Config config, out string pnpId)
    {
        pnpId = null;
        string source = config.SourceFolder;
        if (source.Length == 0)
        {
            try { source = FindM8Drive(out pnpId); }
            catch (Exception ex) { Console.WriteLine("  Automatic detection failed: " + ex.Message); source = null; }
            if (source == null)
            {
                Console.WriteLine("  No M8 detected. Select an SD card in a reader or a folder instead.");
                Console.Write("  Source path (Enter = cancel): ");
                source = ExpandPath(Console.ReadLine());
            }
        }
        if (string.IsNullOrEmpty(source)) return null;
        if (!Directory.Exists(source)) throw new IOException("Source folder does not exist: " + source);
        source = Rooted(source);
        RejectRootLinks(source);
        return source;
    }

    static FileState CompareContent(FileInfo file, string target, bool thorough)
    {
        FileState state = Compare(file, target);
        if (!thorough || state == FileState.New) return state;
        var other = new FileInfo(target);
        return other.Length == file.Length && HashFile(file.FullName) == HashFile(target) ? FileState.Same : FileState.Changed;
    }

    static bool ShowPlan(string label, List<string> lines, long bytes, Config config)
    {
        Console.WriteLine("  {0}: {1:N0} action(s), {2} to copy.", label, lines.Count, FormatSize(bytes));
        if (config.Preview || PreviewOnly)
            foreach (string line in lines) Console.WriteLine("    " + line);
        if (PreviewOnly) { Console.WriteLine("  Preview only; no files changed."); return false; }
        return !config.Preview || AskInConsole("  Proceed? (y/n) ");
    }

    static string UniqueFolder(string root, string name)
    {
        string path = Path.Combine(root, name);
        for (int n = 2; Directory.Exists(path); n++) path = Path.Combine(root, name + "_" + n);
        return path;
    }

    static void AtomicText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + ".tmp";
        File.WriteAllText(temp, contents, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    static string BackupStatus(string folder)
    {
        string path = Path.Combine(folder, ".m8backup", "status.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "legacy (status unknown)";
    }

    static void SetBackupStatus(string folder, string status)
    {
        AtomicText(Path.Combine(folder, ".m8backup", "status.txt"), status + "\n");
    }

    static FileStream LockHistory(string root)
    {
        RejectRootLinks(root);
        Directory.CreateDirectory(root);
        try { return new FileStream(Path.Combine(root, ".history.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("This backup history is already in use by another process."); }
    }
}
