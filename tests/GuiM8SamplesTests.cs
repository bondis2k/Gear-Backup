using System;
using System.IO;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using GearBackup;
using GearBackup.Gui;

partial class Tests
{
    [Test("M8 sample path fields", Gui = true)]
    static void TestM8SamplePaths(string root)
    {
        var original = M8SongReaders.Current;
        M8SongReaders.Current = new FakeSongReader();
        try
        {
            GuiTheme.ApplyTheme("original");
            using (var form = new MainForm(true, new Config { Theme = "original-dark", BackupFolder = root }))
            {
                form.Show(); Application.DoEvents();
                var gears = (GearIconStrip)form.Controls.Find("GearIcons", true)[0];
                gears.SetActive(GearIconStrip.Gears[1]);
                var tabs = (TabControl)form.Controls.Find("WorkflowTabs", true)[0]; tabs.SelectedIndex = 1;
                Func<string, Control> named = name => form.Controls.Find(name, true)[0];
                form.SetDetectedSource(root); form.ShowDevice(Devices.M8);
                Assert(named("SampleCardRoot").Text == root, "detecting M8 after entering its mode fills the empty card path");
                named("SampleSongPath").Text = Path.Combine(root, "demo.m8s");
                named("SampleCardRoot").Text = root;
                ((CheckBox)named("SearchSampleBackups")).Checked = false;
                ((Button)named("CheckM8Samples")).PerformClick();
                WaitForGui(() => named("CheckM8Samples").Enabled, "dark sample check");
                foreach (string field in new[] { "SampleSongPath", "SampleCardRoot", "SampleBackupRoot" })
                {
                    var box = (TextBox)named(field);
                    Assert(box.Text.Length > 0 && ThemeContrast(box.ForeColor, box.BackColor) >= 4.5,
                        "sample path text stays visible after re-enabling in dark mode: " + field);
                }
                gears.SetActive(GearIconStrip.Gears[0]);
                GuiTheme.ApplyTheme("original", form);
                gears.SetActive(GearIconStrip.Gears[1]);
                foreach (string field in new[] { "SampleSongPath", "SampleCardRoot", "SampleBackupRoot" })
                    Assert(ThemeContrast(named(field).ForeColor, named(field).BackColor) >= 4.5, "hidden sample paths follow theme changes: " + field);
            }
        }
        finally { M8SongReaders.Current = original; GuiTheme.ApplyTheme("original"); }
    }

