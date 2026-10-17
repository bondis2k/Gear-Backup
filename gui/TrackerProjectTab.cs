using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    // The two tabs of the Tracker Mini view: the project inspector (what a project holds) and the instrument check (notes that
    // play an instrument with no usable file). Both show the same project, chosen on the inspector.
    internal sealed class TrackerProjectTab
    {
        readonly IWorkflowTabHost host;
        readonly Func<string> source;
        readonly IOperationInteraction interaction;
        readonly TextBox path = new TextBox { Name = "TrackerProjectPath", AccessibleName = "Tracker card or project folder" };
        readonly Button inspect = GuiTheme.Button("Inspect", true), check = GuiTheme.Button("Check project", true);
        readonly ComboBox picker = new ComboBox { Name = "TrackerProjectPicker", AccessibleName = "Project", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Visible = false };
        readonly Label notice = GuiTheme.Label("Choose a Tracker card, or a project folder that holds project.mt, then click Inspect.", 9, GuiTheme.Muted);
        readonly Label summary = GuiTheme.Label("", 10, GuiTheme.Ink);
        readonly DataGridView instruments = new DataGridView { Name = "TrackerInstruments", AccessibleName = "Instruments in the project" };
        readonly DataGridView patterns = new DataGridView { Name = "TrackerPatterns", AccessibleName = "Patterns in the project" };
        readonly Label checkNotice = GuiTheme.Label("Inspect a project first, or click Check project.", 9, GuiTheme.Muted);
        readonly DataGridView missing = new DataGridView { Name = "TrackerMissingInstruments", AccessibleName = "Instruments the patterns play without a usable file" };
        readonly TextBox details = new TextBox { Name = "TrackerCheckDetails", ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Top, Height = 80, Visible = false, AccessibleName = "Other findings" };
        readonly Button viewPattern = GuiTheme.Button("View pattern", false);
        Button browse;
        Control inspectorContent, checkContent;
        readonly GuiBackgroundWork work = new GuiBackgroundWork();
        bool closed, initialized, filling;
        List<string> projects = new List<string>();
        List<string> projectWarnings = new List<string>();
        internal Func<string, string> FolderPicker { get; set; }
        internal TrackerProjectReport Report { get; private set; }
        internal Func<string, OperationContext, TrackerProjectReport> Inspector = TrackerProjectCheck.Inspect;
        // Tests replace the modal window; it receives the dialog instead of showing it.
        internal Action<TrackerPatternDialog> PatternShown { get; set; }

        internal TrackerProjectTab(IWorkflowTabHost host, Func<string> source, IOperationInteraction interaction)
        { this.host = host; this.source = source; this.interaction = interaction; }

        internal Control InspectorContent { get { return inspectorContent; } }
        internal Control CheckContent { get { return checkContent; } }
        internal int InspectorHeight { get { return inspectorContent == null ? 0 : inspectorContent.Height; } }
        internal int CheckHeight { get { return checkContent == null ? 0 : checkContent.Height; } }

        internal Control Build()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.Controls.Add(GuiTheme.Label("Tracker card or project folder", 9, GuiTheme.Muted));
            var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3 };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(new FieldPanel(path) { Dock = DockStyle.Fill }, 0, 0);
            browse = GuiTheme.Button("Browse…", false); browse.Name = "TrackerBrowse"; browse.Click += delegate { if (!work.IsRunning) Browse(); };
            inspect.Name = "TrackerInspect"; inspect.Click += delegate { Run(); };
            row.Controls.Add(browse, 1, 0); row.Controls.Add(inspect, 2, 0);
            panel.Controls.Add(row);
            GuiTheme.StyleComboBox(picker);
            picker.SelectedIndexChanged += delegate { if (!filling && picker.SelectedIndex >= 0) Run(picker.SelectedIndex); };
            panel.Controls.Add(picker);
            notice.Name = "TrackerNotice"; notice.UseMnemonic = false; notice.MaximumSize = new Size(780, 0);
            summary.Name = "TrackerSummary"; summary.UseMnemonic = false; summary.MaximumSize = new Size(780, 0); summary.Visible = false;
            panel.Controls.Add(notice); panel.Controls.Add(summary);
            ConfigureGrid(instruments, new[] { "Instrument", "File", "Sample", "Length (frames)", "Channels", "Play mode", "Status" }, 190);
            ConfigureGrid(patterns, new[] { "Pattern", "File", "Tracks", "Note steps", "Instruments played", "In the song" }, 150);
            panel.Controls.Add(instruments); panel.Controls.Add(patterns);
            viewPattern.Name = "TrackerViewPattern"; viewPattern.Enabled = false;
            viewPattern.Click += delegate { ViewPattern(); };
            patterns.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) ViewPattern(); };
            patterns.SelectionChanged += delegate { viewPattern.Enabled = patterns.CurrentRow != null && Report != null && Report.Patterns.Count > patterns.CurrentRow.Index && Report.Patterns[patterns.CurrentRow.Index].Error == null; };
            panel.Controls.Add(viewPattern);
            path.TextChanged += delegate { work.Invalidate(); SetBusy(false); ClearResults(); };
            panel.Disposed += delegate { closed = true; work.Dispose(); };
            inspectorContent = panel;

            var second = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
            second.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            check.Name = "TrackerCheckButton"; check.Click += delegate { Run(Math.Max(0, picker.SelectedIndex)); };
            second.Controls.Add(check);
            checkNotice.Name = "TrackerCheckNotice"; checkNotice.UseMnemonic = false; checkNotice.MaximumSize = new Size(780, 0);
            second.Controls.Add(checkNotice);
            ConfigureGrid(missing, new[] { "Instrument", "Problem", "Played by patterns", "In the song" }, 190);
            second.Controls.Add(missing);
            second.Controls.Add(details);
            checkContent = second;
            return panel;
        }

        // First time the Tracker Mini view is shown: start from the card the window already knows about.
        internal void Activate()
        {
            if (initialized) return;
            initialized = true;
            SuggestCard(source());
        }

        internal void SuggestCard(string folder)
        {
            if (!work.IsRunning && string.IsNullOrWhiteSpace(path.Text) && !string.IsNullOrWhiteSpace(folder)) path.Text = folder;
        }

        static void ConfigureGrid(DataGridView grid, string[] columns, int height)
        {
            GuiTheme.StyleGrid(grid); grid.ReadOnly = true; grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false; grid.MultiSelect = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.Dock = DockStyle.Top; grid.Height = height;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            foreach (string title in columns) grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = title, SortMode = DataGridViewColumnSortMode.NotSortable });
        }

        void Browse()
        {
            string chosen;
            if (FolderPicker != null) chosen = FolderPicker(path.Name);
            else using (var dialog = new FolderPickerDialog("Choose the Tracker card or a project folder", path.Text, false))
                chosen = dialog.ShowDialog(host.Owner) == DialogResult.OK ? dialog.SelectedPath : null;
            if (!string.IsNullOrEmpty(chosen)) { path.Text = chosen; Run(); }
        }

        void ClearResults()
        {
            Report = null; projects = new List<string>(); projectWarnings = new List<string>(); picker.Visible = false;
            instruments.Rows.Clear(); patterns.Rows.Clear(); missing.Rows.Clear();
            summary.Visible = false; details.Visible = false;
            notice.Text = "Choose a Tracker card, or a project folder that holds project.mt, then click Inspect.";
            checkNotice.Text = "Inspect a project first, or click Check project.";
            host.RefreshLayout();
        }

        // Opens the selected pattern's steps in a window.
        internal void ViewPattern()
        {
            if (work.IsRunning || Report == null || patterns.CurrentRow == null || patterns.CurrentRow.Index >= Report.Patterns.Count) return;
            TrackerPatternInfo info = Report.Patterns[patterns.CurrentRow.Index];
            if (info.Error != null) return;
            TrackerPatternFile file;
            try { file = TrackerPatternFile.Read(Records.ReadBytes(System.IO.Path.Combine(Report.Folder, "patterns", info.File), TrackerFiles.PatternLimit, "Tracker pattern")); }
            catch (Exception ex) when (ExpectedFailure.Is(ex)) { notice.Text = "Cannot open " + info.File + ": " + ex.Message; return; }
            string name = Report.Project.Name.Length > 0 ? Report.Project.Name : System.IO.Path.GetFileName(Report.Folder);
            using (var dialog = new TrackerPatternDialog("Pattern " + info.Number + " · " + name, file, Report.Project.TrackNames))
            {
                if (PatternShown != null) PatternShown(dialog);
                else dialog.ShowDialog(host.Owner);
            }
        }

        void SetBusy(bool busy)
        {
            inspect.Enabled = check.Enabled = path.Enabled = browse.Enabled = picker.Enabled = patterns.Enabled = !busy;
            viewPattern.Enabled = !busy && Report != null && patterns.CurrentRow != null &&
                patterns.CurrentRow.Index < Report.Patterns.Count && Report.Patterns[patterns.CurrentRow.Index].Error == null;
        }

        // Finds the projects below the chosen folder and inspects one (the first, or the one picked).
        internal async void Run(int index = 0)
        {
            if (work.IsRunning || closed) return;
            string folder = path.Text.Trim();
            if (folder.Length == 0) { notice.Text = "Choose a Tracker card or project folder first."; return; }
            var knownProjects = new List<string>(projects);
            var cancellation = work.Begin();
            SetBusy(true);
            notice.Text = "Reading the project…"; checkNotice.Text = notice.Text;
            List<string> found = projects; TrackerProjectReport report = null; string error = null;
            var discoveryWarnings = knownProjects.Count > 0 && index > 0 ? new List<string>(projectWarnings) : new List<string>();
            var inspectProject = Inspector;
            try
            {
                var token = cancellation.Token;
                var result = await Task.Run(delegate {
                    using (new OperationContext(interaction, token) { PreviewOnly = true }.Activate())
                    {
                        List<string> list = knownProjects.Count > 0 && index > 0 ? knownProjects : TrackerProjectCheck.FindProjects(folder, OperationContext.Current, discoveryWarnings);
                        TrackerProjectReport inspected = list.Count == 0 ? null : inspectProject(list[Math.Min(index, list.Count - 1)], OperationContext.Current);
                        if (inspected != null) inspected.Warnings.AddRange(discoveryWarnings);
                        return new KeyValuePair<List<string>, TrackerProjectReport>(list, inspected);
                    }
                });
                found = result.Key; report = result.Value;
            }
            catch (OperationCanceledException) { error = "Cancelled."; }
            catch (Exception ex) { error = "Cannot read the project: " + ex.Message; } // shown in the notice
            finally { cancellation.Dispose(); }
            host.RunOnUi(delegate {
                if (!cancellation.IsCurrent) return;
                SetBusy(false);
                if (error != null) { notice.Text = error; checkNotice.Text = error; return; }
                projectWarnings = discoveryWarnings;
                Show(found, report, index);
                if (report == null && discoveryWarnings.Count > 0)
                    notice.Text = checkNotice.Text = string.Join(Environment.NewLine, discoveryWarnings);
            });
        }

        void Show(List<string> found, TrackerProjectReport report, int index)
        {
            projects = found;
            filling = true;
            try
            {
                picker.Items.Clear();
                foreach (string folderName in found) picker.Items.Add(folderName);
                picker.Visible = found.Count > 1;
                if (found.Count > 0) picker.SelectedIndex = Math.Min(index, found.Count - 1);
            }
            finally { filling = false; }
            Report = report;
            instruments.Rows.Clear(); patterns.Rows.Clear(); missing.Rows.Clear(); details.Visible = false;
            if (report == null)
            {
                summary.Visible = false;
                notice.Text = "No Tracker project (a folder holding project.mt) was found there.";
                checkNotice.Text = notice.Text;
                host.RefreshLayout();
                return;
            }
            if (report.Error != null)
            {
                summary.Visible = false; notice.Text = report.Error; checkNotice.Text = report.Error;
                host.RefreshLayout();
                return;
            }
            ShowSummary(report);
            FillInstruments(report);
            FillPatterns(report);
            ShowCheck(report);
            host.RefreshLayout();
        }

        static string FormatSampleSize(long bytes) { return Formatting.FormatSize(bytes) + " wav"; }

        void ShowSummary(TrackerProjectReport report)
        {
            TrackerProjectFile project = report.Project;
            var text = new StringBuilder();
            text.Append(project.Name.Length > 0 ? project.Name : "(unnamed project)");
            if (project.Tempo > 0) text.Append("  ·  ").Append(project.Tempo.ToString("0.##", CultureInfo.InvariantCulture)).Append(" BPM");
            text.Append("  ·  firmware ").Append(project.FirmwareVersion);
            text.AppendLine();
            text.Append(report.Patterns.Count).Append(" patterns  ·  song of ").Append(project.Playlist.Count).Append(" positions  ·  ")
                .Append(report.Instruments.Count).Append(" instrument files");
            if (report.MidiOrSynthInstruments > 0) text.Append("  ·  ").Append(report.MidiOrSynthInstruments).Append(" MIDI or synth instruments in use");
            summary.Text = text.ToString(); summary.Visible = true;
            notice.Text = report.Folder + (report.Warnings.Count > 0 ? "  ·  " + string.Join(" ", report.Warnings) : "");
        }

        void FillInstruments(TrackerProjectReport report)
        {
            foreach (TrackerInstrumentInfo item in report.Instruments)
            {
                if (item.Error != null) { instruments.Rows.Add(item.Slot.ToString(CultureInfo.InvariantCulture), item.File, "", "", "", "", "Unreadable: " + item.Error); continue; }
                TrackerInstrumentFile d = item.Details;
                if (d.Legacy)
                {
                    // The original Tracker keeps the audio in a wav file beside the instrument.
                    instruments.Rows.Add(item.Slot.ToString(CultureInfo.InvariantCulture), item.File, d.SampleName, item.SampleFileBytes < 0 ? "" : FormatSampleSize(item.SampleFileBytes),
                        "", "Original Tracker", item.SampleFileBytes < 0 ? "Sample file " + System.IO.Path.GetFileName(item.SampleFile) + " is missing" : item.SampleFileBytes <= 44 ? "Sample file is empty" : "OK");
                    continue;
                }
                bool usable = d.Frames > 0 && (d.Wavetable || d.Channels > 0);
                instruments.Rows.Add(item.Slot.ToString(CultureInfo.InvariantCulture), item.File, d.SampleName, d.Frames.ToString("N0", CultureInfo.InvariantCulture),
                    d.Channels == 0 ? "?" : d.Channels == 1 ? "Mono" : "Stereo", d.PlayMode, d.Wavetable ? "Wavetable" : usable ? "OK" : "No usable sample");
            }
        }

        void FillPatterns(TrackerProjectReport report)
        {
            var inSong = new HashSet<int>(report.Project.Playlist);
            foreach (TrackerPatternInfo item in report.Patterns)
            {
                var used = new List<string>();
                foreach (int stored in item.Instruments) used.Add(stored < 48 ? (stored + 1).ToString(CultureInfo.InvariantCulture) : stored < 64 ? "MIDI " + (stored - 47) : "Synth " + (stored - 63));
                patterns.Rows.Add(item.Number.ToString(CultureInfo.InvariantCulture), item.File, item.Error != null ? "" : item.Tracks.ToString(CultureInfo.InvariantCulture),
                    item.Error != null ? "Unreadable: " + item.Error : item.NoteSteps.ToString(CultureInfo.InvariantCulture), string.Join(", ", used), inSong.Contains(item.Number) ? "Yes" : "No");
            }
        }

        void ShowCheck(TrackerProjectReport report)
        {
            foreach (TrackerMissingInstrument item in report.Missing)
                missing.Rows.Add(item.Slot.ToString(CultureInfo.InvariantCulture), char.ToUpperInvariant(item.Reason[0]) + item.Reason.Substring(1),
                    string.Join(", ", item.Patterns), item.InSong ? "Yes" : "No");
            var lines = new List<string>(report.MissingPatterns);
            if (report.UnreadableFiles > 0) lines.Add(report.UnreadableFiles + " file(s) could not be read; they are marked in the inspector.");
            if (report.MidiOrSynthInstruments > 0) lines.Add(report.MidiOrSynthInstruments + " MIDI or synth instrument(s) are played; those have no files to check.");
            lines.AddRange(report.Warnings);
            details.Text = string.Join(Environment.NewLine, lines); details.Visible = lines.Count > 0;
            int inSongCount = report.Missing.FindAll(m => m.InSong).Count;
            checkNotice.Text = report.Missing.Count == 0
                ? (report.Warnings.Count > 0 || report.UnreadableFiles > 0 ? "Check incomplete; see the findings below." : "Every instrument the patterns play has a usable instrument file.") + (report.MissingPatterns.Count > 0 ? " " + report.MissingPatterns.Count + " song position(s) play a pattern with no file; see below." : "")
                : report.Missing.Count + " instrument(s) are played by patterns without a usable file (" + inSongCount + " in the song)" +
                  (report.MissingPatterns.Count > 0 ? "; " + report.MissingPatterns.Count + " song position(s) play a pattern with no file" : "") + ".";
        }
    }
}
