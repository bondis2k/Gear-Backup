// GUI tests of the Restore tab: the file browser, offline restore and ZIP archive backup and restore.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using static PathSafety;
using static ConsoleUi;
using static Formatting;
using static Snapshots;
using static Backups;
using static FujiSettings;
using static MtpMirror;
using static StorageCatalog;
using static AppSettings;
using static ProfileSettings;
using static Session;
using static ProfileNaming;
using static GearGui;
using static BackupFolders;
using static Histories;

partial class Tests
{
    [Test("Restore locations", Gui = true, Folder = "restore-locations")]
    static void TestRestoreLocationsGui(string root)
    {
        Directory.CreateDirectory(root);
        var config = new Config { BackupFolder = Path.Combine(root, "global"), SettingsDirectory = root, Preview = false };
        string alternate = Path.Combine(root, "tracker");
        SaveProfileSettings(config, "device:tracker-mini", new Dictionary<string, string> {
            { "BackupFolder", alternate }, { "Thorough", "yes" }, { "Verify", "yes" }
        });
        string source = Folder(root, "source"), relative = "Projects\\Demo\\take_2026-01-01_120000.wav";
        Put(Path.Combine(source, "Projects", "Demo", "project.mt"), "PROJECT");
        Put(Path.Combine(source, relative), "SAMPLE");
        string history = Path.Combine(HistoriesRoot(new Config { BackupFolder = alternate }), "profiles", "Studio");
        OperationResult made = new BackupEngine().Run(source, history, null, false, ResolveProfileSettings(config, "tracker-mini", "Studio"),
            new OperationContext(new TestInteraction(), CancellationToken.None) { Device = Devices.TrackerMini });
        Assert(made.ExitCode == 0, "the fixture backup succeeds: " + made.Summary);
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

    [Test("Restore file browser", Gui = true)]
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
            var useSelection = (Button)browser.Controls.Find("UseRestoreSelection", true)[0];
            Assert(!useSelection.Enabled, "the restore browser requires a selection before accepting");
            TreeNode songs = null, samples = null;
            foreach (TreeNode node in tree.Nodes) { if ((string)node.Tag == "Songs") songs = node; if ((string)node.Tag == "Samples") samples = node; }
            Assert(!songs.IsExpanded && !samples.IsExpanded && browser.ClientSize.Width <= 720, "the file browser starts with its folders collapsed in a moderate-width window");
            ((Button)browser.Controls.Find("ExpandRestoreFolders", true)[0]).PerformClick();
            Assert(songs.IsExpanded && samples.IsExpanded && browser.SelectedPaths.Length == 0, "expanding folders does not select files");
            tree.SelectedNode = songs;
            tree.Font = new Font(tree.Font.FontFamily, tree.Font.Size + 1);
            Application.DoEvents();
            int checkboxRight = -1, imageLeft = -1;
            for (int x = 0; x < songs.Bounds.Left; x++)
            {
                var hit = tree.HitTest(x, songs.Bounds.Top + songs.Bounds.Height / 2);
                if ((hit.Location & TreeViewHitTestLocations.StateImage) != 0) checkboxRight = x;
                if (imageLeft < 0 && (hit.Location & TreeViewHitTestLocations.Image) != 0) imageLeft = x;
            }
            Assert(checkboxRight >= 0 && imageLeft > checkboxRight && songs.Bounds.Left > imageLeft,
                "Windows keeps the checkbox, native icon and custom text in separate areas after scaling");
            IntPtr imagesBefore = tree.ImageList.Handle;
            tree.Font = new Font(tree.Font.FontFamily, tree.Font.Size, tree.Font.Style);
            Assert(tree.ImageList.Handle == imagesBefore, "equivalent font layout passes reuse the cached native icons");
            Assert(songs.IsExpanded && samples.IsExpanded && tree.SelectedNode == songs && browser.SelectedPaths.Length == 0,
                "a browser font change preserves expanded folders, focus and checkbox selection");
            ((Button)browser.Controls.Find("CollapseRestoreFolders", true)[0]).PerformClick();
            Assert(!songs.IsExpanded && !samples.IsExpanded, "the restore browser can collapse folders again");
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
            Assert(!useSelection.Enabled, "clearing the restore selection disables acceptance again");
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

    [Test("Offline restore", Gui = true, Folder = "offline-restore")]
    static void TestOfflineRestore(string root)
    {
        string source = Folder(root, "source"), target = Folder(root, "restored");
        Put(Path.Combine(source, "Songs", "one.m8s"), "OFFLINE SONG");
        Put(Path.Combine(source, "Samples", "kick.wav"), "OFFLINE SAMPLE");
        var config = new Config { BackupFolder = root, SettingsDirectory = Folder(root, "settings"), Preview = false };
        foreach (DeviceProfile device in new[] { Devices.M8, Devices.OP1Field })
        using (new OperationContext(new TestInteraction(), CancellationToken.None) { Device = device }.Activate())
            RunBackup(source, Path.Combine(Histories.HistoriesRoot(config), "profiles", device.Id), null, false, config);
        using (var form = new MainForm(true, config))
        {
            form.Show(); form.SelectOperation(GuiOperation.Restore); Application.DoEvents(); WaitForRestorePicker(form);
            var backups = (ComboBox)form.Controls.Find("RestoreBackup", true)[0];
            var start = (Button)form.Controls.Find("StartOperation", true)[0];
            var state = (Label)form.Controls.Find("OperationStatus", true)[0];
            Assert(backups.Items.Count == 2 && backups.Text.Contains(Devices.OP1Field.Name) && start.Enabled,
                "Restore starts without detection, listing device names across all available backups with the latest selected");
            var capture = typeof(MainForm).GetMethod("CaptureConfig", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            ((TextBox)form.Controls.Find("RestoreDestination", true)[0]).Text = target;
            var request = ((Config)capture.Invoke(form, new object[] { true })).RestoreSelection;
            Assert(request.DeviceId == Devices.OP1Field.Id, "offline selection takes its device identity from the backup");
            start.PerformClick(); DriveGui(form, state, "Complete", false);
            Assert(File.ReadAllText(Path.Combine(target, "Songs", "one.m8s")) == "OFFLINE SONG" &&
                File.ReadAllText(Path.Combine(target, "Samples", "kick.wav")) == "OFFLINE SAMPLE",
                "offline full restore copies files to a PC folder without source detection");
            form.ShowDevice(Devices.M8); Application.DoEvents(); WaitForRestorePicker(form);
            Assert(backups.Items.Count == 1, "detecting a device filters the all-device restore inventory");
            form.ShowDevice(null); Application.DoEvents(); WaitForRestorePicker(form);
            Assert(backups.Items.Count == 2, "clearing device detection restores the all-device backup list");
            ((ComboBox)form.Controls.Find("RestoreOperation", true)[0]).SelectedIndex = 1;
            using (var timer = new System.Windows.Forms.Timer { Interval = 20 })
            {
                timer.Tick += delegate {
                    foreach (Form owned in form.OwnedForms)
                    {
                        var browser = owned as RestoreBrowserDialog;
                        if (browser == null || !browser.Visible) continue;
                        var tree = (TreeView)browser.Controls.Find("RestoreFileTree", true)[0];
                        foreach (TreeNode node in tree.Nodes) if ((string)node.Tag == "Samples") node.Checked = true;
                        ((Button)browser.Controls.Find("UseRestoreSelection", true)[0]).PerformClick();
                    }
                };
                timer.Start(); ((Button)form.Controls.Find("BrowseRestoreFiles", true)[0]).PerformClick(); timer.Stop();
            }
            string individual = Folder(root, "individual");
            ((TextBox)form.Controls.Find("RestoreDestination", true)[0]).Text = individual;
            start.PerformClick(); DriveGui(form, state, "Complete", false);
            Assert(File.Exists(Path.Combine(individual, "Samples", "kick.wav")) && !Directory.Exists(Path.Combine(individual, "Songs")),
                "offline individual restore uses only the checked files and folders");
            form.Close();
        }
    }

    [Test("ZIP archive backup tab", Gui = true, Folder = "zip-archive")]
    static void TestGuiArchive(string root)
    {
        string source = Folder(root, "source"), destination = Path.Combine(root, "archive.zip");
        Put(Path.Combine(source, "Songs", "one.m8s"), "GUI ARCHIVE SONG");
        var config = new Config { Device = "m8", SourceFolder = source, BackupFolder = Path.Combine(source, "unused"), SettingsDirectory = Folder(root, "settings"), Preview = false };
        using (var form = new MainForm(true, config))
        {
            form.Show(); Application.DoEvents();
            var types = (ComboBox)form.Controls.Find("BackupOperation", true)[0];
            ((TextBox)form.Controls.Find("SourceFolder", true)[0]).Text = source;
            types.SelectedIndex = 2;
            var name = (TextBox)form.Controls.Find("FullBackupName", true)[0];
            var start = (Button)form.Controls.Find("StartOperation", true)[0];
            var state = (Label)form.Controls.Find("OperationStatus", true)[0];
            Assert(types.Text == "Zip archive" && name.Visible && start.Text == "Create ZIP" && !form.Controls.Find("ZipBackup", true)[0].Visible,
                "ZIP archive type exposes an optional name and its own action without the extra ZIP checkbox");
            string suggested = null;
            form.ArchiveDestinationPicker = value => { suggested = value; return destination; };
            ((Button)form.Controls.Find("PreviewOperation", true)[0]).PerformClick(); DriveGui(form, state, "Preview complete", false);
            Assert(suggested.StartsWith("M8_") && suggested.EndsWith(".zip") && !File.Exists(destination) && !Directory.Exists(config.BackupFolder),
                "ZIP preview suggests device/date/time and creates no archive or backup history");
            name.Text = "Live set";
            start.PerformClick(); DriveGui(form, state, "Complete", false);
            Assert(suggested == "Live set.zip" && File.Exists(destination) && !Directory.Exists(config.BackupFolder),
                "standalone GUI ZIP uses the custom name and selected save path without a separate backup");
            using (var zip = new System.IO.Compression.ZipArchive(File.OpenRead(destination), System.IO.Compression.ZipArchiveMode.Read))
                Assert(zip.GetEntry("Songs/one.m8s") != null, "GUI ZIP contains the complete source tree");
            Assert(form.Controls.Find("OpenOutputFolder", true)[0].Text == "Open archive folder", "ZIP result links to its selected destination folder");
            form.ArchiveDestinationPicker = value => null;
            start.PerformClick(); DriveGui(form, state, "Cancelled", false);
            Assert(Directory.GetFiles(root, "*.zip").Length == 1 && !Directory.Exists(config.BackupFolder), "cancelling the save picker starts no archive or backup history");
            form.ShowDevice(Devices.X100VI, "settings", true);
            Assert(types.SelectedIndex == 2 && types.Items.Count == 4, "showing X100VI settings preserves the ZIP selection");
            form.ShowDevice(null);
            Assert(types.SelectedIndex == 2 && types.Items.Count == 3, "clearing camera detection retains ZIP archive in the dropdown");
            form.Close();
        }
    }

    [Test("ZIP archive restore tab", Gui = true, Folder = "zip-restore")]
    static void TestGuiZipRestore(string root)
    {
        string source = Folder(root, "source"), target = Folder(root, "target"), archive = Path.Combine(root, "backup.zip");
        Put(Path.Combine(source, "Songs", "one.m8s"), "GUI ZIP RESTORE");
        var config = new Config { Device = "m8", BackupFolder = Folder(root, "backup-location"), SettingsDirectory = Folder(root, "settings"), Preview = false };
        WithUi(() => ArchiveBackup.Run(source, archive, config));
        using (var form = new MainForm(true, config))
        {
            form.Show(); Application.DoEvents();
            int picked = 0; form.ArchiveSourcePicker = () => { picked++; return archive; };
            form.ArchiveRestoreDeviceValidator = known => { };
            var runner = (GuiOperationRunner)typeof(MainForm).GetField("runner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form);
            var types = (ComboBox)form.Controls.Find("RestoreOperation", true)[0];
            form.SelectOperation(GuiOperation.RestoreZip); Application.DoEvents();
            var start = (Button)form.Controls.Find("StartOperation", true)[0];
            var preview = (Button)form.Controls.Find("PreviewOperation", true)[0];
            var state = (Label)form.Controls.Find("OperationStatus", true)[0];
            var path = (TextBox)form.Controls.Find("RestoreArchivePath", true)[0];
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until && !(bool)typeof(MainForm).GetProperty("ArchiveRestoreReady", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form))
            {
                // No device is detected yet, so wait for the read itself rather than readiness.
                if (!(bool)typeof(MainForm).GetField("loadingArchive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form)) break;
                Application.DoEvents(); Thread.Sleep(10);
            }
            Assert(picked == 1 && types.Text == "Restore ZIP archive" && path.Visible && path.Text == archive && !start.Enabled &&
                !form.Controls.Find("RestoreBackup", true)[0].Visible && !form.Controls.Find("RestorePoint", true)[0].Visible,
                "selecting ZIP restore opens the picker, replaces backup selectors and requires detection");
            runner.ShowDetected(new SourceChoice { Path = target, Detected = Devices.OP1Field }); Application.DoEvents();
            Assert(!start.Enabled, "ZIP restore is disabled for a mismatching detected device");
            runner.ShowDetected(new SourceChoice { Path = target, Detected = Devices.M8 }); Application.DoEvents();
            Assert(start.Enabled && preview.Enabled && form.Controls.Find("RestoreDestination", true)[0].Visible,
                "matching device enables ZIP restore and exposes the destination field");
            preview.PerformClick(); DriveGui(form, state, "Preview complete", false);
            Assert(Directory.GetFileSystemEntries(target).Length == 0, "GUI ZIP restore preview leaves device files unchanged");
            start.PerformClick(); DriveGui(form, state, "Complete", false);
            Assert(File.ReadAllText(Path.Combine(target, "Songs", "one.m8s")) == "GUI ZIP RESTORE" &&
                form.Controls.Find("OpenOutputFolder", true)[0].Text == "Open restore folder", "GUI restores ZIP files to the matching device and links to the result");
            form.ArchiveSourcePicker = () => null;
            ((Button)form.Controls.Find("BrowseRestoreArchive", true)[0]).PerformClick();
            Assert(path.Text == archive && start.Enabled, "cancelling ZIP selection preserves the previous valid archive");
            runner.ShowDetected(null); Application.DoEvents();
            Assert(!start.Enabled, "clearing detection disables ZIP restore again");
            form.SelectOperation(GuiOperation.Check);
            Assert(types.Text == "Check backups", "ZIP restore does not change the existing check operation mapping");
            string mtpArchive = Path.Combine(root, "op1.zip"), mirror = Folder(root, "mirror"), pcTarget = Folder(root, "mtp-target");
            WithUi(() => { using (OperationContext.Current.UseDevice(Devices.OP1Field)) ArchiveBackup.Run(source, mtpArchive, config); });
            form.ArchiveSourcePicker = () => mtpArchive;
            form.SelectOperation(GuiOperation.RestoreZip); Application.DoEvents();
            until = DateTime.UtcNow.AddSeconds(10);
            while ((bool)typeof(MainForm).GetField("loadingArchive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form) && DateTime.UtcNow < until)
            { Application.DoEvents(); Thread.Sleep(10); }
            runner.ShowDetected(new SourceChoice { Path = mirror, Detected = Devices.OP1Field, Mtp = new MtpSource {
                Mirror = mirror, Device = Devices.OP1Field, Info = new MtpDeviceInfo { FriendlyName = "OP-1 Field", InstanceId = "USB\\TEST\\123" } } });
            Application.DoEvents();
            Assert(!start.Enabled, "matching MTP ZIP requires an explicit PC destination");
            ((TextBox)form.Controls.Find("RestoreDestination", true)[0]).Text = pcTarget;
            Assert(start.Enabled && ((Label)form.Controls.Find("ZipRestoreNotice", true)[0]).Text.Contains("direct MTP restore is unavailable"),
                "MTP ZIP restore explains manual transfer and enables when a PC folder is provided");
            start.PerformClick(); DriveGui(form, state, "Complete", false);
            Assert(File.ReadAllText(Path.Combine(pcTarget, "Songs", "one.m8s")) == "GUI ZIP RESTORE" && Directory.GetFileSystemEntries(mirror).Length == 0,
                "GUI MTP ZIP restore writes only to the chosen PC folder");
            form.Close();
        }
    }

    [Test("Lazy restore browser", Gui = true)]
    static void TestLazyRestoreBrowser()
    {
        var snapshot = new Snapshot { Name = "Large backup" };
        snapshot.Folders.Add("Samples");
        for (int i = 0; i < 20000; i++)
            snapshot.Files.Add(new RestoreEntry { Relative = "Samples\\Group " + (i / 100).ToString("D3") + "\\Sample " + i.ToString("D5") + ".wav", Size = 1 });
        using (var owner = new MainForm(true))
        using (var browser = new RestoreBrowserDialog(owner, snapshot, new[] { "Samples" }))
        {
            browser.Show(); Application.DoEvents();
            var tree = (TreeView)browser.Controls.Find("RestoreFileTree", true)[0];
            TreeNode samples = tree.Nodes[0];
            Assert(tree.GetNodeCount(true) == 2 && samples.Checked && browser.SelectedPaths.Length == 1,
                "large backups initially create only root rows and placeholders, preserving unopened selections");
            Assert(browser.SelectedPaths[0] == "Samples", "unopened selection retains the complete folder path");
            samples.Expand();
            Assert(samples.Nodes.Count == 200 && tree.GetNodeCount(true) == 401,
                "opening a folder loads only its direct children and their placeholders");
            TreeNode group = samples.Nodes[0]; group.Expand();
            Assert(group.Nodes.Count == 100 && group.Nodes[0].Checked && tree.GetNodeCount(true) == 500,
                "newly opened files inherit the checked inventory without loading neighboring folders");
            group.Nodes[0].Checked = false;
            Assert(!samples.Checked && !group.Checked && browser.SelectedPaths.Length == 298 &&
                Array.IndexOf(browser.SelectedPaths, (string)group.Nodes[0].Tag) < 0 && samples.Nodes[1].Checked,
                "partial selections preserve unopened sibling subtrees");
            ((Button)browser.Controls.Find("ClearRestoreFiles", true)[0]).PerformClick();
            ((Button)browser.Controls.Find("SelectAllRestoreFiles", true)[0]).PerformClick();
            Assert(browser.SelectedPaths.Length == 1 && tree.GetNodeCount(true) == 500,
                "select and clear operate on the inventory without creating hidden file rows");
            ((Button)browser.Controls.Find("ClearRestoreFiles", true)[0]).PerformClick();
            samples.Nodes[1].Expand();
            Assert(!samples.Nodes[1].Nodes[0].Checked && browser.SelectedPaths.Length == 0,
                "folders opened after clearing show unchecked files");
            browser.Close();
        }
    }

    static void WaitForRestorePicker(Form form)
    {
        var picker = (ComboBox)form.Controls.Find("RestorePoint", true)[0];
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while ((!picker.Enabled || picker.SelectedItem == null) && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        string notice = "";
        foreach (Control control in form.Controls.Find("RestoreNotice", true)) notice = control.Text;
        Assert(picker.Enabled && picker.SelectedItem != null, "restore picker loads matching device backups and defaults to the latest point: enabled=" + picker.Enabled + ", items=" + picker.Items.Count + ", notice=" + notice);
    }
}
