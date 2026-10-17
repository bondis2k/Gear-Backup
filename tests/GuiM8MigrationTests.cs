using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using GearBackup;
using GearBackup.Gui;

partial class Tests
{
    [Test("M8 migration assistant page", Gui = true)]
    static void TestM8MigrationPage(string root)
    {
        string source = Folder(root, "source");
        Put(Path.Combine(source, "Songs", "Demos", "relative.m8s"), "SONG");
        Put(Path.Combine(source, "Songs", "Demos", "Samples", "kick.wav"), "SAMPLE");
        var config = new Config { BackupFolder = Folder(root, "backups"), Preview = false, Theme = "original-dark" };
        string history = Path.Combine(Histories.HistoriesRoot(config), "profiles", "M8");
        InOperation(() => Backups.RunBackup(source, history, null, false, config));
        var original = M8SongReaders.Current; M8SongReaders.Current = new CardTestReader();
        try
        {
            using (var form = new MainForm(true, config))
            {
                form.Show(); Application.DoEvents();
                var gears = (GearIconStrip)form.Controls.Find("GearIcons", true)[0];
                gears.SetActive(GearIconStrip.Gears[1]);
                var tabs = (TabControl)form.Controls.Find("WorkflowTabs", true)[0]; tabs.SelectedIndex = 2;
                Func<string, Control> named = name => form.Controls.Find(name, true)[0];
                var start = (Button)named("StartM8Migration");
                WaitForGui(() => ((ComboBox)named("MigrationPoint")).Items.Count > 0, "migration points load");
                Assert(tabs.SelectedTab.Text == "Card migration" && !start.Enabled && named("MigrationTarget").Text.Length == 0,
                    "assistant loads M8 restore points and requires an explicit destination");
                string target = Folder(root, "replacement"); named("MigrationTarget").Text = target;
                var link = (CheckBox)named("MigrationLinkHistory");
                Assert(!link.Checked, "history linking is opt-in");
                link.Checked = true; start.PerformClick();
                Assert(named("MigrationNotice").Text.Contains("drive root") && Directory.GetFileSystemEntries(target).Length == 0,
                    "linking a rehearsal folder is explained before any migration starts");
                link.Checked = false;
                Assert(start.Enabled && named("MigrationBackupRoot").ForeColor == GuiTheme.Ink, "the themed assistant enables migration with a selected point and destination");
                start.PerformClick();
                var state = (Label)named("OperationStatus");
                Assert(named("CancelOperation").Visible && state.Visible, "global progress and Cancel remain visible while migration runs");
                Assert(!named("MigrationTarget").Enabled && !named("MigrationBackupRoot").Enabled && !link.Enabled,
                    "migration paths and linking choice are locked while the operation runs");
                bool reviewed = false;
                DriveGui(form, state, "Complete", false, dialog => { if (dialog is PreviewDialog) reviewed = true; });
                Assert(reviewed && File.ReadAllText(Path.Combine(target, "Songs", "Demos", "relative.m8s")) == "SONG", "migration copies files only after the preview is approved");
                WaitForGui(() => named("SaveMigrationReport").Enabled, "migration report ready");
                Assert(named("MigrationTarget").Enabled && named("MigrationBackupRoot").Enabled && link.Enabled,
                    "migration inputs become available again after completion");
                Assert(((DataGridView)named("MigrationSongs")).Rows.Count == 1 && named("MigrationReport").Text.Contains("Files verified: 2") &&
                    named("MigrationNotice").Text.Contains("0 with missing samples"), "assistant shows verified file counts and restored-song checks");
                var module = (M8MigrationTab)typeof(MainForm).GetField("migrationTab", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form);
                string saved = Path.Combine(root, "migration-report.txt"); module.ReportPicker = () => saved;
                ((Button)named("SaveMigrationReport")).PerformClick();
                Assert(File.ReadAllText(saved).Contains(target) && File.ReadAllText(saved).Contains("relative.m8s"), "saved report includes the destination and checked songs");
                using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                {
                    form.Controls[0].DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
                    bitmap.Save(Path.Combine(TestBuildDirectory, "m8-migration.png"));
                }
                string cancelled = Folder(root, "cancelled"); named("MigrationTarget").Text = cancelled;
                start.PerformClick(); DriveGui(form, state, "Cancelled", true);
                Assert(Directory.GetFileSystemEntries(cancelled).Length == 0, "declining migration preview leaves the destination empty");
                gears.SetActive(GearIconStrip.Gears[3]); Application.DoEvents();
                Assert(!start.Visible && tabs.TabPages[2].Text == "Planned", "migration is specific to M8 mode");
                gears.SetActive(GearIconStrip.Gears[1]); Application.DoEvents();
                Assert(tabs.TabPages[2].Text == "Card migration" && named("MigrationTarget").Text == cancelled, "assistant selections survive gear-mode changes");
                ((Button)named("RefreshMigrationBackups")).PerformClick();
                named("MigrationBackupRoot").Text = Folder(root, "empty-backup-location");
                ((Button)named("RefreshMigrationBackups")).PerformClick();
                WaitForGui(() => named("MigrationNotice").Text.StartsWith("No M8 backups"), "replacement inventory load settles");
                Assert(((ComboBox)named("MigrationBackup")).Items.Count == 0 && ((ComboBox)named("MigrationPoint")).Items.Count == 0 && !start.Enabled,
                    "superseded inventory results cannot repopulate an empty replacement location");
            }
        }
        finally { M8SongReaders.Current = original; GuiTheme.ApplyTheme("original"); }
    }
}
