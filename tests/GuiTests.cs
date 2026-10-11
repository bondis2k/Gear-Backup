using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using static PathSafety;
using static ConsoleUi;
using static Snapshots;
using static Backups;
using static FujiSettings;
using static MtpMirror;
using static StorageCatalog;
using static AppSettings;
using static ProfileSettings;
using static Session;
using static Workflow;
using static GearGui;

class GuiTestRunner
{
    [STAThread]
    static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Tests.RunGuiTests();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

partial class Tests
{
    public static void RunGuiTests()
    {
        GuiHeadless = true;
        string root = Path.Combine(Environment.CurrentDirectory, ".test-runs", "gui-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "card");
        Put(Path.Combine(source, "Songs", "one.m8s"), "GUI TEST SONG");
        Put(Path.Combine(source, "Samples", "kick.wav"), "GUI TEST SAMPLE");
        using (var form = CreateGui(true))
        {
            form.Show();
            // This workflow exercises snapshot restore; normal startup leaves history off.
            var formConfig = (Config)typeof(MainForm).GetField("config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form);
            formConfig.PointInTimeHistory = true; formConfig.SingleContentStore = false; // this workflow checks browsable backup folders
            Assert(form.Text == AppIdentity.Name, "GUI window uses the shared Gear Backup identity");
            using (var about = new AboutDialog())
            {
                var version = (Label)about.Controls.Find("ApplicationVersion", true)[0];
                Assert(version.Text == "Version " + AppIdentity.Version, "About displays the shared release version");
                Assert(((Label)about.Controls.Find("ApplicationLicense", true)[0]).Text.Contains("GNU General Public License"), "About names the license");
                var aboutIcon = (PictureBox)about.Controls.Find("AboutIcon", true)[0];
                Assert(aboutIcon.Image != null && aboutIcon.Width >= 100 && about.ClientSize.Width >= 520, "About shows the app icon in a roomier window");
            }
            var sourceBox = (TextBox)form.Controls.Find("SourceFolder", true)[0];
            var backupBox = (TextBox)form.Controls.Find("BackupFolder", true)[0];
            var fullName = (TextBox)form.Controls.Find("FullBackupName", true)[0];
            var incrementalSet = (ComboBox)form.Controls.Find("IncrementalBackup", true)[0];
            Action<int> selectOperation = index => ((MainForm)form).SelectOperation((GuiOperation)index);
            var start = (Button)form.Controls.Find("StartOperation", true)[0];
            var preview = (Button)form.Controls.Find("PreviewOperation", true)[0];
            var cancel = (Button)form.Controls.Find("CancelOperation", true)[0];
            var state = (Label)form.Controls.Find("OperationStatus", true)[0];
            var zipBox = (CheckBox)form.Controls.Find("ZipBackup", true)[0];
            var deviceImage = (PictureBox)form.Controls.Find("DeviceImage", true)[0];
            var deviceLabel = (Label)form.Controls.Find("DetectedDevice", true)[0];
            var connectionLabel = (Label)form.Controls.Find("DeviceConnectionMode", true)[0];
            var sourceLabel = (Label)form.Controls.Find("SourceCaption", true)[0];
            var resultCounts = (Label)form.Controls.Find("CompletionCounts", true)[0];
            var openOutput = (Button)form.Controls.Find("OpenOutputFolder", true)[0];
            var moreOptions = (Button)form.Controls.Find("ToggleBackupOptions", true)[0];
            var optionalFields = form.Controls.Find("OptionalBackupFields", true)[0];
            var workflowTabs = (TabControl)form.Controls.Find("WorkflowTabs", true)[0];
            var restoreTargetBox = (TextBox)form.Controls.Find("RestoreDestination", true)[0];
            Assert(workflowTabs.TabPages.Count == 3 && workflowTabs.TabPages[0].Text == "Device" && workflowTabs.TabPages[1].Text == "Backup" && workflowTabs.TabPages[2].Text == "Restore" && workflowTabs.SelectedIndex == 0,
                "main window starts with Device, followed by separate Backup and Restore tabs");
            Assert(!optionalFields.Visible && !sourceBox.Visible && !backupBox.Visible && !form.Controls.Find("Profile", true)[0].Visible && !cancel.Visible,
                "main window initially focuses on detection and backup instead of optional fields");
            workflowTabs.SelectedIndex = 1;
            var backupTypes = (ComboBox)form.Controls.Find("BackupOperation", true)[0];
            Assert(backupTypes.Items.Count == 2 && !backupTypes.Items.Contains("Restore files") && !backupTypes.Items.Contains("Check backups"), "Backup tab starts with only full and incremental backup");
            moreOptions.PerformClick();
            Assert(optionalFields.Visible && sourceBox.Visible && backupBox.Visible, "More options exposes the folder and profile controls");
            moreOptions.PerformClick();
            Assert(!optionalFields.Visible, "optional fields can be collapsed again");
            using (var configured = new MainForm(true, new Config { SingleContentStore = false, SourceFolder = @"D:\Card", Profile = "Studio", BackupFolder = @"E:\Backups" }))
            {
                configured.Show(); Application.DoEvents();
                ((TabControl)configured.Controls.Find("WorkflowTabs", true)[0]).SelectedIndex = 1;
                // Design previews leave the source box empty, so type the folder as a user would.
                configured.Controls.Find("SourceFolder", true)[0].Text = @"D:\Card";
                string summary = configured.Controls.Find("BackupDestinationSummary", true)[0].Text;
                Assert(!configured.Controls.Find("OptionalBackupFields", true)[0].Visible,
                    "More options starts collapsed even when a source folder and profile are set");
                Assert(summary.StartsWith(@"Source: D:\Card") && summary.Contains("History name: Studio"),
                    "the summary line shows the set source folder and profile while collapsed: " + summary);
            }
            Assert(deviceImage.Image != null && deviceImage.AccessibleName == "No device detected", "the header shows the general picture at startup");
            Icon windowIcon = form.Icon;
            ((MainForm)form).ShowDevice(Devices.OP1Field);
            Assert(deviceLabel.Text.Contains("OP-1 Field") && deviceLabel.Text.Contains("detected"), "detected device is named visibly");
            Assert(deviceImage.AccessibleName == "OP-1 Field" && deviceImage.Image != null, "a detected OP-1 Field shows its picture in the header");
            ((MainForm)form).ShowDevice(new DeviceProfile { Id = "other", Name = "Other" });
            Assert(deviceImage.AccessibleName == "OP-1 Field", "a device without a picture keeps what's shown");
            ((MainForm)form).ShowDevice(Devices.OPXY);
            Assert(deviceImage.AccessibleName == "OP-XY" && deviceImage.Image != null, "a detected OP-XY shows its picture");
            ((MainForm)form).ShowDevice(Devices.TrackerMini);
            Assert(deviceImage.AccessibleName == "Tracker Mini" && deviceImage.Image != null, "a detected Tracker Mini shows its picture");
            ((MainForm)form).ShowDevice(Devices.X100VI);
            Assert(deviceImage.AccessibleName == "X100VI" && deviceImage.Image != null, "a detected X100VI shows its picture");
            ((MainForm)form).ShowDevice(Devices.X100VI, FujiModeName(true));
            Assert(connectionLabel.Text.Contains("settings mode"), "camera settings mode remains visible in the header");
            ((MainForm)form).ShowDevice(Devices.X100VI, FujiModeName(false));
            Assert(connectionLabel.Text.Contains("photo-transfer mode") && !connectionLabel.Text.Contains("settings mode"),
                "camera photo mode replaces the previously displayed settings mode");
            ((MainForm)form).ShowDevice(Devices.M8);
            Assert(deviceImage.AccessibleName == "M8", "a detected M8 shows its picture");
            ((MainForm)form).ShowDevice(null);
            Assert(deviceImage.AccessibleName == "No device detected", "no device detected brings back the general picture");
            Assert(form.Icon == windowIcon, "the window and taskbar icon never change");
            var detectionRunner = (GuiOperationRunner)typeof(MainForm).GetField("runner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form);
            detectionRunner.ShowDetected(new SourceChoice { Path = @"E:\", Detected = Devices.M8 });
            Assert(sourceBox.Text == @"E:\" && connectionLabel.Text.Contains("USB drive"), "USB detection selects and identifies the drive");
            detectionRunner.ShowDetected(new SourceChoice { Path = root, Detected = Devices.M8 });
            Assert(form.Controls.Find("DeviceStorageDetails", true)[0].Text.Contains("Capacity:") && form.Controls.Find("DeviceStorageDetails", true)[0].Text.Contains("Free:") && form.Controls.Find("DeviceStorageDetails", true)[0].Text.Contains("Filesystem:"),
                "drive detection displays capacity, free space and filesystem without a file scan");
            detectionRunner.ShowDetected(new SourceChoice { Path = Path.Combine(root, "mtp-mirror"), Detected = Devices.OP1Field,
                Mtp = new MtpSource { Device = Devices.OP1Field } });
            Assert(sourceBox.Text == "" && connectionLabel.Text == "MTP", "MTP detection clears the previous USB source and displays its transport");
            Assert(form.Controls.Find("DeviceStorageDetails", true)[0].Text.Contains("not reported"), "MTP details do not misreport the PC mirror's capacity as device storage");
            workflowTabs.SelectedIndex = 0;
            detectionRunner.ShowDetected(new SourceChoice { Detected = Devices.X100VI, Mtp = new MtpSource { Device = Devices.X100VI }, CameraSettingsMode = true, CameraMode = FujiModeName(true) });
            Assert(workflowTabs.SelectedIndex == 0 && backupTypes.SelectedIndex == 2 && form.Controls.Find("DeviceStorageDetails", true)[0].Text.Contains("C1–C7"),
                "camera detection stays on Device and prepares settings capture on Backup");
            Assert(backupTypes.Items.Count == 3, "settings backup appears for a detected X100VI in settings mode");
            detectionRunner.ShowDetected(new SourceChoice { Detected = Devices.X100VI, Mtp = new MtpSource { Device = Devices.X100VI }, CameraMode = FujiModeName(false) });
            Assert(backupTypes.Items.Count == 2 && backupTypes.SelectedIndex == 0, "photo mode removes settings backup and resets a stale settings selection");
            detectionRunner.ShowDetected(new SourceChoice { Detected = Devices.OP1Field, Mtp = new MtpSource { Device = Devices.OP1Field } });
            Assert(typeof(GuiOperationRunner).GetField("detectedDrive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(detectionRunner) == null,
                "MTP detection also clears the previous drive selection used for ejection");
            Assert(deviceLabel.Font.Size >= 17 && deviceLabel.ForeColor == GuiTheme.AccentDark && deviceImage.Width >= 88,
                "the detected device has a prominent name, highlight and larger picture");
            detectionRunner.ShowDetected(null);
            Assert(!connectionLabel.Visible && sourceBox.Text == "", "failed detection clears stale source and connection details");
            Assert(backupTypes.Items.Count == 2, "settings backup is hidden after device detection is cleared");
            sourceBox.Text = source; backupBox.Text = Path.Combine(root, "backups");
            detectionRunner.ShowDetected(new SourceChoice { Detected = Devices.X100VI, Mtp = new MtpSource { Device = Devices.X100VI }, CameraSettingsMode = true, CameraMode = FujiModeName(true) });
            selectOperation(4);
            Assert(!sourceBox.Enabled && preview.Enabled && zipBox.Enabled && sourceLabel.Text.Contains("C1–C7"),
                "camera settings operation uses the connected camera, supports preview and zip, and identifies its scope");
            var settingsConfig = (Config)typeof(MainForm).GetMethod("CaptureConfig", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(form, new object[] { true });
            Assert(settingsConfig.SourceFolder == "", "camera settings capture ignores an unused source path");
            sourceBox.Text = source;
            selectOperation(3);
            Assert(!sourceBox.Enabled && sourceLabel.Text.Contains("not needed"), "check mode disables the unused source and explains it");
            Assert(start.Text == "Check" && !preview.Enabled, "GUI check mode disables copy preview");
            Assert(!zipBox.Enabled, "zip option is only available for full backups");
            selectOperation(0);
            Assert(zipBox.Enabled, "zip option is available for a full backup");
            Assert(fullName.Visible && !incrementalSet.Visible, "full mode offers a name field instead of the incremental set picker");
            fullName.Text = "Live October";
            zipBox.Checked = true;
            Assert(InvokeGuiShortcut((MainForm)form, Keys.Control | Keys.P), "Ctrl+P starts the preview");
            Assert(cancel.Enabled && !sourceBox.Enabled, "GUI disables editable inputs while worker runs");
            DriveGui(form, state, "Preview complete", false);
            Assert(!Directory.Exists(backupBox.Text), "GUI preview writes no backup directories");
            Assert(!cancel.Enabled && sourceBox.Enabled, "GUI reenables inputs after preview");
            Assert(InvokeGuiShortcut((MainForm)form, Keys.Control | Keys.Enter), "Ctrl+Enter starts the selected operation");
            DriveGui(form, state, "Complete", false);
            string history = Path.Combine(backupBox.Text, "Gear Backups", "profiles", InOperation(() => ProfileId(Rooted(source), "")));
            string backup = ListBackups(history)[0];
            Assert(BackupSetNames.Read(backup) == "Live October", "GUI saves full backup name without changing automatic history grouping");
            Assert(resultCounts.Text.Contains("Copied 2") && resultCounts.Text.Contains("Failed 0") && openOutput.Visible,
                "completed backup shows structured counts and an open-folder action");
            Assert(File.ReadAllText(Path.Combine(backup, "Songs", "one.m8s")) == "GUI TEST SONG", "GUI full backup executes shared engine");
            Assert(File.Exists(backup + ".zip"), "GUI full backup with the zip option saves a zip");
            zipBox.Checked = false;
            selectOperation(1);
            DateTime selectionDeadline = DateTime.UtcNow.AddSeconds(15);
            while (!incrementalSet.Enabled && DateTime.UtcNow < selectionDeadline) { Application.DoEvents(); Thread.Sleep(10); }
            Assert(!fullName.Visible && incrementalSet.Visible && incrementalSet.Enabled && incrementalSet.Text.Contains("Live October"), "incremental mode lists the named full set");
            start.PerformClick();
            DriveGui(form, state, "Complete", false);
            Assert(resultCounts.Text.Contains("Copied 0") && resultCounts.Text.Contains("Unchanged 2"), "unchanged incremental summary distinguishes unchanged files from copies");
            Assert(Directory.GetFiles(SnapshotRoot(backup), "*.manifest").Length == 2, "GUI incremental records snapshot without proceed prompt when unchanged");

            string target = Path.Combine(root, "restore-target");
            Directory.CreateDirectory(target);
            ((MainForm)form).ShowDevice(Devices.M8, "USB drive");
            restoreTargetBox.Text = target; selectOperation(2);
            WaitForRestorePicker(form);
            ((MainForm)form).DrainDisplay();
            Assert(!state.Text.Contains("Scanning") && !state.Text.Contains("Loading"), "background restore inventory never leaves a scanning operation status");
            string settledStatus = state.Text;
            ((MainForm)form).UpdateProgress(new OperationProgress { Stage = Stage.ScanningBackup, Path = backup });
            ((MainForm)form).DrainDisplay();
            Assert(state.Text == settledStatus, "late queued progress cannot overwrite a settled idle operation status");
            Assert(workflowTabs.SelectedIndex == 2 && sourceLabel.Text.StartsWith("Restore destination") && restoreTargetBox.AccessibleName == "Restore destination" && restoreTargetBox.Enabled,
                "restore mode clearly identifies the writable destination");
            Assert(optionalFields.Visible && restoreTargetBox.Visible && !sourceBox.Visible && sourceBox.Text == source, "restore exposes its independent destination without changing the backup source");
            var preservedConfig = (Config)typeof(MainForm).GetMethod("CaptureConfig", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(form, new object[] { false });
            Assert(preservedConfig.SourceFolder == Path.GetFullPath(source), "saving settings from Restore preserves the backup source");
            start.PerformClick();
            DriveGui(form, state, "Complete", false);
            Assert(File.ReadAllText(Path.Combine(target, "Songs", "one.m8s")) == "GUI TEST SONG", "GUI restore wizard restores selected snapshot");
            Assert(openOutput.Text == "Open restore folder" && resultCounts.Text.Contains("Copied 2"), "restore result links to its destination and shows copy counts");

            var restoreScope = (ComboBox)form.Controls.Find("RestoreOperation", true)[0];
            var browseRestoreFiles = (Button)form.Controls.Find("BrowseRestoreFiles", true)[0];
            var pointPicker = (ComboBox)form.Controls.Find("RestorePoint", true)[0];
            restoreScope.SelectedIndex = 1;
            Assert(browseRestoreFiles.Visible && !start.Enabled && pointPicker.SelectedIndex == 0, "individual restore keeps the latest point selected and requires a checked selection");
            using (var browserAutomation = new System.Windows.Forms.Timer { Interval = 20 })
            {
                browserAutomation.Tick += delegate {
                    foreach (Form owned in form.OwnedForms)
                    {
                        var browser = owned as RestoreBrowserDialog;
                        if (browser == null || !browser.Visible) continue;
                        var tree = (TreeView)browser.Controls.Find("RestoreFileTree", true)[0];
                        foreach (TreeNode node in tree.Nodes) if ((string)node.Tag == "Samples") node.Checked = true;
                        ((Button)browser.Controls.Find("UseRestoreSelection", true)[0]).PerformClick();
                    }
                };
                browserAutomation.Start(); browseRestoreFiles.PerformClick(); browserAutomation.Stop();
            }
            Assert(start.Enabled && form.Controls.Find("RestoreFileTree", true).Length == 0, "the browser publishes its checked selection back to the restore tab");
            string individualTarget = Folder(root, "individual-restore-target");
            restoreTargetBox.Text = individualTarget; start.PerformClick();
            DriveGui(form, state, "Complete", false);
            Assert(File.Exists(Path.Combine(individualTarget, "Samples", "kick.wav")) && !Directory.Exists(Path.Combine(individualTarget, "Songs")), "main-window individual restore copies only the checked subtree");
            restoreScope.SelectedIndex = 0; restoreTargetBox.Text = target;
            ((MainForm)form).ShowDevice(Devices.OP1Field, "MTP");
            DateTime filterDeadline = DateTime.UtcNow.AddSeconds(15);
            while (!form.Controls.Find("RestoreBackup", true)[0].Enabled && DateTime.UtcNow < filterDeadline && ((ComboBox)form.Controls.Find("RestoreBackup", true)[0]).Items.Count != 0) { Application.DoEvents(); Thread.Sleep(10); }
            Assert(((ComboBox)form.Controls.Find("RestoreBackup", true)[0]).Items.Count == 0 && !start.Enabled, "changing the identified device removes nonmatching restore backups");
            ((MainForm)form).ShowDevice(Devices.M8, "USB drive"); WaitForRestorePicker(form);

            selectOperation(3); sourceBox.Text = "bad:unused:path";
            start.PerformClick();
            DriveGui(form, state, "Complete", false);
            Assert(state.Text == "Complete", "GUI integrity check needs no source card");

            selectOperation(0); sourceBox.Text = source;
            Assert(restoreTargetBox.Text == target, "returning to Backup preserves the independent restore destination");
            int backupsBefore = ListBackups(history).Count;
            start.PerformClick();
            DriveGui(form, state, "Complete", true);
            Assert(ListBackups(history).Count == backupsBefore, "dismissing GUI acceptance dialog prevents writes");

            // Cancel inside a marshalled choice dialog: cancellation must survive Invoke.
            ((MainForm)form).ShowDevice(Devices.M8, "USB drive");
            selectOperation(2); restoreTargetBox.Text = target;
            WaitForRestorePicker(form);
            Put(Path.Combine(target, "Songs", "one.m8s"), "DIFFERENT TARGET");
            start.PerformClick();
            DriveGui(form, state, "Cancelled", true);
            Assert(state.Text == "Cancelled", "GUI dialog cancellation is not reported as an invocation error");
            form.Close();
        }
        // Choice buttons show the option labels exactly; the same key can label different buttons.
        using (var dialog = new ChoiceDialog("Resume the unfinished backup?", new[] {
            new Option('r', "Resume into it"), new Option('n', "New full backup"), new Option('c', "Cancel") }))
        {
            var labels = new List<Button>();
            CollectGuiButtons(dialog, labels);
            Assert(labels.Exists(b => b.Text == "Resume into it") && labels.Exists(b => b.Text == "New full backup"),
                "GUI choice buttons use the question's own labels");
        }
        using (var dialog = new ChoiceDialog("Some target files differ from the backup.", new[] {
            new Option('r', "Replace them"), new Option('k', "Keep them (copy only missing files)") }))
        {
            var labels = new List<Button>();
            CollectGuiButtons(dialog, labels);
            Assert(labels.Exists(b => b.Text == "Replace them"), "the same key gets a different label in a different question");
        }
        string ini = Path.Combine(root, "shared-settings.ini");
        Put(ini, "; keep this comment\nVerify=no\nCustomOption=unchanged\nVerify=no\n");
        var saved = new Config { SingleContentStore = false, BackupFolder = root, Verify = true, Recovery = false, Thorough = false, Profile = "Studio", HardLinks = true };
        SaveConfig(saved, ini);
        string text = File.ReadAllText(ini);
        Assert(text.Contains("; keep this comment") && text.Contains("CustomOption=unchanged"), "GUI settings save preserves comments and unrelated keys");
        Assert(text.Contains("Verify=yes") && !text.Contains("Verify=no") && text.Contains("Recovery=no") && text.Contains("Thorough=no"),
            "shared settings save updates duplicates and keeps checksum/recovery defaults optional");
        Assert(text.Contains("Profile=Studio") && text.Contains("SourceFolder="), "settings save includes paths and profile");
        Assert(text.Contains("HardLinks=yes"), "settings save keeps HardLinks");
        TestPreviewFiltering();
        TestRestoreFileBrowser();
        TestSettingsInteraction(root);
        TestGuiThemes(root);
        TestAllWindowThemes(Path.Combine(root, "window-themes"));
        TestThemeReadability();
        TestActionButtonsFollowOperation();
        TestGuiProgress();
        TestManagementGui(Path.Combine(root, "management-tools"));
        TestDashboardDeviceFilter();
        TestRecipeButtons(Path.Combine(root, "recipe-buttons"));
        TestWindowPlacement(Path.Combine(root, "window-placement"));
        TestHistoryNamesGui(Path.Combine(root, "history-names"));
        TestRestoreLocationsGui(Path.Combine(root, "restore-locations"));
        TestScaledGuiLayouts();
        Console.WriteLine("PASS: " + assertions + " GUI assertions.");
        RemoveFixtures(root);
    }

    static void TestPreviewFiltering()
    {
        var actions = new List<string> { @"New: Songs\added.m8s", @"Changed: Songs\old.m8s", @"REMOVE extra file: Samples\extra.wav",
            @"Create folder: Empty", @"Skip unchanged: Songs\same.m8s" };
        var plan = new OperationPlan { Label = "Restore preview", Actions = actions, Bytes = 12,
            FileSizes = new Dictionary<string, long> { { @"Songs\added.m8s", 4 }, { @"Songs\old.m8s", 8 }, { @"Samples\extra.wav", 16 } } };
        using (var dialog = new PreviewDialog(plan))
        {
            dialog.Show(); Application.DoEvents();
            var grid = (DataGridView)dialog.Controls.Find("PreviewChanges", true)[0];
            var filter = (ComboBox)dialog.Controls.Find("PreviewFilter", true)[0];
            var search = (TextBox)dialog.Controls.Find("PreviewSearch", true)[0];
            Assert(grid.Rows.Count == 5 && grid.Rows[0].Cells[2].Value.ToString() == "4 B", "preview displays actions, paths and actual per-file sizes");
            Assert(InvokeGuiShortcut(dialog, Keys.Control | Keys.F) && dialog.ActiveControl == search, "Ctrl+F focuses preview search");
            using (var bitmap = new Bitmap(dialog.ClientSize.Width, dialog.ClientSize.Height))
            {
                dialog.Controls[0].DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.ClientSize));
                bitmap.Save(Path.Combine(Environment.CurrentDirectory, "tests", ".build", "preview-example.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            filter.SelectedIndex = 3;
            Assert(grid.Rows.Count == 1 && grid.Rows[0].Cells[1].Value.ToString() == @"Samples\extra.wav", "removal filter exposes destructive changes");
            filter.SelectedIndex = 2;
            Assert(grid.Rows.Count == 1 && grid.Rows[0].Cells[0].Value.ToString() == "Changed", "replacement filter includes incremental changes");
            filter.SelectedIndex = 1;
            Assert(grid.Rows.Count == 2, "addition filter includes new files and folders");
            filter.SelectedIndex = 0; search.Text = "SONGS";
            Assert(grid.Rows.Count == 3, "preview path search is case insensitive");
            search.Text = "no matching rows";
            Assert(grid.Rows.Count == 0 && plan.Actions.Count == 5 && dialog.Accept.Enabled,
                "filtering changes display only; the original approval plan remains intact");
            search.Text = ""; filter.SelectedIndex = 0;
            Assert(grid.Rows.Count == 5 && grid.ReadOnly, "clearing filters restores all read-only rows");
            dialog.Close();
        }
    }

    static void TestSettingsInteraction(string root)
    {
        var config = new Config { SingleContentStore = false, HardLinks = true, RecoveryFolder = Path.Combine(root, "recovery") };
        using (var dialog = new SettingsDialog(config))
        {
            dialog.Show(); Application.DoEvents();
            var tabs = FindGuiControl<TabControl>(dialog);
            Assert(tabs.TabCount == 4 && tabs.TabPages[0].Text == "Backup" && tabs.TabPages[1].Text == "Restore" && tabs.TabPages[3].Text == "Appearance", "settings group operations and appearance separately");
            var history = (CheckBox)dialog.Controls.Find("PointInTimeHistory", true)[0];
            Assert(!history.Checked, "settings show history disabled by default");
            history.Checked = true;
            var single = (CheckBox)dialog.Controls.Find("SingleContentStore", true)[0];
            Assert(!single.Checked, "single content store is optional by default");
            single.Checked = true;
            Assert(!history.Enabled && history.Checked, "single store disables copied history without losing its preference");
            tabs.SelectedIndex = 1;
            var recovery = (CheckBox)dialog.Controls.Find("RecoveryCopies", true)[0];
            var path = (TextBox)dialog.Controls.Find("RecoveryLocation", true)[0];
            Assert(!path.Enabled && path.Text == config.RecoveryFolder, "disabled recovery preserves its configured location");
            recovery.Checked = true;
            Assert(path.Enabled, "enabling recovery enables its location controls");
            recovery.Checked = false;
            string expectedPath = path.Text;
            ((Button)dialog.Controls.Find("SaveSettings", true)[0]).PerformClick();
            Assert(config.PointInTimeHistory, "settings save the choice to enable history");
            Assert(config.SingleContentStore, "settings save the single content store choice");
            Assert(!config.Recovery && !config.Verify && !config.Thorough && config.HardLinks && config.RecoveryFolder == expectedPath,
                "saving grouped settings preserves default-off options, recovery location and hidden settings: " +
                config.Recovery + ", " + config.Verify + ", " + config.Thorough + ", " + config.HardLinks + ", " + config.RecoveryFolder + " / " + expectedPath);
        }
    }

    static void TestGuiThemes(string root)
    {
        string settingsPath = Path.Combine(root, "theme-settings"); Directory.CreateDirectory(settingsPath);
        var config = new Config { SingleContentStore = false, BackupFolder = root, SettingsDirectory = settingsPath, SourceFolder = Path.Combine(root, "card") };
        try
        {
            using (var form = new MainForm(false, config))
            using (var table = new PreviewDialog(new OperationPlan { Label = "Theme test", Actions = new List<string> { "New: song.txt" }, Bytes = 4 }))
            {
                form.Show(); table.Show(form); Application.DoEvents();
                Assert(form.Icon != null && object.ReferenceEquals(form.Icon, table.Icon),
                    "Review changes uses the shared Gear Backup window icon");
                form.ShowDevice(Devices.OPXY, "MTP");
                Assert(form.BackColor == Color.FromArgb(243, 245, 247), "Original is the default desktop palette");
                var source = (TextBox)form.Controls.Find("SourceFolder", true)[0]; string previousSource = source.Text;
                var grid = FindGuiControl<DataGridView>(table);
                SetGuiTheme(form, 0, true);
                Assert(GuiTheme.ThemeName == "original" && form.BackColor == Color.FromArgb(243, 245, 247), "saving Original recolors the main window immediately");
                CheckNativeTitleBar(form, false);
                Assert(grid.BackgroundColor == Color.White && grid.DefaultCellStyle.BackColor == Color.White,
                    "open dialog tables change along with the main window: " + grid.BackgroundColor + " / " + grid.DefaultCellStyle.BackColor);
                Assert(grid.AlternatingRowsDefaultCellStyle.BackColor == GuiTheme.Surface &&
                    grid.ColumnHeadersDefaultCellStyle.BackColor == GuiTheme.Background &&
                    grid.DefaultCellStyle.SelectionBackColor == GuiTheme.AccentSoft,
                    "table headers, alternating rows and selections use the selected palette");
                Assert(source.Text == previousSource && ((Label)form.Controls.Find("DetectedDevice", true)[0]).Text.Contains("OP-XY"),
                    "theme changes preserve source selections and detected device information");
                Config loaded = null; WithUi(() => loaded = LoadConfig(settingsPath));
                Assert(loaded.Theme == "original", "selected theme persists in the shared INI");
                SetGuiTheme(form, 1, false);
                Assert(GuiTheme.ThemeName == "original", "cancelling a theme change leaves the app unchanged");
                WithUi(() => loaded = LoadConfig(settingsPath));
                Assert(loaded.Theme == "original", "cancelling does not save the new theme");
                using (var restarted = new MainForm(false, loaded))
                    Assert(restarted.BackColor == Color.FromArgb(243, 245, 247), "startup applies the saved Original theme");
                SetGuiTheme(form, 1, true);
                Assert(GuiTheme.ThemeName == "original-dark" && form.BackColor == Color.FromArgb(19, 25, 30),
                    "saving Original dark applies the charcoal palette immediately");
                CheckNativeTitleBar(form, true);
                CheckNativeTitleBar(table, true);
                using (var about = new AboutDialog())
                {
                    about.Show(form); Application.DoEvents();
                    CheckNativeTitleBar(about, true);
                    Assert(object.ReferenceEquals(form.Icon, about.Icon), "new About window uses the app icon");
                }
                Assert(grid.DefaultCellStyle.BackColor == GuiTheme.Surface && grid.DefaultCellStyle.ForeColor == GuiTheme.Ink &&
                    ((ComboBox)table.Controls.Find("PreviewFilter", true)[0]).BackColor == GuiTheme.Surface,
                    "open dialog tables and native inputs use dark surfaces and readable text");
                WithUi(() => loaded = LoadConfig(settingsPath));
                Assert(loaded.Theme == "original-dark", "dark theme persists in the INI");
                using (var restarted = new MainForm(false, loaded))
                {
                    restarted.Show();
                    Assert(restarted.BackColor == Color.FromArgb(19, 25, 30), "startup restores the dark palette");
                    CheckNativeTitleBar(restarted, true);
                    typeof(Control).GetMethod("RecreateHandle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(restarted, null);
                    CheckNativeTitleBar(restarted, true);
                }
                string themedManual = Path.Combine(settingsPath, "Manual-original-dark.html");
                File.WriteAllText(themedManual, "test");
                Assert(MainForm.ManualPath(settingsPath, "original-dark") == themedManual, "Help selects the matching dark manual");
                Assert(MainForm.ManualPath(settingsPath, "original") == Path.Combine(settingsPath, "Manual.html"), "Help falls back when a themed manual is absent");
                SetGuiTheme(form, 2, true);
                CheckNativeTitleBar(form, false);
                CheckNativeTitleBar(table, false);
                Assert(form.BackColor == Color.FromArgb(0, 128, 128) && grid.BackgroundColor == Color.FromArgb(255, 255, 240),
                    "switching back to Retro restores the desktop and paper colors");
                Assert(((Label)form.Controls.Find("DetectedDevice", true)[0]).ForeColor == GuiTheme.AccentDark,
                    "switching themes restores the detected device highlight");
            }
        }
        finally { GuiTheme.ApplyTheme("original"); }
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    static void CheckNativeTitleBar(Form form, bool dark)
    {
        int value;
        if (DwmGetWindowAttribute(form.Handle, 20, out value, sizeof(int)) == 0)
            Assert(value == (dark ? 1 : 0), "native title bar follows theme for " + form.GetType().Name);
        if (DwmGetWindowAttribute(form.Handle, 35, out value, sizeof(int)) == 0)
            Assert(value == ColorTranslator.ToWin32(GuiTheme.ThemeName == "retro" ? GuiTheme.Accent : GuiTheme.Background),
                "native caption exactly matches the selected palette");
        if (DwmGetWindowAttribute(form.Handle, 36, out value, sizeof(int)) == 0)
            Assert(value == ColorTranslator.ToWin32(GuiTheme.ThemeName == "retro" ? Color.White : GuiTheme.Ink),
                "native caption text matches the selected palette");
    }

    // The Restore tab holds Restore/Preview back until a point is chosen; going back to Backup must release them.
    static void TestActionButtonsFollowOperation()
    {
        using (var form = new MainForm(true))
        {
            form.Show(); Application.DoEvents();
            var start = (Button)form.Controls.Find("StartOperation", true)[0];
            var preview = (Button)form.Controls.Find("PreviewOperation", true)[0];
            form.SelectOperation(GuiOperation.FullBackup);
            Assert(start.Enabled && preview.Enabled, "Backup starts with its buttons available");
            form.SelectOperation(GuiOperation.Restore);
            Assert(!start.Enabled && !preview.Enabled, "Restore waits for a device and restore point");
            form.SelectOperation(GuiOperation.FullBackup);
            Assert(start.Enabled && preview.Enabled, "going back from Restore to a full backup re-enables Back up and Preview");
            form.SelectOperation(GuiOperation.Restore); form.SelectOperation(GuiOperation.IncrementalBackup);
            Assert(start.Enabled && preview.Enabled, "going back from Restore to an incremental backup re-enables Back up and Preview");
            var fullName = (TextBox)form.Controls.Find("FullBackupName", true)[0];
            var incrementalName = (TextBox)form.Controls.Find("IncrementalBackupName", true)[0];
            form.SelectOperation(GuiOperation.FullBackup);
            Assert(fullName.Parent.Visible && !incrementalName.Parent.Visible, "a full backup asks for the set name only");
            form.SelectOperation(GuiOperation.IncrementalBackup);
            Assert(!fullName.Parent.Visible && incrementalName.Parent.Visible && incrementalName.Visible, "an incremental backup can be given its own name");
            // Tab switches hold the window's repainting while the new page is arranged, and always let go again.
            var tabs = (TabControl)form.Controls.Find("WorkflowTabs", true)[0];
            foreach (int index in new[] { 2, 1, 0, 2, 1 }) { tabs.SelectedIndex = index; Application.DoEvents(); }
            var freezes = typeof(MainForm).GetField("redrawFreezes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert((int)freezes.GetValue(form) == 0, "switching tabs never leaves the window frozen");
            Assert(start.Enabled && preview.Enabled && tabs.SelectedIndex == 1, "the Backup tab is usable after switching tabs");
            form.SelectOperation(GuiOperation.Restore); form.SelectOperation(GuiOperation.Check);
            Assert(start.Enabled && !preview.Enabled, "Check is available without a preview");
        }
    }

    static double ThemeContrast(Color a, Color b)
    {
        Func<Color, double> luminance = delegate(Color c) {
            Func<int, double> channel = delegate(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); };
            return 0.2126 * channel(c.R) + 0.7152 * channel(c.G) + 0.0722 * channel(c.B);
        };
        double x = luminance(a), y = luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    // Every theme's text colours stay readable on the surfaces they are drawn on.
    static void TestThemeReadability()
    {
        try
        {
            foreach (string theme in new[] { "original", "original-dark", "retro" })
            {
                GuiTheme.ApplyTheme(theme);
                foreach (Color surface in new[] { GuiTheme.Background, GuiTheme.Surface })
                    foreach (Color ink in new[] { GuiTheme.Ink, GuiTheme.Muted, GuiTheme.AccentDark })
                        Assert(ThemeContrast(ink, surface) >= 4.5, "text is readable in " + theme + ": " + ink.Name + " on " + surface.Name);
                Assert(ThemeContrast(GuiTheme.PrimaryInk, GuiTheme.Accent) >= 4.5, "primary button text is readable in " + theme);
            }
            // A disabled check box keeps readable text in the dark theme (Windows would draw it dark grey on dark).
            GuiTheme.ApplyTheme("original-dark");
            using (var panel = new Panel { Size = new Size(260, 30), BackColor = GuiTheme.Surface })
            using (var box = new CheckBox { Text = "Remove extra target files", AutoSize = true, Enabled = false })
            {
                panel.Controls.Add(box); GuiTheme.ApplyTo(panel);
                panel.CreateControl(); box.CreateControl();
                using (var bitmap = new Bitmap(box.Width, box.Height))
                {
                    box.DrawToBitmap(bitmap, new Rectangle(Point.Empty, box.Size));
                    double best = 0;
                    for (int x = 24; x < bitmap.Width; x++)
                        for (int y = 0; y < bitmap.Height; y++) best = Math.Max(best, ThemeContrast(bitmap.GetPixel(x, y), GuiTheme.Surface));
                    Assert(best >= 2.0, "disabled check box text stays visible in the dark theme");
                }
            }
            using (var dialog = new SelectionDialog("Choose", new List<string> { "One" }))
                Assert(dialog.List.DrawMode == DrawMode.OwnerDrawFixed, "choice list draws its selection in the theme's colours");
        }
        finally { GuiTheme.ApplyTheme("original"); }
    }

    static void TestAllWindowThemes(string root)
    {
        Config config; BackupCatalog catalog = ManagementFixture(root, out config);
        try
        {
            foreach (string initial in new[] { "original", "original-dark", "retro" })
            using (var owner = new MainForm(true, new Config { SingleContentStore = false, Theme = initial }))
            using (var profiles = new ProfileSettingsDialog(owner, config))
            {
                owner.Show();
                var windows = new List<Form> {
                    new AboutDialog(), new SettingsDialog(config),
                    new PreviewDialog(new OperationPlan { Label = "Review changes", Actions = new List<string> { "New: song.txt" } }),
                    new SelectionDialog("Choose backup", new List<string> { "Backup" }),
                    new ChoiceDialog("Continue?", new List<Option>()), new SearchDialog("Search", "Enter a name"),
                    new CatalogDialog(owner, config, false, catalog), new CatalogDialog(owner, config, true, catalog),
                    new RecipeViewerDialog(owner, null), new RestoreBrowserDialog(owner, new Snapshot { Name = "Theme test" }), profiles.CreateProfilePrompt()
                };
                try
                {
                    foreach (Form window in windows)
                    {
                        CheckNativeTitleBar(window, initial == "original-dark");
                        Assert(object.ReferenceEquals(owner.Icon, window.Icon), "window has app icon before opening: " + window.Text);
                        window.Show(owner);
                    }
                    profiles.Show(owner);
                    foreach (string theme in new[] { "original-dark", "retro", "original" })
                    {
                        GuiTheme.ApplyTheme(theme);
                        CheckNativeTitleBar(owner, theme == "original-dark");
                        CheckNativeTitleBar(profiles, theme == "original-dark");
                        foreach (Form window in windows) CheckNativeTitleBar(window, theme == "original-dark");
                        var preferences = (SettingsDialog)windows[1];
                        foreach (string name in new[] { "VerifyCopies", "CompareContents", "ReviewChanges", "PointInTimeHistory", "RecoveryCopies" })
                            Assert(preferences.Controls.Find(name, true)[0].ForeColor == GuiTheme.Ink,
                                "settings checkbox label follows the palette: " + theme + " / " + name);
                    }
                }
                finally { foreach (Form window in windows) window.Dispose(); }
            }
        }
        finally { GuiTheme.ApplyTheme("original"); }
    }

    static void SetGuiTheme(MainForm form, int selected, bool save)
    {
        Exception failure = null;
        using (var automation = new System.Windows.Forms.Timer { Interval = 20 })
        {
            automation.Tick += delegate {
                foreach (Form owned in form.OwnedForms)
                {
                    if (!(owned is SettingsDialog)) continue;
                    automation.Stop();
                    try
                    {
                        var tabs = FindGuiControl<TabControl>(owned); tabs.SelectedIndex = 3;
                        var theme = (ComboBox)owned.Controls.Find("ColorTheme", true)[0];
                        Assert(theme.Items.Count == 3 && ((string)theme.Items[0]).StartsWith("Original —") && ((string)theme.Items[1]).StartsWith("Original (dark)") &&
                            ((string)theme.Items[2]).StartsWith("Retro"), "Settings lists Original, Original (dark) and Retro, in that order");
                        theme.SelectedIndex = selected;
                        if (save) ((Button)owned.Controls.Find("SaveSettings", true)[0]).PerformClick();
                        else ((Button)owned.CancelButton).PerformClick();
                    }
                    catch (Exception ex) { failure = ex; owned.Close(); }
                    break;
                }
            };
            automation.Start(); ((Button)form.Controls.Find("OpenSettings", true)[0]).PerformClick();
            if (failure != null) throw failure;
        }
    }

    static void TestGuiProgress()
    {
        using (var form = new MainForm(true))
        {
            form.Show(); form.PrepareOperation();
            form.UpdateProgress(new OperationProgress { Stage = Stage.Copying, Path = "private-file.wav", OverallOnly = true, Bytes = 5, TotalBytes = 10 });
            form.DrainDisplay();
            Assert(!((Label)form.Controls.Find("ProgressMetrics", true)[0]).Text.Contains("50%"), "full backups suppress individual file copy progress");
            form.UpdateProgress(new OperationProgress { Stage = Stage.BackupCopy, Path = "private-file.wav", OverallOnly = true,
                Bytes = 5, TotalBytes = 10, Files = 1, TotalFiles = 2 });
            form.DrainDisplay();
            string text = ((Label)form.Controls.Find("ProgressMetrics", true)[0]).Text;
            Assert(text.Contains("50%") && text.Contains("5 B / 10 B") && text.Contains("Elapsed"), "overall progress includes percentage, byte counts and elapsed time");
            Assert(!((Label)form.Controls.Find("OperationDetail", true)[0]).Text.Contains("private-file.wav"), "full backup progress does not expose individual paths");
            form.CompleteOperation(new OperationResult { Summary = "Done", Counts = new OperationCounts { Copied = 1 } }, false);
            form.Close();
        }
    }

    // Histories in windows: Rename is offered for a device's history (only under profiles), and Device &
    // backup settings lists histories by nickname or readable name while keeping the folder name as key.
    static void TestHistoryNamesGui(string root)
    {
        var config = new Config { SingleContentStore = false, BackupFolder = Path.Combine(root, "store"), SettingsDirectory = root };
        string histories = HistoriesRoot(config), named = Path.Combine(histories, "profiles", "m8-card-DCE92C2E");
        Directory.CreateDirectory(Path.Combine(named, "2026-10-01"));
        Directory.CreateDirectory(Path.Combine(histories, "2026-09-01"));
        HistoryNames.SetNickname(named, "Studio card");
        var catalog = new BackupCatalog();
        catalog.Backups.Add(new CatalogBackup { Profile = HistoryNames.DisplayName(named), History = named, Path = Path.Combine(named, "2026-10-01"),
            Status = Status.Complete, CheckResult = "OK", Latest = true, Device = Devices.M8 });
        catalog.Backups.Add(new CatalogBackup { Profile = "Unmanaged folder", History = histories, Path = Path.Combine(histories, "2026-09-01"),
            Status = Status.Complete, CheckResult = "OK", Latest = true, Device = Devices.M8 });
        using (var owner = new MainForm(true, config))
        {
            using (var dashboard = new CatalogDialog(owner, config, false, catalog))
            {
                dashboard.Show(); Application.DoEvents();
                var table = (DataGridView)dashboard.Controls.Find("DashboardTable", true)[0];
                var rename = (Button)dashboard.Controls.Find("RenameHistory", true)[0];
                table.CurrentCell = table.Rows[0].Cells[0];
                Assert(Convert.ToString(table.Rows[0].Cells[0].Value) == "Studio card" && rename.Enabled, "a device's history shows its nickname and can be renamed");
                table.CurrentCell = table.Rows[1].Cells[0];
                Assert(!rename.Enabled, "an unmanaged folder cannot be renamed");
            }
            using (var settings = new ProfileSettingsDialog(owner, config))
            {
                settings.Show(); Application.DoEvents();
                Assert(settings.Text.StartsWith("Device & backup settings"), "the settings window has its new name: " + settings.Text);
                var kind = (ComboBox)FindGuiControls<ComboBox>(settings)[0];
                kind.SelectedIndex = 1; Application.DoEvents();
                var scope = (ComboBox)FindGuiControls<ComboBox>(settings)[1];
                var labels = new List<string>();
                foreach (object item in scope.Items) labels.Add(item.ToString());
                Assert(labels.Contains("Studio card"), "backup-history settings list histories by nickname: " + string.Join(" | ", labels.ToArray()));
            }
        }
    }

    static List<T> FindGuiControls<T>(Control parent) where T : Control
    {
        var found = new List<T>();
        foreach (Control child in parent.Controls)
        {
            if (child is T) found.Add((T)child);
            found.AddRange(FindGuiControls<T>(child));
        }
        return found;
    }

    // Window placement: sub-windows centre on the main window and stay on screen; the main window's
    // position is saved and restored, and ignored when it's damaged or no longer on any screen.
    static void TestWindowPlacement(string root)
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        Assert(GuiTheme.PlaceWindow(new Size(600, 400), new Rectangle(100, 100, 1000, 700), null, area) == new Point(300, 250),
            "a sub-window is centred on the main window");
        Assert(GuiTheme.PlaceWindow(new Size(600, 400), new Rectangle(1700, 800, 400, 300), null, area) == new Point(1320, 640),
            "a sub-window centred on a window near the screen edge is kept on screen");
        Assert(GuiTheme.PlaceWindow(new Size(1000, 720), null, new Point(200, 150), area) == new Point(200, 150),
            "the main window keeps its restored position");
        Assert(GuiTheme.PlaceWindow(new Size(1000, 720), null, null, area) == new Point(460, 160),
            "without a saved position the main window is centred on the screen");

        bool maximized;
        Rectangle? parsed = WindowPlacement.Parse(WindowPlacement.Format(new Rectangle(-1500, 40, 1100, 760), true).Split('\n'), out maximized);
        Assert(parsed == new Rectangle(-1500, 40, 1100, 760) && maximized, "a saved position reads back, including maximized");
        Assert(WindowPlacement.Parse(new[] { "Left=10", "Top=10", "Width=abc", "Height=500" }, out maximized) == null &&
            WindowPlacement.Parse(new[] { "Left=10", "Top=10", "Width=50", "Height=50" }, out maximized) == null,
            "a damaged or tiny saved position is ignored");
        var screens = new List<Rectangle> { area, new Rectangle(1920, 0, 1280, 1000) };
        Assert(WindowPlacement.OnScreen(new Rectangle(2000, 100, 900, 600), screens) && !WindowPlacement.OnScreen(new Rectangle(4000, 100, 900, 600), screens),
            "a position on a monitor that's no longer connected is not used");

        Directory.CreateDirectory(root);
        Rectangle primary = Screen.PrimaryScreen.WorkingArea;
        var saved = new Rectangle(primary.Left + 40, primary.Top + 30, 900, 650);
        using (var form = new Form { StartPosition = FormStartPosition.Manual, Bounds = saved })
            WindowPlacement.Save(form, root);
        using (var form = new Form { StartPosition = FormStartPosition.CenterScreen })
        {
            Assert(WindowPlacement.Restore(form, root) && form.StartPosition == FormStartPosition.Manual && form.Bounds == saved,
                "the main window opens where it was closed");
        }
        File.WriteAllText(Path.Combine(root, WindowPlacement.FileName), "Left=99999\r\nTop=99999\r\nWidth=900\r\nHeight=650\r\n");
        using (var form = new Form { StartPosition = FormStartPosition.CenterScreen })
            Assert(!WindowPlacement.Restore(form, root) && form.StartPosition == FormStartPosition.CenterScreen,
                "an off-screen saved position falls back to opening centred");
    }

    // View recipes, on Dashboard and Storage, is available only for a selected backup holding recipe
    // exports (an X100VI settings backup); ordinary backups and Storage's summary rows grey it out.
    static void TestRecipeButtons(string root)
    {
        string photos = Path.Combine(root, "m8-card-1", "2026-10-01"), settings = Path.Combine(root, "x100vi-settings-1", "2026-10-01");
        Put(Path.Combine(photos, "Songs", "song.m8s"), "SONG");
        Put(Path.Combine(settings, "C1.xml"), "<GearBackupCameraSettings />");
        var catalog = new BackupCatalog();
        catalog.Backups.Add(new CatalogBackup { Profile = "m8-card-1", History = Path.GetDirectoryName(photos), Path = photos,
            Status = Status.Complete, CheckResult = "OK", Latest = true, Device = Devices.M8 });
        catalog.Backups.Add(new CatalogBackup { Profile = "x100vi-settings-1", History = Path.GetDirectoryName(settings), Path = settings,
            Status = Status.Complete, CheckResult = "OK", Latest = true, Device = Devices.X100VI });
        foreach (bool storageMode in new[] { false, true })
        using (var owner = new MainForm(true, new Config { SingleContentStore = false }))
        using (var window = new CatalogDialog(owner, new Config { SingleContentStore = false }, storageMode, catalog))
        {
            window.Show(); Application.DoEvents();
            string where = storageMode ? "Storage" : "Dashboard";
            var table = (DataGridView)window.Controls.Find(storageMode ? "StorageTable" : "DashboardTable", true)[0];
            var button = (Button)window.Controls.Find("ViewCatalogRecipes", true)[0];
            table.CurrentCell = table.Rows[0].Cells[0];
            Assert(!button.Enabled, where + ": View recipes is greyed out for a backup without recipes");
            table.CurrentCell = table.Rows[1].Cells[0];
            Assert(button.Enabled, where + ": View recipes is available for a settings backup with recipes");
            if (storageMode)
            {
                table.CurrentCell = table.Rows[table.Rows.Count - 1].Cells[0];
                Assert(!button.Enabled, "Storage: View recipes is greyed out on a summary row");
            }
        }
    }

    // The Dashboard's Device filter: All by default, one entry per device with histories (plus Unknown
    // device for older ones), and a choice that survives a refresh.
    static void TestDashboardDeviceFilter()
    {
        var catalog = new BackupCatalog();
        foreach (var history in new[] { new { Profile = "m8-card-1", Device = Devices.M8 }, new { Profile = "Studio", Device = Devices.M8 },
                                       new { Profile = "op-xy-ABC", Device = Devices.OPXY }, new { Profile = "Old card", Device = (DeviceProfile)null } })
            catalog.Backups.Add(new CatalogBackup { Profile = history.Profile, History = @"C:\Backups\" + history.Profile,
                Path = @"C:\Backups\" + history.Profile + @"\2026-10-01", Status = Status.Complete, CheckResult = "OK", Latest = true, Device = history.Device });
        using (var owner = new MainForm(true, new Config { SingleContentStore = false }))
        using (var dashboard = new CatalogDialog(owner, new Config { SingleContentStore = false }, false, catalog))
        {
            dashboard.Show(); Application.DoEvents();
            var filter = (ComboBox)dashboard.Controls.Find("DashboardDevice", true)[0];
            var table = (DataGridView)dashboard.Controls.Find("DashboardTable", true)[0];
            var choices = new List<string>();
            foreach (object item in filter.Items) choices.Add((string)item);
            Assert(string.Join("|", choices.ToArray()) == "All devices|M8|OP-XY|Unknown device",
                "the Device filter lists All devices, each device with histories, and Unknown device: " + string.Join("|", choices.ToArray()));
            Assert((string)filter.SelectedItem == "All devices" && table.Rows.Count == 4, "the Dashboard shows every profile by default");
            filter.SelectedItem = "M8";
            Assert(table.Rows.Count == 2 && Convert.ToString(table.Rows[0].Cells[0].Value) == "m8-card-1" &&
                Convert.ToString(table.Rows[1].Cells[0].Value) == "Studio", "choosing a device lists only its profiles");
            filter.SelectedItem = "Unknown device";
            Assert(table.Rows.Count == 1 && Convert.ToString(table.Rows[0].Cells[0].Value) == "Old card", "Unknown device lists histories without a device");
            filter.SelectedItem = "OP-XY";
            dashboard.Populate(catalog);
            Assert((string)filter.SelectedItem == "OP-XY" && table.Rows.Count == 1 && Convert.ToString(table.Rows[0].Cells[0].Value) == "op-xy-ABC",
                "the chosen device stays selected after a refresh");
            filter.SelectedItem = "All devices";
            Assert(table.Rows.Count == 4, "All devices lists every profile again");
        }

        // Storage: the same filter for space usage and cleanup. Backups, old versions and ZIPs follow their
        // history's device; a mirror follows its folder name, so a device with only a mirror is listed too.
        catalog.Backups.Add(new CatalogBackup { Profile = "m8-card-1", History = @"C:\Backups\m8-card-1", Path = @"C:\Backups\m8-card-1\2026-09-01",
            Status = Status.Complete, CheckResult = "OK", Latest = false, Device = Devices.M8 });
        catalog.Cleanup.Add(new StorageItem { Kind = StorageKind.OlderBackup, History = @"C:\Backups\m8-card-1", Path = @"C:\Backups\m8-card-1\2026-09-01", Bytes = 100, Folder = true });
        catalog.Cleanup.Add(new StorageItem { Kind = StorageKind.Zip, History = @"C:\Backups\op-xy-ABC", Path = @"C:\Backups\op-xy-ABC\2026-10-01.zip", Bytes = 30 });
        catalog.Cleanup.Add(new StorageItem { Kind = StorageKind.Mirror, History = @"C:\Mirrors", Path = @"C:\Mirrors\x100vi-SERIAL", Bytes = 500, Folder = true });
        using (var owner = new MainForm(true, new Config { SingleContentStore = false }))
        using (var storage = new CatalogDialog(owner, new Config { SingleContentStore = false }, true, catalog))
        {
            storage.Show(); Application.DoEvents();
            var filter = (ComboBox)storage.Controls.Find("StorageDevice", true)[0];
            var usage = (DataGridView)storage.Controls.Find("StorageTable", true)[0];
            var cleanup = (DataGridView)storage.Controls.Find("CleanupItems", true)[0];
            var choices = new List<string>();
            foreach (object item in filter.Items) choices.Add((string)item);
            Assert(string.Join("|", choices.ToArray()) == "All devices|M8|OP-XY|X100VI|Unknown device",
                "the Storage filter also lists devices that only have a mirror: " + string.Join("|", choices.ToArray()));
            Assert((string)filter.SelectedItem == "All devices" && usage.Rows.Count == 7 && cleanup.Rows.Count == 3,
                "Storage shows every backup and cleanup item by default");
            cleanup.Rows[0].Cells[0].Value = true;
            filter.SelectedItem = "M8";
            Assert(usage.Rows.Count == 5 && cleanup.Rows.Count == 1 && cleanup.Rows[0].Tag == catalog.Cleanup[0],
                "choosing a device lists only its backups and cleanup items");
            Assert(storage.SelectedCleanup().Count == 0, "changing the filter clears selections, so nothing hidden can be recycled");
            filter.SelectedItem = "X100VI";
            Assert(usage.Rows.Count == 2 && cleanup.Rows.Count == 1 && Convert.ToString(usage.Rows[1].Cells[1].Value) == FormatSize(500) &&
                Convert.ToString(usage.Rows[0].Cells[1].Value) == FormatSize(0), "summary rows add up only the chosen device's ZIPs and mirrors");
            filter.SelectedItem = "OP-XY";
            Assert(cleanup.Rows.Count == 1 && Convert.ToString(usage.Rows[usage.Rows.Count - 2].Cells[1].Value) == FormatSize(30),
                "a ZIP follows its history's device");
            filter.SelectedItem = "All devices";
            Assert(usage.Rows.Count == 7 && cleanup.Rows.Count == 3, "All devices shows everything again");

            // Cleanup in plain words: what each item is, what removing it means, and what's selected.
            Func<int, string> itemText = r => Convert.ToString(cleanup.Rows[r].Cells["Item"].Value);
            Func<int, string> effect = r => Convert.ToString(cleanup.Rows[r].Cells["If removed"].Value);
            Assert(itemText(0) == "Older backup · m8-card-1 · 2026-09-01" && effect(0) == "Can't restore this date anymore",
                "an older backup is described with its history and date: " + itemText(0));
            Assert(itemText(1) == "ZIP copy · op-xy-ABC · 2026-10-01" && effect(1) == "Backup folder stays", "a ZIP copy is described: " + itemText(1));
            Assert(itemText(2) == "Downloaded device copy · X100VI · photos" && effect(2) == "Downloaded again next backup" &&
                cleanup.Rows[2].Cells["If removed"].ToolTipText.Contains("Your backups are not affected"),
                "a mirror is described as a downloaded device copy that's safe to remove: " + itemText(2));
            Assert(cleanup.Columns["Selected"].HeaderText == "Remove", "the tick column is labelled Remove");
            var selection = storage.Controls.Find("CleanupSelection", true)[0];
            var preview = (Button)storage.Controls.Find("PreviewCleanup", true)[0];
            Assert(selection.Text.StartsWith("Nothing selected") && !preview.Enabled, "Preview cleanup waits until something is ticked");
            cleanup.Rows[0].Cells[0].Value = true;
            cleanup.Rows[2].Cells[0].Value = true;
            Assert(selection.Text == "2 items selected · " + FormatSize(600) && preview.Enabled, "the selection total follows the ticks: " + selection.Text);
            cleanup.Rows[0].Cells[0].Value = false;
            cleanup.Rows[2].Cells[0].Value = false;
            Assert(!preview.Enabled, "unticking everything greys out Preview cleanup again");
            var tabs = FindGuiControl<TabControl>(storage);
            var rename = (Button)storage.Controls.Find("RenameHistory", true)[0];
            usage.CurrentCell = usage.Rows[0].Cells[0];
            tabs.SelectedIndex = 1; Application.DoEvents();
            Assert(!rename.Enabled, "Rename is greyed out while the Cleanup tab is showing");
            tabs.SelectedIndex = 0; Application.DoEvents();
        }

        // A device detected in the main window is where both windows' filters start.
        foreach (bool storageMode in new[] { false, true })
        using (var owner = new MainForm(true, new Config { SingleContentStore = false }))
        {
            string where = storageMode ? "Storage" : "Dashboard";
            using (var window = new CatalogDialog(owner, new Config { SingleContentStore = false }, storageMode, catalog, Devices.OPXY))
            {
                window.Show(); Application.DoEvents();
                var filter = (ComboBox)window.Controls.Find(storageMode ? "StorageDevice" : "DashboardDevice", true)[0];
                Assert((string)filter.SelectedItem == "OP-XY", where + " starts filtered to the detected device: " + filter.SelectedItem);
                filter.SelectedItem = "M8";
                window.Populate(catalog);
                Assert((string)filter.SelectedItem == "M8", where + ": after choosing another device, a refresh keeps that choice");
            }
            using (var window = new CatalogDialog(owner, new Config { SingleContentStore = false }, storageMode, catalog, Devices.TrackerMini))
            {
                window.Show(); Application.DoEvents();
                var filter = (ComboBox)window.Controls.Find(storageMode ? "StorageDevice" : "DashboardDevice", true)[0];
                Assert((string)filter.SelectedItem == "All devices", where + " stays on All devices when the detected device has nothing to show");
            }
        }
    }

    static void TestManagementGui(string root)
    {
        Config config; BackupCatalog catalog = ManagementFixture(root, out config);
        string output = Path.Combine(Environment.CurrentDirectory, "tests", ".build");
        using (var owner = new MainForm(true))
        {
            owner.Show(); Application.DoEvents();
            foreach (string name in new[] { "OpenDashboard", "OpenStorage", "OpenProfiles" })
                Assert(owner.Controls.Find(name, true).Length == 1, "main window exposes " + name);
            Assert(owner.Controls.Find("OpenRecipes", true).Length == 0, "recipes open from Dashboard and Storage, not the main window");
            // A scan started from a tool window shows its progress there, never as a main-window operation.
            // Without an old-version record (backups from before it existed) the scan walks each backup's
            // files, reporting progress for every one.
            foreach (string record in Directory.GetFiles(HistoriesRoot(config), "old-versions.txt", SearchOption.AllDirectories)) File.Delete(record);
            var mainStatus = (Label)owner.Controls.Find("OperationStatus", true)[0];
            string statusBefore = mainStatus.Text;
            foreach (bool storageMode in new[] { false, true })
            using (var scanning = new CatalogDialog(owner, config, storageMode))
            {
                string window = storageMode ? "storage" : "dashboard";
                var notice = (Label)scanning.Controls.Find("ToolNotice", true)[0];
                var table = (DataGridView)scanning.Controls.Find(storageMode ? "StorageTable" : "DashboardTable", true)[0];
                scanning.Show();
                var waited = Stopwatch.StartNew();
                while ((table.Rows.Count == 0 || notice.Text.StartsWith("Working")) && waited.Elapsed < TimeSpan.FromSeconds(30))
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(20);
                }
                for (int i = 0; i < 20; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                Assert(table.Rows.Count > 0 && !notice.Text.StartsWith("Working"), "the " + window + " scans in the background and fills its table");
                if (!storageMode) Assert(Convert.ToString(table.Rows[0].Cells[5].Value) == "See Storage",
                    "Dashboard metadata scan directs users to Storage instead of displaying zero sizes");
                Assert(mainStatus.Text == statusBefore && !mainStatus.Text.Contains("Scanning"),
                    "a " + window + " scan leaves the main window's status alone: " + mainStatus.Text);
            }
            using (var dashboard = new CatalogDialog(owner, config, false, catalog))
            {
                dashboard.Show(); Application.DoEvents();
                var table = (DataGridView)dashboard.Controls.Find("DashboardTable", true)[0];
                Assert(table.Rows.Count == 1 && Convert.ToString(table.Rows[0].Cells[2].Value) == Status.Complete.ToString(),
                    "dashboard groups backups by profile and shows the latest status");
                CheckGuiLayout(dashboard, 150, Path.Combine(output, "dashboard-150.png"));
            }
            using (var storage = new CatalogDialog(owner, config, true, catalog))
            {
                storage.Show(); Application.DoEvents();
                var table = (DataGridView)storage.Controls.Find("CleanupItems", true)[0];
                Assert(storage.SelectedCleanup().Count == 0, "storage cleanup starts with no selected items");
                table.Rows[0].Cells[0].Value = true;
                Assert(storage.SelectedCleanup().Count == 1, "cleanup selection selects only checked items");
                CheckGuiLayout(storage, 150, Path.Combine(output, "storage-150.png"));
            }
            using (var dialog = new ProfileSettingsDialog(owner, config))
            {
                dialog.Show(); Application.DoEvents();
                Assert(dialog.Controls.Find("ProfileCategoryButtons", true)[0].Controls.Count == 3,
                    "profile category buttons are present before and after rebuilding scope fields");
                var scope = (ComboBox)dialog.Controls.Find("SettingsScope", true)[0]; scope.SelectedIndex = 0;
                var inherited = (CheckBox)dialog.Controls.Find("ProfileUseDefault_PointInTimeHistory", true)[0];
                var historyValue = (ComboBox)dialog.Controls.Find("ProfileValue_PointInTimeHistory", true)[0];
                Assert(inherited.Checked && !historyValue.Enabled, "profile settings clearly disable values while using defaults");
                inherited.Checked = false; historyValue.SelectedIndex = 1;
                Assert(historyValue.Enabled && historyValue.Text == "Off", "custom profile values use plain On and Off choices");
                scope.SelectedIndex = 1; scope.SelectedIndex = 0;
                historyValue = (ComboBox)dialog.Controls.Find("ProfileValue_PointInTimeHistory", true)[0];
                Assert(historyValue.Text == "Off", "switching devices preserves unsaved settings");
                ((Button)dialog.Controls.Find("SaveProfilePreferences", true)[0]).PerformClick();
                Assert(!ResolveProfileSettings(config, "m8", "m8-test").PointInTimeHistory && config.PointInTimeHistory,
                    "profile GUI saves an override without changing global settings");
                ((Button)dialog.Controls.Find("ResetProfilePreferences", true)[0]).PerformClick();
                Assert(historyValue.Text == "On", "reset displays the inherited value in the disabled field");
                Assert(!ResolveProfileSettings(config, "m8", "m8-test").PointInTimeHistory, "reset does not change saved preferences before Save changes");
                ((Button)dialog.Controls.Find("SaveProfilePreferences", true)[0]).PerformClick();
                Assert(ResolveProfileSettings(config, "m8", "m8-test").PointInTimeHistory, "saving a reset returns to inherited preferences");
                CheckGuiLayout(dialog, 150, Path.Combine(output, "profiles-150.png"));
            }
            Dictionary<string, byte[]> files;
            using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate()) files = CaptureFujiSettings(new FakeFujiSettings());
            string folder = Path.Combine(root, "recipes"); Directory.CreateDirectory(folder);
            foreach (var file in files) File.WriteAllBytes(Path.Combine(folder, file.Key), file.Value);
            var stored = new BackupEngine().Run(folder, Path.Combine(root, "recipe-content-store"), null, false,
                new Config { SingleContentStore = true, Preview = false }, new OperationContext(new TestInteraction(), CancellationToken.None));
            Assert(stored.ExitCode == 0 && RecipeViewerDialog.HasRecipes(stored.OutputFolder), "single content store exposes backed-up camera recipes");
            using (var storedViewer = new RecipeViewerDialog(owner, stored.OutputFolder))
                Assert(((ComboBox)storedViewer.Controls.Find("RecipeSelection", true)[0]).Items.Count == 8, "recipe viewer reads all recipes directly from stored content");
            using (var viewer = new RecipeViewerDialog(owner, folder))
            {
                viewer.Show(); Application.DoEvents();
                var picker = (ComboBox)viewer.Controls.Find("RecipeSelection", true)[0];
                var table = (DataGridView)viewer.Controls.Find("RecipeValues", true)[0];
                Assert(picker.Items.Count == 8 && table.Rows.Count == 23, "recipe viewer loads C1–C7 and shooting settings");
                Assert(Convert.ToString(table.Rows[4].Cells[1].Value) == "PROVIA / Standard", "recipe viewer displays film names");
                picker.SelectedIndex = 1;
                Assert(Convert.ToString(table.Rows[4].Cells[1].Value) == "Velvia / Vivid", "recipe selection updates readable values");
                CheckGuiLayout(viewer, 150, Path.Combine(output, "recipes-150.png"));
            }
            using (var dashboard = new CatalogDialog(owner, config, false, catalog)) CheckGuiLayout(dashboard, 200, Path.Combine(output, "dashboard-200.png"));
            using (var storage = new CatalogDialog(owner, config, true, catalog)) CheckGuiLayout(storage, 200, Path.Combine(output, "storage-200.png"));
            using (var dialog = new ProfileSettingsDialog(owner, config)) CheckGuiLayout(dialog, 200, Path.Combine(output, "profiles-200.png"));
            using (var viewer = new RecipeViewerDialog(owner, folder)) CheckGuiLayout(viewer, 200, Path.Combine(output, "recipes-200.png"));

            // A damaged device record or a linked history doesn't stop the Profiles window opening.
            string profilesRoot = Path.Combine(HistoriesRoot(config), "profiles");
            string damaged = AppIdentity.MetadataRoot(Path.Combine(profilesRoot, "Damaged", "2026-03-01"));
            Directory.CreateDirectory(damaged);
            File.WriteAllText(Path.Combine(damaged, "device.txt"), new string('x', 200));
            string elsewhere = Path.Combine(root, "linked-history");
            Directory.CreateDirectory(Path.Combine(elsewhere, "2026-03-01"));
            CreateTestJunction(Path.Combine(profilesRoot, "Linked"), elsewhere);
            string failure = null;
            try { using (var dialog = new ProfileSettingsDialog(owner, config)) { dialog.Show(); Application.DoEvents(); } }
            catch (Exception ex) { failure = ex.Message; }
            Assert(failure == null, "the Profiles window opens despite a damaged device record and a linked history: " + failure);
            owner.Close();
        }
        string deviceLocation = Path.Combine(root, "device-location"), profileLocation = Path.Combine(root, "profile-location");
        WithUi(() => {
            SaveProfileSettings(config, "device:m8", new Dictionary<string, string> { { "BackupFolder", deviceLocation }, { "PointInTimeHistory", "no" }, { "Preview", "no" } });
            SaveProfileSettings(config, "profile:Studio", new Dictionary<string, string> { { "BackupFolder", profileLocation } });
        });
        using (var form = new MainForm(true))
        {
            typeof(MainForm).GetField("config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(form, config);
            form.Show(); Application.DoEvents();
            ((TextBox)form.Controls.Find("SourceFolder", true)[0]).Text = Path.Combine(root, "source");
            ((TextBox)form.Controls.Find("BackupFolder", true)[0]).Text = "";
            var emptyLocation = (Config)typeof(MainForm).GetMethod("CaptureConfig", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(form, new object[] { false });
            Assert(emptyLocation.BackupFolder == DefaultBackupFolder(), "an empty GUI location uses the user profile Backup folder");
            ((TextBox)form.Controls.Find("BackupFolder", true)[0]).Text = config.BackupFolder;
            ((TextBox)form.Controls.Find("Profile", true)[0]).Text = "Studio";
            form.SelectOperation(GuiOperation.FullBackup);
            ((TabControl)form.Controls.Find("WorkflowTabs", true)[0]).SelectedIndex = 1;
            ((Button)form.Controls.Find("StartOperation", true)[0]).PerformClick();
            var status = (Label)form.Controls.Find("OperationStatus", true)[0];
            bool askedToCreate = false;
            DriveGui(form, status, "Complete", false, dialog => {
                if (dialog is ChoiceDialog)
                {
                    askedToCreate = true;
                    Assert(!Directory.Exists(profileLocation), "GUI asks before creating the profile destination");
                }
            });
            Assert(askedToCreate, "GUI presents the missing-folder creation question");
            string backup = LatestBackup(Path.Combine(HistoriesRoot(new Config { SingleContentStore = false, BackupFolder = profileLocation }), "profiles", "Studio"));
            Assert(backup != null && File.Exists(Path.Combine(backup, "song.txt")) && !Directory.Exists(SnapshotRoot(backup)),
                "GUI backup applies profile location and inherited device history preferences before running");
            Assert(!Directory.Exists(deviceLocation), "profile destination takes priority over the device destination");
            form.Close();
        }
    }

    static void TestRestoreLocationsGui(string root)
    {
        Directory.CreateDirectory(root);
        var config = new Config { SingleContentStore = false, BackupFolder = Path.Combine(root, "global"), SettingsDirectory = root, Preview = false };
        string alternate = Path.Combine(root, "tracker");
        SaveProfileSettings(config, "device:tracker-mini", new Dictionary<string, string> {
            { "BackupFolder", alternate }, { "PointInTimeHistory", "no" }, { "Verify", "yes" }
        });
        string source = Folder(root, "source"), relative = "Projects\\Demo\\take_2026-01-01_120000.wav";
        Put(Path.Combine(source, "Projects", "Demo", "project.mt"), "PROJECT");
        Put(Path.Combine(source, relative), "SAMPLE");
        string history = Path.Combine(HistoriesRoot(new Config { SingleContentStore = false, BackupFolder = alternate }), "profiles", "Studio");
        new BackupEngine().Run(source, history, null, false, ResolveProfileSettings(config, "tracker-mini", "Studio"),
            new OperationContext(new TestInteraction(), CancellationToken.None) { Device = Devices.TrackerMini });
        using (var form = new MainForm(true))
        {
            typeof(MainForm).GetField("config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(form, config);
            form.Show(); Application.DoEvents();
            string target = Folder(root, "target");
            ((TextBox)form.Controls.Find("RestoreDestination", true)[0]).Text = target;
            ((TextBox)form.Controls.Find("BackupFolder", true)[0]).Text = config.BackupFolder;
            ((TextBox)form.Controls.Find("Profile", true)[0]).Text = "";
            var start = (Button)form.Controls.Find("StartOperation", true)[0];
            var status = (Label)form.Controls.Find("OperationStatus", true)[0];
            form.ShowDevice(Devices.TrackerMini, "USB drive");
            form.SelectOperation(GuiOperation.Restore); WaitForRestorePicker(form); start.PerformClick();
            DriveGui(form, status, "Complete", false);
            Assert(((ComboBox)form.Controls.Find("RestoreBackup", true)[0]).Items.Count == 1, "GUI lists only backups matching the identified Tracker Mini across override locations");
            Assert(File.ReadAllText(Path.Combine(target, relative)) == "SAMPLE", "GUI current-file restore preserves dated samples from an override location");
            form.SelectOperation(GuiOperation.Check); start.PerformClick();
            DriveGui(form, status, "Complete", false);
            Assert(File.Exists(Path.Combine(AppIdentity.MetadataRoot(LatestBackup(history)), "last-check.txt")),
                "GUI checks find alternate locations without a named profile or source device");
            Assert(!Directory.Exists(config.BackupFolder), "restore and check do not create an unused global backup location");
            form.Close();
        }
    }

    static void TestScaledGuiLayouts()
    {
        string output = Path.Combine(Environment.CurrentDirectory, "tests", ".build");
        foreach (int percent in new[] { 100, 150, 200 })
        {
            using (var form = new MainForm(true)) CheckGuiLayout(form, percent, Path.Combine(output, "main-" + percent + ".png"));
            using (var form = new MainForm(true))
            {
                form.ShowDevice(Devices.OP1Field, "MTP");
                form.ShowDeviceInformation(DeviceInformation.Read(new SourceChoice { Mtp = new MtpSource { Device = Devices.OP1Field } }));
                CheckGuiLayout(form, percent, Path.Combine(output, "main-device-" + percent + ".png"));
            }
            using (var form = new MainForm(true))
            {
                form.Show(); ((TabControl)form.Controls.Find("WorkflowTabs", true)[0]).SelectedIndex = 1;
                ((Button)form.Controls.Find("ToggleBackupOptions", true)[0]).PerformClick();
                CheckGuiLayout(form, percent, Path.Combine(output, "main-options-" + percent + ".png"));
            }
            using (var form = new MainForm(true))
            {
                form.Show(); ((TabControl)form.Controls.Find("WorkflowTabs", true)[0]).SelectedIndex = 2;
                CheckGuiLayout(form, percent, Path.Combine(output, "main-restore-" + percent + ".png"));
            }
            using (var form = new SettingsDialog(new Config { SingleContentStore = false })) CheckGuiLayout(form, percent, Path.Combine(output, "settings-" + percent + ".png"));
            using (var form = new SettingsDialog(new Config { SingleContentStore = false }))
            {
                FindGuiControl<TabControl>(form).SelectedIndex = 3;
                CheckGuiLayout(form, percent, Path.Combine(output, "appearance-" + percent + ".png"));
            }
            using (var form = new MainForm(true, new Config { SingleContentStore = false, Theme = "original" }))
                CheckGuiLayout(form, percent, Path.Combine(output, "main-original-" + percent + ".png"));
            GuiTheme.ApplyTheme("retro");
            foreach (string theme in new[] { "original", "original-dark" })
            {
                using (var form = new MainForm(true, new Config { SingleContentStore = false, Theme = theme }))
                    CheckGuiLayout(form, percent, Path.Combine(output, "main-" + theme + "-" + percent + ".png"));
                using (var form = new MainForm(true, new Config { SingleContentStore = false, Theme = theme }))
                {
                    form.ShowDevice(Devices.OP1Field, "MTP");
                    form.ShowDeviceInformation(DeviceInformation.Read(new SourceChoice { Mtp = new MtpSource { Device = Devices.OP1Field } }));
                    CheckGuiLayout(form, percent, Path.Combine(output, "main-device-" + theme + "-" + percent + ".png"));
                }
                using (var form = new MainForm(true, new Config { SingleContentStore = false, Theme = theme }))
                {
                    form.Show(); ((TabControl)form.Controls.Find("WorkflowTabs", true)[0]).SelectedIndex = 1;
                    ((Button)form.Controls.Find("ToggleBackupOptions", true)[0]).PerformClick();
                    CheckGuiLayout(form, percent, Path.Combine(output, "main-options-" + theme + "-" + percent + ".png"));
                }
                using (var form = new SettingsDialog(new Config { SingleContentStore = false, Theme = theme }))
                    CheckGuiLayout(form, percent, Path.Combine(output, "settings-" + theme + "-" + percent + ".png"));
                using (var form = new SettingsDialog(new Config { SingleContentStore = false, Theme = theme }))
                {
                    FindGuiControl<TabControl>(form).SelectedIndex = 3;
                    CheckGuiLayout(form, percent, Path.Combine(output, "appearance-" + theme + "-" + percent + ".png"));
                }
                using (var form = new PreviewDialog(new OperationPlan { Label = "Review changes", Actions = new List<string> { @"New: Songs\demo.m8s" }, Bytes = 4 }))
                    CheckGuiLayout(form, percent, Path.Combine(output, "preview-" + theme + "-" + percent + ".png"));
            }
            GuiTheme.ApplyTheme("retro");
            using (var form = new PreviewDialog(new OperationPlan { Label = "Review changes", Actions = new List<string> { @"New: Songs\demo.m8s" }, Bytes = 4 }))
                CheckGuiLayout(form, percent, Path.Combine(output, "preview-" + percent + ".png"));
        }
    }
    static void CheckGuiLayout(Form form, int percent, string path)
    {
        form.Show(); Application.DoEvents();
        SizeF current = form.CurrentAutoScaleDimensions;
        float factor = percent / 100f / (current.Height / 96f);
        var fonts = new Dictionary<Control, Font>();
        RememberGuiFonts(form, fonts);
        Size original = form.ClientSize;
        form.AutoScaleMode = AutoScaleMode.None;
        form.Scale(new SizeF(factor, factor));
        form.ClientSize = new Size((int)(original.Width * factor), (int)(original.Height * factor));
        foreach (var entry in fonts) entry.Key.Font = new Font(entry.Value.FontFamily, entry.Value.Size * factor, entry.Value.Style);
        form.PerformLayout(); Application.DoEvents();
        var buttons = new List<Button>(); CollectGuiButtons(form, buttons);
        foreach (Button button in buttons)
            if (button.Visible) Assert(button.Height >= button.Font.Height + 4 && button.Parent.ClientRectangle.Contains(button.Bounds),
                "action button remains usable at " + percent + "%: " + button.Text);
        using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
        {
            form.Controls[0].DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        form.Close();
    }
    static void RememberGuiFonts(Control control, Dictionary<Control, Font> fonts)
    {
        fonts[control] = control.Font;
        foreach (Control child in control.Controls) RememberGuiFonts(child, fonts);
    }
    static bool InvokeGuiShortcut(Form form, Keys keys)
    {
        var method = form.GetType().GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        object[] arguments = { Message.Create(form.Handle, 0x100, IntPtr.Zero, IntPtr.Zero), keys };
        return (bool)method.Invoke(form, arguments);
    }

    static void TestRestoreFileBrowser()
    {
        var snapshot = new Snapshot { Name = "Example" };
        snapshot.Folders.AddRange(new[] { "Songs", @"Songs\Empty", "Samples" });
        snapshot.Files.Add(new RestoreEntry { Relative = @"Songs\one.m8s", Size = 4 });
        snapshot.Files.Add(new RestoreEntry { Relative = @"Songs\two.m8s", Size = 4 });
        snapshot.Files.Add(new RestoreEntry { Relative = @"Samples\kick.wav", Size = 4 });
        using (var owner = new MainForm(true))
        using (var browser = new RestoreBrowserDialog(owner, snapshot))
        {
            browser.Show(); Application.DoEvents();
            var tree = (TreeView)browser.Controls.Find("RestoreFileTree", true)[0];
            TreeNode songs = null, samples = null;
            foreach (TreeNode node in tree.Nodes) { if ((string)node.Tag == "Songs") songs = node; if ((string)node.Tag == "Samples") samples = node; }
            Assert(!songs.IsExpanded && !samples.IsExpanded && browser.ClientSize.Width <= 720, "the file browser starts with its folders collapsed in a moderate-width window");
            var closeButton = (Button)browser.CancelButton;
            Assert(browser.RectangleToClient(closeButton.RectangleToScreen(closeButton.ClientRectangle)).Bottom >= browser.ClientSize.Height - 40,
                "tool window buttons sit at the bottom of the window with no dead space beneath them");
            songs.Checked = true;
            Assert(browser.SelectedPaths.Length == 1 && browser.SelectedPaths[0] == "Songs" && !samples.Checked, "checking a folder selects its subtree without including other folders");
            TreeNode one = null; foreach (TreeNode node in songs.Nodes) if ((string)node.Tag == @"Songs\one.m8s") one = node;
            one.Checked = false;
            Assert(!songs.Checked && Array.IndexOf(browser.SelectedPaths, @"Songs\one.m8s") < 0 && Array.IndexOf(browser.SelectedPaths, @"Songs\two.m8s") >= 0,
                "unchecking one child preserves the other selected files and empty folder");
            ((Button)browser.Controls.Find("ClearRestoreFiles", true)[0]).PerformClick();
            Assert(browser.SelectedPaths.Length == 0, "clear selection clears every subtree");
            one.Checked = true;
            ((Button)browser.Controls.Find("UseRestoreSelection", true)[0]).PerformClick();
            Assert(browser.DialogResult == DialogResult.OK && browser.SelectedPaths.Length == 1 && browser.SelectedPaths[0] == @"Songs\one.m8s", "file browser accepts an individual file");
        }
        using (var owner = new MainForm(true))
        using (var browser = new RestoreBrowserDialog(owner, snapshot, new[] { @"Songs\Empty" }))
        {
            browser.Show(); Application.DoEvents();
            Assert(browser.SelectedPaths.Length == 1 && browser.SelectedPaths[0] == @"Songs\Empty", "opening the browser again restores the previous empty-folder selection");
            ((Button)browser.Controls.Find("SelectAllRestoreFiles", true)[0]).PerformClick();
            Assert(browser.SelectedPaths.Length == 2, "select all selects complete top-level subtrees");
            browser.Close();
        }
    }

    static void WaitForRestorePicker(Form form)
    {
        var picker = (ComboBox)form.Controls.Find("RestorePoint", true)[0];
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while ((!picker.Enabled || picker.SelectedItem == null) && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Assert(picker.Enabled && picker.SelectedItem != null, "restore picker loads matching device backups and defaults to the latest point");
    }

    static void DriveGui(Form form, Label state, string expected, bool dismiss, Action<Form> inspect = null)
    {
        var timer = Stopwatch.StartNew();
        var handled = new HashSet<Form>();
        Exception failure = null;
        using (var automation = new System.Windows.Forms.Timer { Interval = 20 })
        {
        automation.Tick += delegate {
            try
            {
                foreach (Form owned in form.OwnedForms)
                {
                    if (!owned.Visible || !handled.Add(owned)) continue;
                    if (inspect != null) inspect(owned);
                    if (dismiss) { owned.DialogResult = DialogResult.Cancel; owned.Close(); continue; }
                    var choices = FindGuiControl<ListBox>(owned);
                    if (choices != null && choices.Items.Count > 0 && choices.SelectionMode != SelectionMode.None) choices.SelectedIndex = 0;
                    var buttons = new List<Button>();
                    CollectGuiButtons(owned, buttons);
                    Button button = buttons.Find(b => b.Text == "All files" || b.Text == "Merge");
                    if (button == null) button = buttons.Find(b => b.Text == "Proceed" || b.Text == "Close preview" || b.Text == "Continue");
                    if (button == null) button = buttons.Find(b => b.Text == "Yes");
                    if (button == null) throw new Exception("Unexpected GUI decision: " + owned.Text);
                    button.PerformClick();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                foreach (Form owned in form.OwnedForms) { owned.DialogResult = DialogResult.Cancel; owned.Close(); }
            }
        };
        automation.Start();
        while (timer.Elapsed.TotalSeconds < 25)
        {
            Application.DoEvents();
            if (failure != null) throw failure;
            if (state.Text == expected) return;
            if (state.Text == "Needs attention") throw new Exception("GUI operation failed: " + FindGuiControl<RichTextBox>(form).Text);
            Thread.Sleep(15);
        }
        throw new Exception("GUI operation timed out; state=" + state.Text);
        }
    }
    static T FindGuiControl<T>(Control parent) where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T) return (T)child;
            T found = FindGuiControl<T>(child); if (found != null) return found;
        }
        return null;
    }
    static void CollectGuiButtons(Control parent, List<Button> buttons)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Button) buttons.Add((Button)child);
            CollectGuiButtons(child, buttons);
        }
    }
}
