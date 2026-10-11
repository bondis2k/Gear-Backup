using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using static SettingsLocation;
using static PathSafety;
using static AppSettings;
using static ConsoleUi;
using static ProfileSettings;
using static Workflow;
using static FolderPicker;
using static GearGui;

internal sealed partial class MainForm : Form
{
    Config config;
    readonly TextBox source = new TextBox(), backupFolder = new TextBox(), profile = new TextBox();
    GuiOperation selectedOperation = GuiOperation.FullBackup;
    // Only for full backups; not saved in the settings, so it's a per-run choice like the CLI's --zip.
    readonly CheckBox zip = new CheckBox { Text = "Also save as zip", AutoSize = true, Name = "ZipBackup" };
    readonly Button start = GuiTheme.Button("Back up", true), preview = GuiTheme.Button("Preview", false),
        cancel = GuiTheme.Button("Cancel", false), settings = GuiTheme.GhostButton("Settings"), detect = GuiTheme.Button("Detect device", true),
        about = GuiTheme.GhostButton("About"), help = GuiTheme.GhostButton("Help");
    readonly RichTextBox log = new RichTextBox();
    readonly Label sourceCaption = GuiTheme.Label("", 9, GuiTheme.Muted), deviceName = GuiTheme.Label("No device detected", 10, GuiTheme.Muted),
        metrics = GuiTheme.Label("", 9, GuiTheme.Muted), counts = GuiTheme.Label("", 10, GuiTheme.Ink);
    readonly Label deviceConnection = GuiTheme.Label("", 10, GuiTheme.Muted);
    readonly Button openFolder = GuiTheme.Button("Open backup folder", false);
    readonly Button dashboard = GuiTheme.GhostButton("Dashboard"), storage = GuiTheme.GhostButton("Storage"),
        profiles = GuiTheme.GhostButton("Device && backup settings");
    Button sourceBrowse;
    string outputFolder;
    readonly System.Diagnostics.Stopwatch elapsed = new System.Diagnostics.Stopwatch();
    OperationProgress latestProgress;
    bool operationProgressActive;
    readonly ToolTip hints = new ToolTip();
    readonly Label status = new Label { Text = "Ready", AutoSize = true, ForeColor = GuiTheme.Ink, Font = GuiTheme.Heading(16), Margin = new Padding(0, 0, 12, 4) }, detail = GuiTheme.Label("Choose your source and an operation to begin.", 10, GuiTheme.Muted);
    readonly GuiProgressBar progress = new GuiProgressBar();
    // The detected device's picture on the Device tab; see ShowDevice.
    readonly PictureBox deviceImage = new PictureBox { Name = "DeviceImage", Size = new Size(88, 88),
        SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 4, 16, 0) };
    readonly CardPanel inputs = new CardPanel();
    readonly Button optionsToggle = GuiTheme.GhostButton("More options ▾");
    readonly Label destinationSummary = new Label { Name = "BackupDestinationSummary", AutoEllipsis = true, Dock = DockStyle.Top, Height = 28, ForeColor = GuiTheme.Muted };
    TableLayoutPanel optionalFields;
    Label profileLabel;
    FieldPanel profileField;
    bool optionsExpanded;
    DeviceProfile displayedDevice;
    // Worker threads add log text and progress here; the display timer moves them into the window every
    // 150 ms, so a fast copy doesn't flood the UI thread with updates.
    readonly object queueLock = new object();
    readonly StringBuilder pendingLog = new StringBuilder();
    OperationProgress pendingProgress;
    readonly System.Windows.Forms.Timer displayTimer = new System.Windows.Forms.Timer { Interval = 150 };
    readonly GuiOperationRunner runner;
    bool closeAfterCancel;

    public MainForm(bool designPreview, Config startupOptions = null)
    {
        runner = new GuiOperationRunner(this);
        Text = AppIdentity.Name;
        // The window and taskbar always use the general icon embedded in the exe (build-gui.cmd adds
        // assets\Backup.ico); only the header picture changes when a device is detected.
        Font = new Font("Segoe UI", 10);
        ForeColor = GuiTheme.Ink;
        BackColor = GuiTheme.Desktop;
        ClientSize = new Size(1000, 720);
        MinimumSize = new Size(840, 620);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        if (GuiHeadless) { Opacity = 0; ShowInTaskbar = false; }
        source.Name = "SourceFolder"; backupFolder.Name = "BackupFolder"; profile.Name = "Profile";
        start.Name = "StartOperation"; preview.Name = "PreviewOperation";
        cancel.Name = "CancelOperation"; status.Name = "OperationStatus";
        about.Name = "AboutApplication";
        settings.Name = "OpenSettings";
        sourceCaption.Name = "SourceCaption"; deviceName.Name = "DetectedDevice";
        deviceConnection.Name = "DeviceConnectionMode";
        deviceName.MaximumSize = new Size(520, 0); deviceConnection.MaximumSize = new Size(520, 0);
        metrics.Name = "ProgressMetrics"; counts.Name = "CompletionCounts"; openFolder.Name = "OpenOutputFolder";
        detail.Name = "OperationDetail";
        counts.Visible = false; metrics.Visible = false;
        source.AccessibleName = "Source folder"; backupFolder.AccessibleName = "Backup location";
        profile.AccessibleName = "History name"; log.AccessibleName = "Activity log";

        BuildLayout();
        Shown += delegate { GuiTheme.FitToScreen(this); };

        var interaction = new GuiInteraction(this);
        using (new OperationContext(interaction, CancellationToken.None).Activate())
            config = startupOptions ?? (designPreview ? new Config { BackupFolder = @"C:\Users\YourName\Backup" } : LoadConfig());
        // Opens where it was last closed; previews and tests always open centred and save nothing.
        if (!designPreview && !GuiHeadless)
        {
            string settingsFolder = config.SettingsDirectory ?? DefaultSettingsDirectory();
            WindowPlacement.Restore(this, settingsFolder);
            FormClosed += delegate { WindowPlacement.Save(this, settingsFolder); };
        }
        GuiTheme.ApplyTheme(config.Theme, this);
        ShowDevice(null);
        source.Text = designPreview ? "" : config.SourceFolder;
        backupFolder.Text = config.BackupFolder; profile.Text = config.Profile;
        selectedOperation = config.Mode == BackupMode.Incremental ? GuiOperation.IncrementalBackup : GuiOperation.FullBackup;
        InitializeWorkflow();
        start.Click += delegate { StartOperation(false); };
        preview.Click += delegate { StartOperation(true); };
        cancel.Click += delegate { RequestCancel(); };
        detect.Click += delegate { runner.DetectSource(); };
        settings.Click += delegate { EditSettings(); };
        about.Click += delegate { using (var dialog = new AboutDialog()) dialog.ShowDialog(this); };
        help.Click += delegate { OpenManual(); };
        source.TextChanged += delegate { backupSetSource = null; ShowDevice(null); RefreshDestinationSummary(); RefreshBackupSets(); };
        restoreDestination.TextChanged += delegate { RefreshDestinationSummary(); };
        backupFolder.TextChanged += delegate { RefreshDestinationSummary(); RefreshBackupSets(); RefreshRestoreBackups(); };
        profile.TextChanged += delegate { RefreshDestinationSummary(); RefreshBackupSets(); };
        optionsToggle.Click += delegate { SetOptionsExpanded(!optionsExpanded); };
        openFolder.Click += delegate { OpenOutputFolder(); };
        dashboard.Name = "OpenDashboard"; storage.Name = "OpenStorage"; profiles.Name = "OpenProfiles";
        dashboard.Click += delegate { OpenTool(0); }; storage.Click += delegate { OpenTool(1); };
        profiles.Click += delegate { OpenTool(2); };
        RefreshOperationControls();
        // Starts collapsed; the summary line under the buttons shows a set source folder, the backup
        // location and the profile. Choosing Restore opens it, since a destination must be chosen.
        SetOptionsExpanded(false);
        detail.Text = "Connect your device, click Detect device, then Back up.";
        hints.SetToolTip(start, "Start the selected operation (Ctrl+Enter)");
        hints.SetToolTip(preview, "Preview without changing backup or restore files (Ctrl+P)");
        hints.SetToolTip(detect, "Detect a connected device (Ctrl+D)");
        hints.SetToolTip(settings, "Preferences (Ctrl+,)"); hints.SetToolTip(cancel, "Cancel safely (Esc)");
        hints.SetToolTip(help, "Open the manual (F1)");
        hints.SetToolTip(profile, "Leave blank: each card, folder or device keeps its own history automatically.\nA name keeps backups in a separate history under that name, e.g. for a second project on the same card.");
        // Closing while an operation runs cancels it first; CompleteOperation closes the window once it stops.
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (runner.IsBusy) { e.Cancel = true; closeAfterCancel = true; RequestCancel(); }
        };
        displayTimer.Tick += delegate { DrainDisplay(); };
        displayTimer.Start();
        Shown += delegate { RefreshRestoreBackups(); };
        FormClosed += delegate { displayTimer.Dispose(); hints.Dispose(); if (deviceImage.Image != null) deviceImage.Image.Dispose(); };
    }

    void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(30), ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        Controls.Add(viewport); viewport.Controls.Add(layout);
        layout.Dock = DockStyle.Top; layout.Height = ClientSize.Height;
        layout.MinimumSize = new Size(0, 580);
        bool sizingLayout = false;
        Action sizeLayout = delegate {
            if (sizingLayout) return;
            sizingLayout = true;
            try { layout.Height = Math.Max(layout.MinimumSize.Height, viewport.ClientSize.Height); }
            finally { sizingLayout = false; }
        };
        viewport.Resize += delegate { sizeLayout(); };
        viewport.Layout += delegate { sizeLayout(); };

        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 18), BackColor = GuiTheme.Background, Padding = new Padding(12) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var branding = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var title = GuiTheme.Label(AppIdentity.Name, 22, GuiTheme.Accent);
        title.Font = GuiTheme.Heading(22);
        title.Margin = new Padding(0, 0, 0, 0);
        branding.Controls.Add(title);
        branding.Controls.Add(GuiTheme.Label("Your gear. Safely backed up.", 10, GuiTheme.Muted));
        deviceName.Margin = new Padding(0, 0, 0, 0);
        header.Controls.Add(branding, 1, 0);
        var headerActions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        headerActions.Controls.Add(help); headerActions.Controls.Add(about); headerActions.Controls.Add(settings);
        header.Controls.Add(headerActions, 2, 0);
        layout.Controls.Add(header, 0, 0);

        inputs.Dock = DockStyle.Top; inputs.AutoSize = true; inputs.Padding = new Padding(22, 16, 22, 18);
        inputs.Margin = new Padding(0, 0, 0, 0);
        var inputLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputs.Controls.Add(inputLayout);
        optionalFields = new TableLayoutPanel { Name = "OptionalBackupFields", Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 7, Visible = false };
        var fields = optionalFields;
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < 6; i++) fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sourceBrowse = AddPath(fields, 0, sourceCaption, source, false); sourceBrowse.AccessibleName = "Browse source or restore destination";
        AddPath(fields, 2, FieldLabel("Backup location"), backupFolder, true);
        profileLabel = FieldLabel("History name (optional)  ·  groups backups separately; leave blank to use the device's own");
        fields.Controls.Add(profileLabel, 0, 4); fields.SetColumnSpan(profileLabel, 3);
        profileField = new FieldPanel(profile) { Dock = DockStyle.Fill, Margin = Padding.Empty };
        fields.Controls.Add(profileField, 0, 5); fields.SetColumnSpan(profileField, 3);
        zip.Margin = new Padding(0, 12, 0, 0); fields.Controls.Add(zip, 0, 6); fields.SetColumnSpan(zip, 3);
        layout.Controls.Add(BuildWorkflowTabs(), 0, 1);

        var actions = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 5, Padding = new Padding(0, 0, 0, 12), Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        for (int i = 0; i < 3; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        zip.ForeColor = GuiTheme.Ink;
        actions.Controls.Add(BuildOperationSelectors(), 0, 0);
        actions.Controls.Add(BuildBackupSetField(), 1, 0);
        actions.Controls.Add(BuildRestoreBackupField(), 1, 0);
        preview.Anchor = start.Anchor = AnchorStyles.Bottom;
        actions.Controls.Add(preview, 2, 0); actions.Controls.Add(start, 3, 0);
        cancel.Enabled = false; cancel.Visible = false;
        inputLayout.Controls.Add(actions, 0, 0);
        inputLayout.Controls.Add(BuildRestoreDetails(), 0, 1);
        inputLayout.Controls.Add(destinationSummary, 0, 2);
        optionsToggle.Name = "ToggleBackupOptions"; optionsToggle.Margin = new Padding(0, 4, 0, 4);
        inputLayout.Controls.Add(optionsToggle, 0, 3);
        inputLayout.Controls.Add(fields, 0, 4);

        var state = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 5, Margin = new Padding(0, 0, 0, 14), BackColor = GuiTheme.Background, Padding = new Padding(12) };
        for (int i = 0; i < 5; i++) state.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var resultHeader = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        resultHeader.Controls.Add(status); openFolder.Visible = false; resultHeader.Controls.Add(openFolder); resultHeader.Controls.Add(cancel);
        state.Controls.Add(resultHeader, 0, 0);
        state.Controls.Add(counts, 0, 1); detail.AutoSize = false; detail.Height = 28; detail.Dock = DockStyle.Top; detail.AutoEllipsis = true;
        state.Controls.Add(detail, 0, 2); state.Controls.Add(metrics, 0, 3);
        progress.Dock = DockStyle.Top; progress.Height = 8; progress.Margin = new Padding(0, 4, 0, 0); state.Controls.Add(progress, 0, 4);
        layout.Controls.Add(state, 0, 2);

        // The activity card: a heading with the tools on the right, then the log.
        var activityCard = new CardPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(18, 12, 12, 14) };
        var activity = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = GuiTheme.Surface, RowCount = 3, ColumnCount = 2, Margin = Padding.Empty };
        activity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        activity.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        activity.RowStyles.Add(new RowStyle(SizeType.AutoSize)); activity.RowStyles.Add(new RowStyle(SizeType.Absolute, 9)); activity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var activityTitle = GuiTheme.SectionTitle("Activity");
        activityTitle.Anchor = AnchorStyles.Left;
        activity.Controls.Add(activityTitle, 0, 0);
        var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = GuiTheme.Surface, Anchor = AnchorStyles.Right };
        tools.Controls.Add(dashboard); tools.Controls.Add(storage); tools.Controls.Add(profiles);
        activity.Controls.Add(tools, 1, 0);
        var rule = new Panel { Height = 1, Dock = DockStyle.Bottom, BackColor = GuiTheme.Border, Margin = new Padding(0, 0, 6, 8) };
        activity.Controls.Add(rule, 0, 1); activity.SetColumnSpan(rule, 2);
        log.Dock = DockStyle.Fill; log.BorderStyle = BorderStyle.None; log.ReadOnly = true;
        log.BackColor = GuiTheme.Surface; log.ForeColor = GuiTheme.Ink; log.Font = new Font("Consolas", 9.5f);
        log.DetectUrls = false; log.WordWrap = false; activity.Controls.Add(log, 0, 2); activity.SetColumnSpan(log, 2);
        activityCard.Controls.Add(activity); layout.Controls.Add(activityCard, 0, 3);
        RefreshWorkflowLayout();

    }

    // A small caption above a field.
    static Label FieldLabel(string text)
    {
        var label = GuiTheme.Label(text, 9, GuiTheme.Muted);
        label.Margin = new Padding(0, 8, 0, 6);
        return label;
    }

    Button AddPath(TableLayoutPanel fields, int row, Label label, TextBox text, bool destination)
    {
        label.Margin = new Padding(0, row == 0 ? 0 : 10, 0, 6);
        fields.Controls.Add(label, 0, row); fields.SetColumnSpan(label, 3);
        var field = new FieldPanel(text) { Dock = DockStyle.Fill };
        fields.Controls.Add(field, 0, row + 1);
        if (text == source) sourceField = field;
        var browse = GuiTheme.Button("Browse…", false); browse.MinimumSize = new Size(110, 36);
        fields.Controls.Add(browse, 1, row + 1);
        browse.AccessibleName = destination ? "Browse backup location" : "Browse source folder";
        browse.Click += delegate { BrowseFolder(this, text == source && Selected == GuiOperation.Restore ? restoreDestination : text, destination || Selected == GuiOperation.Restore,
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
        if (value.OverallOnly && value.Stage.IsFileDetail) return;
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
        if (value == null || !operationProgressActive) return;
        if (runner.CancellationRequested) return;
        latestProgress = value; RefreshProgressMetrics();
        status.Text = value.Stage.ToString();
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
            ? DefaultBackupFolder() : ExpandPath(backupFolder.Text);
        copy.SourceFolder = forOperation && (Selected == GuiOperation.Check || Selected == GuiOperation.CameraSettings) ? "" :
            ExpandPath(forOperation && Selected == GuiOperation.Restore ? restoreDestination.Text : source.Text);
        copy.Profile = profile.Text.Trim();
        copy.Zip = zip.Checked && ZipApplies;
        if (copy.Profile.Length > 0) ValidateProfile(copy.Profile);
        if (forOperation && Selected == GuiOperation.Restore) copy.RestoreSelection = CaptureRestoreSelection();
        if (forOperation && Selected == GuiOperation.FullBackup) copy.FullBackupName = BackupSetNames.Validate(fullBackupName.Text);
        if (forOperation && Selected == GuiOperation.IncrementalBackup)
        {
            copy.IncrementalBackupName = BackupSetNames.Validate(incrementalName.Text);
            var item = incrementalBackup.SelectedItem as BackupSetItem;
            if (loadingBackupSets || item == null) throw new IOException("Choose a full backup to update. Detect the device or choose a source first.");
            copy.IncrementalBackup = item.Path;
        }
        return copy;
    }
    void RefreshOperationControls()
    {
        RefreshBackupSetField();
        RefreshRestoreControls();
        bool checking = Selected == GuiOperation.Check, restoring = Selected == GuiOperation.Restore, cameraSettings = Selected == GuiOperation.CameraSettings;
        sourceCaption.Text = cameraSettings ? "X100VI · USB RAW CONV./BACKUP RESTORE · C1–C7 and shooting settings" :
            checking ? "Source not needed · select backups from the backup location" :
            restoring ? "Restore destination · folder or connected card" : "Source folder · leave blank to detect a device";
        source.AccessibleName = "Source folder";
        sourceBrowse.AccessibleName = restoring ? "Browse restore destination" : "Browse source folder";
        source.Enabled = sourceBrowse.Enabled = !checking && !cameraSettings && !runner.IsBusy;
        restoreDestination.Enabled = !runner.IsBusy;
        detect.Enabled = !runner.IsBusy;
        start.Text = restoring ? "Restore" : checking ? "Check" : "Back up";
        preview.Enabled = !checking && !runner.IsBusy;
        zip.Enabled = ZipApplies && !runner.IsBusy;
        zip.Visible = !restoring && !checking;
        optionsToggle.Visible = profileLabel.Visible = profileField.Visible = !restoring && !checking;
        sourceBrowse.Visible = !checking;
        RefreshSourceField(restoring);
        if (restoring || checking) SetOptionsExpanded(true);
        else if (!checking) SetOptionsExpanded(backupOptionsExpanded);
        RefreshDestinationSummary();
        if (!elapsed.IsRunning && status.Text == "Ready")
            detail.Text = restoring ? "Detect a device, then choose its backup and a restore destination." :
                checking ? "Check saved backups without connecting a device." :
                cameraSettings ? "Connect X100VI in settings mode, then Back up." :
                "Detect your device on the Device tab, then open Backup.";
        RefreshRestoreActionState();
    }
    void SetOptionsExpanded(bool expanded)
    {
        if (Selected != GuiOperation.Restore && Selected != GuiOperation.Check) backupOptionsExpanded = expanded;
        optionsExpanded = expanded;
        optionalFields.Visible = expanded;
        optionsToggle.Text = expanded ? "Fewer options ▴" : "More options ▾";
        optionsToggle.AccessibleDescription = expanded ? "Source, destination, history name and zip options expanded" : "Show source, destination, history name and zip options";
        RefreshWorkflowLayout();
    }
    void RefreshDestinationSummary()
    {
        if (config == null) return;
        string folder = string.IsNullOrWhiteSpace(backupFolder.Text) ? DefaultBackupFolder() : backupFolder.Text;
        try
        {
            Config options = config.Clone(); options.BackupFolder = ExpandPath(folder);
            string name = profile.Text.Trim();
            DeviceProfile device = Selected == GuiOperation.CameraSettings ? Devices.X100VI : displayedDevice;
            if (device != null || name.Length > 0)
                folder = ResolveProfileSettings(options, device == null ? options.Device : device.Id, name).BackupFolder;
        }
        catch (Exception) { /* Keep partially edited paths visible; validation happens when starting. */ }
        // A source folder set in More options replaces device detection, so it's shown even while collapsed.
        string sourceFolder = (Selected == GuiOperation.Restore ? restoreDestination : source).Text.Trim();
        string sourcePart = sourceFolder.Length == 0 || Selected == GuiOperation.Check || Selected == GuiOperation.CameraSettings ? ""
            : (Selected == GuiOperation.Restore ? "Restore to: " : "Source: ") + sourceFolder + "   ·   ";
        destinationSummary.Text = sourcePart + "Backup location: " + folder + (profile.Text.Trim().Length > 0 ? "   ·   History name: " + profile.Text.Trim() : "");
        hints.SetToolTip(destinationSummary, destinationSummary.Text + "\nChange folders or the history name in More options. Device & backup settings can override the location.");
    }
    public void SelectCameraSettings()
    {
        if (!cameraSettingsAvailable) return;
        bool previous = synchronizingNavigation; synchronizingNavigation = true;
        try { SelectOperation(GuiOperation.CameraSettings, false); backupMode.SelectedIndex = 2; }
        finally { synchronizingNavigation = previous; }
    }
    GuiOperation Selected { get { return selectedOperation; } }
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
        string manual = ManualPath(root, config.Theme);
        OpenLocalItem(manual);
    }
    internal static string ManualPath(string root, string theme)
    {
        string name = theme == "windows" ? "Manual-windows.html" : theme == "original-dark" ? "Manual-original-dark.html" : theme == "retro" ? "Manual-retro.html" : "Manual-original.html";
        string path = Path.Combine(root, name);
        return File.Exists(path) ? path : Path.Combine(root, "Manual.html");
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
            if (keyData == (Keys.Control | Keys.Enter)) { if (workflowTabs.SelectedIndex == 0) workflowTabs.SelectedIndex = 1; start.PerformClick(); return true; }
            if (keyData == (Keys.Control | Keys.P)) { if (workflowTabs.SelectedIndex == 0) workflowTabs.SelectedIndex = 1; preview.PerformClick(); return true; }
            if (keyData == (Keys.Control | Keys.D)) { workflowTabs.SelectedIndex = 0; detect.PerformClick(); return true; }
            if (keyData == (Keys.Control | Keys.Oemcomma)) { settings.PerformClick(); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    void SetBusy(bool busy)
    {
        optionalFields.Enabled = !busy; optionsToggle.Enabled = !busy; detect.Enabled = !busy;
        start.Enabled = !busy; settings.Enabled = !busy;
        workflowTabs.Enabled = !busy; backupMode.Enabled = restoreMode.Enabled = !busy;
        backupSetField.Enabled = !busy;
        restoreBackupField.Enabled = restoreDetails.Enabled = !busy;
        preview.Enabled = !busy && Selected != GuiOperation.Check; cancel.Enabled = busy;
        cancel.Visible = busy;
        zip.Enabled = !busy && ZipApplies;
        openFolder.Enabled = !busy;
        dashboard.Enabled = storage.Enabled = profiles.Enabled = !busy;
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

    // Shows a detected device's picture on the Device tab; null shows the general backup picture (at
    // startup, and when detection finds nothing). The window and taskbar icon never change. A device
    // without a picture, or a build without the embedded pictures, keeps what's shown.
    public void ShowDevice(DeviceProfile device, string mode = null, bool cameraSettingsMode = false)
    {
        displayedDevice = device;
        restoreContextKey = null;
        RefreshRestoreBackups();
        UpdateCameraSettingsAvailability(device == Devices.X100VI && cameraSettingsMode);
        deviceDetails.Text = device == null ? "Connect your device and click Detect device. You can also choose a local source folder on the Backup tab." :
            "Click Detect device to refresh storage and connection details.";
        RefreshDestinationSummary();
        deviceName.Text = device == null ? "No device detected" : device.Name + " · detected";
        deviceName.Font = device == null ? GuiTheme.Font(10) : GuiTheme.Heading(17);
        deviceName.ForeColor = device == null ? GuiTheme.Muted : GuiTheme.AccentDark;
        deviceName.BackColor = device == null ? Color.Transparent : GuiTheme.AccentSoft;
        deviceName.Padding = device == null ? Padding.Empty : new Padding(10, 4, 10, 5);
        deviceConnection.Text = mode ?? "";
        deviceConnection.Visible = device != null && !string.IsNullOrEmpty(mode);
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

    internal static Image LoadImage(string name)
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
        operationProgressActive = true;
        lock (queueLock) { pendingLog.Length = 0; pendingProgress = null; }
        SetBusy(true);
        counts.Text = ""; counts.Visible = false; openFolder.Visible = false; outputFolder = null; latestProgress = null; elapsed.Restart();
        log.Clear();
    }
    public void CompleteOperation(OperationResult result, bool previewOnly)
    {
        operationProgressActive = false;
        elapsed.Stop();
        if (!previewOnly && (Selected == GuiOperation.FullBackup || Selected == GuiOperation.IncrementalBackup || Selected == GuiOperation.CameraSettings)) restoreContextKey = null;
        SetBusy(false);
        status.Text = result.Cancelled ? "Cancelled" : result.ExitCode != 0 ? "Needs attention" : previewOnly ? "Preview complete" : "Complete";
        status.ForeColor = result.ExitCode != 0 ? GuiTheme.Danger : GuiTheme.Ink;
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
            GuiTheme.ApplyTheme(config.Theme, this);
            backupFolder.Text = config.BackupFolder; profile.Text = config.Profile;
            restoreContextKey = null; RefreshRestoreBackups();
            RefreshDestinationSummary();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    void OpenTool(int choice)
    {
        try
        {
            Config options = CaptureConfig();
            // Saved X100VI recipes open from a settings backup's row in Dashboard or Storage (View recipes).
            // Dashboard and Storage start filtered to the device detected here, if any.
            using (Form dialog = choice < 2 ? (Form)new CatalogDialog(this, options, choice == 1, null, displayedDevice) : new ProfileSettingsDialog(this, options))
                dialog.ShowDialog(this);
            RefreshDestinationSummary();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open tool", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
