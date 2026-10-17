// GUI tests of the settings dialog and the management windows (dashboard, storage, recipes, history names).
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
using static GearBackup.Histories;
using GearBackup;
using GearBackup.Cli;
using GearBackup.Gui;

partial class Tests
{
    [Test("Settings dialog", Gui = true, Folder = ".")]
    static void TestSettingsInteraction(string root)
    {
        var config = new Config { RecoveryFolder = Path.Combine(root, "recovery") };
        using (var dialog = new SettingsDialog(config))
        {
            dialog.Show(); Application.DoEvents();
            var tabs = FindGuiControl<TabControl>(dialog);
            Assert(tabs.TabCount == 4 && tabs.TabPages[0].Text == "Backup" && tabs.TabPages[1].Text == "Restore" && tabs.TabPages[3].Text == "Appearance", "settings group operations and appearance separately");
            var compare = (CheckBox)dialog.Controls.Find("CompareContents", true)[0];
            Assert(!compare.Checked, "settings show content comparison disabled by default");
            compare.Checked = true;
            tabs.SelectedIndex = 1;
            var recovery = (CheckBox)dialog.Controls.Find("RecoveryCopies", true)[0];
            var path = (TextBox)dialog.Controls.Find("RecoveryLocation", true)[0];
            Assert(!path.Enabled && path.Text == config.RecoveryFolder, "disabled recovery preserves its configured location");
            recovery.Checked = true;
            Assert(path.Enabled, "enabling recovery enables its location controls");
            recovery.Checked = false;
            string expectedPath = path.Text;
            ((Button)dialog.Controls.Find("SaveSettings", true)[0]).PerformClick();
            Assert(config.Thorough, "settings save the choice to compare contents");
            Assert(!config.Recovery && !config.Verify && config.RecoveryFolder == expectedPath,
                "saving grouped settings preserves default-off options and the recovery location: " +
                config.Recovery + ", " + config.Verify + ", " + config.RecoveryFolder + " / " + expectedPath);
        }
    }