    sealed class WaitingSampleReader : IM8SongReader
    {
        internal readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false);
        public M8SongSummary Read(string path)
        {
            Entered.Set();
            if (!Release.WaitOne(15000)) throw new M8SongException("Timed out waiting for test reader.");
            return new FakeSongReader().Read(path);
        }
    }

    [Test("M8 sample checker page", Gui = true)]
    static void TestM8SamplesPage(string root)
    {
        var original = M8SongReaders.Current;
        M8SongReaders.Current = new FakeSongReader();
        try
        {
            string card = Folder(root, "card");
            Put(Path.Combine(card, "Samples", "kick.wav"), "KICK");
            using (var form = new MainForm(true))
            {
                form.Show(); Application.DoEvents();
                var gears = (GearIconStrip)form.Controls.Find("GearIcons", true)[0];
                gears.SetActive(GearIconStrip.Gears[1]);
                var tabs = (TabControl)form.Controls.Find("WorkflowTabs", true)[0]; tabs.SelectedIndex = 1;
                Application.DoEvents();
                Func<string, Control> named = name => form.Controls.Find(name, true)[0];
                Assert(tabs.SelectedTab.Text == "Sample check" && named("CheckM8Samples").Visible, "M8 tools expose sample checking");
                named("SampleSongPath").Text = Path.Combine(root, "demo.m8s"); named("SampleCardRoot").Text = card;
                ((CheckBox)named("SearchSampleBackups")).Checked = false;
                ((Button)named("CheckM8Samples")).PerformClick();
                WaitForGui(() => named("CheckM8Samples").Enabled, "sample check completes");
                var grid = (DataGridView)named("SampleResults");
                Assert(grid.Rows.Count == 1 && (string)grid.Rows[0].Cells[2].Value == "Present on card", "card sample presence appears on the M8 page");
                named("SampleCardRoot").Text = root;
                Assert(grid.Rows.Count == 0, "changing inputs clears stale results");
                ((Button)named("CheckM8Samples")).PerformClick();
                WaitForGui(() => named("CheckM8Samples").Enabled, "missing sample check completes");
                Assert((string)grid.Rows[0].Cells[2].Value == "Missing on card", "missing samples appear without altering the card");
                var backupConfig = new Config { BackupFolder = Folder(root, "backup-store"), Preview = false };
                string history = Path.Combine(Histories.HistoriesRoot(backupConfig), "profiles", "M8");
                InOperation(() => Backups.RunBackup(card, history, null, false, backupConfig));
                named("SampleBackupRoot").Text = backupConfig.BackupFolder;
                ((CheckBox)named("SearchSampleBackups")).Checked = true;
                ((Button)named("CheckM8Samples")).PerformClick();
                WaitForGui(() => named("CheckM8Samples").Enabled, "backup sample search completes");
                var matches = (DataGridView)named("SampleBackupMatches");
                Assert(grid.Rows.Count == 1 && matches.Rows.Count == 1 && (string)matches.Rows[0].Cells[0].Value == "Exact path" &&
                    (string)matches.Rows[0].Cells[4].Value == "Verified copy", "selected sample exposes a verified backup copy and restore point");
                ((CheckBox)named("SearchSampleBackups")).Checked = false;
                Assert(grid.Rows.Count == 1 && matches.Rows.Count == 0 && (string)grid.Rows[0].Cells[3].Value == "Not searched",
                    "disabling backup search preserves card results and hides history copies");
                ((CheckBox)named("SearchSampleBackups")).Checked = true;
                WaitForGui(() => named("CheckM8Samples").Enabled && matches.Rows.Count == 1, "history returns after re-enabling search");
                Assert(grid.Rows.Count == 1 && (string)matches.Rows[0].Cells[4].Value == "Verified copy", "re-enabling search automatically refreshes history without another Check click");
                var module = (M8SamplesTab)typeof(MainForm).GetField("samplesTab", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form);
                var chosen = (M8SampleMatch)matches.Rows[0].Tag;
                int confirmations = 0;
                bool acceptRestore = false;
                module.ConfirmRestore = question => { confirmations++; Assert(question.Contains(chosen.Relative) && question.Contains(chosen.Point), "restore question identifies the file and saved point"); return acceptRestore; };
                Action<int> doubleClick = row => typeof(DataGridView).GetMethod("OnCellDoubleClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(matches, new object[] { new DataGridViewCellEventArgs(0, row) });
                doubleClick(-1);
                Assert(confirmations == 0, "double-clicking the header does not ask to restore");
                doubleClick(0);
                Assert(confirmations == 1 && gears.Active.Device == Devices.M8, "declining restore leaves sample checking unchanged");
                using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                {
                    form.Controls[0].DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
                    bitmap.Save(Path.Combine(TestBuildDirectory, "m8-sample-check.png"));
                }
                acceptRestore = true;
                doubleClick(0);
                WaitForGui(() => ((ComboBox)named("RestorePoint")).SelectedItem is RestorePoint && named("StartOperation").Enabled, "sample restore selection loads");
                var restoreSelection = (RestoreRequest)typeof(MainForm).GetMethod("CaptureRestoreSelection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, null);
                Assert(confirmations == 2 && gears.Active.Device == null && tabs.SelectedTab.Text == "Restore" && restoreSelection.Point == chosen.PointId &&
                    restoreSelection.Paths.Length == 1 && restoreSelection.Paths[0] == chosen.Relative && !restoreSelection.Exact,
                    "accepting opens individual restore with the precise saved point and only the chosen sample");
                Assert(named("RestoreDestination").Text == root && File.ReadAllText(Path.Combine(card, "Samples", "kick.wav")) == "KICK", "restore handoff preserves destination and waits for the normal Restore action");
                gears.SetActive(GearIconStrip.Gears[3]); Application.DoEvents();
                Assert(tabs.TabPages[1].Text == "Coming soon" && !named("CheckM8Samples").Visible, "M8 checking does not leak into other device pages");
                gears.SetActive(GearIconStrip.Gears[1]); Application.DoEvents();
                Assert(((DataGridView)named("SampleResults")).Rows.Count == 1, "M8 results survive switching device modes");
                tabs.SelectedIndex = 1;
                var waiting = new WaitingSampleReader(); M8SongReaders.Current = waiting;
                try
                {
                    ((Button)named("CheckM8Samples")).PerformClick();
                    WaitForGui(() => waiting.Entered.WaitOne(0), "sample reader starts");
                    ((Button)named("CancelM8SampleCheck")).PerformClick();
                    waiting.Release.Set();
                    WaitForGui(() => named("CheckM8Samples").Enabled, "cancelled check settles");
                    Assert(grid.Rows.Count == 0 && named("SampleCheckNotice").Text.Contains("cancelled"), "cancelled work cannot publish stale results");
                }
                finally { waiting.Release.Set(); waiting.Entered.Dispose(); waiting.Release.Dispose(); }
            }
        }
        finally { M8SongReaders.Current = original; }
    }
}
