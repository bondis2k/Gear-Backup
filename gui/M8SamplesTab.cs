using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    internal sealed partial class M8SamplesTab : IWorkflowTab
    {
        readonly IWorkflowTabHost host;
        readonly Func<Config> settings;
        readonly Func<string> source;
        readonly IOperationInteraction interaction;
        readonly TextBox song = new TextBox { Name = "SampleSongPath", AccessibleName = "Song to check" };
        readonly TextBox card = new TextBox { Name = "SampleCardRoot", AccessibleName = "M8 card root" };
        readonly TextBox backups = new TextBox { Name = "SampleBackupRoot", AccessibleName = "Backup location" };
        readonly CheckBox search = new CheckBox { Name = "SearchSampleBackups", Text = "Search M8 backup histories (including older restore points)", Checked = true, AutoSize = true };
        readonly Button check = GuiTheme.Button("Check samples", true), cancel = GuiTheme.Button("Cancel", false);
        readonly Label notice = GuiTheme.Label("Choose a song and its M8 card root. Paths starting with / use the card root; other paths use the song's folder.", 9, GuiTheme.Muted);
        readonly DataGridView samples = new DataGridView { Name = "SampleResults", AccessibleName = "Sample availability" };
        readonly DataGridView matches = new DataGridView { Name = "SampleBackupMatches", AccessibleName = "Copies in backups" };
        readonly TextBox warnings = new TextBox { Name = "SampleCheckWarnings", ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Top, Height = 60, Visible = false };
        Control content;
        readonly GuiBackgroundWork work = new GuiBackgroundWork();
        bool closed, initialized;
        Config checkedConfig;
        internal Func<string> SongPicker { get; set; }
        internal Func<string, string> FolderPicker { get; set; }
        internal M8SampleReport Report { get; private set; }
        internal Control BuildContent { get { return content; } }
        internal Func<string, bool> ConfirmRestore { get; set; }
        internal Action<M8SampleMatch, string, Config> RestoreRequested;

        internal M8SamplesTab(IWorkflowTabHost host, Func<Config> settings, Func<string> source, IOperationInteraction interaction)
        { this.host = host; this.settings = settings; this.source = source; this.interaction = interaction; }

        public int ContentHeight { get { return content == null ? 0 : content.Height; } }

        public Control Build()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddPath(panel, "M8 song (.m8s)", song, () => PickSong());
            AddPath(panel, "M8 card root / extracted card folder (optional for backup-only search)", card, () => PickFolder(card));
            AddPath(panel, "Backup location (also searches configured device/history locations)", backups, () => PickFolder(backups));
            panel.Controls.Add(search);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
            check.Name = "CheckM8Samples"; cancel.Name = "CancelM8SampleCheck"; cancel.Enabled = false;
            actions.Controls.Add(check); actions.Controls.Add(cancel); panel.Controls.Add(actions);
            BuildCardCheck(panel, actions);
            check.Click += delegate { Run(); }; cancel.Click += delegate { work.Cancel(); };
            notice.Name = "SampleCheckNotice"; notice.UseMnemonic = false; notice.MaximumSize = new Size(780, 0);
            panel.Controls.Add(notice);
            ConfigureGrid(samples, new[] { "Slots", "Sample reference", "Card", "Backup copies" }, 160);
            ConfigureGrid(matches, new[] { "Match", "Backup folder", "Restore point", "Original path", "Integrity" }, 160);
            samples.CurrentCellChanged += delegate { ShowMatches(); };
            matches.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { AskRestoreCopy(e.RowIndex); };
            search.CheckedChanged += delegate { RefreshSearchMode(); };
            panel.Controls.Add(samples);
            var explanation = GuiTheme.Label("Select a sample to see backup copies below. Double-click a copy to prepare its restore. Filename-only matches are candidates; even an exact path may contain a different version.", 9, GuiTheme.Muted);
            explanation.MaximumSize = new Size(780, 0); panel.Controls.Add(explanation);
            panel.Controls.Add(matches); panel.Controls.Add(warnings);
            panel.Disposed += delegate { closed = true; work.Dispose(); };
            content = panel; return panel;
        }

        internal void Activate()
        {
            if (initialized) return;
            initialized = true;
            card.Text = source() ?? "";
            backups.Text = settings().BackupFolder;
        }

        internal void SuggestCard(string path)
        {
            if (!work.IsRunning && string.IsNullOrWhiteSpace(card.Text) && !string.IsNullOrWhiteSpace(path)) card.Text = path;
        }

        void AddPath(TableLayoutPanel panel, string caption, TextBox path, Action browse)
        {
            panel.Controls.Add(GuiTheme.Label(caption, 9, GuiTheme.Muted));
            var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(new FieldPanel(path) { Dock = DockStyle.Fill }, 0, 0);
            var button = GuiTheme.Button("Browse…", false); button.Click += delegate { if (!work.IsRunning) browse(); };
            row.Controls.Add(button, 1, 0); panel.Controls.Add(row);
            path.TextChanged += delegate { ClearResults(); };
        }

        void PickSong()
        {
            string path;
            if (SongPicker != null) path = SongPicker();
            else using (var picker = new OpenFileDialog { Title = "Choose an M8 song", Filter = "M8 song (*.m8s)|*.m8s", CheckFileExists = true, RestoreDirectory = true })
                path = picker.ShowDialog(host.Owner) == DialogResult.OK ? picker.FileName : null;
            if (!string.IsNullOrEmpty(path)) song.Text = path;
        }

        void PickFolder(TextBox target)
        {
            string path;
            if (FolderPicker != null) path = FolderPicker(target.Name);
            else using (var picker = new FolderPickerDialog("Choose " + target.AccessibleName, target.Text, false))
                path = picker.ShowDialog(host.Owner) == DialogResult.OK ? picker.SelectedPath : null;
            if (!string.IsNullOrEmpty(path)) target.Text = path;
        }

        static void ConfigureGrid(DataGridView grid, string[] columns, int height)
        {
            GuiTheme.StyleGrid(grid); grid.ReadOnly = true; grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false; grid.MultiSelect = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.Dock = DockStyle.Top; grid.Height = height;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            foreach (string title in columns) grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = title, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns[0].FillWeight = 50;
        }

        void ClearResults()
        {
            Report = null; checkedConfig = null; samples.Rows.Clear(); matches.Rows.Clear(); warnings.Visible = false;
            cardReport = null;
            cardSongs.Rows.Clear(); cardSongs.Visible = false;
            notice.Text = "Choose Check samples to refresh the results.";
        }

        void RefreshSearchMode()
        {
            if (work.IsRunning || host.Busy) return;
            if (!search.Checked)
            {
                checkedConfig = null;
                matches.Rows.Clear();
                foreach (DataGridViewRow row in samples.Rows) row.Cells[3].Value = "Not searched";
                notice.Text = "Backup search disabled. Card results are retained; enable it to search histories for the selected song.";
                return;
            }
            if (cardReport != null)
            {
                SearchSelectedCardSong();
                return;
            }
            if (!string.IsNullOrWhiteSpace(song.Text)) Run();
            else notice.Text = "Backup search enabled. Choose a song and click Check samples, or select a checked song from the card list.";
        }

        async void Run(bool preserveCardResults = false, string selectedSong = null)
        {
            if (closed || host.Busy || work.IsRunning) return;
            if (preserveCardResults)
            {
                Report = null; checkedConfig = null; samples.Rows.Clear(); matches.Rows.Clear(); warnings.Visible = false;
            }
            else ClearResults();
            string songFile = selectedSong ?? song.Text, cardRoot = card.Text;
            Config config;
            try
            {
                if (string.IsNullOrWhiteSpace(songFile)) throw new IOException("Choose an M8 song first.");
                config = search.Checked ? settings().Clone() : null;
                if (config != null && !string.IsNullOrWhiteSpace(backups.Text)) config.BackupFolder = PathSafety.ExpandPath(backups.Text);
                if (config == null && string.IsNullOrWhiteSpace(cardRoot)) throw new IOException("Choose a card folder or enable backup search.");
            }
            catch (Exception ex) { notice.Text = ex.Message; return; }
            var cancellation = work.Begin();
            SetChecking(true);
            notice.Text = "Checking sample references and verifying matching backup objects…";
            try
            {
                var reader = M8SongReaders.Current;
                var report = await Task.Run(() => {
                    using (new OperationContext(interaction, cancellation.Token).Activate())
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        return M8SampleCheck.Run(reader.Read(songFile), cardRoot, config, songFile);
                    }
                });
                host.RunOnUi(delegate {
                    if (!cancellation.CanPublish) return;
                    Report = report;
                    checkedConfig = config;
                    foreach (var item in report.Samples)
                    {
                        int index = samples.Rows.Add(item.Slots, item.Reference, item.CardStatus,
                            config == null ? "Not searched" : item.Matches.Count(m => m.Exact && m.Status == "Verified copy") + " exact; " + item.Matches.Count(m => !m.Exact) + " candidates");
                        samples.Rows[index].Tag = item;
                        samples.Rows[index].Cells[1].ToolTipText = item.Relative == null ? item.CardStatus : "Card-relative path: " + item.Relative;
                    }
                    samples.Height = Math.Min(host.Font.Height * 9, samples.ColumnHeadersHeight + samples.RowTemplate.Height * Math.Max(1, samples.Rows.Count) + 5);
                    notice.Text = report.Samples.Count + " unique sample reference(s); " + report.Samples.Count(s => s.CardStatus == "Missing on card") +
                        " missing on card; " + report.BackupsSearched + " M8 backup set(s) searched. " + (report.Warnings.Count > 0 ? "See warnings below; results may be incomplete." : "") +
                        " All sampler slots are included, even unused ones. Card presence does not verify audio contents.";
                    warnings.Text = string.Join(Environment.NewLine, report.Warnings); warnings.Visible = report.Warnings.Count > 0;
                    ShowMatches();
                });
            }
            catch (OperationCanceledException) { host.RunOnUi(delegate { if (cancellation.IsCurrent) notice.Text = "Sample check cancelled. Run it again for complete results."; }); }
            catch (Exception ex) { host.RunOnUi(delegate { if (cancellation.IsCurrent) notice.Text = "Cannot check samples: " + ex.Message; }); }
            finally
            {
                cancellation.Dispose();
                host.RunOnUi(delegate {
                    if (cancellation.IsCurrent)
                    {
                        if (cancellation.IsCancellationRequested) notice.Text = "Sample check cancelled. Run it again for complete results.";
                        SetChecking(false); host.RefreshLayout();
                    }
                });
            }
        }

        void ShowMatches()
        {
            matches.Rows.Clear();
            var selected = samples.CurrentRow == null ? null : samples.CurrentRow.Tag as M8SampleResult;
            if (selected == null || !search.Checked) return;
            foreach (var match in selected.Matches.OrderByDescending(m => m.Exact))
            {
                int row = matches.Rows.Add(match.Exact ? "Exact path" : "Filename only", match.Backup, match.Point, match.Relative, match.Status);
                matches.Rows[row].Tag = match;
            }
        }

        void AskRestoreCopy(int row)
        {
            if (row < 0 || row >= matches.Rows.Count || work.IsRunning || host.Busy) return;
            var match = matches.Rows[row].Tag as M8SampleMatch;
            if (match == null || checkedConfig == null) return;
            if (match.Status != "Verified copy")
            {
                notice.Text = "This copy is unavailable or damaged. Choose a verified copy, or run Check backups.";
                return;
            }
            string question = "Restore this sample from the selected backup?" + Environment.NewLine + Environment.NewLine +
                match.Relative + Environment.NewLine + match.Backup + Environment.NewLine + match.Point + Environment.NewLine + Environment.NewLine +
                (match.Exact ? "" : "This is a filename-only candidate and may be a different sample. It will retain its original folder path." + Environment.NewLine + Environment.NewLine) +
                "The Restore tab will open with this file selected. Review the destination and click Restore to continue.";
            bool accepted;
            if (ConfirmRestore != null) accepted = ConfirmRestore(question);
            else using (var dialog = new SampleRestoreDialog(question)) accepted = dialog.ShowDialog(host.Owner) == DialogResult.OK;
            if (accepted && RestoreRequested != null) RestoreRequested(match, card.Text, checkedConfig.Clone());
        }
    }
}
