using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GearBackup;
using GearBackup.Gui;

partial class Tests
{
    [Test("M8 check all songs button", Gui = true)]
    static void TestM8CardPage(string root)
    {
        string card = Folder(root, "card");
        MakeCardCheckFixture(card);
        var backupConfig = new Config { BackupFolder = Folder(root, "backups"), Preview = false };
        string history = Path.Combine(Histories.HistoriesRoot(backupConfig), "profiles", "M8");
        InOperation(() => Backups.RunBackup(card, history, null, false, backupConfig));
        var original = M8SongReaders.Current;
        M8SongReaders.Current = new CardTestReader();
        try
        {
            using (var form = new MainForm(true))
            {
                form.Show(); Application.DoEvents();
                var gears = (GearIconStrip)form.Controls.Find("GearIcons", true)[0];
                gears.SetActive(GearIconStrip.Gears[1]);
                ((TabControl)form.Controls.Find("WorkflowTabs", true)[0]).SelectedIndex = 1;
                Func<string, Control> named = name => form.Controls.Find(name, true)[0];
                named("SampleCardRoot").Text = card;
                named("SampleBackupRoot").Text = backupConfig.BackupFolder;
                ((CheckBox)named("SearchSampleBackups")).Checked = false;
                ((Button)named("CheckAllM8Songs")).PerformClick();
                WaitForGui(() => named("CheckAllM8Songs").Enabled, "all songs check completes");
                var songs = (DataGridView)named("M8CardSongs");
                Assert(songs.Visible && songs.Rows.Count == 5 && named("SampleSongPath").Text.Length == 0,
                    "whole-card check works without choosing an individual song");
                Assert(named("SampleCheckNotice").Text.Contains("1 with missing samples") && named("SampleCheckNotice").Text.Contains("2 needing attention"), "summary distinguishes missing samples from errors");
                var missing = songs.Rows.Cast<DataGridViewRow>().Single(r => ((M8CardSongResult)r.Tag).Missing > 0);
                var good = songs.Rows.Cast<DataGridViewRow>().Single(r => ((M8CardSongResult)r.Tag).Relative.EndsWith("good.m8s"));
                songs.CurrentCell = good.Cells[0];
                ((CheckBox)named("SearchSampleBackups")).Checked = true;
                var copies = (DataGridView)named("SampleBackupMatches");
                WaitForGui(() => named("CheckM8Samples").Enabled && copies.Rows.Count > 0, "card selection history search");
                Assert(songs.Visible && songs.Rows.Count == 5 && copies.Rows.Count == 1, "re-enabling history searches the selected card song without discarding the song list");
                ((CheckBox)named("SearchSampleBackups")).Checked = false;
                Assert(songs.Visible && songs.Rows.Count == 5 && copies.Rows.Count == 0, "turning search off preserves the whole-card results");
                ((CheckBox)named("SearchSampleBackups")).Checked = true;
                WaitForGui(() => named("CheckM8Samples").Enabled && copies.Rows.Count == 1, "card history search can be enabled again");
                songs.CurrentCell = missing.Cells[0];
                WaitForGui(() => named("CheckM8Samples").Enabled, "new selected song history search");
                var samples = (DataGridView)named("SampleResults");
                Assert(samples.Rows.Count == 1 && (string)samples.Rows[0].Cells[1].Value == "/Samples/lost.wav" &&
                    (string)samples.Rows[0].Cells[2].Value == "Missing on card", "selecting a song shows its missing sample paths: rows=" + samples.Rows.Count +
                    "; selected=" + ((M8CardSongResult)songs.CurrentRow.Tag).Relative + "; reference=" + (samples.Rows.Count == 0 ? "none" : samples.Rows[0].Cells[1].Value) + "; notice=" + named("SampleCheckNotice").Text);
                using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                {
                    form.Controls[0].DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
                    bitmap.Save(Path.Combine(Environment.CurrentDirectory, "tests", ".build", "m8-card-check.png"));
                }
                typeof(DataGridView).GetMethod("OnCellDoubleClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(songs, new object[] { new DataGridViewCellEventArgs(0, missing.Index) });
                WaitForGui(() => named("CheckM8Samples").Enabled, "chosen card song check completes");
                Assert(named("SampleSongPath").Text.EndsWith("missing.M8S") && !songs.Visible && samples.Rows.Count == 1, "double-clicking opens the song's individual check");
                var waiting = new WaitingSampleReader(); M8SongReaders.Current = waiting;
                try
                {
                    ((Button)named("CheckAllM8Songs")).PerformClick();
                    WaitForGui(() => waiting.Entered.WaitOne(0), "batch reader starts");
                    ((Button)named("CancelM8SampleCheck")).PerformClick(); waiting.Release.Set();
                    WaitForGui(() => named("CheckAllM8Songs").Enabled, "batch cancellation finishes");
                    Assert(songs.Rows.Count == 0 && named("SampleCheckNotice").Text.Contains("cancelled"), "cancelled batch does not publish incomplete results");
                }
                finally { waiting.Release.Set(); waiting.Entered.Dispose(); waiting.Release.Dispose(); }
            }
        }
        finally { M8SongReaders.Current = original; }
    }
}
