using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

class GuiTestRunner
{
    [STAThread]
    static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            M8Backup.RunGuiTests();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

partial class M8Backup
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
            var sourceBox = (TextBox)form.Controls.Find("SourceFolder", true)[0];
            var backupBox = (TextBox)form.Controls.Find("BackupFolder", true)[0];
            var mode = (ComboBox)form.Controls.Find("Operation", true)[0];
            var start = (Button)form.Controls.Find("StartOperation", true)[0];
            var preview = (Button)form.Controls.Find("PreviewOperation", true)[0];
            var cancel = (Button)form.Controls.Find("CancelOperation", true)[0];
            var state = (Label)form.Controls.Find("OperationStatus", true)[0];
            var zipBox = (CheckBox)form.Controls.Find("ZipBackup", true)[0];
            sourceBox.Text = source; backupBox.Text = Path.Combine(root, "backups");
            mode.SelectedIndex = 3;
            Assert(start.Text == "Check" && !preview.Enabled, "GUI check mode disables copy preview");
            Assert(!zipBox.Enabled, "zip option is only available for full backups");
            mode.SelectedIndex = 0;
            Assert(zipBox.Enabled, "zip option is available for a full backup");
            zipBox.Checked = true;
            preview.PerformClick();
            Assert(cancel.Enabled && !sourceBox.Enabled, "GUI disables editable inputs while worker runs");
            DriveGui(form, state, "Preview complete", false);
            Assert(!Directory.Exists(backupBox.Text), "GUI preview writes no backup directories");
            Assert(!cancel.Enabled && sourceBox.Enabled, "GUI reenables inputs after preview");
            start.PerformClick();
            DriveGui(form, state, "Complete", false);
            string history = Path.Combine(backupBox.Text, "M8 Backups", "profiles", ProfileId(Rooted(source), ""));
            string backup = ListBackups(history)[0];
            Assert(File.ReadAllText(Path.Combine(backup, "Songs", "one.m8s")) == "GUI TEST SONG", "GUI full backup executes shared engine");
            Assert(File.Exists(backup + ".zip"), "GUI full backup with the zip option saves a zip");
            zipBox.Checked = false;
            mode.SelectedIndex = 1;
            start.PerformClick();
            DriveGui(form, state, "Complete", false);
            Assert(Directory.GetFiles(SnapshotRoot(backup), "*.manifest").Length == 2, "GUI incremental records snapshot without proceed prompt when unchanged");

            string target = Path.Combine(root, "restore-target");
            Directory.CreateDirectory(target);
            sourceBox.Text = target; mode.SelectedIndex = 2;
            start.PerformClick();
            DriveGui(form, state, "Complete", false);
            Assert(File.ReadAllText(Path.Combine(target, "Songs", "one.m8s")) == "GUI TEST SONG", "GUI restore wizard restores selected snapshot");

            mode.SelectedIndex = 3; sourceBox.Text = "";
            start.PerformClick();
            DriveGui(form, state, "Complete", false);
            Assert(state.Text == "Complete", "GUI integrity check needs no source card");

            mode.SelectedIndex = 0; sourceBox.Text = source;
            int backupsBefore = ListBackups(history).Count;
            start.PerformClick();
            DriveGui(form, state, "Complete", true);
            Assert(ListBackups(history).Count == backupsBefore, "dismissing GUI acceptance dialog prevents writes");

            // Cancel inside a marshalled choice dialog: cancellation must survive Invoke.
            mode.SelectedIndex = 2; sourceBox.Text = target;
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
        var saved = new Config { BackupFolder = root, Verify = true, Recovery = false, Thorough = false, Profile = "Studio", HardLinks = true };
        SaveConfig(saved, ini);
        string text = File.ReadAllText(ini);
        Assert(text.Contains("; keep this comment") && text.Contains("CustomOption=unchanged"), "GUI settings save preserves comments and unrelated keys");
        Assert(text.Contains("Verify=yes") && !text.Contains("Verify=no") && text.Contains("Recovery=no") && text.Contains("Thorough=no"),
            "shared settings save updates duplicates and keeps checksum/recovery defaults optional");
        Assert(text.Contains("Profile=Studio") && text.Contains("SourceFolder="), "settings save includes paths and profile");
        Assert(text.Contains("HardLinks=yes"), "settings save keeps HardLinks");
        Console.WriteLine("PASS: " + assertions + " GUI assertions.");
        RemoveFixtures(root);
    }

    static void DriveGui(Form form, Label state, string expected, bool dismiss)
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
                    if (dismiss) { owned.DialogResult = DialogResult.Cancel; owned.Close(); continue; }
                    var choices = FindGuiControl<ListBox>(owned);
                    if (choices != null && choices.Items.Count > 0 && choices.SelectionMode != SelectionMode.None) choices.SelectedIndex = 0;
                    var buttons = new List<Button>();
                    CollectGuiButtons(owned, buttons);
                    Button button = buttons.Find(b => b.Text == "All files" || b.Text == "Merge");
                    if (button == null) button = buttons.Find(b => b.Text == "Proceed" || b.Text == "Close preview" || b.Text == "Continue");
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
