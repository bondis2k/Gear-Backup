using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

partial class M8Backup
{
    sealed class MainForm : Form
    {
        Config config;
        readonly TextBox source = new TextBox(), backupFolder = new TextBox(), profile = new TextBox();
        readonly ComboBox operation = new ComboBox();
        // Only for full backups; not saved in the settings, so it's a per-run choice like the CLI's --zip.
        readonly CheckBox zip = new CheckBox { Text = "Also save as zip", AutoSize = true, Name = "ZipBackup" };
        readonly Button start = GuiTheme.Button("Back up", true), preview = GuiTheme.Button("Preview", false),
            cancel = GuiTheme.Button("Cancel", false), settings = GuiTheme.Button("Settings", false), detect = GuiTheme.Button("Detect device", false);
        readonly RichTextBox log = new RichTextBox();
        readonly Label status = GuiTheme.Label("Ready", 17, GuiTheme.Ink), detail = GuiTheme.Label("Choose your source and an operation to begin.", 10, GuiTheme.Muted);
        readonly GuiProgressBar progress = new GuiProgressBar();
        // The detected device's picture in the header; see ShowDevice.
        readonly PictureBox deviceImage = new PictureBox { Name = "DeviceImage", Size = new Size(64, 64),
            SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 4, 16, 0) };
        readonly Panel inputs = new Panel();
        // Worker threads add log text and progress here; the display timer moves them into the window every
        // 150 ms, so a fast copy doesn't flood the UI thread with updates.
        readonly object queueLock = new object();
        readonly StringBuilder pendingLog = new StringBuilder();
        OperationProgress pendingProgress;
        readonly System.Windows.Forms.Timer displayTimer = new System.Windows.Forms.Timer { Interval = 150 };
        readonly GuiOperationRunner runner;
        bool closeAfterCancel;