    // Histories in windows: Rename is offered for a device's history (only under profiles), and Device &
    // backup settings lists histories by nickname or readable name while keeping the folder name as key.
    [Test("History names in windows", Gui = true, Folder = "history-names")]
    static void TestHistoryNamesGui(string root)
    {
        var config = new Config { BackupFolder = Path.Combine(root, "store"), SettingsDirectory = root };
        string histories = HistoriesRoot(config), named = Path.Combine(histories, "profiles", "m8-card-DCE92C2E");
        Directory.CreateDirectory(Path.Combine(named, "2026-10-01"));
        Directory.CreateDirectory(Path.Combine(histories, "2026-09-01"));
        HistoryNames.SetNickname(named, "Studio card");
        var catalog = new BackupCatalog();
        catalog.BackupList.Add(new CatalogBackup { Profile = HistoryNames.DisplayName(named), History = named, Path = Path.Combine(named, "2026-10-01"),
            Status = Status.Complete, CheckResult = "OK", Latest = true, Device = Devices.M8 });
        catalog.BackupList.Add(new CatalogBackup { Profile = "Unmanaged folder", History = histories, Path = Path.Combine(histories, "2026-09-01"),
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

    // View recipes, on Dashboard and Storage, is available only for a selected backup holding recipe
    // exports (an X100VI settings backup); ordinary backups and Storage's summary rows grey it out.
    [Test("Recipe buttons", Gui = true, Folder = "recipe-buttons")]
    static void TestRecipeButtons(string root)
    {
        string photos = Path.Combine(root, "m8-card-1", "2026-10-01"), settings = Path.Combine(root, "x100vi-settings-1", "2026-10-01");
        Put(Path.Combine(photos, "Songs", "song.m8s"), "SONG");
        Put(Path.Combine(settings, "C1.xml"), "<GearBackupCameraSettings />");
        var catalog = new BackupCatalog();
        catalog.BackupList.Add(new CatalogBackup { Profile = "m8-card-1", History = Path.GetDirectoryName(photos), Path = photos,
            Status = Status.Complete, CheckResult = "OK", Latest = true, Device = Devices.M8 });
        catalog.BackupList.Add(new CatalogBackup { Profile = "x100vi-settings-1", History = Path.GetDirectoryName(settings), Path = settings,
            Status = Status.Complete, CheckResult = "OK", Latest = true, Device = Devices.X100VI });
        foreach (bool storageMode in new[] { false, true })
        using (var owner = new MainForm(true, new Config { }))
        using (var window = new CatalogDialog(owner, new Config { }, storageMode, catalog))
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
    [Test("Dashboard device filter", Gui = true)]
    static void TestDashboardDeviceFilter()
    {
        var catalog = new BackupCatalog();
        foreach (var history in new[] { new { Profile = "m8-card-1", Device = Devices.M8 }, new { Profile = "Studio", Device = Devices.M8 },
                                       new { Profile = "op-xy-ABC", Device = Devices.OPXY }, new { Profile = "Old card", Device = (DeviceProfile)null } })
            catalog.BackupList.Add(new CatalogBackup { Profile = history.Profile, History = @"C:\Backups\" + history.Profile,
                Path = @"C:\Backups\" + history.Profile + @"\2026-10-01", Status = Status.Complete, CheckResult = "OK", Latest = true, Device = history.Device });
        using (var owner = new MainForm(true, new Config { }))
        using (var dashboard = new CatalogDialog(owner, new Config { }, false, catalog))
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
        catalog.BackupList.Add(new CatalogBackup { Profile = "m8-card-1", History = @"C:\Backups\m8-card-1", Path = @"C:\Backups\m8-card-1\2026-09-01",
            Status = Status.Complete, CheckResult = "OK", Latest = false, Device = Devices.M8 });
        catalog.Cleanup.Add(new StorageItem { Kind = StorageKind.OlderBackup, History = @"C:\Backups\m8-card-1", Path = @"C:\Backups\m8-card-1\2026-09-01", Bytes = 100, Folder = true });
        catalog.Cleanup.Add(new StorageItem { Kind = StorageKind.Zip, History = @"C:\Backups\op-xy-ABC", Path = @"C:\Backups\op-xy-ABC\2026-10-01.zip", Bytes = 30 });
        catalog.Cleanup.Add(new StorageItem { Kind = StorageKind.Mirror, History = @"C:\Mirrors", Path = @"C:\Mirrors\x100vi-SERIAL", Bytes = 500, Folder = true });
        using (var owner = new MainForm(true, new Config { }))
        using (var storage = new CatalogDialog(owner, new Config { }, true, catalog))
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
        using (var owner = new MainForm(true, new Config { }))
        {
            string where = storageMode ? "Storage" : "Dashboard";
            using (var window = new CatalogDialog(owner, new Config { }, storageMode, catalog, Devices.OPXY))
            {
                window.Show(); Application.DoEvents();
                var filter = (ComboBox)window.Controls.Find(storageMode ? "StorageDevice" : "DashboardDevice", true)[0];
                Assert((string)filter.SelectedItem == "OP-XY", where + " starts filtered to the detected device: " + filter.SelectedItem);
                filter.SelectedItem = "M8";
                window.Populate(catalog);
                Assert((string)filter.SelectedItem == "M8", where + ": after choosing another device, a refresh keeps that choice");
            }
            using (var window = new CatalogDialog(owner, new Config { }, storageMode, catalog, Devices.TrackerMini))
            {
                window.Show(); Application.DoEvents();
                var filter = (ComboBox)window.Controls.Find(storageMode ? "StorageDevice" : "DashboardDevice", true)[0];
                Assert((string)filter.SelectedItem == "All devices", where + " stays on All devices when the detected device has nothing to show");
            }
        }
    }

    [Test("Management tools", Gui = true, Folder = "management-tools")]
    static void TestManagementGui(string root)
    {
        Config config; BackupCatalog catalog = ManagementFixture(root, out config);
        string output = TestBuildDirectory;
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
                var inherited = (CheckBox)dialog.Controls.Find("ProfileUseDefault_Thorough", true)[0];
                var historyValue = (ComboBox)dialog.Controls.Find("ProfileValue_Thorough", true)[0];
                Assert(inherited.Checked && !historyValue.Enabled, "profile settings clearly disable values while using defaults");
                inherited.Checked = false; historyValue.SelectedIndex = 1;
                Assert(historyValue.Enabled && historyValue.Text == "Off", "custom profile values use plain On and Off choices");
                scope.SelectedIndex = 1; scope.SelectedIndex = 0;
                historyValue = (ComboBox)dialog.Controls.Find("ProfileValue_Thorough", true)[0];
                Assert(historyValue.Text == "Off", "switching devices preserves unsaved settings");
                ((Button)dialog.Controls.Find("SaveProfilePreferences", true)[0]).PerformClick();
                Assert(!ResolveProfileSettings(config, "m8", "m8-test").Thorough && config.Thorough,
                    "profile GUI saves an override without changing global settings");
                ((Button)dialog.Controls.Find("ResetProfilePreferences", true)[0]).PerformClick();
                Assert(historyValue.Text == "On", "reset displays the inherited value in the disabled field");
                Assert(!ResolveProfileSettings(config, "m8", "m8-test").Thorough, "reset does not change saved preferences before Save changes");
                ((Button)dialog.Controls.Find("SaveProfilePreferences", true)[0]).PerformClick();
                Assert(ResolveProfileSettings(config, "m8", "m8-test").Thorough, "saving a reset returns to inherited preferences");
                CheckGuiLayout(dialog, 150, Path.Combine(output, "profiles-150.png"));
            }
            Dictionary<string, byte[]> files;
            using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate()) files = CaptureFujiSettings(new FakeFujiSettings());
            string folder = Path.Combine(root, "recipes"); Directory.CreateDirectory(folder);
            foreach (var file in files) File.WriteAllBytes(Path.Combine(folder, file.Key), file.Value);
            var stored = new BackupEngine().Run(folder, Path.Combine(root, "recipe-content-store"), null, false,
                new Config { Preview = false }, new OperationContext(new TestInteraction(), CancellationToken.None));
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
            SaveProfileSettings(config, "device:m8", new Dictionary<string, string> { { "BackupFolder", deviceLocation }, { "Thorough", "no" }, { "Preview", "no" } });
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
            string backup = LatestBackup(Path.Combine(HistoriesRoot(new Config { BackupFolder = profileLocation }), "profiles", "Studio"));
            Assert(backup != null && Snapshots.CurrentBackup(backup).Files.Exists(f => f.Relative == "song.txt"),
                "GUI backup applies the profile location and inherited device preferences before running");
            Assert(!Directory.Exists(deviceLocation), "profile destination takes priority over the device destination");
            form.Close();
        }
    }
}
