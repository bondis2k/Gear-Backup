// GUI tests of the main window: the Backup and Restore workflow, previews, folder picker, progress, placement and larger text sizes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using static GearBackup.PathSafety;
using static GearBackup.ConsoleUi;
using static GearBackup.Formatting;
using static GearBackup.Snapshots;
using static GearBackup.Backups;
using static GearBackup.FujiSettings;
using static GearBackup.MtpMirror;
using static GearBackup.StorageCatalog;
using static GearBackup.AppSettings;
using static GearBackup.ProfileSettings;
using static GearBackup.Session;
using static GearBackup.ProfileNaming;
using static GearBackup.Gui.GearGui;
using static GearBackup.BackupFolders;
using GearBackup;
using GearBackup.Cli;
using GearBackup.Gui;

partial class Tests
{
    [Test("Main window workflow", Gui = true, Folder = ".")]
    static void TestMainWindowWorkflow(string root)
    {
        string source = Path.Combine(root, "card");
        Put(Path.Combine(source, "Songs", "one.m8s"), "GUI TEST SONG");
        Put(Path.Combine(source, "Samples", "kick.wav"), "GUI TEST SAMPLE");
        using (var form = CreateGui(true))
        {
            form.Show();
            // This workflow exercises snapshot restore; normal startup leaves history off.
            var formConfig = (Config)typeof(MainForm).GetField("config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form); // this workflow checks browsable backup folders
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
            Assert(workflowTabs.TabPages.Count == 3 && workflowTabs.TabPages[0].Text == "Device" && workflowTabs.TabPages[1].Text == "Backup" && workflowTabs.TabPages[2].Text == "Restore" &&
                workflowTabs.SelectedIndex == 0 && form.Controls.Find("BrowseSong", true).Length == 0,
                "main window starts with Device, followed by separate Backup and Restore tabs, and no Song tab");
            Assert(!optionalFields.Visible && !sourceBox.Visible && !backupBox.Visible && !form.Controls.Find("Profile", true)[0].Visible && !cancel.Visible,
                "main window initially focuses on detection and backup instead of optional fields");
            workflowTabs.SelectedIndex = 1;
            var backupTypes = (ComboBox)form.Controls.Find("BackupOperation", true)[0];
            Assert(backupTypes.Items.Count == 3 && backupTypes.Items[2].ToString() == "Zip archive" && !backupTypes.Items.Contains("Restore files") && !backupTypes.Items.Contains("Check backups"), "Backup tab starts with full, incremental and ZIP archive backup");
            moreOptions.PerformClick();
            Assert(optionalFields.Visible && sourceBox.Visible && backupBox.Visible, "More options exposes the folder and profile controls");
            moreOptions.PerformClick();
            Assert(!optionalFields.Visible, "optional fields can be collapsed again");
            using (var configured = new MainForm(true, new Config { SourceFolder = @"D:\Card", Profile = "Studio", BackupFolder = @"E:\Backups" }))
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
            Assert(workflowTabs.SelectedIndex == 0 && backupTypes.SelectedIndex == 3 && form.Controls.Find("DeviceStorageDetails", true)[0].Text.Contains("C1–C7"),
                "camera detection stays on Device and prepares settings capture on Backup");
            Assert(backupTypes.Items.Count == 4, "settings backup appears for a detected X100VI in settings mode");
            detectionRunner.ShowDetected(new SourceChoice { Detected = Devices.X100VI, Mtp = new MtpSource { Device = Devices.X100VI }, CameraMode = FujiModeName(false) });
            Assert(backupTypes.Items.Count == 3 && backupTypes.SelectedIndex == 0, "photo mode removes settings backup and resets a stale settings selection");
            detectionRunner.ShowDetected(new SourceChoice { Detected = Devices.OP1Field, Mtp = new MtpSource { Device = Devices.OP1Field } });
            Assert(typeof(GuiOperationRunner).GetField("detectedDrive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(detectionRunner) == null,
                "MTP detection also clears the previous drive selection used for ejection");
            Assert(deviceLabel.Font.Size >= 17 && deviceLabel.ForeColor == GuiTheme.AccentDark && deviceImage.Width >= 88,
                "the detected device has a prominent name, highlight and larger picture");
            detectionRunner.ShowDetected(null);
            Assert(!connectionLabel.Visible && sourceBox.Text == "", "failed detection clears stale source and connection details");
            Assert(backupTypes.Items.Count == 3, "settings backup is hidden after device detection is cleared");
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
            Assert(File.ReadAllText(Snapshots.CurrentBackup(backup).Files.Find(f => f.Relative == @"Songs\one.m8s").StoredPath) == "GUI TEST SONG", "GUI full backup executes shared engine");
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
            DriveGui(form, state, "Cancelled", true);
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
        var saved = new Config { BackupFolder = root, Verify = true, Recovery = false, Thorough = false, Profile = "Studio" };
        SaveConfig(saved, ini);
        string text = File.ReadAllText(ini);
        Assert(text.Contains("; keep this comment") && text.Contains("CustomOption=unchanged"), "GUI settings save preserves comments and unrelated keys");
        Assert(text.Contains("Verify=yes") && !text.Contains("Verify=no") && text.Contains("Recovery=no") && text.Contains("Thorough=no"),
            "shared settings save updates duplicates and keeps checksum/recovery defaults optional");
        Assert(text.Contains("Profile=Studio") && text.Contains("SourceFolder="), "settings save includes paths and profile");
    }

    [Test("Preview filtering", Gui = true)]
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
                bitmap.Save(Path.Combine(TestBuildDirectory, "preview-example.png"), System.Drawing.Imaging.ImageFormat.Png);
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

    // The Restore tab holds Restore/Preview back until a point is chosen; going back to Backup must release them.
    [Test("Action buttons follow the operation", Gui = true)]
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

    // Browse buttons open the app's own folder picker, which follows the colour theme (Windows' picker does not).
    [Test("Folder picker", Gui = true)]
    static void TestFolderPicker()
    {
        // Under the working folder, not the temp folder: temp can be a short 8.3 path on a build machine.
        string top = Path.Combine(Environment.CurrentDirectory, ".test-runs", "gear-folder-picker-" + Guid.NewGuid().ToString("N")), deep = Path.Combine(top, "Songs", "Demos");
        Directory.CreateDirectory(deep); Directory.CreateDirectory(Path.Combine(top, "Samples"));
        try
        {
            // A short 8.3 spelling of the same folder (where the drive makes them) opens on the real folder too.
            string shortDeep = ShortPathOf(deep);
            if (shortDeep != deep)
                using (var shortPicker = new FolderPickerDialog("Choose a folder", shortDeep, false))
                {
                    var shortTree = (TreeView)shortPicker.Controls.Find("FolderTree", true)[0];
                    Assert(shortTree.SelectedNode != null && shortTree.SelectedNode.Text == "Demos" && ((TextBox)shortPicker.Controls.Find("FolderPath", true)[0]).Text == deep,
                        "the folder picker opens on a folder given by its short 8.3 path");
                }
            using (var picker = new FolderPickerDialog("Choose a folder", deep, true))
            using (var automation = new System.Windows.Forms.Timer { Interval = 50 })
            {
                int step = 0; Exception failure = null;
                var tree = (TreeView)picker.Controls.Find("FolderTree", true)[0];
                var path = (TextBox)picker.Controls.Find("FolderPath", true)[0];
                var message = picker.Controls.Find("FolderPickerMessage", true)[0];
                automation.Tick += delegate {
                    try
                    {
                        if (step == 0)
                        {
                            Assert(tree.SelectedNode != null && tree.SelectedNode.Text == "Demos" && path.Text == deep, "the folder picker opens on the current folder");
                            Assert(picker.Controls.Find("NewFolder", true).Length == 1, "the folder picker can make a new folder when asked to");
                            Assert(tree.BackColor == GuiTheme.Surface && tree.ForeColor == GuiTheme.Ink, "the folder picker uses the theme's colours");
                            var up = (Button)picker.Controls.Find("FolderUp", true)[0];
                            var go = (Button)picker.Controls.Find("FolderGo", true)[0];
                            up.PerformClick();
                            Assert(path.Text == Path.Combine(top, "Songs") && tree.SelectedNode.Text == "Songs", "Up navigates to the selected folder's parent");
                            path.Text = deep; go.PerformClick();
                            Assert(tree.SelectedNode.Text == "Demos" && path.Text == deep, "Go navigates to the entered path without accepting the dialog");
                            path.Text = Path.Combine(top, "Missing"); go.PerformClick();
                            Assert(picker.Visible && message.Text.Contains("not found") && tree.SelectedNode.Text == "Demos", "an invalid address keeps the current selection and reports the error");
                            path.Text = deep; go.PerformClick();
                            TreeNode songs = tree.SelectedNode.Parent;
                            foreach (TreeNode sibling in songs.Parent.Nodes) if (sibling.Text == "Samples") tree.SelectedNode = sibling;
                            Assert(path.Text == Path.Combine(top, "Samples"), "choosing a folder in the tree fills in its path");
                            path.Text = Path.Combine(top, "Missing");
                            picker.Accept.PerformClick();
                        }
                        else if (step == 1)
                        {
                            Assert(picker.Visible && message.Text.Length != 0, "a folder that does not exist is not accepted");
                            path.Text = Path.Combine(top, "Songs");
                            picker.Accept.PerformClick();
                        }
                    }
                    catch (Exception ex) { failure = ex; picker.DialogResult = DialogResult.Cancel; }
                    step++;
                };
                automation.Start();
                picker.ShowDialog();
                automation.Stop();
                if (failure != null) throw failure;
                Assert(picker.DialogResult == DialogResult.OK && picker.SelectedPath == Path.Combine(top, "Songs"), "the folder picker returns the chosen folder");
            }
            using (var picker = new FolderPickerDialog("Choose a folder", null, false))
                Assert(picker.Controls.Find("NewFolder", true).Length == 0, "the folder picker offers no new folder when the folder must exist");
        }
        finally { try { Directory.Delete(top, true); } catch (IOException) { } }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern uint GetShortPathName(string longPath, System.Text.StringBuilder shortPath, uint size);

    static string ShortPathOf(string path)
    {
        var buffer = new System.Text.StringBuilder(1024);
        uint length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
    }

    [Test("Progress display", Gui = true)]
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
            form.CompleteOperation(new OperationResult(OperationOutcome.Success) { Summary = "Done", Counts = new OperationCounts { Copied = 1 } }, false);
            var operationStatus = (Label)form.Controls.Find("OperationStatus", true)[0];
            Assert(operationStatus.Text == "Complete", "a successful result needs no title to complete");
            form.CompleteOperation(new OperationResult(OperationOutcome.Cancelled) { Title = "Stopped" }, false);
            Assert(operationStatus.Text == "Cancelled", "cancelled selections are displayed as cancelled");
            form.CompleteOperation(new OperationResult(OperationOutcome.Skipped), false);
            Assert(operationStatus.Text == "No changes", "skipped work has its own status");
            form.CompleteOperation(new OperationResult(OperationOutcome.Preview), false);
            Assert(operationStatus.Text == "Preview complete", "the result can explicitly identify a preview");
            form.CompleteOperation(new OperationResult(OperationOutcome.Failed, 2), false);
            Assert(operationStatus.Text == "Needs attention", "verification failure is not displayed as complete");
            form.Close();
        }
    }

    // Window placement: sub-windows centre on the main window and stay on screen; the main window's
    // position is saved and restored, and ignored when it's damaged or no longer on any screen.
    [Test("Window placement", Gui = true, Folder = "window-placement")]
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

    [Test("Layouts at larger text sizes", Gui = true)]
    static void TestScaledGuiLayouts()
    {
        string output = TestBuildDirectory;
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
            using (var form = new SettingsDialog(new Config { })) CheckGuiLayout(form, percent, Path.Combine(output, "settings-" + percent + ".png"));
            using (var form = new SettingsDialog(new Config { }))
            {
                FindGuiControl<TabControl>(form).SelectedIndex = 3;
                CheckGuiLayout(form, percent, Path.Combine(output, "appearance-" + percent + ".png"));
            }
            using (var form = new MainForm(true, new Config { Theme = "original" }))
                CheckGuiLayout(form, percent, Path.Combine(output, "main-original-" + percent + ".png"));
            GuiTheme.ApplyTheme("retro");
            foreach (string theme in new[] { "original", "original-dark" })
            {
                using (var form = new MainForm(true, new Config { Theme = theme }))
                    CheckGuiLayout(form, percent, Path.Combine(output, "main-" + theme + "-" + percent + ".png"));
                using (var form = new MainForm(true, new Config { Theme = theme }))
                {
                    form.ShowDevice(Devices.OP1Field, "MTP");
                    form.ShowDeviceInformation(DeviceInformation.Read(new SourceChoice { Mtp = new MtpSource { Device = Devices.OP1Field } }));
                    CheckGuiLayout(form, percent, Path.Combine(output, "main-device-" + theme + "-" + percent + ".png"));
                }
                using (var form = new MainForm(true, new Config { Theme = theme }))
                {
                    form.Show(); ((TabControl)form.Controls.Find("WorkflowTabs", true)[0]).SelectedIndex = 1;
                    ((Button)form.Controls.Find("ToggleBackupOptions", true)[0]).PerformClick();
                    CheckGuiLayout(form, percent, Path.Combine(output, "main-options-" + theme + "-" + percent + ".png"));
                }
                using (var form = new SettingsDialog(new Config { Theme = theme }))
                    CheckGuiLayout(form, percent, Path.Combine(output, "settings-" + theme + "-" + percent + ".png"));
                using (var form = new SettingsDialog(new Config { Theme = theme }))
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
}
