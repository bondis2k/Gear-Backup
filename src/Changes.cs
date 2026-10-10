// What changed since the last backup, and names the M8 may not handle well.
using System;
using System.Collections.Generic;
using System.IO;

partial class M8Backup
{
    class Changes
    {
        public bool HasBaseline;
        public int TotalSongs, OtherNew, OtherChanged;
        public List<string> NewSongs = new List<string>(), EditedSongs = new List<string>(), RemovedSongs = new List<string>();
    }

    static bool IsSong(string path)
    {
        return CurrentDevice.IsSong(path);
    }

    // "Songs\Live\Jam.m8s" -> "Live\Jam" (the device's songs folder is left out)
    static string SongName(string relative)
    {
        string name = Path.Combine(Path.GetDirectoryName(relative), Path.GetFileNameWithoutExtension(relative));
        string folder = CurrentDevice.SongsFolder == null ? null : CurrentDevice.SongsFolder + "\\";
        return folder != null && name.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? name.Substring(folder.Length) : name;
    }

    static Changes FindChanges(ScanResult scan, string source, FileState[] states, string baseline)
    {
        var changes = new Changes { HasBaseline = baseline != null };
        var onM8 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < scan.Files.Count; i++)
        {
            string relative = scan.Files[i].FullName.Substring(source.Length);
            onM8.Add(relative);
            bool song = IsSong(relative);
            if (song) changes.TotalSongs++;
            if (states[i] == FileState.New)
            {
                if (song) changes.NewSongs.Add(SongName(relative)); else changes.OtherNew++;
            }
            else if (states[i] == FileState.Changed)
            {
                if (song) changes.EditedSongs.Add(SongName(relative)); else changes.OtherChanged++;
            }
        }

        if (baseline != null)
        {
            foreach (string path in Directory.GetFiles(baseline, "*.m8s", SearchOption.AllDirectories))
            {
                string relative = path.Substring(baseline.Length).TrimStart('\\');
                if (IsSong(relative) && !IsOldVersion(relative) && !onM8.Contains(relative))
                    changes.RemovedSongs.Add(SongName(relative));
            }
        }
        changes.NewSongs.Sort(StringComparer.OrdinalIgnoreCase);
        changes.EditedSongs.Sort(StringComparer.OrdinalIgnoreCase);
        changes.RemovedSongs.Sort(StringComparer.OrdinalIgnoreCase);
        return changes;
    }

    static void WriteChanges(TextWriter report, Changes changes, string baseline)
    {
        // A device without song files (e.g. the OP-1 Field) only gets file counts.
        if (CurrentDevice.SongExtensions.Length == 0)
        {
            if (changes.HasBaseline)
                report.WriteLine("  Changes since the last backup ({0}): {1:N0} new, {2:N0} changed file(s)",
                    Path.GetFileName(baseline), changes.OtherNew, changes.OtherChanged);
            report.WriteLine();
            return;
        }
        if (!changes.HasBaseline)
        {
            report.WriteLine("  {0}: {1:N0} (first backup, nothing to compare with)", Capitalized(CurrentDevice.SongsLabel), changes.TotalSongs);
            report.WriteLine();
            return;
        }
        report.WriteLine("  Changes since the last backup ({0}):", Path.GetFileName(baseline));
        string songs = CurrentDevice.SongsLabel;
        WriteNameList(report, "New " + songs, changes.NewSongs);
        WriteNameList(report, "Edited " + songs, changes.EditedSongs);
        WriteNameList(report, Capitalized(songs) + " no longer on the " + CurrentDevice.Name, changes.RemovedSongs);
        report.WriteLine("    Other files: {0:N0} new, {1:N0} changed", changes.OtherNew, changes.OtherChanged);
        report.WriteLine();
    }

    static void WriteNameList(TextWriter report, string label, List<string> names)
    {
        if (names.Count == 0)
        {
            report.WriteLine("    {0}: none", label);
            return;
        }
        report.WriteLine("    {0} ({1}):", label, names.Count);
        foreach (string name in names) report.WriteLine("      " + name);
    }

    // For the notification, e.g. " Songs: 2 new (Jam, Idea7), 1 edited (Track01)."
    static string ShortSongSummary(Changes changes)
    {
        if (!changes.HasBaseline) return "";
        if (CurrentDevice.SongExtensions.Length == 0)
            return changes.OtherNew + changes.OtherChanged == 0 ? " No files changed."
                : string.Format(" {0} new, {1} changed file(s).", changes.OtherNew, changes.OtherChanged);
        var parts = new List<string>();
        if (changes.NewSongs.Count > 0) parts.Add(changes.NewSongs.Count + " new (" + FirstNames(changes.NewSongs) + ")");
        if (changes.EditedSongs.Count > 0) parts.Add(changes.EditedSongs.Count + " edited (" + FirstNames(changes.EditedSongs) + ")");
        string songs = CurrentDevice.SongsLabel;
        return parts.Count == 0 ? " No " + songs + " changed." : " " + Capitalized(songs) + ": " + string.Join(", ", parts.ToArray()) + ".";
    }

    static string Capitalized(string word)
    {
        return word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word.Substring(1);
    }

    static string FirstNames(List<string> names)
    {
        var shown = new List<string>();
        for (int i = 0; i < names.Count && i < 3; i++) shown.Add(Path.GetFileName(names[i]));
        return string.Join(", ", shown.ToArray()) + (names.Count > 3 ? ", ..." : "");
    }

    // Flags names the M8 may not show or load properly: characters outside plain ASCII, very long names,
    // and names starting or ending with a space.
    static List<string> CheckNames(ScanResult scan, string source, int maxLength)
    {
        var paths = new List<string>(scan.Folders);
        foreach (FileInfo f in scan.Files) paths.Add(f.FullName);

        var warnings = new List<string>();
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            var problems = new List<string>();
            foreach (char c in name)
                if (c < 32 || c > 126) { problems.Add("characters other than plain letters, digits and symbols"); break; }
            if (maxLength > 0 && name.Length > maxLength)
                problems.Add("name is " + name.Length + " characters long (MaxNameLength is " + maxLength + ")");
            if (name != name.Trim())
                problems.Add("starts or ends with a space");
            if (problems.Count > 0)
                warnings.Add(path.Substring(source.Length) + ": " + string.Join(", ", problems.ToArray()));
        }
        return warnings;
    }
}
