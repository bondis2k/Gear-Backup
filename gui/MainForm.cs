using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

partial class GearBackup
{
    sealed class MainForm : Form
    {
        Config config;
        readonly TextBox source = new TextBox(), backupFolder = new TextBox(), profile = new TextBox();
        readonly ComboBox operation = new ComboBox();
        // Only for full backups; not saved in the settings, so it's a per-run choice like the CLI's --zip.
        readonly CheckBox zip = new CheckBox { Text = "Also save as zip", AutoSize = true, Name = "ZipBackup" };
        readonly Button start = GuiTheme.Button("Back up", true), preview = GuiTheme.Button("Preview", false),
            cancel = GuiTheme.Button("Cancel", false), settings = GuiTheme.Button("Settings", false), detect = GuiTheme.Button("Detect device", false),
            about = GuiTheme.Button("About", false), help = GuiTheme.Button("Help", false);
        readonly RichTextBox log = new RichTextBox();
        readonly Label sourceCaption = GuiTheme.Label("", 9, GuiTheme.Muted), deviceName = GuiTheme.Label("No device detected", 10, GuiTheme.Muted),
            metrics = GuiTheme.Label("", 9, GuiTheme.Muted), counts = GuiTheme.Label("", 10, GuiTheme.Ink);
        readonly Button openFolder = GuiTheme.Button("Open backup folder", false);
        readonly Button dashboard = GuiTheme.Button("Dashboard", false), storage = GuiTheme.Button("Storage", false),
            profiles = GuiTheme.Button("Profiles", false), recipes = GuiTheme.Button("X100VI recipes", false);
        Button sourceBrowse;
        string outputFolder;
        readonly System.Diagnostics.Stopwatch elapsed = new System.Diagnostics.Stopwatch();
        OperationProgress latestProgress;
        readonly ToolTip hints = new ToolTip();
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
            Text = AppIdentity.Name;
            // The window and taskbar always use the general icon embedded in the exe (build-gui.cmd adds
            // assets\Backup.ico); only the header picture changes when a device is detected.
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            Font = new Font("Segoe UI", 10);
            ForeColor = GuiTheme.Ink;
            BackColor = GuiTheme.Background;
            ClientSize = new Size(1000, 800);
            MinimumSize = new Size(840, 720);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            if (GuiHeadless) { Opacity = 0; ShowInTaskbar = false; }
            source.Name = "SourceFolder"; backupFolder.Name = "BackupFolder"; profile.Name = "Profile";
            operation.Name = "Operation"; start.Name = "StartOperation"; preview.Name = "PreviewOperation";
            cancel.Name = "CancelOperation"; status.Name = "OperationStatus";
            about.Name = "AboutApplication";
            sourceCaption.Name = "SourceCaption"; deviceName.Name = "DetectedDevice";
            metrics.Name = "ProgressMetrics"; counts.Name = "CompletionCounts"; openFolder.Name = "OpenOutputFolder";
            detail.Name = "OperationDetail";
            counts.Visible = false; metrics.Visible = false;
            source.AccessibleName = "Source folder"; backupFolder.AccessibleName = "Backup location";
            profile.AccessibleName = "Backup profile"; operation.AccessibleName = "Operation"; log.AccessibleName = "Activity log";

            BuildLayout();
            Shown += delegate { GuiTheme.FitToScreen(this); };

