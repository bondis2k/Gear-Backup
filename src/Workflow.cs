using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

partial class M8Backup
{
    static bool PreviewOnly
    {
        get { return OperationContext.Current.PreviewOnly; }
        set { OperationContext.Current.PreviewOnly = value; }
    }
    // Profile names become folder names, so they're limited to safe characters.
    static void ValidateProfile(string name)
    {
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9 _-]{0,63}$") || name.EndsWith(" ") ||
            Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
            throw new ArgumentException("Profile must be 1-64 letters, digits, spaces, - or _, and not a Windows reserved name.");
    }

    // The history folder name for a source. A whole drive is identified by its volume serial number
    // ("card-1A2B3C4D"), so the same SD card keeps its history in another reader or drive letter. A
    // folder gets a short hash of its full path. A named Profile setting overrides both.
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

    // Lets the user pick one of the histories that has backups: each profile, plus backups made before
    // profiles existed ("Legacy backups", directly under M8 Backups). Null when cancelled.
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
        OperationOutput.WriteLine("  Backup histories:");
        for (int i = 0; i < roots.Count; i++)
            OperationOutput.WriteLine("    {0}) {1}{2}", i + 1, roots[i] == root ? "Legacy backups" : Path.GetFileName(roots[i]),
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

    // The folder to back up or restore to: SourceFolder if set, otherwise the detected M8, otherwise a
    // path typed by the user. pnpId is set only for a detected M8 (needed to eject it). Null if cancelled.
    static string SelectSource(Config config, out string pnpId)
    {
        pnpId = null;
        string source = config.SourceFolder;
        if (source.Length == 0)
        {
            try { source = FindM8Drive(out pnpId); }
            catch (Exception ex) { OperationOutput.WriteLine("  Automatic detection failed: " + ex.Message); source = null; }
            if (source == null)
            {
                OperationOutput.WriteLine("  No M8 detected. Select an SD card in a reader or a folder instead.");
                OperationOutput.Write("  Source path (Enter = cancel): ");
                source = ExpandPath(OperationOutput.ReadLine());
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
        string sourceHash;
        return CompareContent(file, target, thorough, out sourceHash);
    }

    // Thorough: compare SHA-256 of both files (sourceHash returns the source's, to reuse). Otherwise
    // size and modified time, as Compare does.
    static FileState CompareContent(FileInfo file, string target, bool thorough, out string sourceHash)
    {
        sourceHash = null;
        if (!thorough) return Compare(file, target);
        var other = new FileInfo(target);
        if (!other.Exists) return FileState.New;
        if (other.Length != file.Length) return FileState.Changed;
        sourceHash = HashFile(file.FullName);
        return sourceHash == HashFile(target) ? FileState.Same : FileState.Changed;
    }

    // True when the operation should go ahead: approved, and not just a --preview.
    static bool ShowPlan(string label, List<string> lines, long bytes, Config config)
    {
        OperationContext.Current.CheckCancellation();
        bool approved = OperationContext.Current.Interaction.ReviewPlan(new OperationPlan {
            Label = label, Actions = lines, Bytes = bytes, ShowDetails = config.Preview, PreviewOnly = PreviewOnly });
        OperationContext.Current.CheckCancellation();
        return approved && !PreviewOnly;
    }

    // root\name, or root\name_2, name_3, ... if that folder already exists.
    static string UniqueFolder(string root, string name)
    {
        string path = Path.Combine(root, name);
        for (int n = 2; Directory.Exists(path); n++) path = Path.Combine(root, name + "_" + n);
        return path;
    }

    // Writes a text file so readers see either the old or the new contents, never half a file:
    // the text goes to a temporary file that then replaces the original.
    static void AtomicText(string path, string contents)
    {
        OperationContext.Current.CheckCancellation();
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + ".tmp";
        File.WriteAllText(temp, contents, new UTF8Encoding(false));
        if (File.Exists(path)) ReplaceSafely(temp, path);
        else File.Move(temp, path);
    }

    static void ReplaceSafely(string staged, string target)
    {
        // ReplaceFile can lose the old name on certain failures without a backup argument.
        // Keep a transient on-volume rollback file even when optional PC recovery is off.
        string rollback = Path.Combine(Path.GetDirectoryName(target), ".m8rollback-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { File.Replace(staged, target, rollback); }
        catch (Exception ex)
        {
            try
            {
                if (!File.Exists(target) && File.Exists(rollback)) File.Move(rollback, target);
            }
            catch (Exception rollbackError)
            {
                throw new IOException("Replacement failed. Original copy is at " + rollback + "; rollback failed: " + rollbackError.Message, ex);
            }
            if (File.Exists(rollback)) throw new IOException("Replacement failed. Retained original copy: " + rollback, ex);
            throw;
        }
        TryDelete(rollback);
    }

    // "incomplete" while a backup runs (or after it was interrupted), then "complete" or "failed".
    // Backups made before status files existed report "legacy (status unknown)".
    static string BackupStatus(string folder)
    {
        string path = Path.Combine(folder, ".m8backup", "status.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "legacy (status unknown)";
    }

    static void SetBackupStatus(string folder, string status)
    {
        AtomicText(Path.Combine(folder, ".m8backup", "status.txt"), status + "\n");
    }

    // Holds .history.lock open without sharing, so a second program or window can't write to the same
    // history at the same time. Windows releases it when the stream is disposed or the process ends.
    static FileStream LockHistory(string root)
    {
        RejectRootLinks(root);
        Directory.CreateDirectory(root);
        try { return new FileStream(Path.Combine(root, ".history.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("This backup history is already in use by another process."); }
    }
}
