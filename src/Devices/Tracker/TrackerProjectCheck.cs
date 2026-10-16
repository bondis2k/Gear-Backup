// Looking inside a Polyend Tracker project folder: its patterns and instruments, and which instruments the patterns play that have
// no instrument file. A Tracker instrument holds its sample inside the .pti file and a project has no sample paths, so the check
// is by instrument number: a note that plays instrument 7 needs instruments\instrument_07.pti.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace GearBackup
{
    internal sealed class TrackerPatternInfo
    {
        public string File, Error;
        public int Number;              // the number in the file name (pattern_07.mtp is 7)
        public int Tracks, NoteSteps;
        public SortedSet<int> Instruments = new SortedSet<int>();
    }

    internal sealed class TrackerInstrumentInfo
    {
        public string File, Error;
        public int Slot;                // 1-48, as the Tracker shows it
        public TrackerInstrumentFile Details;
    }

    internal sealed class TrackerMissingInstrument
    {
        public int Slot;                // 1-48
        public string Reason;           // "no file" or why the file cannot play
        public List<int> Patterns = new List<int>();
        public bool InSong;
    }

    internal sealed class TrackerProjectReport
    {
        public string Folder, Error;
        public TrackerProjectFile Project;
        public readonly List<TrackerPatternInfo> Patterns = new List<TrackerPatternInfo>();
        public readonly List<TrackerInstrumentInfo> Instruments = new List<TrackerInstrumentInfo>();
        public readonly List<TrackerMissingInstrument> Missing = new List<TrackerMissingInstrument>();
        // Song positions that play a pattern with no pattern file, as "position N plays pattern P".
        public readonly List<string> MissingPatterns = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        // Counts of MIDI and synth-engine instruments the patterns use; they have no files.
        public int MidiOrSynthInstruments;
        public int UnreadableFiles;
    }

    // What a Tracker card holds, checked quickly enough to show when the device is detected.
    internal sealed class TrackerCardSummary
    {
        public int Projects, Checked;
        public string NewestName = "";
        public DateTime NewestTime;
        public readonly List<string> Problems = new List<string>();

        internal string Describe()
        {
            if (Projects == 0) return "No Tracker projects (folders with project.mt) were found on this card.";
            var text = new System.Text.StringBuilder();
            text.Append("Tracker projects: ").Append(Projects);
            if (NewestName.Length > 0) text.Append("    Newest: ").Append(NewestName).Append(" (").Append(NewestTime.ToString("yyyy-MM-dd")).Append(")");
            text.AppendLine();
            string scope = Checked < Projects ? "The first " + Checked + " projects were checked" : Projects == 1 ? "The project was checked" : "All " + Projects + " projects were checked";
            if (Problems.Count == 0) text.Append(scope).Append(": no missing instruments or unreadable files.");
            else
            {
                text.Append(scope).Append(". Needs attention:");
                for (int i = 0; i < Problems.Count && i < 5; i++) text.AppendLine().Append("  ").Append(Problems[i]);
                if (Problems.Count > 5) text.AppendLine().Append("  … and ").Append(Problems.Count - 5).Append(" more (see the Tracker Mini tabs)");
            }
            return text.ToString();
        }
    }

    static class TrackerProjectCheck
    {
        const int MaxFiles = 2000, MaxProjects = 500, MaxDepth = 4;
        static readonly Regex Digits = new Regex(@"(\d+)\.[A-Za-z]+$", RegexOptions.Compiled);

        // Project folders (those holding project.mt) at the folder itself or up to four levels below it.
        internal static List<string> FindProjects(string root)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return found;
            Search(Path.GetFullPath(root).TrimEnd('\\'), 0, found);
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }

        static void Search(string folder, int depth, List<string> found)
        {
            if (found.Count >= MaxProjects) return;
            try
            {
                if (File.Exists(Path.Combine(folder, "project.mt"))) { found.Add(folder); return; }
                if (depth >= MaxDepth) return;
                foreach (string sub in Directory.GetDirectories(folder))
                {
                    OperationContext.Current.CheckCancellation();
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                    string name = Path.GetFileName(sub);
                    if (name == "System Volume Information" || name.StartsWith("$")) continue;
                    Search(sub, depth + 1, found);
                }
            }
            catch (Exception ex) when (ExpectedFailure.Is(ex)) { } // an unreadable folder just holds no projects we can list
        }

        // Counts the projects on a card and checks the first few of them (newest first), so the Device tab can say at a glance
        // whether anything needs attention.
        internal static TrackerCardSummary Summarize(string root, OperationContext context = null, int maxChecked = 25)
        {
            context = context ?? OperationContext.Current;
            var summary = new TrackerCardSummary();
            var projects = new List<KeyValuePair<DateTime, string>>();
            foreach (string folder in FindProjects(root))
            {
                DateTime time = DateTime.MinValue;
                try { time = File.GetLastWriteTime(Path.Combine(folder, "project.mt")); } catch (Exception ex) when (ExpectedFailure.Is(ex)) { } // an unreadable date sorts last
                projects.Add(new KeyValuePair<DateTime, string>(time, folder));
            }
            projects.Sort((a, b) => b.Key.CompareTo(a.Key));
            summary.Projects = projects.Count;
            foreach (var item in projects)
            {
                if (summary.Checked >= maxChecked) break;
                context.CheckCancellation();
                TrackerProjectReport report = Inspect(item.Value, context);
                summary.Checked++;
                string name = report.Project != null && report.Project.Name.Length > 0 ? report.Project.Name : Path.GetFileName(item.Value);
                if (summary.NewestName.Length == 0) { summary.NewestName = name; summary.NewestTime = item.Key; }
                var problems = new List<string>();
                if (report.Error != null) problems.Add("project.mt cannot be read");
                if (report.Missing.Count > 0) problems.Add(report.Missing.Count + (report.Missing.Count == 1 ? " instrument" : " instruments") + " played without a usable file");
                if (report.UnreadableFiles > 0) problems.Add(report.UnreadableFiles + (report.UnreadableFiles == 1 ? " unreadable file" : " unreadable files"));
                if (report.MissingPatterns.Count > 0) problems.Add(report.MissingPatterns.Count + " song position(s) without a pattern");
                if (problems.Count > 0) summary.Problems.Add(name + ": " + string.Join(", ", problems));
            }
            return summary;
        }

        // Reads a project folder. Unreadable files are reported, not thrown, so one damaged pattern never hides the rest.
        internal static TrackerProjectReport Inspect(string folder, OperationContext context = null)
        {
            context = context ?? OperationContext.Current;
            var report = new TrackerProjectReport { Folder = folder };
            string projectFile = Path.Combine(folder, "project.mt");
            try
            {
                PathSafety.RejectRootLinks(folder);
                report.Project = TrackerProjectFile.Read(Records.ReadBytes(projectFile, TrackerFiles.ProjectLimit, "Tracker project"));
            }
            catch (Exception ex) when (ExpectedFailure.Is(ex)) { report.Error = "Cannot read project.mt: " + ex.Message; return report; }

            ReadPatterns(folder, report, context);
            ReadInstruments(folder, report, context);
            Check(report);
            return report;
        }

        static void ReadPatterns(string folder, TrackerProjectReport report, OperationContext context)
        {
            string patterns = Path.Combine(folder, "patterns");
            if (!Directory.Exists(patterns)) { report.Warnings.Add("The project has no patterns folder."); return; }
            foreach (string path in Limited(Directory.GetFiles(patterns, "*.mtp"), report))
            {
                context.CheckCancellation();
                var info = new TrackerPatternInfo { File = Path.GetFileName(path), Number = Number(path) };
                try
                {
                    TrackerPatternFile pattern = TrackerPatternFile.Read(Records.ReadBytes(path, TrackerFiles.PatternLimit, "Tracker pattern"));
                    info.Tracks = pattern.TrackCount; info.NoteSteps = pattern.NoteSteps; info.Instruments = pattern.Instruments;
                }
                catch (Exception ex) when (ExpectedFailure.Is(ex)) { info.Error = ex.Message; report.UnreadableFiles++; }
                report.Patterns.Add(info);
            }
            report.Patterns.Sort((a, b) => a.Number != b.Number ? a.Number.CompareTo(b.Number) : string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase));
        }

        static void ReadInstruments(string folder, TrackerProjectReport report, OperationContext context)
        {
            string instruments = Path.Combine(folder, "instruments");
            if (!Directory.Exists(instruments)) { report.Warnings.Add("The project has no instruments folder."); return; }
            string[] files = Limited(Directory.GetFiles(instruments, "*.pti"), report);
            // Instrument files are numbered from 1 (instrument_01.pti); a file numbered 0 means the folder counts from 0.
            bool zeroBased = Array.Exists(files, f => Number(f) == 0 && Digits.IsMatch(f));
            foreach (string path in files)
            {
                context.CheckCancellation();
                var info = new TrackerInstrumentInfo { File = Path.GetFileName(path), Slot = Number(path) + (zeroBased ? 1 : 0) };
                try { info.Details = TrackerInstrumentFile.Read(path); }
                catch (Exception ex) when (ExpectedFailure.Is(ex)) { info.Error = ex.Message; report.UnreadableFiles++; }
                report.Instruments.Add(info);
            }
            report.Instruments.Sort((a, b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase));
        }

        static string[] Limited(string[] files, TrackerProjectReport report)
        {
            if (files.Length <= MaxFiles) return files;
            report.Warnings.Add("The folder holds more than " + MaxFiles + " files; only the first " + MaxFiles + " are read.");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return new List<string>(files).GetRange(0, MaxFiles).ToArray();
        }

        static int Number(string path)
        {
            Match match = Digits.Match(path);
            int value;
            return match.Success && int.TryParse(match.Groups[1].Value, out value) ? value : -1;
        }

        // Which notes play an instrument with no usable file, and which song positions play a pattern with no file.
        static void Check(TrackerProjectReport report)
        {
            var songPatterns = new HashSet<int>(report.Project.Playlist);
            var missing = new SortedDictionary<int, TrackerMissingInstrument>();
            var byNumber = new Dictionary<int, TrackerPatternInfo>();
            foreach (TrackerPatternInfo pattern in report.Patterns) if (pattern.Error == null && !byNumber.ContainsKey(pattern.Number)) byNumber[pattern.Number] = pattern;
            var slots = new Dictionary<int, TrackerInstrumentInfo>();
            foreach (TrackerInstrumentInfo instrument in report.Instruments) if (!slots.ContainsKey(instrument.Slot)) slots[instrument.Slot] = instrument;
            var midiOrSynth = new HashSet<int>();
            foreach (TrackerPatternInfo pattern in report.Patterns)
                foreach (int stored in pattern.Instruments)
                {
                    if (stored >= 48) { midiOrSynth.Add(stored); continue; }
                    int slot = stored + 1;
                    TrackerInstrumentInfo instrument;
                    string reason = null;
                    if (!slots.TryGetValue(slot, out instrument)) reason = "no instrument file";
                    else if (instrument.Error != null) reason = "the instrument file cannot be read";
                    else if (instrument.Details.Frames == 0 || (!instrument.Details.Wavetable && instrument.Details.Channels == 0)) reason = "the instrument file holds no usable sample";
                    if (reason == null) continue;
                    TrackerMissingInstrument entry;
                    if (!missing.TryGetValue(slot, out entry)) missing[slot] = entry = new TrackerMissingInstrument { Slot = slot, Reason = reason };
                    entry.Patterns.Add(pattern.Number);
                    if (songPatterns.Contains(pattern.Number)) entry.InSong = true;
                }
            report.MidiOrSynthInstruments = midiOrSynth.Count;
            report.Missing.AddRange(missing.Values);
            for (int position = 0; position < report.Project.Playlist.Count; position++)
                if (!byNumber.ContainsKey(report.Project.Playlist[position]) && !report.Patterns.Exists(p => p.Number == report.Project.Playlist[position]))
                    report.MissingPatterns.Add("Song position " + (position + 1) + " plays pattern " + report.Project.Playlist[position] + ", which has no pattern file.");
        }
    }
}
