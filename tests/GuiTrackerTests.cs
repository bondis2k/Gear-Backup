using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GearBackup;
using GearBackup.Gui;

// The Tracker Mini view: project inspector and instrument check tabs.
partial class Tests
{
    [Test("Tracker Mini tabs", Gui = true, Folder = "tracker-gui")]
    static void TestTrackerTabs(string root)
    {
        string card = Path.Combine(root, "card"), project = Path.Combine(card, "Projects", "User", "Jam");
        Directory.CreateDirectory(Path.Combine(project, "patterns"));
        File.WriteAllBytes(Path.Combine(project, "project.mt"), TrackerProject("Jam", 128f, new[] { 1, 2 }));
        File.WriteAllBytes(Path.Combine(project, "patterns", "pattern_01.mtp"), TrackerPattern(16, 16, new[] { 0, 0, 60, 0 }, new[] { 0, 1, 62, 4 }));
        File.WriteAllBytes(Path.Combine(project, "patterns", "pattern_02.mtp"), TrackerPattern(16, 16, new[] { 0, 0, 60, 0 }));
        TrackerInstrument(Path.Combine(project, "instruments", "instrument_01.pti"), "kick", 1000, 1);

        string summaryText = null;
        WithUi(() => summaryText = DeviceInformation.Read(new SourceChoice { Path = card, Detected = Devices.TrackerMini }));
        Assert(summaryText.Contains("Tracker projects: 1") && summaryText.Contains("Needs attention:") && summaryText.Contains("Jam"), "detecting a Tracker card adds its project summary to the Device tab: " + summaryText);

        using (var form = new MainForm(true))
        {
            form.Show(); Application.DoEvents();
            var tabs = (TabControl)form.Controls.Find("WorkflowTabs", true)[0];
            Func<string> titles = () => string.Join("|", tabs.TabPages.Cast<TabPage>().Select(p => p.Text));
            var strip = (GearIconStrip)form.Controls.Find("GearIcons", true)[0];
            Func<bool> onTabs = () => tabs.TabPages.Cast<TabPage>().Any(p => p.Controls.Find("TrackerInspect", true).Length > 0);
            Assert(!onTabs(), "the Gear Backup view has no project inspector");
            strip.SetActive(GearIconStrip.Gears[2]); Application.DoEvents();
            Assert(titles() == "Device|Project inspector|Instrument check", "the Tracker Mini view has the inspector and the instrument check: " + titles());
            tabs.SelectedIndex = 1; Application.DoEvents();
            Assert(form.Controls.Find("StartOperation", true).Length == 0 && onTabs(), "the Tracker Mini view has no backup controls");
            var path = (TextBox)form.Controls.Find("TrackerProjectPath", true)[0];
            var notice = form.Controls.Find("TrackerNotice", true)[0];
            var summary = form.Controls.Find("TrackerSummary", true)[0];
            var instruments = (DataGridView)form.Controls.Find("TrackerInstruments", true)[0];
            var patterns = (DataGridView)form.Controls.Find("TrackerPatterns", true)[0];
            var missing = (DataGridView)form.Controls.Find("TrackerMissingInstruments", true)[0];
            var checkNotice = form.Controls.Find("TrackerCheckNotice", true)[0];
            var inspect = (Button)form.Controls.Find("TrackerInspect", true)[0];
            Assert(!summary.Visible && notice.Text.Contains("Choose a Tracker card"), "the inspector starts with a prompt");

            path.Text = Path.Combine(root, "empty");
            Directory.CreateDirectory(path.Text);
            inspect.PerformClick();
            WaitForGui(() => notice.Text.Contains("No Tracker project"), "no project found");

            path.Text = card;
            inspect.PerformClick();
            WaitForGui(() => summary.Visible, "project inspected");
            Assert(summary.Text.Contains("Jam") && summary.Text.Contains("128 BPM") && summary.Text.Contains("2 patterns") && summary.Text.Contains("1 instrument files"), "the project's summary is shown: " + summary.Text);
            Assert(instruments.Rows.Count == 1 && (string)instruments.Rows[0].Cells[2].Value == "kick" && (string)instruments.Rows[0].Cells[6].Value == "OK", "the instrument file is listed with its sample");
            Assert(patterns.Rows.Count == 2 && (string)patterns.Rows[0].Cells[4].Value == "1, 5" && (string)patterns.Rows[1].Cells[5].Value == "Yes", "each pattern lists the instruments it plays");
            Assert(missing.Rows.Count == 1 && (string)missing.Rows[0].Cells[0].Value == "5" && checkNotice.Text.Contains("1 instrument(s)"), "the instrument check lists instrument 5, which has no file: " + checkNotice.Text);

            // The pattern viewer shows a pattern's steps.
            patterns.CurrentCell = patterns.Rows[0].Cells[0];
            var viewButton = (Button)form.Controls.Find("TrackerViewPattern", true)[0];
            Assert(viewButton.Enabled, "View pattern is available once a pattern is selected");
            var tabUnderTest = (TrackerProjectTab)typeof(MainForm).GetField("trackerTab", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form);
            string[] header = null; string firstCell = null, secondCell = null; int rowCount = 0;
            tabUnderTest.PatternShown = dialog => {
                header = dialog.Grid.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText).ToArray();
                rowCount = dialog.Grid.Rows.Count;
                firstCell = (string)dialog.Grid.Rows[0].Cells[1].Value; secondCell = (string)dialog.Grid.Rows[1].Cells[1].Value;
            };
            viewButton.PerformClick();
            Assert(header != null && header.Length == 2 && header[0] == "Step" && header[1] == "1" && rowCount == 16 && firstCell == "C4 01" && secondCell == "D4 05", "the pattern's steps are shown, one column per track with content: " + (header == null ? "none" : string.Join("|", header)) + " " + firstCell + " " + secondCell);
            Assert(TrackerPatternDialog.Cell(new TrackerStep { Note = 62, Instrument = 49, Fx0Type = 18, Fx0Value = 80 }) == "D4 M2 V80" && TrackerPatternDialog.Cell(new TrackerStep { Note = -3 }) == "CUT" && TrackerPatternDialog.Cell(new TrackerStep { Note = -1 }) == "", "step cells are written like the Tracker's");
            TrackerInstrument(Path.Combine(project, "instruments", "instrument_05.pti"), "hat", 100, 1);
            tabs.SelectedIndex = 2; Application.DoEvents();
            ((Button)form.Controls.Find("TrackerCheckButton", true)[0]).PerformClick();
            WaitForGui(() => missing.Rows.Count == 0, "re-check finds the new instrument");
            Assert(checkNotice.Text.Contains("Every instrument"), "with the instrument added the check passes: " + checkNotice.Text);

            strip.SetActive(GearIconStrip.Gears[0]); Application.DoEvents();
            Assert(titles() == "Device|Backup|Restore" && !onTabs(), "leaving the Tracker Mini view brings the normal tabs back");
        }
    }
}
