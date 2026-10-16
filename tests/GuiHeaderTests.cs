// GUI tests of the header (gear icons and their modes) and the Song tab.
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
using GearBackup;
using GearBackup.Cli;
using GearBackup.Gui;

partial class Tests
{
    [Test("Gear icons and modes", Gui = true)]
    static void TestGearIcons()
    {
        using (var form = new MainForm(true))
        {
            form.Show(); Application.DoEvents();
            var strip = (GearIconStrip)form.Controls.Find("GearIcons", true)[0];
            var buttons = strip.Controls.Cast<Control>().ToList();
            Assert(buttons.Select(b => b.AccessibleName).SequenceEqual(new[] { "Gear Backup", "M8", "Tracker Mini", "OP-XY", "OP-1 Field" }),
                "the header offers the Gear Backup, M8, Tracker Mini, OP-XY and OP-1 icons in that order");
            Assert(buttons.All(b => b is GearIconButton && b.Visible && b.Width > 0), "every gear icon is a visible button");
            Assert(strip.Active.Device == null, "Gear Backup is the active gear");
            Control help = form.Controls.Cast<Control>().SelectMany(Descendants).OfType<Button>().First(b => b.Text == "Help");
            Control title = form.Controls.Cast<Control>().SelectMany(Descendants).OfType<Label>().First(l => l.Text == AppIdentity.Name);
            Point left = form.PointToClient(title.PointToScreen(new Point(title.Width, 0)));
            Point icons = form.PointToClient(strip.PointToScreen(Point.Empty));
            Point right = form.PointToClient(help.PointToScreen(Point.Empty));
            Assert(icons.X >= left.X && icons.X + strip.Width <= right.X, "the icons sit between the title and the Help, About and Settings buttons");
            var runner = (GuiOperationRunner)typeof(MainForm).GetField("runner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form);
            string folder = Path.Combine(Environment.CurrentDirectory, ".test-runs", "gear-icons-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var tabs = (TabControl)form.Controls.Find("WorkflowTabs", true)[0];
                Func<string> titles = () => string.Join("|", tabs.TabPages.Cast<TabPage>().Select(page => page.Text));
                Func<string, Control> named = name => form.Controls.Find(name, true).FirstOrDefault();
                Assert(titles() == "Device|Backup|Restore" && named("GoToBackup").Visible, "Gear Backup mode has Device, Backup and Restore, and no Song tab");

                runner.ShowDetected(new SourceChoice { Path = folder, Detected = Devices.M8 });
                Application.DoEvents();
                Assert(strip.Active.Device == null && titles() == "Device|Backup|Restore" && form.Controls.Find("StartOperation", true).Length > 0,
                    "identifying a device in Gear Backup mode keeps every backup tool");
                strip.SetActive(GearIconStrip.Gears[1]); Application.DoEvents();
                Assert(titles() == "Device|Sample check|Card migration|Song" && !named("GoToBackup").Visible && !named("GoToRestore").Visible,
                    "M8 mode replaces Backup with Sample check and drops backup/restore shortcuts: " + titles());
                Assert(form.Controls.Find("StartOperation", true).Length == 0 && named("DetectDevice").Visible && named("CheckM8Samples") != null,
                    "M8 mode exposes Detect device and sample checking without backup controls");
                form.ShowDevice(Devices.TrackerMini); Assert(strip.Active.Device == Devices.TrackerMini && named("GearPlaceholder1").Text.Contains("Tracker Mini"), "in a device mode an identified device of another type switches to its mode");
                form.ShowDevice(null); Assert(strip.Active.Device == Devices.TrackerMini, "no device keeps the chosen mode");
                form.ShowDevice(Devices.X100VI);
                Assert(strip.Active.Device == null && titles() == "Device|Backup|Restore" && form.Controls.Find("StartOperation", true).Length > 0 && named("GoToBackup").Visible,
                    "a device without a mode of its own uses Gear Backup, with Backup and Restore back as they were");

                tabs.SelectedIndex = 2; Application.DoEvents();
                strip.SetActive(GearIconStrip.Gears[3]); Application.DoEvents();
                Assert(titles() == "Device|Coming soon|Planned" && form.Controls.Find("StartOperation", true).Length == 0, "switching mode while on the Restore tab shows its placeholder");
                strip.SetActive(GearIconStrip.Gears[0]); Application.DoEvents();
                Assert(tabs.SelectedIndex == 2 && tabs.SelectedTab.Controls.Find("StartOperation", true).Length > 0 && named("RestoreOperation").Visible && !named("BackupOperation").Visible,
                    "returning to Gear Backup puts the restore controls back on the Restore tab");
                tabs.SelectedIndex = 0;

                var asked = new List<DeviceProfile>();
                bool available = true;
                runner.Detector = only => { asked.Add(only); return available ? new SourceChoice { Path = folder, Detected = only ?? Devices.M8 } : null; };
                Action<int> click = index => {
                    int before = asked.Count;
                    ((Button)buttons[index]).PerformClick();
                    var timer = Stopwatch.StartNew();
                    while ((asked.Count == before || runner.IsBusy) && timer.Elapsed.TotalSeconds < 20) { Application.DoEvents(); Thread.Sleep(10); }
                    Application.DoEvents();
                    Assert(asked.Count == before + 1, "clicking a gear icon looks for a device");
                };
                click(3);
                Assert(asked[asked.Count - 1] == Devices.OPXY && strip.Active.Device == Devices.OPXY && titles() == "Device|Coming soon|Planned",
                    "clicking OP-XY enters the OP-XY mode and looks for an OP-XY");
                available = false;
                click(1);
                Assert(asked[asked.Count - 1] == Devices.M8 && strip.Active.Device == Devices.M8 && titles() == "Device|Sample check|Card migration|Song",
                    "clicking M8 selects the M8 mode even when no M8 is connected");
                click(0);
                Assert(asked[asked.Count - 1] == null && strip.Active.Device == null && titles() == "Device|Backup|Restore",
                    "clicking Gear Backup returns to the full app and looks for any device");
                Assert(form.Controls.Find("StartOperation", true).Length > 0 && named("GoToBackup").Visible, "Gear Backup behaves as it did before");
                runner.Detector = Session.DetectSource;
            }
            finally { RemoveFixtures(folder); }
        }
    }

    static IEnumerable<Control> Descendants(Control parent)
    {
        yield return parent;
        foreach (Control child in parent.Controls) foreach (Control inner in Descendants(child)) yield return inner;
    }

    sealed class FakeSongReader : IM8SongReader
    {
        public string LastPath;
        public M8SongSummary Read(string path)
        {
            LastPath = path;
            if (path.EndsWith("bad.m8s")) throw new M8SongException("Not a readable M8 song: test");
            var song = new M8SongSummary { Name = "DEMO & CO", Firmware = "6.2.0", Tempo = 128, Transpose = 12, SongRows = 3, Chains = 2, Phrases = 4, Tables = 1 };
            song.TrackRows[0] = 3;
            song.Instruments.Add(new M8InstrumentInfo { Slot = 10, Kind = "Sampler", Name = "KICK", Sample = "/Samples/kick.wav" });
            song.Instruments.Add(new M8InstrumentInfo { Slot = 11, Kind = "FM Synth", Name = "BASS" });
            return song;
        }
    }

    [Test("Song tab", Gui = true)]
    static void TestSongTab()
    {
        IM8SongReader original = M8SongReaders.Current;
        var fake = new FakeSongReader();
        M8SongReaders.Current = fake;
        try
        {
            using (var form = new MainForm(true))
            {
                form.Show(); Application.DoEvents();
                var tabs = (TabControl)form.Controls.Find("WorkflowTabs", true)[0];
                Assert(tabs.TabPages.Count == 3, "the Song tab is not shown in the Gear Backup view");
                ((GearIconStrip)form.Controls.Find("GearIcons", true)[0]).SetActive(GearIconStrip.Gears[1]); Application.DoEvents();
                var pathBox = (TextBox)form.Controls.Find("SongFilePath", true)[0];
                var notice = form.Controls.Find("SongNotice", true)[0];
                var summary = form.Controls.Find("SongSummary", true)[0];
                var grid = (DataGridView)form.Controls.Find("SongInstruments", true)[0];
                var browse = (Button)form.Controls.Find("BrowseSong", true)[0];
                tabs.SelectedIndex = 3; Application.DoEvents();
                Assert(tabs.SelectedTab.Text == "Song" && tabs.SelectedTab.Contains(browse) && notice.Visible && !summary.Visible && !grid.Visible && pathBox.ReadOnly,
                    "the Song tab starts with a prompt to choose a song");
                form.SongFilePicker = () => @"C:\Music\demo.m8s";
                browse.PerformClick();
                WaitForGui(() => summary.Visible, "song details");
                Assert(fake.LastPath == @"C:\Music\demo.m8s" && pathBox.Text == @"C:\Music\demo.m8s" && !notice.Visible, "choosing a song reads it through the reader");
                Assert(!((Label)summary).UseMnemonic && !((Label)notice).UseMnemonic && summary.Text.Contains("DEMO & CO"), "song text shows an & literally instead of as a shortcut key");
                Assert(summary.Text.Contains("DEMO") && summary.Text.Contains("128 BPM") && summary.Text.Contains("firmware 6.2.0") && summary.Text.Contains("1: 3") && summary.Text.Contains("2 instruments"),
                    "the song's name, firmware, tempo, tracks and counts are shown: " + summary.Text);
                Assert(grid.Visible && grid.Rows.Count == 2 && (string)grid.Rows[0].Cells[0].Value == "0A" && (string)grid.Rows[0].Cells[1].Value == "Sampler" &&
                    (string)grid.Rows[0].Cells[3].Value == "/Samples/kick.wav", "instruments are listed with hex slot, type, name and sample");
                form.SongFilePicker = () => @"C:\Music\bad.m8s";
                browse.PerformClick();
                WaitForGui(() => notice.Text.StartsWith("Not a readable"), "error message");
                Assert(notice.Text.Contains("Not a readable M8 song") && !summary.Visible && !grid.Visible && grid.Rows.Count == 0, "an unreadable song shows the reason and clears the old details");
                tabs.SelectedIndex = 3; Application.DoEvents();
                var icons = (GearIconStrip)form.Controls.Find("GearIcons", true)[0];
                icons.SetActive(GearIconStrip.Gears[3]); Application.DoEvents();
                Assert(tabs.TabPages.Count == 3 && tabs.SelectedIndex == 0 && form.Controls.Find("BrowseSong", true).Length == 0, "leaving the M8 view while on the Song tab removes it and returns to Device");
                icons.SetActive(GearIconStrip.Gears[0]); Application.DoEvents();
                tabs.SelectedIndex = 1; Application.DoEvents();
                Assert(tabs.SelectedTab.Text == "Backup" && form.Controls.Find("StartOperation", true)[0].Visible, "switching back to Backup still shows its controls");
            }
        }
        finally { M8SongReaders.Current = original; }
    }
}
