using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GearBackup
{
    internal sealed class M8SampleResult
    {
        internal string Reference, Relative, Slots, CardStatus;
        internal readonly List<M8SampleMatch> Matches = new List<M8SampleMatch>();
    }

    internal sealed class M8SampleMatch
    {
        internal string Backup, Point, PointId, Relative, Hash, Status;
        internal bool Exact;
    }

    internal sealed class M8SampleReport
    {
        internal readonly List<M8SampleResult> Samples = new List<M8SampleResult>();
        internal readonly List<string> Warnings = new List<string>();
        internal int BackupsSearched;
    }

    // An advisory, read-only search. A matching path does not prove which historical version a song intended.
    internal static class M8SampleCheck
    {
        internal const int MatchLimit = 100;

        internal static M8SampleReport Run(M8SongSummary song, string cardRoot, Config backups, string songPath = null, OperationContext context = null)
        {
            context = context ?? OperationContext.Current;
            var report = new M8SampleReport();
            var byReference = new Dictionary<string, M8SampleResult>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(cardRoot))
            {
                PathSafety.RejectRootLinks(cardRoot);
                if (!Directory.Exists(cardRoot)) throw new IOException("The selected card folder is unavailable.");
            }
            foreach (var instrument in song.Instruments)
            {
                context.CheckCancellation();
                string raw = instrument.RawSample ?? instrument.Sample;
                if (string.IsNullOrEmpty(raw)) continue;
                M8SampleResult sample;
                if (byReference.TryGetValue(raw, out sample)) { sample.Slots += ", " + instrument.Slot.ToString("X2"); continue; }
                sample = new M8SampleResult { Reference = M8SongJson.Clean(raw), Slots = instrument.Slot.ToString("X2") };
                byReference.Add(raw, sample); report.Samples.Add(sample);
                try
                {
                    if (raw != M8SongJson.Clean(raw) || raw.StartsWith("//") || raw.StartsWith(@"\\"))
                        throw new IOException("Unsafe sample reference.");
                    string relative = raw.Replace('/', '\\');
                    bool rootedOnCard = relative.StartsWith("\\");
                    if (rootedOnCard) relative = relative.Substring(1);
                    PathSafety.CheckedPath(Path.GetTempPath(), relative);
                    if (relative.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|' }) >= 0) throw new IOException("Invalid sample reference.");
                    if (!rootedOnCard)
                    {
                        // Bundle/demo songs can store Samples/foo.wav beside the song. The embedded
                        // directory can be stale after a song is moved; use its actual location on this card.
                        if (string.IsNullOrWhiteSpace(cardRoot) || string.IsNullOrWhiteSpace(songPath) || !PathSafety.Within(songPath, cardRoot))
                        {
                            sample.CardStatus = "Cannot resolve reference";
                            report.Warnings.Add(sample.Reference + ": choose the song's copy within the selected card root to resolve this relative path.");
                            continue;
                        }
                        string songRelative = Path.GetFullPath(songPath).Substring(PathSafety.Rooted(cardRoot).Length);
                        PathSafety.SafePath(cardRoot, songRelative);
                        string folder = Path.GetDirectoryName(songRelative);
                        if (!string.IsNullOrEmpty(folder)) relative = Path.Combine(folder, relative);
                        PathSafety.CheckedPath(cardRoot, relative);
                    }
                    sample.Relative = relative;
                    if (string.IsNullOrWhiteSpace(cardRoot)) sample.CardStatus = "Card not checked";
                    else
                    {
                        string path = PathSafety.SafePath(cardRoot, relative);
                        // Open the file to distinguish an accessible sample from an unreadable path.
                        try { using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                            sample.CardStatus = "Present on card"; }
                        catch (FileNotFoundException) { sample.CardStatus = "Missing on card"; }
                        catch (DirectoryNotFoundException) { sample.CardStatus = "Missing on card"; }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
                {
                    sample.CardStatus = sample.Relative == null ? "Invalid reference" : "Cannot check card";
                    report.Warnings.Add(sample.Reference + ": " + ex.Message);
                }
            }
            if (backups == null || report.Samples.All(s => s.Relative == null)) return report;
            List<string> sets;
            try
            {
                sets = new List<string>();
                foreach (string history in Histories.AvailableHistories(backups))
                foreach (string backup in BackupFolders.ListBackups(history))
                {
                    context.CheckCancellation();
                    try
                    {
                        var device = DeviceSelection.BackupDevice(backup);
                        if (device == null) report.Warnings.Add("Unrecognized device record; skipped " + backup);
                        else if (device.Id == Devices.M8.Id) sets.Add(backup);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    { report.Warnings.Add("Unreadable device record in " + backup + ": " + ex.Message); }
                }
                sets.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(b, a));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { report.Warnings.Add("Backup search incomplete: " + ex.Message); return report; }
            var names = new Dictionary<string, List<M8SampleResult>>(StringComparer.OrdinalIgnoreCase);
            foreach (var sample in report.Samples.Where(s => s.Relative != null))
            {
                Add(names, Path.GetFileName(sample.Relative), sample);
            }
            foreach (string backup in sets)
            {
                context.CheckCancellation();
                try
                {
                    RestoreSet set = RestoreCatalog.Load(backup, Devices.M8);
                    report.BackupsSearched++;
                    if (!set.Sqlite) report.Warnings.Add(backup + ": " + set.Notice);
                    var validated = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var point in set.Points.Where(p => p.Id != "current" || set.Points.Count == 1))
                    foreach (var file in point.Snapshot.Files)
                    {
                        context.CheckCancellation();
                        List<M8SampleResult> interested;
                        if (!names.TryGetValue(Path.GetFileName(file.Relative), out interested)) continue;
                        foreach (var sample in interested)
                        {
                            bool exact = string.Equals(sample.Relative, file.Relative, StringComparison.OrdinalIgnoreCase);
                            if (sample.Matches.Count >= MatchLimit)
                            {
                                int candidate = sample.Matches.FindLastIndex(m => !m.Exact);
                                if (!exact || candidate < 0) continue;
                                sample.Matches.RemoveAt(candidate);
                            }
                            string key = file.StoredPath + "|" + file.Hash + "|" + file.Size;
                            string status;
                            if (!validated.TryGetValue(key, out status))
                            {
                                try { Snapshots.ValidateEntry(file); status = "Verified copy"; }
                                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                                { status = "Unavailable / damaged: " + ex.Message; }
                                validated[key] = status;
                            }
                            sample.Matches.Add(new M8SampleMatch { Backup = backup, Point = point.Label, PointId = point.Id, Relative = file.Relative,
                                Hash = file.Hash, Exact = exact, Status = status });
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { report.Warnings.Add("Could not search " + backup + ": " + ex.Message); }
            }
            foreach (var sample in report.Samples)
                if (sample.Matches.Count == MatchLimit) report.Warnings.Add(sample.Reference + ": showing the first " + MatchLimit + " matches.");
            return report;
        }

        static void Add(Dictionary<string, List<M8SampleResult>> index, string key, M8SampleResult sample)
        {
            List<M8SampleResult> list;
            if (!index.TryGetValue(key, out list)) index.Add(key, list = new List<M8SampleResult>());
            list.Add(sample);
        }
    }
}
