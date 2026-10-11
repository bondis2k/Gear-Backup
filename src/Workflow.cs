using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using static FileCopy;
using static PathSafety;
using static Backups;

partial class GearBackup
{
    internal static bool PreviewOnly
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

    // The history folder name for a source, starting with the current device's id so each device's
    // histories are easy to tell apart. A whole drive is identified by its volume serial number
    // ("m8-card-1A2B3C4D"), so the same SD card keeps its history in another reader or drive letter. A
    // folder gets a short hash of its full path ("m8-folder-..."). A mirrored MTP device is named by its
    // mirror folder ("op1-field-L1PRC121"). A named Profile setting overrides all of these.
    static string ProfileId(string source, string requested)
    {
        if (requested.Length > 0) { ValidateProfile(requested); return requested; }
        string normalized = Rooted(source);
        if (Within(normalized, MirrorRoot) && Rooted(normalized) != Rooted(MirrorRoot))
            return Path.GetFileName(normalized.TrimEnd('\\'));
        return CurrentDevice.Id + "-" + SourceKey(normalized);
    }

    // What identifies a drive or folder, without the device: "card-1A2B3C4D" or "folder-<hash>". Before
    // histories were named per device, this alone was the history's name.
    static string SourceKey(string normalized)
    {
        if (normalized == Rooted(Path.GetPathRoot(normalized)))
        {
            // Asked of Windows directly: WMI's Win32_LogicalDisk leaves the serial blank for some cards
            // (the Tracker Mini's exFAT card). Same 8 hex digits as WMI.
            uint serial, maxComponent, flags;
            if (GetVolumeInformationW(normalized, null, 0, out serial, out maxComponent, out flags, null, 0) && serial != 0)
                return "card-" + serial.ToString("X8");
        }
        using (var sha = SHA256.Create())
            return "folder-" + ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized.ToUpperInvariant()))).Substring(0, 16);
    }

    // The history folder for a source under profiles. A card keeps one history whatever it's taken for:
    // an existing history of the same card or folder under another device's name is used as it is, and
    // one from before histories were named per device ("card-1A2B3C4D") is renamed to the new name
    // (except in a preview, which changes nothing).
    static string ProfileFolder(string profiles, string source, string requested)
    {
        string id = ProfileId(source, requested);
        string folder = Path.Combine(profiles, id);
        string normalized = Rooted(source);
        if (requested.Length > 0 || Directory.Exists(folder) || !Directory.Exists(profiles) ||
            (Within(normalized, MirrorRoot) && Rooted(normalized) != Rooted(MirrorRoot)))
            return folder;
        string key = SourceKey(normalized);
        foreach (DeviceProfile device in Devices.All)
        {
            string other = Path.Combine(profiles, device.Id + "-" + key);
            if (Directory.Exists(other)) return other;
        }
        string old = Path.Combine(profiles, key);
        if (!Directory.Exists(old)) return folder;
        if (PreviewOnly) return old;
        try
        {
            Directory.Move(old, folder);
            OperationOutput.WriteLine("  History " + key + " renamed to " + id + ".");
            return folder;
        }
        catch (IOException) { return old; }           // e.g. open in Explorer or locked by another backup
        catch (UnauthorizedAccessException) { return old; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformationW(string rootPath, StringBuilder volumeName, int volumeNameSize,
        out uint serialNumber, out uint maxComponentLength, out uint fileSystemFlags, StringBuilder fileSystemName, int fileSystemNameSize);

    // Lets the user pick one of the histories that has backups: each profile, plus backups made before
    // profiles existed ("Legacy backups", directly under Gear Backups). Null when cancelled.
    static string ChooseHistory(string root, string preferred)
    {
        var roots = new List<string>();
        CollectHistories(root, roots);
        return ChooseHistoryList(roots, preferred, preferred ?? root);
    }

    // The histories with backups in every configured location. Looked up once per operation; callers
    // get their own copy of the list.
    static List<string> AvailableHistories(Config config)
    {
        List<string> locations = ConfiguredBackupLocations(config);
        OperationContext context = OperationContext.Current;
        Dictionary<string, List<string>> cache = context.Fallback ? null : context.HistoryCache;
        string key = string.Join("|", locations.ConvertAll(location => Rooted(location)).ToArray());
        List<string> roots;
        if (cache == null || !cache.TryGetValue(key, out roots))
        {
            roots = new List<string>();
            foreach (string location in locations)
                CollectHistories(HistoriesRoot(new Config { BackupFolder = location }), roots);
            roots.Sort(StringComparer.OrdinalIgnoreCase);
            if (cache != null) cache[key] = roots;
        }
        return new List<string>(roots);
    }

    static void CollectHistories(string root, List<string> roots)
    {
        RejectRootLinks(root);
        string profiles = Path.Combine(root, "profiles");
        if (Directory.Exists(profiles))
        {
            RejectRootLinks(profiles);
            foreach (string dir in Directory.GetDirectories(profiles))
                if (ListBackups(dir).Count > 0 && !roots.Exists(p => Rooted(p) == Rooted(dir))) roots.Add(dir);
        }
        if (ListBackups(root).Count > 0 && !roots.Exists(p => Rooted(p) == Rooted(root))) roots.Add(root);
    }

    static string ChooseHistory(Config config, string preferred)
    {
        var roots = AvailableHistories(config);
        if (preferred != null && ListBackups(preferred).Count > 0 && !roots.Exists(p => Rooted(p) == Rooted(preferred)))
            roots.Add(preferred);
        return ChooseHistoryList(roots, preferred, preferred ?? HistoriesRoot(config));
    }

    static string ChooseHistoryList(List<string> roots, string preferred, string fallback)
    {
        roots.Sort(StringComparer.OrdinalIgnoreCase);
        if (roots.Count == 0) return fallback;
        var labels = new List<string>();
        foreach (string history in roots)
            labels.Add((Path.GetFileName(Path.GetDirectoryName(history)).Equals("profiles", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(history) : "Legacy backups") + " — " + history +
                (string.Equals(history, preferred, StringComparison.OrdinalIgnoreCase) ? " (current source)" : ""));
        int pick = AskItem("Choose a backup history.", labels);
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

    static bool HasHistory(Config config) { return AvailableHistories(config).Count > 0; }

    // The CLI's source: SourceFolder if set, otherwise a detected device, otherwise a path typed by the
    // user. Null if cancelled. An MTP device's mirror isn't read yet: RunPrepared syncs it.
    static SourceChoice SelectSource(Config config)
    {
        if (config.SourceFolder.Length > 0) return new SourceChoice { Path = config.SourceFolder };
        SourceChoice found = null;
        try { found = DetectSource(); }
        catch (Exception ex) { OperationOutput.WriteLine("  Automatic detection failed: " + ex.Message); }
        if (found != null)
        {
            if (found.Mtp != null)
                OperationOutput.WriteLine("  " + found.Mtp.Device.Name + " found over MTP (" + found.Mtp.Info.FriendlyName + ", serial " + found.Mtp.Info.Serial + ").");
            if (found.CameraMode != null) OperationOutput.WriteLine("  Camera connection: " + found.CameraMode + ".");
            return found;
        }
        OperationOutput.WriteLine("  No " + Devices.Names + " detected. Select an SD card in a reader or a folder instead.");
        string typed = ExpandPath(AskText("Source path (Enter = cancel):"));
        return typed.Length == 0 ? null : new SourceChoice { Path = typed };
    }

    internal static FileState CompareContent(FileInfo file, string target, bool thorough)
    {
        string sourceHash;
        return CompareContent(file, target, thorough, out sourceHash);
    }

    // Thorough: compare SHA-256 of both files (sourceHash returns the source's, to reuse). Otherwise
    // size and modified time, as Compare does.
    internal static FileState CompareContent(FileInfo file, string target, bool thorough, out string sourceHash)
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
    internal static bool ShowPlan(string label, List<string> lines, long bytes, Config config, Dictionary<string, long> fileSizes = null)
    {
        OperationContext.Current.CheckCancellation();
        bool approved = OperationContext.Current.Interaction.ReviewPlan(new OperationPlan {
            Label = label, Actions = lines, Bytes = bytes, FileSizes = fileSizes, ShowDetails = config.Preview, PreviewOnly = PreviewOnly });
        OperationContext.Current.CheckCancellation();
        return approved && !PreviewOnly;
    }

    // root\name, or root\name_2, name_3, ... if that folder already exists.
    internal static string UniqueFolder(string root, string name)
    {
        string path = Path.Combine(root, name);
        for (int n = 2; Directory.Exists(path); n++) path = Path.Combine(root, name + "_" + n);
        return path;
    }

    // Writes a text file so readers see either the old or the new contents, never half a file:
    // the text goes to a temporary file that then replaces the original.
    internal static void AtomicText(string path, string contents)
    {
        OperationContext.Current.CheckCancellation();
        string folder = Path.GetDirectoryName(path);
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, ".geartext-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            // CreateNew refuses an existing file or link; never truncate a planted temporary file.
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(contents);
            }
            OperationContext.Current.CheckCancellation();
            if (File.Exists(path)) ReplaceSafely(temp, path);
            else File.Move(temp, path);
        }
        finally { if (created) TryDelete(temp); }
    }

    internal static void ReplaceSafely(string staged, string target)
    {
        // ReplaceFile can lose the old name on certain failures without a backup argument.
        // Keep a transient on-volume rollback file even when optional PC recovery is off.
        string rollback = Path.Combine(Path.GetDirectoryName(target), ".gearrollback-" + Guid.NewGuid().ToString("N") + ".tmp");
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

    // Size limits for the records the program reads back from a backup: far above what it writes, so
    // a damaged or planted file is reported instead of being read whole into memory.
    internal const long SmallRecordLimit = 4 * 1024, ListRecordLimit = 128L * 1024 * 1024;

    internal static void CheckRecordSize(string path, long limit, string what)
    {
        if (new FileInfo(path).Length > limit) throw new IOException(what + " is too large: " + path);
    }

    // Status.Incomplete while a backup runs (or after it was interrupted), then Complete or Failed.
    // Backups made before status files existed report Status.Legacy.
    internal static string BackupStatus(string folder)
    {
        string path = Path.Combine(AppIdentity.MetadataRoot(folder), "status.txt");
        if (!File.Exists(path)) return Status.Legacy;
        CheckRecordSize(path, SmallRecordLimit, "Backup status record");
        return File.ReadAllText(path).Trim();
    }

    internal static void SetBackupStatus(string folder, string status)
    {
        AtomicText(Path.Combine(AppIdentity.MetadataRoot(folder), "status.txt"), status + "\n");
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
