using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    internal sealed class M8MigrationTab : IWorkflowTab
    {
        readonly IWorkflowTabHost host;
        readonly Func<Config> settings;
        readonly Action<Config, RestoreRequest, string, bool, Action<M8MigrationReport>> run;
        readonly CheckBox linkHistory = new CheckBox { Name = "MigrationLinkHistory", Text = "Link replacement card to the original backup history", AutoSize = true };
        readonly TextBox backupRoot = new TextBox { Name = "MigrationBackupRoot", AccessibleName = "Migration backup location" };
        readonly TextBox target = new TextBox { Name = "MigrationTarget", AccessibleName = "Replacement card root or empty test folder" };
        readonly ComboBox backups = new ComboBox { Name = "MigrationBackup", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly ComboBox points = new ComboBox { Name = "MigrationPoint", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly Button refresh = GuiTheme.Button("Refresh backups", false), start = GuiTheme.Button("Migrate and verify…", true), save = GuiTheme.Button("Save report…", false);
        readonly Label notice = GuiTheme.Label("Choose an M8 backup and an empty replacement card or test folder.", 9, GuiTheme.Muted);
        readonly DataGridView songs = new DataGridView { Name = "MigrationSongs", AccessibleName = "Migrated song dependency results", Visible = false };
        readonly TextBox details = new TextBox { Name = "MigrationReport", ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Top, Height = 150, BorderStyle = BorderStyle.None, Visible = false };
        Control content;
        RestoreSet set;
        M8MigrationReport report;
        bool initialized, filling, loading, migrating, closed;
        int generation;
        internal Func<string, string> FolderPicker { get; set; }
        internal Func<string> ReportPicker { get; set; }
        internal Control Content { get { return content; } }
        public int ContentHeight { get { return content == null ? 0 : content.Height; } }

        sealed class BackupItem
        {
            internal string Path;
            public override string ToString() { return BackupSetNames.Label(Path) + " · " + System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path)); }
        }

        internal M8MigrationTab(IWorkflowTabHost host, Func<Config> settings, Action<Config, RestoreRequest, string, bool, Action<M8MigrationReport>> run)
        { this.host = host; this.settings = settings; this.run = run; }

        public Control Build()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var instructions = GuiTheme.Label("Restore a complete M8 backup to an empty replacement card, verify every copied file, then check all restored songs for missing samples. Use a card already prepared for the M8. This copies files; it does not format cards, flash firmware or remove existing content. An empty PC folder can be used for a rehearsal.", 9, GuiTheme.Muted);
            instructions.MaximumSize = new Size(780, 0); panel.Controls.Add(instructions);
            AddPath(panel, "Backup location", backupRoot, false);
            refresh.Name = "RefreshMigrationBackups"; refresh.Click += delegate { RefreshBackups(); }; panel.Controls.Add(refresh);
            GuiTheme.StyleComboBox(backups); GuiTheme.StyleComboBox(points);
            panel.Controls.Add(GuiTheme.Label("M8 backup set", 9, GuiTheme.Muted)); panel.Controls.Add(backups);
            panel.Controls.Add(GuiTheme.Label("Complete restore point", 9, GuiTheme.Muted)); panel.Controls.Add(points);
            AddPath(panel, "Replacement card root / empty test folder", target, true);
            panel.Controls.Add(linkHistory);
            var linkNotice = GuiTheme.Label("Optional: future backups of this card use the selected history and its preferences. Saved with this app's settings after successful verification. Requires a card drive root; leave unchecked for a test folder. An explicit Profile setting takes priority.", 9, GuiTheme.Muted);
            linkNotice.MaximumSize = new Size(780, 0); panel.Controls.Add(linkNotice);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
            start.Name = "StartM8Migration"; save.Name = "SaveMigrationReport";
            actions.Controls.Add(start); actions.Controls.Add(save); panel.Controls.Add(actions);
            start.Click += delegate { StartMigration(); }; save.Click += delegate { SaveReport(); };
            notice.Name = "MigrationNotice"; notice.UseMnemonic = false; notice.MaximumSize = new Size(780, 0); panel.Controls.Add(notice);
            GuiTheme.StyleGrid(songs); songs.ReadOnly = true; songs.AllowUserToAddRows = songs.AllowUserToDeleteRows = false;
            songs.RowHeadersVisible = false; songs.Dock = DockStyle.Top; songs.Height = host.Font.Height * 8;
            songs.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            songs.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            foreach (string column in new[] { "Song", "Missing samples", "Result" }) songs.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column });
            songs.Columns[0].FillWeight = 200;
            panel.Controls.Add(songs); panel.Controls.Add(details);
            backups.SelectedIndexChanged += delegate { if (!filling) LoadPoints(); };
            points.SelectedIndexChanged += delegate { ClearReport(); ShowSelection(); };
            backupRoot.TextChanged += delegate {
                ++generation; set = null; filling = true; backups.Items.Clear(); points.Items.Clear(); filling = false;
                loading = false; ClearReport(); notice.Text = "Click Refresh backups to load this location."; UpdateAvailability();
            };
            target.TextChanged += delegate { ClearReport(); UpdateAvailability(); };
            panel.Disposed += delegate { closed = true; ++generation; };
            content = panel; UpdateAvailability(); return panel;
        }

        void AddPath(TableLayoutPanel panel, string caption, TextBox field, bool allowNew)
        {
            panel.Controls.Add(GuiTheme.Label(caption, 9, GuiTheme.Muted));
            var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(new FieldPanel(field) { Dock = DockStyle.Fill }, 0, 0);
            var browse = GuiTheme.Button("Browse…", false);
            browse.Click += delegate {
                if (host.Busy) return;
                string path;
                if (FolderPicker != null) path = FolderPicker(field.Name);
                else using (var picker = new FolderPickerDialog("Choose " + caption.ToLowerInvariant(), field.Text, allowNew))
                    path = picker.ShowDialog(host.Owner) == DialogResult.OK ? picker.SelectedPath : null;
                if (!string.IsNullOrEmpty(path)) field.Text = path;
            };
            row.Controls.Add(browse, 1, 0); panel.Controls.Add(row);
        }

        internal void Activate()
        {
            if (!initialized) { initialized = true; backupRoot.Text = settings().BackupFolder; RefreshBackups(); }
            UpdateAvailability();
        }

        Config Options()
        {
            var config = settings().Clone(); config.BackupFolder = PathSafety.ExpandPath(backupRoot.Text);
            if (string.IsNullOrWhiteSpace(config.BackupFolder)) throw new IOException("Choose a backup location first.");
            return config;
        }

        async void RefreshBackups()
        {
            if (host.Busy) return;
            int scan = ++generation;
            Config config;
            try { config = Options(); } catch (Exception ex) { notice.Text = ex.Message; return; }
            loading = true; set = null; ClearReport(); UpdateAvailability(); notice.Text = "Loading M8 backups…";
            try
            {
                var paths = await Task.Run(() => {
                    using (new OperationContext(new MigrationReadInteraction(), CancellationToken.None).Activate()) return RestoreCatalog.BackupsForDevice(config, Devices.M8);
                }).ConfigureAwait(false);
                host.RunOnUi(delegate {
                    if (closed || scan != generation) return;
                    filling = true; backups.Items.Clear(); points.Items.Clear();
                    foreach (string path in paths) backups.Items.Add(new BackupItem { Path = path });
                    if (backups.Items.Count > 0) backups.SelectedIndex = 0;
                    filling = false; loading = false;
                    if (paths.Count > 0) LoadPoints(); else { notice.Text = "No M8 backups found in the configured locations."; UpdateAvailability(); }
                });
            }
            catch (Exception ex) { host.RunOnUi(delegate { if (!closed && scan == generation) { loading = false; notice.Text = "Cannot load backups: " + ex.Message; UpdateAvailability(); } }); }
        }

        async void LoadPoints()
        {
            int scan = ++generation;
            var backup = backups.SelectedItem as BackupItem;
            set = null; points.Items.Clear(); ClearReport();
            if (backup == null) { loading = false; UpdateAvailability(); return; }
            loading = true; UpdateAvailability(); notice.Text = "Loading restore points…";
            try
            {
                var loaded = await Task.Run(() => {
                    using (new OperationContext(new MigrationReadInteraction(), CancellationToken.None).Activate()) return RestoreCatalog.Load(backup.Path, Devices.M8);
                }).ConfigureAwait(false);
                host.RunOnUi(delegate {
                    if (closed || scan != generation) return;
                    set = loaded; loading = false;
                    foreach (var point in loaded.Points.Where(p => p.Id != "current" || loaded.Points.Count == 1)) points.Items.Add(point);
                    if (points.Items.Count > 0) points.SelectedIndex = 0;
                    ShowSelection();
                });
            }
            catch (Exception ex) { host.RunOnUi(delegate { if (!closed && scan == generation) { loading = false; notice.Text = "Cannot read backup: " + ex.Message; UpdateAvailability(); } }); }
        }

        void ShowSelection()
        {
            var point = points.SelectedItem as RestorePoint;
            if (point != null) notice.Text = point.Snapshot.Files.Count + " file(s), " + Formatting.FormatSize(point.Snapshot.Files.Sum(f => f.Size)) +
                ". Choose an empty destination. Migration always verifies checksums and asks you to review the copy plan.";
            UpdateAvailability(); host.RefreshLayout();
        }

        void ClearReport() { report = null; songs.Rows.Clear(); details.Clear(); songs.Visible = details.Visible = false; save.Enabled = false; }

        internal void UpdateAvailability()
        {
            bool busy = host.Busy || migrating;
            refresh.Enabled = !busy && !loading;
            backups.Enabled = points.Enabled = !busy && !loading;
            start.Enabled = !busy && !loading && set != null && points.SelectedItem is RestorePoint && !string.IsNullOrWhiteSpace(target.Text);
            save.Enabled = !busy && report != null;
            linkHistory.Enabled = !busy;
        }

        void StartMigration()
        {
            if (host.Busy || migrating || loading || set == null) return;
            var point = points.SelectedItem as RestorePoint;
            if (point == null) return;
            try
            {
                Config config = Options(); string destination = PathSafety.ExpandPath(target.Text);
                if (linkHistory.Checked) ProfileLinks.ValidateLink(config, ProfileLinks.Key(destination), Path.GetDirectoryName(set.Backup));
                var request = new RestoreRequest { Backup = set.Backup, Point = point.Id, DeviceId = Devices.M8.Id, Stamp = RestoreCatalog.Stamp(point.Snapshot) };
                ClearReport(); migrating = true; notice.Text = "Preparing migration. Review the copy plan; use Cancel in the activity area to stop."; UpdateAvailability();
                run(config, request, destination, linkHistory.Checked, ShowReport);
            }
            catch (Exception ex) { migrating = false; notice.Text = ex.Message; UpdateAvailability(); }
        }

        void ShowReport(M8MigrationReport result)
        {
            if (closed) return;
            migrating = false; report = result; notice.Text = result.Operation.Summary;
            details.Text = result.Text();
            if (result.Songs != null) foreach (var song in result.Songs.Songs)
                songs.Rows.Add(M8SongJson.Clean(song.Relative), song.Error == null ? song.Missing.ToString() : "—",
                    song.Error != null ? "Cannot read song" : song.Missing > 0 ? "Missing samples" : song.NeedsAttention ? "Needs attention" : "OK");
            songs.Visible = songs.Rows.Count > 0; details.Visible = true;
            songs.Height = Math.Min(host.Font.Height * 8, songs.ColumnHeadersHeight + songs.RowTemplate.Height * Math.Max(1, songs.Rows.Count) + 5);
            UpdateAvailability(); host.RefreshLayout();
        }

        void SaveReport()
        {
            if (report == null) return;
            try
            {
                string path;
                if (ReportPicker != null) path = ReportPicker();
                else using (var picker = new SaveFileDialog { Title = "Save M8 migration report", FileName = "M8-card-migration-report.txt", Filter = "Text report (*.txt)|*.txt", OverwritePrompt = true })
                    path = picker.ShowDialog(host.Owner) == DialogResult.OK ? picker.FileName : null;
                if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, report.Text(), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) { notice.Text = "Cannot save report: " + ex.Message; }
        }

        // Inventory browsing never asks questions or changes backup metadata.
        sealed class MigrationReadInteraction : IOperationInteraction
        {
            public void Write(string text) { } public void WriteLine(string text) { } public void WriteColored(string text, ConsoleColor color) { }
            public char Choose(string question, System.Collections.Generic.IList<Option> options) { throw new InvalidOperationException(question); }
            public int ChooseItem(string question, System.Collections.Generic.IList<string> items, string noneLabel) { throw new InvalidOperationException(question); }
            public string AskText(string question) { throw new InvalidOperationException(question); }
            public bool ReviewPlan(OperationPlan plan) { return false; } public void ReportProgress(OperationProgress progress) { }
        }
    }
}
