using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

partial class M8Backup
{
    static int Restore(string m8, List<string> backups, Config config, out string title, out string summary)
    {
        title = summary = null;
        if (backups.Count == 0) { Console.WriteLine("  No backups found."); return 1; }
        var choices = new List<string>(backups);
        choices.Reverse();
        for (int i = 0; i < choices.Count; i++)
            Console.WriteLine("    {0}) {1} [{2}]", i + 1, Path.GetFileName(choices[i]), BackupStatus(choices[i]));
        int pick = AskNumber("  Restore from which backup? Enter = cancel: ", choices.Count);
        if (pick == 0) return 0;
        string backup = choices[pick - 1];
        Snapshot snapshot = ChooseSnapshot(backup);
        bool historical = snapshot != null;
        if (!historical)
        {
            string status = BackupStatus(backup);
            if (status != "complete" && !AskInConsole("  Backup status is " + status + ". Browse its available files anyway? (y/n) ")) return 0;
            snapshot = CurrentBackup(backup);
        }
        Console.WriteLine("  Selected: " + snapshot.Name);
        char mode = AskKey("  Restore [A]ll, a [B]undle, a [S]ingle file, or a song with [D]ependent samples? ", "absd");
        List<RestoreEntry> items;
        string bundle = null;
        if (mode == 'a') items = snapshot.Files.FindAll(f => historical || !IsOldVersion(f.Relative));
        else if (mode == 'b')
        {
            bundle = PickBundle(snapshot);
            if (bundle == null) return 0;
            items = snapshot.Files.FindAll(f => f.Relative.StartsWith(bundle + "\\", StringComparison.OrdinalIgnoreCase) &&
                (historical || !IsOldVersion(f.Relative)));
        }
        else
        {
            RestoreEntry file = PickEntry(snapshot, mode == 'd');
            if (file == null) return 0;
            items = mode == 'd' ? SongDependencies(file, snapshot, m8, config.Verify) : new List<RestoreEntry> { file };
            if (!historical && IsOldVersion(file.Relative))
            {
                if (AskKey("  Restore under the [O]riginal name, or keep the [D]ated name? ", "od") == 'o')
                {
                    var renamed = new RestoreEntry { Relative = OriginalName(file.Relative), StoredPath = file.StoredPath,
                        Hash = file.Hash, Size = file.Size, ModifiedTicks = file.ModifiedTicks, CreatedTicks = file.CreatedTicks };
                    items[0] = renamed;
                }
            }
        }
        if (items.Count == 0) { Console.WriteLine("  No files selected."); return 0; }

        // Exact restore is available only for manifests, whose file membership is authoritative.
        bool exact = historical && mode == 'a' && AskKey("  [M]erge with the card, or [E]xact snapshot (remove extra user files)? ", "me") == 'e';
        var removals = new List<string>();
        var extraFolders = new List<string>();
        if (exact)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RestoreEntry item in items) wanted.Add(item.Relative);
            var scan = new ScanResult();
            CollectFiles(m8, scan);
            foreach (FileInfo file in scan.Files)
                if (!wanted.Contains(file.FullName.Substring(m8.Length))) removals.Add(file.FullName.Substring(m8.Length));
            var wantedFolders = new HashSet<string>(snapshot.Folders, StringComparer.OrdinalIgnoreCase);
            foreach (string folder in scan.Folders)
                if (!wantedFolders.Contains(folder.Substring(m8.Length))) extraFolders.Add(folder.Substring(m8.Length));
        }

        var toCopy = new List<RestoreEntry>();
        var plan = new List<string>();
        var targetHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int differing = 0, identical = 0, newer = 0;
        long bytes = 0, missingBytes = 0;
        foreach (RestoreEntry item in items)
        {
            string target = SafePath(m8, item.Relative);
            CheckParents(m8, target);
            if (Directory.Exists(target)) throw new IOException("A folder blocks the file: " + item.Relative);
            // Preflight every selected backup file, including ones that appear unchanged on the card.
            ValidateEntry(item, config.Verify);
            string actual = Fingerprint(target, config.Verify || config.Thorough);
            targetHashes[item.Relative] = actual;
            if (EntryMatchesTarget(item, target, config.Thorough)) { identical++; plan.Add("Skip unchanged: " + item.Relative); continue; }
            if (actual != null)
            {
                differing++;
                if (File.GetLastWriteTimeUtc(target).Ticks > item.ModifiedTicks + TimeSpan.FromSeconds(2).Ticks) newer++;
            }
            else missingBytes += item.Size;
            toCopy.Add(item);
            bytes += item.Size;
            plan.Add((actual == null ? "Add: " : "Replace: ") + item.Relative);
        }
        if (newer > 0) WriteColored("  " + newer + " target file(s) are newer than this backup.", ConsoleColor.Yellow);
        if (differing > 0 && !PreviewOnly)
        {
            char answer = AskKey("  [R]eplace differing files, [K]eep them (missing files only), or [C]ancel? ", "rkc");
            if (answer == 'c') return 0;
            if (answer == 'k')
            {
                if (exact) { Console.WriteLine("  Keeping differing files also disables exact removal."); exact = false; removals.Clear(); extraFolders.Clear(); }
                toCopy.RemoveAll(f => targetHashes[f.Relative] != null);
                plan.RemoveAll(line => line.StartsWith("Replace: "));
                bytes = missingBytes;
            }
        }
        foreach (string relative in removals)
        {
            targetHashes[relative] = Fingerprint(SafePath(m8, relative), config.Verify || config.Thorough);
            plan.Add("REMOVE extra file: " + relative);
        }
        foreach (string folder in extraFolders) plan.Add("Remove folder if empty: " + folder);
        List<string> folders = snapshot.Folders.FindAll(f => mode == 'a' || (bundle != null &&
            (f.Equals(bundle, StringComparison.OrdinalIgnoreCase) || f.StartsWith(bundle + "\\", StringComparison.OrdinalIgnoreCase))));
        foreach (string folder in folders)
        {
            string target = SafePath(m8, folder);
            CheckParents(m8, target);
            if (File.Exists(target)) throw new IOException("A file blocks the folder: " + folder);
            if (!Directory.Exists(target)) plan.Add("Create folder: " + folder);
        }
        long targetReserve = RestoreSpaceNeeded(m8, toCopy);
        string error = CheckFreeSpace(m8, targetReserve);
        if (error != null) throw new IOException(error);
        Console.WriteLine("  Target needs up to {0} working space.", FormatSize(targetReserve));
        if (config.Recovery)
        {
            string recoveryRoot = RecoveryRoot(config);
            RejectRootLinks(recoveryRoot);
            if (Within(recoveryRoot, m8) || Within(m8, recoveryRoot)) throw new IOException("Recovery folder and target must not contain one another.");
            var recoveryPaths = new HashSet<string>(removals, StringComparer.OrdinalIgnoreCase);
            foreach (RestoreEntry item in toCopy) if (targetHashes[item.Relative] != null) recoveryPaths.Add(item.Relative);
            long recoveryBytes = 0;
            foreach (string relative in recoveryPaths) recoveryBytes += new FileInfo(SafePath(m8, relative)).Length;
            Console.WriteLine("  Recovery: {0} in {1} ({2} existing file(s)).", FormatSize(recoveryBytes), recoveryRoot, recoveryPaths.Count);
            bool sameVolume = string.Equals(Path.GetPathRoot(recoveryRoot), Path.GetPathRoot(m8), StringComparison.OrdinalIgnoreCase);
            string recoveryError = CheckFreeSpace(recoveryRoot, recoveryBytes + (sameVolume ? targetReserve : 0));
            if (recoveryError != null) throw new IOException(recoveryError);
        }
        else Console.WriteLine("  Recovery copies: disabled (Recovery=no).");
        Console.WriteLine("  SHA-256 integrity comparison: " + (config.Verify ? "enabled" : "disabled (Verify=no)"));
        if (!ShowPlan("Restore preview (" + identical + " unchanged)", plan, bytes, config)) return 0;
        if (exact && !AskInConsole("  Confirm removal of " + removals.Count + " extra file(s) from the selected target? (y/n) ")) return 0;
        ExecuteRestore(m8, toCopy, removals, folders, extraFolders, targetHashes, config, out title, out summary);
        return title == "M8 restore complete" ? 0 : 2;
    }

    static void ValidateEntry(RestoreEntry item, bool verify = true)
    {
        if (!File.Exists(item.StoredPath) || new FileInfo(item.StoredPath).Length != item.Size)
            throw new IOException("Backup file is missing or truncated: " + item.Relative);
        if (verify && item.Hash != null && HashFile(item.StoredPath) != item.Hash)
            throw new IOException("Backup file is damaged: " + item.Relative);
    }

    static bool EntryMatchesTarget(RestoreEntry item, string target, bool thorough)
    {
        var info = new FileInfo(target);
        if (!info.Exists || info.Length != item.Size) return false;
        if (thorough) return HashFile(target) == (item.Hash ?? HashFile(item.StoredPath));
        if (!UsableFileTime(new DateTime(item.ModifiedTicks, DateTimeKind.Utc)) || !UsableFileTime(info.LastWriteTimeUtc))
            return FilesEqual(item.StoredPath, target);
        return Math.Abs(info.LastWriteTimeUtc.Ticks - item.ModifiedTicks) <= TimeSpan.FromSeconds(2).Ticks;
    }

    static string Fingerprint(string path, bool checksum)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        return checksum ? "sha:" + HashFile(path) : "meta:" + info.Length + ":" + info.LastWriteTimeUtc.Ticks;
    }

    static long RestoreSpaceNeeded(string targetRoot, List<RestoreEntry> copies)
    {
        long needed = 0, largest = 0;
        foreach (RestoreEntry item in copies)
        {
            var existing = new FileInfo(SafePath(targetRoot, item.Relative));
            if (!existing.Exists) needed += item.Size;
            else { needed += Math.Max(0, item.Size - existing.Length); largest = Math.Max(largest, item.Size); }
        }
        return needed + largest;
    }

    static string RecoveryRoot(Config config)
    {
        return config.RecoveryFolder.Length > 0 ? config.RecoveryFolder : Path.Combine(config.BackupFolder, "M8 Recovery");
    }

    static void AssertTargetUnchanged(string target, string expected)
    {
        string current = Fingerprint(target, expected != null && expected.StartsWith("sha:", StringComparison.Ordinal));
        if (current != expected) throw new IOException("Target changed since the preview: " + target);
    }

    static string SaveRecovery(string targetRoot, List<RestoreEntry> copies, List<string> removals,
        Dictionary<string, string> targetHashes, Config config)
    {
        if (!config.Recovery) return null;
        var paths = new HashSet<string>(removals, StringComparer.OrdinalIgnoreCase);
        foreach (RestoreEntry item in copies) if (targetHashes[item.Relative] != null) paths.Add(item.Relative);
        if (paths.Count == 0) return null;
        string root = RecoveryRoot(config);
        RejectRootLinks(root);
        if (Within(root, targetRoot) || Within(targetRoot, root)) throw new IOException("Recovery folder and target must not contain one another.");
        string folder = UniqueFolder(root, DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        long needed = 0;
        foreach (string relative in paths) needed += new FileInfo(SafePath(targetRoot, relative)).Length;
        string space = CheckFreeSpace(root, needed);
        if (space != null) throw new IOException(space);
        Directory.CreateDirectory(folder);
        AtomicText(Path.Combine(folder, ".m8backup", "recovery-status.txt"), "incomplete\n");
        var sums = new StringBuilder();
        foreach (string relative in paths)
        {
            string from = SafePath(targetRoot, relative), to = SafePath(folder, relative);
            AssertTargetUnchanged(from, targetHashes[relative]);
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            string hash = CopyFile(new FileInfo(from), to, config.Verify);
            AssertTargetUnchanged(from, targetHashes[relative]);
            if (config.Verify && targetHashes[relative].StartsWith("sha:", StringComparison.Ordinal) &&
                "sha:" + hash != targetHashes[relative]) throw new IOException("Recovery source changed: " + relative);
            sums.Append(hash).Append("  ").Append(relative.Replace('\\', '/')).Append('\n');
        }
        AtomicText(Path.Combine(folder, ".m8backup", "recovery-checksums.sha256"), sums.ToString());
        AtomicText(Path.Combine(folder, ".m8backup", "recovery-status.txt"), "complete\nOriginal target: " + targetRoot + "\n");
        Console.WriteLine("  Recovery copies saved to " + folder);
        return folder;
    }

    static void ExecuteRestore(string targetRoot, List<RestoreEntry> copies, List<string> removals, List<string> folders,
        List<string> extraFolders, Dictionary<string, string> targetHashes, Config config, out string title, out string summary)
    {
        // No target mutation until every recovery copy has succeeded.
        string recovery = SaveRecovery(targetRoot, copies, removals, targetHashes, config);
        int copied = 0, failed = 0, removed = 0;
        foreach (string folder in folders) Directory.CreateDirectory(SafePath(targetRoot, folder));
        foreach (RestoreEntry item in copies)
        {
            string target = SafePath(targetRoot, item.Relative);
            string temp = Path.Combine(Path.GetDirectoryName(target), ".m8restore-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                ValidateEntry(item, config.Verify);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                string hash = CopyFile(new FileInfo(item.StoredPath), temp, config.Verify);
                if (config.Verify && item.Hash != null && hash != item.Hash) throw new IOException("Backup checksum changed during restore.");
                DateTime modified = new DateTime(item.ModifiedTicks, DateTimeKind.Utc);
                DateTime created = new DateTime(item.CreatedTicks, DateTimeKind.Utc);
                if (UsableFileTime(modified)) File.SetLastWriteTimeUtc(temp, modified);
                if (UsableFileTime(created)) File.SetCreationTimeUtc(temp, created);
                AssertTargetUnchanged(target, targetHashes[item.Relative]);
                if (File.Exists(target)) ReplaceSafely(temp, target);
                else File.Move(temp, target);
                copied++;
                Console.WriteLine("  Restored: " + item.Relative);
            }
            catch (Exception ex) { failed++; WriteColored("  Failed: " + item.Relative + " (" + ex.Message + ")", ConsoleColor.Red); }
            finally { TryDelete(temp); }
        }
        // Never remove extras after a partially failed restore.
        if (failed == 0)
        {
            foreach (string relative in removals)
            {
                try
                {
                    string path = SafePath(targetRoot, relative);
                    AssertTargetUnchanged(path, targetHashes[relative]);
                    File.Delete(path);
                    removed++;
                }
                catch (Exception ex) { failed++; WriteColored("  Removal failed: " + ex.Message, ConsoleColor.Red); break; }
            }
            extraFolders.Sort((a, b) => b.Length.CompareTo(a.Length));
            if (failed == 0) foreach (string relative in extraFolders)
            {
                string folder = SafePath(targetRoot, relative);
                if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0) Directory.Delete(folder);
            }
        }
        title = failed == 0 ? "M8 restore complete" : "M8 restore finished with errors";
        summary = string.Format("{0} file(s) restored, {1} removed, {2} failed.", copied, removed, failed);
        if (recovery != null) summary += " Recovery: " + recovery;
        Console.WriteLine("  " + summary);
    }

    static RestoreEntry PickEntry(Snapshot snapshot, bool songsOnly)
    {
        while (true)
        {
            Console.Write("  Search file name (Enter = songs, q = cancel): ");
            string filter = Console.ReadLine();
            if (filter == null || filter.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) return null;
            filter = filter.Trim();
            var matches = snapshot.Files.FindAll(f => (!songsOnly || IsSong(f.Relative)) &&
                (filter.Length == 0 ? IsSong(f.Relative) : f.Relative.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0));
            matches.Sort((a, b) => string.Compare(a.Relative, b.Relative, StringComparison.OrdinalIgnoreCase));
            if (matches.Count == 0) { Console.WriteLine("  No matching files."); continue; }
            for (int i = 0; i < matches.Count; i++) Console.WriteLine("    {0}) {1} ({2})", i + 1, matches[i].Relative, FormatSize(matches[i].Size));
            int pick = AskNumber("  Choose file (Enter = search again): ", matches.Count);
            if (pick > 0) return matches[pick - 1];
        }
    }

    static string PickBundle(Snapshot snapshot)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RestoreEntry file in snapshot.Files)
        {
            string[] parts = file.Relative.Split('\\');
            if (parts.Length >= 3 && parts[0].Equals("Bundles", StringComparison.OrdinalIgnoreCase)) names.Add("Bundles\\" + parts[1]);
        }
        var list = new List<string>(names);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        if (list.Count == 0) { Console.WriteLine("  No bundles found. Create a bundle on the M8 before backing up."); return null; }
        for (int i = 0; i < list.Count; i++) Console.WriteLine("    {0}) {1}", i + 1, list[i]);
        int pick = AskNumber("  Choose bundle, Enter = cancel: ", list.Count);
        return pick == 0 ? null : list[pick - 1];
    }
}
