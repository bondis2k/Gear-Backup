using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GearBackup
{
    internal sealed class M8CardSongResult
    {
        internal string Relative, Error;
        internal M8SampleReport Samples;
        internal int Missing { get { return Samples == null ? 0 : Samples.Samples.Count(s => s.CardStatus == "Missing on card"); } }
        internal bool NeedsAttention { get { return Error != null || (Samples != null && Samples.Warnings.Count != 0); } }
    }

    internal sealed class M8CardReport
    {
        internal readonly List<M8CardSongResult> Songs = new List<M8CardSongResult>();
        internal readonly List<string> Warnings = new List<string>();
    }

    internal static class M8CardCheck
    {
        internal const int MaxSongs = 10000, MaxEntries = 1000000;

        internal static M8CardReport Run(string root, IM8SongReader reader, Action<int, string> progress = null, OperationContext context = null)
        {
            context = context ?? OperationContext.Current;
            if (string.IsNullOrWhiteSpace(root)) throw new IOException("Choose the M8 card root first.");
            root = PathSafety.Rooted(root);
            PathSafety.RejectRootLinks(root);
            if (!Directory.Exists(root)) throw new IOException("The selected M8 card folder is unavailable.");
            var report = new M8CardReport();
            var folders = new Stack<string>(); folders.Push(root);
            var songs = new List<string>();
            int entries = 0;
            bool limit = false;
            while (folders.Count != 0 && !limit)
            {
                context.CheckCancellation();
                string folder = folders.Pop();
                try
                {
                    PathSafety.RejectRootLinks(folder);
                    foreach (string path in Directory.EnumerateFileSystemEntries(folder))
                    {
                        context.CheckCancellation();
                        if (++entries > MaxEntries) { limit = true; break; }
                        try
                        {
                            FileAttributes attributes = File.GetAttributes(path);
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                            { Warn(report, "Skipped linked path: " + path); continue; }
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                string name = Path.GetFileName(path);
                                if (name != "System Volume Information" && name != "$RECYCLE.BIN") folders.Push(path);
                            }
                            else if (string.Equals(Path.GetExtension(path), ".m8s", StringComparison.OrdinalIgnoreCase))
                            {
                                if (songs.Count == MaxSongs) { limit = true; break; }
                                songs.Add(path.Substring(root.Length));
                            }
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        { Warn(report, "Could not inspect " + path + ": " + ex.Message); }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { Warn(report, "Could not scan " + folder + ": " + ex.Message); }
            }
            if (limit) Warn(report, "Scan incomplete: reached the limit of " + MaxSongs + " songs or " + MaxEntries + " filesystem entries.");
            songs.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string relative in songs)
            {
                context.CheckCancellation();
                var song = new M8CardSongResult { Relative = relative };
                try
                {
                    string path = PathSafety.SafePath(root, relative);
                    song.Samples = M8SampleCheck.Run(reader.Read(path), root, null, path);
                }
                catch (Exception ex) when (ex is M8SongException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                { song.Error = ex.Message; }
                report.Songs.Add(song);
                if (progress != null) progress(report.Songs.Count, relative);
            }
            context.CheckCancellation();
            return report;
        }

        static void Warn(M8CardReport report, string text)
        {
            if (report.Warnings.Count < 100) report.Warnings.Add(text);
            else if (report.Warnings.Count == 100) report.Warnings.Add("Additional scan warnings omitted.");
        }
    }
}