            var interaction = new GuiInteraction(this);
            using (new OperationContext(interaction, CancellationToken.None).Activate())
                config = designPreview ? new Config { BackupFolder = @"D:\Music backups" } : LoadConfig();
            ShowDevice(null);
            source.Text = designPreview ? @"E:\" : config.SourceFolder;
            backupFolder.Text = config.BackupFolder; profile.Text = config.Profile;
            operation.SelectedIndex = (int)(config.Mode == BackupMode.Incremental ? GuiOperation.IncrementalBackup : GuiOperation.FullBackup);
            start.Click += delegate { StartOperation(false); };
            preview.Click += delegate { StartOperation(true); };
            cancel.Click += delegate { RequestCancel(); };
            detect.Click += delegate { runner.DetectSource(); };
            settings.Click += delegate { EditSettings(); };
            about.Click += delegate { using (var dialog = new AboutDialog()) dialog.ShowDialog(this); };
            help.Click += delegate { OpenManual(); };
            operation.SelectedIndexChanged += delegate { RefreshOperationControls(); };
            source.TextChanged += delegate { ShowDevice(null); };
            openFolder.Click += delegate { OpenOutputFolder(); };
            dashboard.Name = "OpenDashboard"; storage.Name = "OpenStorage"; profiles.Name = "OpenProfiles"; recipes.Name = "OpenRecipes";
            dashboard.Click += delegate { OpenTool(0); }; storage.Click += delegate { OpenTool(1); };
            profiles.Click += delegate { OpenTool(2); }; recipes.Click += delegate { OpenTool(3); };
            RefreshOperationControls();
            hints.SetToolTip(start, "Start the selected operation (Ctrl+Enter)");
            hints.SetToolTip(preview, "Preview without changing backup or restore files (Ctrl+P)");
            hints.SetToolTip(detect, "Detect a connected device (Ctrl+D)");
            hints.SetToolTip(settings, "Preferences (Ctrl+,)"); hints.SetToolTip(cancel, "Cancel safely (Esc)");
            hints.SetToolTip(help, "Open the manual (F1)");
            hints.SetToolTip(profile, "Leave blank to keep separate histories automatically for each card or folder.");
            // Closing while an operation runs cancels it first; CompleteOperation closes the window once it stops.
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (runner.IsBusy) { e.Cancel = true; closeAfterCancel = true; RequestCancel(); }
            };
            displayTimer.Tick += delegate { DrainDisplay(); };
            displayTimer.Start();
            FormClosed += delegate { displayTimer.Dispose(); hints.Dispose(); if (deviceImage.Image != null) deviceImage.Image.Dispose(); };
        }

        void BuildLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(30), ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            Controls.Add(viewport); viewport.Controls.Add(layout);
            layout.Dock = DockStyle.Top; layout.Height = ClientSize.Height;
            layout.MinimumSize = new Size(0, 680);
            viewport.Resize += delegate { layout.Height = Math.Max(layout.MinimumSize.Height, viewport.ClientSize.Height); };

            var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 18) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(deviceImage, 0, 0);
            var branding = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var title = GuiTheme.Label(AppIdentity.Name, 25, GuiTheme.Ink); title.Font = new Font("Segoe UI", 25, FontStyle.Bold);
            title.Margin = new Padding(0, 0, 0, 0);
            branding.Controls.Add(title);
            branding.Controls.Add(GuiTheme.Label("Your gear. Safely backed up.", 10, GuiTheme.Muted));
            branding.Controls.Add(deviceName);
            header.Controls.Add(branding, 1, 0);
            var headerActions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
            headerActions.Controls.Add(help); headerActions.Controls.Add(about); headerActions.Controls.Add(settings);
            header.Controls.Add(headerActions, 2, 0);
            layout.Controls.Add(header, 0, 0);

            inputs.Dock = DockStyle.Top; inputs.AutoSize = true; inputs.BackColor = Color.White; inputs.Padding = new Padding(20, 14, 20, 14);
            inputs.Margin = new Padding(0, 0, 0, 0);
            var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 6 };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 0; i < 6; i++) fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            sourceBrowse = AddPath(fields, 0, sourceCaption, source, false); sourceBrowse.AccessibleName = "Browse source or restore destination";
            fields.Controls.Add(detect, 2, 1); detect.MinimumSize = new Size(110, 28); detect.Padding = Padding.Empty;
            AddPath(fields, 2, GuiTheme.Label("Backup location", 9, GuiTheme.Muted), backupFolder, true);
            var profileLabel = GuiTheme.Label("Profile  ·  leave blank for automatic card identity", 9, GuiTheme.Muted);
            fields.Controls.Add(profileLabel, 0, 4); fields.SetColumnSpan(profileLabel, 3);
            profile.Dock = DockStyle.Fill; fields.Controls.Add(profile, 0, 5); fields.SetColumnSpan(profile, 3);
            inputs.Controls.Add(fields); layout.Controls.Add(inputs, 0, 1);

            var actions = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 5, Padding = new Padding(0, 14, 0, 14), Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            operation.DropDownStyle = ComboBoxStyle.DropDownList; operation.Dock = DockStyle.Fill;
            // In the order of GuiOperation: the selected index is the operation.
            operation.Items.AddRange(new object[] { "Full backup", "Incremental backup", "Restore files", "Check backups", "Back up X100VI settings" });
            operation.SelectedIndex = 0; operation.Margin = new Padding(0, 4, 24, 0);
            zip.Margin = new Padding(0, 8, 20, 0); zip.ForeColor = GuiTheme.Ink;
            actions.Controls.Add(operation, 0, 0); actions.Controls.Add(zip, 1, 0);
            actions.Controls.Add(preview, 2, 0); actions.Controls.Add(start, 3, 0); actions.Controls.Add(cancel, 4, 0);
            cancel.Enabled = false; layout.Controls.Add(actions, 0, 2);

            var state = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 5, Margin = new Padding(0, 0, 0, 14) };
            for (int i = 0; i < 5; i++) state.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var resultHeader = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            resultHeader.Controls.Add(status); openFolder.Visible = false; resultHeader.Controls.Add(openFolder);
            state.Controls.Add(resultHeader, 0, 0);
            state.Controls.Add(counts, 0, 1); detail.AutoSize = false; detail.Height = 28; detail.Dock = DockStyle.Top; detail.AutoEllipsis = true;
            state.Controls.Add(detail, 0, 2); state.Controls.Add(metrics, 0, 3);
            progress.Dock = DockStyle.Top; progress.Height = 6; progress.Margin = Padding.Empty; state.Controls.Add(progress, 0, 4);
            layout.Controls.Add(state, 0, 3);

            var activity = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16), RowCount = 3 };
            activity.RowStyles.Add(new RowStyle(SizeType.AutoSize)); activity.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); activity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 12) };
            tools.Controls.Add(dashboard); tools.Controls.Add(storage); tools.Controls.Add(profiles); tools.Controls.Add(recipes); activity.Controls.Add(tools, 0, 0);
            activity.Margin = Padding.Empty; activity.Controls.Add(GuiTheme.Label("ACTIVITY", 9, GuiTheme.Muted), 0, 1);
            log.Dock = DockStyle.Fill; log.BorderStyle = BorderStyle.None; log.ReadOnly = true;
            log.BackColor = Color.White; log.ForeColor = GuiTheme.Ink; log.Font = new Font("Consolas", 9);
            log.DetectUrls = false; log.WordWrap = false; activity.Controls.Add(log, 0, 2); layout.Controls.Add(activity, 0, 4);

        }

        Button AddPath(TableLayoutPanel fields, int row, Label label, TextBox text, bool destination)
        {
            fields.Controls.Add(label, 0, row); fields.SetColumnSpan(label, 3);
            text.Dock = DockStyle.Fill; text.Margin = new Padding(0, 0, 8, 0); fields.Controls.Add(text, 0, row + 1);
            var browse = GuiTheme.Button("Browse…", false); browse.MinimumSize = new Size(100, 28); browse.Padding = Padding.Empty;
            fields.Controls.Add(browse, 1, row + 1);
            if (destination) fields.SetColumnSpan(text, 1);
            browse.AccessibleName = destination ? "Browse backup location" : "Browse source folder";
            browse.Click += delegate { BrowseFolder(this, text, destination || Selected == GuiOperation.Restore,
                destination ? "Choose the backup location" : Selected == GuiOperation.Restore ? "Choose the restore destination" : "Choose the card or source folder"); };
            return browse;
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
            RefreshProgressMetrics();
            if (value == null) return;
            if (runner.CancellationRequested) return;
            latestProgress = value; RefreshProgressMetrics();
            status.Text = value.Stage;
            detail.Text = value.OverallOnly ? (value.TotalFiles > 0 ? value.Files + " / " + value.TotalFiles + " files" : "Working…") : value.Path;
            if (value.TotalBytes > 0)
            {
                progress.Style = ProgressBarStyle.Continuous;
                progress.Value = (int)Math.Min(100, Math.Max(0, value.Bytes * 100.0 / value.TotalBytes));
            }
            else progress.Style = ProgressBarStyle.Marquee;
        }

        Config CaptureConfig(bool forOperation = false)
        {
            // Start from every saved setting, so settings without a field in this window are kept.
            Config copy = config.Clone();
            copy.BackupFolder = string.IsNullOrWhiteSpace(backupFolder.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) : ExpandPath(backupFolder.Text);
            copy.SourceFolder = forOperation && (Selected == GuiOperation.Check || Selected == GuiOperation.CameraSettings) ? "" : ExpandPath(source.Text);
            copy.Profile = profile.Text.Trim();
            copy.Zip = zip.Checked && ZipApplies;
            if (copy.Profile.Length > 0) ValidateProfile(copy.Profile);
            return copy;
        }
        void RefreshOperationControls()
        {
            bool checking = Selected == GuiOperation.Check, restoring = Selected == GuiOperation.Restore, cameraSettings = Selected == GuiOperation.CameraSettings;
            sourceCaption.Text = cameraSettings ? "X100VI · USB RAW CONV./BACKUP RESTORE · C1–C7 and shooting settings" :
                checking ? "Source not needed · select backups from the backup location" :
                restoring ? "Restore destination · folder or connected card" : "Source folder · leave blank to detect a device";
            source.AccessibleName = restoring ? "Restore destination" : "Source folder";
            sourceBrowse.AccessibleName = restoring ? "Browse restore destination" : "Browse source folder";
            source.Enabled = sourceBrowse.Enabled = detect.Enabled = !checking && !cameraSettings && !runner.IsBusy;
            start.Text = restoring ? "Restore" : checking ? "Check" : "Back up";
            preview.Enabled = !checking && !runner.IsBusy;
            zip.Enabled = ZipApplies && !runner.IsBusy;
        }
        public void SelectCameraSettings() { operation.SelectedIndex = (int)GuiOperation.CameraSettings; }
        GuiOperation Selected { get { return (GuiOperation)operation.SelectedIndex; } }
        // A zip is made of a full backup, including a camera-settings snapshot.
        bool ZipApplies { get { return Selected == GuiOperation.FullBackup || Selected == GuiOperation.CameraSettings; } }
        void RefreshProgressMetrics()
        {
            if (!elapsed.IsRunning) return;
            metrics.Visible = true;
            string text = "Elapsed " + FormatTime(elapsed.Elapsed);
            if (latestProgress != null && latestProgress.TotalBytes > 0)
            {
                double percent = Math.Min(100, Math.Max(0, latestProgress.Bytes * 100.0 / latestProgress.TotalBytes));
                text = percent.ToString("0") + "% · " + FormatSize(latestProgress.Bytes) + " / " + FormatSize(latestProgress.TotalBytes) + " · " + text;
            }
            metrics.Text = text;
            progress.AccessibleName = "Operation progress"; progress.AccessibleDescription = text;
        }
        void OpenOutputFolder()
        {
            if (string.IsNullOrEmpty(outputFolder) || !Directory.Exists(outputFolder)) return;
            OpenLocalItem(outputFolder);
        }
        void OpenManual()
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string manual = Path.Combine(root, "Manual.html");
            OpenLocalItem(manual);
        }
        void OpenLocalItem(string path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1) { OpenManual(); return true; }
            if (keyData == Keys.Escape && runner.IsBusy) { RequestCancel(); return true; }
            if (!runner.IsBusy)
            {
                if (keyData == (Keys.Control | Keys.Enter)) { start.PerformClick(); return true; }
                if (keyData == (Keys.Control | Keys.P)) { preview.PerformClick(); return true; }
                if (keyData == (Keys.Control | Keys.D)) { detect.PerformClick(); return true; }
                if (keyData == (Keys.Control | Keys.Oemcomma)) { settings.PerformClick(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        void SetBusy(bool busy)
        {
            inputs.Enabled = !busy; operation.Enabled = !busy; start.Enabled = !busy; settings.Enabled = !busy;
            preview.Enabled = !busy && Selected != GuiOperation.Check; cancel.Enabled = busy;
            zip.Enabled = !busy && ZipApplies;
            openFolder.Enabled = !busy;
            dashboard.Enabled = storage.Enabled = profiles.Enabled = recipes.Enabled = !busy;
            if (busy) { status.ForeColor = GuiTheme.Ink; status.Text = "Preparing"; detail.Text = "You can cancel at any time."; progress.Style = ProgressBarStyle.Marquee; }
            else { progress.Style = ProgressBarStyle.Continuous; }
            if (!busy) RefreshOperationControls();
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
            try { options = CaptureConfig(true); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Check settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            runner.Run(options, Selected, previewOnly);
        }

        public void SetDetectedSource(string found) { source.Text = found; }

        // Shows a detected device's picture in the header; null shows the general backup picture (at
        // startup, and when detection finds nothing). The window and taskbar icon never change. A device
        // without a picture, or a build without the embedded pictures, keeps what's shown.
        public void ShowDevice(DeviceProfile device, string mode = null)
        {
            deviceName.Text = device == null ? "No device detected" : device.Name + " · detected" +
                (string.IsNullOrEmpty(mode) ? "" : " · " + mode);
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
            counts.Text = ""; counts.Visible = false; openFolder.Visible = false; outputFolder = null; latestProgress = null; elapsed.Restart();
            log.Clear();
        }
        public void CompleteOperation(OperationResult result, bool previewOnly)
        {
            elapsed.Stop();
            SetBusy(false);
            status.Text = result.Cancelled ? "Cancelled" : result.ExitCode != 0 ? "Needs attention" : previewOnly ? "Preview complete" : "Complete";
            status.ForeColor = result.ExitCode != 0 ? Color.FromArgb(175, 69, 47) : GuiTheme.Ink;
            detail.Text = result.Summary ?? "No files changed.";
            counts.Text = result.Counts == null ? "" : string.Format("Copied {0:N0}   ·   Unchanged {1:N0}   ·   Removed {2:N0}   ·   Failed {3:N0}",
                result.Counts.Copied, result.Counts.Unchanged, result.Counts.Removed, result.Counts.Failed);
            counts.Visible = result.Counts != null;
            outputFolder = previewOnly ? null : result.OutputFolder;
            openFolder.Text = Selected == GuiOperation.Restore ? "Open restore folder" : "Open backup folder";
            openFolder.Visible = !string.IsNullOrEmpty(outputFolder) && Directory.Exists(outputFolder);
            metrics.Text = "Elapsed " + FormatTime(result.Elapsed > TimeSpan.Zero ? result.Elapsed : elapsed.Elapsed);
            metrics.Visible = true;
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
                config = next; status.Text = "Settings saved"; detail.Text = "Saved to " + AppIdentity.ConfigFileName + " for both interfaces.";
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        void OpenTool(int choice)
        {
            try
            {
                Config options = CaptureConfig();
                using (Form dialog = choice < 2 ? (Form)new CatalogDialog(this, options, choice == 1) : choice == 2 ?
                    (Form)new ProfileSettingsDialog(this, options) : new RecipeViewerDialog(this,
                        outputFolder != null && File.Exists(Path.Combine(outputFolder, "C1.xml")) ? outputFolder : null))
                    dialog.ShowDialog(this);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open tool", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

}
