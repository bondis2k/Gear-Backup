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
        // For an original-Tracker .mti: the wav file in the samples folder that holds its audio, and its size (-1 when missing).
        public string SampleFile;
        public long SampleFileBytes = -1;
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
        // Song positions that play a pattern with no pattern file, as "position N plays pattern P". Often an empty pattern, which the Tracker
        // does not appear to save, so this is information rather than a fault.
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
            if (Projects == 0 && Problems.Count == 0) return "No Tracker projects (folders with project.mt) were found on this card.";
            var text = new System.Text.StringBuilder();
            text.Append("Tracker projects: ").Append(Projects);
            if (NewestName.Length > 0) text.Append("    Newest: ").Append(NewestName).Append(" (").Append(NewestTime.ToString("yyyy-MM-dd")).Append(")");
            text.AppendLine();
            string scope = Checked == 0 ? "No projects were checked" : Checked < Projects ? "The first " + Checked + " projects were checked" : Projects == 1 ? "The project was checked" : "All " + Projects + " projects were checked";
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
        internal const int MaxFiles = 2000, MaxProjects = 500;
        const int MaxDepth = 4, MaxEntries = 100000;
        static readonly Regex Digits = new Regex(@"(\d+)\.[A-Za-z]+$", RegexOptions.Compiled);
        // A Tracker Mini instrument is named by its slot and its sample ("1 kick.pti", "12 Stereo Wavetable 14.pti"); the original
        // Tracker's are "instrument_01.mti". The slot is the number the name starts with, or else the one it ends with.
        static readonly Regex LeadingSlot = new Regex(@"^(\d+)(?:[ _.-]|$)", RegexOptions.Compiled);

        // Project folders (those holding project.mt) at the folder itself or up to four levels below it.
        internal static List<string> FindProjects(string root, OperationContext context = null, List<string> warnings = null)
        {
            context = context ?? OperationContext.Current;
            context.CheckCancellation();
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return found;
            string full = Path.GetFullPath(root);
            if (full.Length > Path.GetPathRoot(full).Length) full = full.TrimEnd('\\'); // keep the backslash of a drive root such as D:\
            int entries = 0;
            Search(full, 0, found, context, warnings, ref entries);
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }

        static void Search(string folder, int depth, List<string> found, OperationContext context, List<string> warnings, ref int entries)
        {
            context.CheckCancellation();
            if (found.Count >= MaxProjects || entries > MaxEntries) return;
            try
            {
                PathSafety.RejectRootLinks(folder);
                if (File.Exists(Path.Combine(folder, "project.mt"))) { found.Add(folder); return; }
                if (depth >= MaxDepth) return;
                foreach (string sub in Directory.EnumerateFileSystemEntries(folder))
                {
                    context.CheckCancellation();
                    if (++entries > MaxEntries) { if (warnings != null) warnings.Add("Project search incomplete: reached " + MaxEntries + " filesystem entries."); return; }
                    FileAttributes attributes = File.GetAttributes(sub);
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) continue;
                    string name = Path.GetFileName(sub);
                    if (string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase) || name.StartsWith("$")) continue;
                    Search(sub, depth + 1, found, context, warnings, ref entries);
                    if (entries > MaxEntries) return;
                    if (found.Count >= MaxProjects) { if (warnings != null) warnings.Add("Project search may be incomplete: reached " + MaxProjects + " projects."); return; }
                }
            }
            catch (Exception ex) when (ExpectedFailure.Is(ex))
            { if (warnings != null && warnings.Count < 100) warnings.Add("Could not search " + folder + ": " + ex.Message); }
        }

        // Counts the projects on a card and checks the first few of them (newest first), so the Device tab can say at a glance
        // whether anything needs attention.
        internal static TrackerCardSummary Summarize(string root, OperationContext context = null, int maxChecked = 25)
        {
            context = context ?? OperationContext.Current;
            var summary = new TrackerCardSummary();
            var projects = new List<KeyValuePair<DateTime, string>>();
            foreach (string folder in FindProjects(root, context, summary.Problems))
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
                if (report.Warnings.Count > 0) problems.Add("incomplete checks: " + string.Join(" ", report.Warnings));
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

            if (report.Project.Truncated) report.Warnings.Add("The project is truncated; some fields could not be read.");
            try { ReadPatterns(folder, report, context); }
            catch (Exception ex) when (ExpectedFailure.Is(ex)) { report.Warnings.Add("Cannot inspect patterns: " + ex.Message); }
            try { ReadInstruments(folder, report, context); }
            catch (Exception ex) when (ExpectedFailure.Is(ex)) { report.Warnings.Add("Cannot inspect instruments: " + ex.Message); }
            Check(report);
            return report;
        }

        static void ReadPatterns(string folder, TrackerProjectReport report, OperationContext context)
        {
            string patterns = Path.Combine(folder, "patterns");
            if (!Directory.Exists(patterns)) { report.Warnings.Add("The project has no patterns folder."); return; }
            PathSafety.RejectRootLinks(patterns);
            foreach (string path in LimitedFiles(Directory.EnumerateFiles(patterns), report, context, ".mtp"))
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
            PathSafety.RejectRootLinks(instruments);
            string[] files = LimitedFiles(Directory.EnumerateFiles(instruments), report, context, ".pti", ".mti");
            // The number in the name is the slot the Tracker shows (from 1); a folder with an instrument numbered 0 counts from 0.
            bool zeroBased = Array.Exists(files, f => Slot(f) == 0);
            foreach (string path in files)
            {
                context.CheckCancellation();
                var info = new TrackerInstrumentInfo { File = Path.GetFileName(path), Slot = Slot(path) + (zeroBased ? 1 : 0) };
                try
                {
                    info.Details = TrackerInstrumentFile.Read(path);
                    if (info.Details.Legacy)
                    {
                        // The original Tracker keeps the audio in samples\instrNN.wav, named by the slot.
                        info.SampleFile = Path.Combine(folder, "samples", "instr" + Slot(path).ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ".wav");
                        PathSafety.RejectRootLinks(info.SampleFile);
                        var wav = new FileInfo(info.SampleFile);
                        if (wav.Exists) info.SampleFileBytes = wav.Length;
                    }
                }
                catch (Exception ex) when (ExpectedFailure.Is(ex)) { info.Error = ex.Message; report.UnreadableFiles++; }
                report.Instruments.Add(info);
            }
            report.Instruments.Sort((a, b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase));
        }

        // The instrument slot a file name stands for, or -1.
        internal static int Slot(string path)
        {
            Match lead = LeadingSlot.Match(Path.GetFileName(path));
            int value;
            if (lead.Success && int.TryParse(lead.Groups[1].Value, out value)) return value;
            return Number(path);
        }

        internal static string[] LimitedFiles(IEnumerable<string> files, TrackerProjectReport report, OperationContext context, params string[] extensions)
        {
            context.CheckCancellation();
            var selected = new List<string>();
            var accepted = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
            int entries = 0;
            foreach (string file in files)
            {
                context.CheckCancellation();
                if (++entries > MaxEntries) { report.Warnings.Add("Folder scan incomplete: reached " + MaxEntries + " directory entries."); break; }
                if (!accepted.Contains(Path.GetExtension(file))) continue;
                if (selected.Count == MaxFiles) { report.Warnings.Add("The folder holds more than " + MaxFiles + " matching files; only the first " + MaxFiles + " are read."); break; }
                selected.Add(file);
            }
            selected.Sort(StringComparer.OrdinalIgnoreCase);
            return selected.ToArray();
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
                    else if (instrument.Details.Legacy)
                        reason = instrument.SampleFileBytes < 0 ? "its sample file " + Path.GetFileName(instrument.SampleFile) + " is missing from the samples folder"
                            : instrument.SampleFileBytes <= 44 ? "its sample file " + Path.GetFileName(instrument.SampleFile) + " is empty" : null;
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
                    report.MissingPatterns.Add("Song position " + (position + 1) + " plays pattern " + report.Project.Playlist[position] + ", which has no pattern file (the Tracker may not save an empty pattern).");
        }
    }
}
