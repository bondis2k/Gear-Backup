using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using GearBackup.Gui;

partial class Tests
{
    [Test("Sample restore confirmation theme", Gui = true)]
    static void TestSampleRestoreDialog()
    {
        try
        {
            foreach (string theme in new[] { "original", "original-dark", "windows", "retro" })
            using (var owner = new MainForm(true, new GearBackup.Config { Theme = theme }))
            {
                owner.Show(); Application.DoEvents();
                foreach (bool accept in new[] { false, true })
                using (var dialog = new SampleRestoreDialog("Restore this sample?\r\nSongs\\Demos\\Samples\\kick & snare.wav\r\nFull backup · 2026-10-16\r\nReview the destination in Restore."))
                using (var timer = new System.Windows.Forms.Timer { Interval = 20 })
                {
                    Exception failure = null;
                    timer.Tick += delegate {
                        timer.Stop();
                        try
                        {
                            var details = (Label)dialog.Controls.Find("SampleRestoreDetails", true)[0];
                            Assert(dialog.BackColor == GuiTheme.Background && details.BackColor == GuiTheme.Surface && details.ForeColor == GuiTheme.Ink,
                                "sample confirmation uses the current palette: " + theme);
                            Assert(ThemeContrast(details.ForeColor, details.BackColor) >= 4.5 && details.Text.Contains("kick & snare.wav"), "paths remain readable and literal: " + theme);
                            Assert(dialog.AcceptButton == dialog.CancelButton && dialog.Accept.Text == "Open Restore", "cancellation is the default and the positive action describes the handoff");
                            CheckNativeTitleBar(dialog, GuiTheme.IsDark);
                            if (theme == "original-dark" && !accept)
                                using (var bitmap = new Bitmap(dialog.ClientSize.Width, dialog.ClientSize.Height))
                                {
                                    dialog.Controls[0].DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.ClientSize));
                                    bitmap.Save(Path.Combine(TestBuildDirectory, "sample-restore-dialog.png"));
                                }
                            if (accept) dialog.Accept.PerformClick(); else ((Button)dialog.CancelButton).PerformClick();
                        }
                        catch (Exception ex) { failure = ex; dialog.Close(); }
                    };
                    timer.Start();
                    DialogResult result = dialog.ShowDialog(owner);
                    if (failure != null) throw failure;
                    Assert(result == (accept ? DialogResult.OK : DialogResult.Cancel), "the themed confirmation returns the selected action: " + theme);
                }
            }
        }
        finally { GuiTheme.ApplyTheme("original"); }
    }
}