        public MainForm(bool designPreview)
        {
            runner = new GuiOperationRunner(this);
            Text = "M8 Backup";
            // The window and taskbar always use the general icon embedded in the exe (build-gui.cmd adds
            // assets\Backup.ico); only the header picture changes when a device is detected.
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            Font = new Font("Segoe UI", 10);
            ForeColor = GuiTheme.Ink;
            BackColor = GuiTheme.Background;
            ClientSize = new Size(960, 730);
            MinimumSize = new Size(820, 680);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            if (GuiHeadless) { Opacity = 0; ShowInTaskbar = false; }
            source.Name = "SourceFolder"; backupFolder.Name = "BackupFolder"; profile.Name = "Profile";
            operation.Name = "Operation"; start.Name = "StartOperation"; preview.Name = "PreviewOperation";
            cancel.Name = "CancelOperation"; status.Name = "OperationStatus";

            BuildLayout();

            var interaction = new GuiInteraction(this);
            using (new OperationContext(interaction, CancellationToken.None).Activate())
                config = designPreview ? new Config { BackupFolder = @"D:\Music backups" } : LoadConfig();
            ShowDevice(null);
            source.Text = designPreview ? @"E:\" : config.SourceFolder;
            backupFolder.Text = config.BackupFolder; profile.Text = config.Profile;
            operation.SelectedIndex = config.Mode == BackupMode.Incremental ? 1 : 0;
            start.Click += delegate { StartOperation(false); };
            preview.Click += delegate { StartOperation(true); };
            cancel.Click += delegate { RequestCancel(); };
            detect.Click += delegate { runner.DetectSource(); };
            settings.Click += delegate { EditSettings(); };
            operation.SelectedIndexChanged += delegate {
                start.Text = operation.SelectedIndex == 2 ? "Restore" : operation.SelectedIndex == 3 ? "Check" : "Back up";
                preview.Enabled = operation.SelectedIndex != 3 && !runner.IsBusy;
                zip.Enabled = operation.SelectedIndex == 0 && !runner.IsBusy;
            };
            // Closing while an operation runs cancels it first; CompleteOperation closes the window once it stops.
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (runner.IsBusy) { e.Cancel = true; closeAfterCancel = true; RequestCancel(); }
            };
            displayTimer.Tick += delegate { DrainDisplay(); };
            displayTimer.Start();
            FormClosed += delegate { displayTimer.Dispose(); };
        }

        void BuildLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(30), ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 205));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(deviceImage, 0, 0);
            var branding = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var title = GuiTheme.Label("M8 Backup", 25, GuiTheme.Ink); title.Font = new Font("Segoe UI", 25, FontStyle.Bold);
            title.Margin = new Padding(0, 0, 0, 0);
            branding.Controls.Add(title);
            branding.Controls.Add(GuiTheme.Label("Your music. Safely kept.", 10, GuiTheme.Muted));
            header.Controls.Add(branding, 1, 0);
            settings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            header.Controls.Add(settings, 2, 0);
            layout.Controls.Add(header, 0, 0);

            inputs.Dock = DockStyle.Fill; inputs.BackColor = Color.White; inputs.Padding = new Padding(20, 14, 20, 14);
            inputs.Margin = new Padding(0, 0, 0, 0);
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 6 };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 114));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            for (int i = 0; i < 3; i++) { fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); }
            AddPath(fields, 0, "Device, card or source folder  ·  leave blank to detect an " + Devices.Names, source, false);
            fields.Controls.Add(detect, 2, 1); detect.MinimumSize = new Size(110, 28); detect.Padding = Padding.Empty;
            AddPath(fields, 2, "Backup location", backupFolder, true);
            var profileLabel = GuiTheme.Label("Profile  ·  leave blank for automatic card identity", 9, GuiTheme.Muted);
            fields.Controls.Add(profileLabel, 0, 4); fields.SetColumnSpan(profileLabel, 3);
            profile.Dock = DockStyle.Fill; fields.Controls.Add(profile, 0, 5); fields.SetColumnSpan(profile, 3);
            inputs.Controls.Add(fields); layout.Controls.Add(inputs, 0, 1);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, Padding = new Padding(0, 14, 0, 14), Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            operation.DropDownStyle = ComboBoxStyle.DropDownList; operation.Dock = DockStyle.Fill;
            // The index is the operation number GuiOperationRunner.Execute uses: 0 full, 1 incremental, 2 restore, 3 check.
            operation.Items.AddRange(new object[] { "Full backup", "Incremental backup", "Restore files", "Check backups" });
            operation.SelectedIndex = 0; operation.Margin = new Padding(0, 4, 24, 0);
            zip.Margin = new Padding(0, 8, 20, 0); zip.ForeColor = GuiTheme.Ink;
            actions.Controls.Add(operation, 0, 0); actions.Controls.Add(zip, 1, 0);
            actions.Controls.Add(preview, 2, 0); actions.Controls.Add(start, 3, 0); actions.Controls.Add(cancel, 4, 0);
            cancel.Enabled = false; layout.Controls.Add(actions, 0, 2);

            var state = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            state.RowStyles.Add(new RowStyle(SizeType.Absolute, 33)); state.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
            state.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            state.Controls.Add(status, 0, 0); detail.AutoSize = false; detail.Dock = DockStyle.Fill; detail.AutoEllipsis = true;
            state.Controls.Add(detail, 0, 1); progress.Dock = DockStyle.Top; progress.Height = 6; progress.Margin = Padding.Empty; state.Controls.Add(progress, 0, 2);
            layout.Controls.Add(state, 0, 3);

            var activity = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16), RowCount = 2 };
            activity.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); activity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            activity.Margin = Padding.Empty; activity.Controls.Add(GuiTheme.Label("ACTIVITY", 9, GuiTheme.Muted), 0, 0);
            log.Dock = DockStyle.Fill; log.BorderStyle = BorderStyle.None; log.ReadOnly = true;
            log.BackColor = Color.White; log.ForeColor = GuiTheme.Ink; log.Font = new Font("Consolas", 9);
            log.DetectUrls = false; log.WordWrap = false; activity.Controls.Add(log, 0, 1); layout.Controls.Add(activity, 0, 4);

        }

        void AddPath(TableLayoutPanel fields, int row, string label, TextBox text, bool destination)
        {
            fields.Controls.Add(GuiTheme.Label(label, 9, GuiTheme.Muted), 0, row); fields.SetColumnSpan(fields.GetControlFromPosition(0, row), 3);
            text.Dock = DockStyle.Fill; text.Margin = new Padding(0, 0, 8, 0); fields.Controls.Add(text, 0, row + 1);
            var browse = GuiTheme.Button("Browse…", false); browse.MinimumSize = new Size(100, 28); browse.Padding = Padding.Empty;
            fields.Controls.Add(browse, 1, row + 1);
            if (destination) fields.SetColumnSpan(text, 1);
            browse.Click += delegate { BrowseFolder(this, text, destination); };
        }

        // Runs action on the UI thread and waits for its result; used by worker threads to show dialogs.
        // Invoke wraps exceptions, so the original one (e.g. a cancellation) is rethrown unwrapped.
        public T OnUi<T>(Func<T> action)
        {
            if (IsDisposed) throw new OperationCanceledException();
            try { return InvokeRequired ? (T)Invoke(action) : action(); }
            catch (System.Reflection.TargetInvocationException ex)
            {
                if (ex.InnerException != null) throw ex.InnerException;
                throw;
            }
        }
        // Called from worker threads; keeps at most the last 200,000 characters waiting to be shown.
        public void AppendActivity(string text)
        {
            lock (queueLock)
            {
                pendingLog.Append(text);
                if (pendingLog.Length > 200000) pendingLog.Remove(0, pendingLog.Length - 200000);
            }
        }
        public void UpdateProgress(OperationProgress value)
        {
            if (value.OverallOnly && Stage.IsFileDetail(value.Stage)) return;
            lock (queueLock) pendingProgress = value;
        }
        public void DrainDisplay()
        {
            progress.Advance();
            string text; OperationProgress value;
            lock (queueLock) { text = pendingLog.ToString(); pendingLog.Length = 0; value = pendingProgress; pendingProgress = null; }
            if (text.Length > 0)
            {
                log.AppendText(text);
                if (log.TextLength > 200000) { log.Select(0, log.TextLength - 100000); log.SelectedText = ""; }
                log.SelectionStart = log.TextLength; log.ScrollToCaret();
            }
            if (value == null) return;
            if (runner.CancellationRequested) return;
            status.Text = value.Stage;
            detail.Text = value.OverallOnly ? (value.TotalFiles > 0 ? value.Files + " / " + value.TotalFiles + " files" : "Working…") : value.Path;
            if (value.TotalBytes > 0)
            {
                progress.Style = ProgressBarStyle.Continuous;
                progress.Value = (int)Math.Min(100, Math.Max(0, value.Bytes * 100.0 / value.TotalBytes));
            }
            else progress.Style = ProgressBarStyle.Marquee;
        }

        Config CaptureConfig()
        {
            // Start from every saved setting, so settings without a field in this window are kept.
            Config copy = config.Clone();
            copy.BackupFolder = string.IsNullOrWhiteSpace(backupFolder.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) : ExpandPath(backupFolder.Text);
            copy.SourceFolder = ExpandPath(source.Text);
            copy.Profile = profile.Text.Trim();
            copy.Zip = zip.Checked && operation.SelectedIndex == 0;
            if (copy.Profile.Length > 0) ValidateProfile(copy.Profile);
            return copy;
        }
        void SetBusy(bool busy)
        {
            inputs.Enabled = !busy; operation.Enabled = !busy; start.Enabled = !busy; settings.Enabled = !busy;
            preview.Enabled = !busy && operation.SelectedIndex != 3; cancel.Enabled = busy;
            zip.Enabled = !busy && operation.SelectedIndex == 0;
            if (busy) { status.ForeColor = GuiTheme.Ink; status.Text = "Preparing"; detail.Text = "You can cancel at any time."; progress.Style = ProgressBarStyle.Marquee; }
            else { progress.Style = ProgressBarStyle.Continuous; }
        }
        void RequestCancel()
        {
            if (!runner.IsBusy) return;
            runner.Cancel(); cancel.Enabled = false; status.Text = "Cancelling";
            detail.Text = "Waiting for the current operation to finish safely.";
        }
        void StartOperation(bool previewOnly)
        {
            Config options;
            try { options = CaptureConfig(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Check settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            runner.Run(options, operation.SelectedIndex, previewOnly);
        }

        public void SetDetectedSource(string found) { source.Text = found; }

        // Shows a detected device's picture in the header; null shows the general backup picture (at
        // startup, and when detection finds nothing). The window and taskbar icon never change. A device
        // without a picture, or a build without the embedded pictures, keeps what's shown.
        public void ShowDevice(DeviceProfile device)
        {
            string picture = device == null ? "Backup" : device.IconName;
            if (picture == null) return;
            Image image = LoadImage(picture + "-256.png");
            if (image == null) return;
            Image old = deviceImage.Image;
            deviceImage.Image = image;
            deviceImage.AccessibleName = device == null ? "No device detected" : device.Name;
            if (old != null) old.Dispose();
        }

        // build-gui.cmd embeds the device pictures from the assets folder under their file names.
        static Stream Resource(string name) { return typeof(MainForm).Assembly.GetManifestResourceStream(name); }

        static Image LoadImage(string name)
        {
            try
            {
                using (Stream stream = Resource(name))
                    return stream == null ? null : new Bitmap(Image.FromStream(stream));
            }
            catch (Exception) { return null; }
        }
        public void PrepareOperation()
        {
            lock (queueLock) { pendingLog.Length = 0; pendingProgress = null; }
            SetBusy(true);
            log.Clear();
        }
        public void CompleteOperation(OperationResult result, bool previewOnly)
        {
            SetBusy(false);
            status.Text = result.Cancelled ? "Cancelled" : result.ExitCode != 0 ? "Needs attention" : previewOnly ? "Preview complete" : "Complete";
            status.ForeColor = result.ExitCode != 0 ? Color.FromArgb(175, 69, 47) : GuiTheme.Ink;
            detail.Text = result.Summary ?? "No files changed.";
            progress.Value = result.ExitCode == 0 && !result.Cancelled ? 100 : 0;
            if (closeAfterCancel) Close();
        }

        void EditSettings()
        {
            try
            {
                Config next = CaptureConfig();
                using (var dialog = new SettingsDialog(next))
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                SaveConfig(next);
                config = next; status.Text = "Settings saved"; detail.Text = "Saved to M8Backup.ini for both interfaces.";
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

}
