// The GUI test program: starts the shared runner (see TestHarness.cs) on an STA thread, plus helpers the GUI tests share.
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

class GuiTestRunner
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            GearGui.GuiHeadless = true; // windows stay invisible and out of the taskbar
            return Tests.RunSelected(args, true);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

partial class Tests
{
    static void WaitForGui(Func<bool> done, string message)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (timer.Elapsed.TotalSeconds > 10) throw new Exception("GUI timed out: " + message);
            Application.DoEvents(); Thread.Sleep(10);
        }
    }

    static List<T> FindGuiControls<T>(Control parent) where T : Control
    {
        var found = new List<T>();
        foreach (Control child in parent.Controls)
        {
            if (child is T) found.Add((T)child);
            found.AddRange(FindGuiControls<T>(child));
        }
        return found;
    }

    static bool InvokeGuiShortcut(Form form, Keys keys)
    {
        var method = form.GetType().GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        object[] arguments = { Message.Create(form.Handle, 0x100, IntPtr.Zero, IntPtr.Zero), keys };
        return (bool)method.Invoke(form, arguments);
    }

    static void DriveGui(Form form, Label state, string expected, bool dismiss, Action<Form> inspect = null)
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
                    if (inspect != null) inspect(owned);
                    if (dismiss) { owned.DialogResult = DialogResult.Cancel; owned.Close(); continue; }
                    var choices = FindGuiControl<ListBox>(owned);
                    if (choices != null && choices.Items.Count > 0 && choices.SelectionMode != SelectionMode.None) choices.SelectedIndex = 0;
                    var buttons = new List<Button>();
                    CollectGuiButtons(owned, buttons);
                    Button button = buttons.Find(b => b.Text == "All files" || b.Text == "Merge");
                    if (button == null) button = buttons.Find(b => b.Text == "Proceed" || b.Text == "Close preview" || b.Text == "Continue");
                    if (button == null) button = buttons.Find(b => b.Text == "Yes");
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
