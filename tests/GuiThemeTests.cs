// GUI tests of the appearances: themes, the Windows appearance, readability and native title bars.
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
    [Test("Appearance themes", Gui = true, Folder = ".")]
    static void TestGuiThemes(string root)
    {
        string settingsPath = Path.Combine(root, "theme-settings"); Directory.CreateDirectory(settingsPath);
        var config = new Config { BackupFolder = root, SettingsDirectory = settingsPath, SourceFolder = Path.Combine(root, "card") };
        try
        {
            using (var form = new MainForm(false, config))
            using (var table = new PreviewDialog(new OperationPlan { Label = "Theme test", Actions = new List<string> { "New: song.txt" }, Bytes = 4 }))
            {
                form.Show(); table.Show(form); Application.DoEvents();
                Assert(form.Icon != null && object.ReferenceEquals(form.Icon, table.Icon),
                    "Review changes uses the shared Gear Backup window icon");
                form.ShowDevice(Devices.OPXY, "MTP");
                Assert(form.BackColor == Color.FromArgb(243, 245, 247), "Original is the default desktop palette");
                var source = (TextBox)form.Controls.Find("SourceFolder", true)[0]; string previousSource = source.Text;
                var grid = FindGuiControl<DataGridView>(table);
                SetGuiTheme(form, 0, true);
                Assert(GuiTheme.ThemeName == "original" && form.BackColor == Color.FromArgb(243, 245, 247), "saving Original recolors the main window immediately");
                CheckNativeTitleBar(form, false);
                Assert(grid.BackgroundColor == Color.White && grid.DefaultCellStyle.BackColor == Color.White,
                    "open dialog tables change along with the main window: " + grid.BackgroundColor + " / " + grid.DefaultCellStyle.BackColor);
                Assert(grid.AlternatingRowsDefaultCellStyle.BackColor == GuiTheme.Surface &&
                    grid.ColumnHeadersDefaultCellStyle.BackColor == GuiTheme.Background &&
                    grid.DefaultCellStyle.SelectionBackColor == GuiTheme.AccentSoft,
                    "table headers, alternating rows and selections use the selected palette");
                Assert(source.Text == previousSource && ((Label)form.Controls.Find("DetectedDevice", true)[0]).Text.Contains("OP-XY"),
                    "theme changes preserve source selections and detected device information");
                Config loaded = null; WithUi(() => loaded = LoadConfig(settingsPath));
                Assert(loaded.Theme == "original", "selected theme persists in the shared INI");
                SetGuiTheme(form, 1, false);
                Assert(GuiTheme.ThemeName == "original", "cancelling a theme change leaves the app unchanged");
                WithUi(() => loaded = LoadConfig(settingsPath));
                Assert(loaded.Theme == "original", "cancelling does not save the new theme");
                using (var restarted = new MainForm(false, loaded))
                    Assert(restarted.BackColor == Color.FromArgb(243, 245, 247), "startup applies the saved Original theme");
                SetGuiTheme(form, 1, true);
                Assert(GuiTheme.ThemeName == "original-dark" && form.BackColor == Color.FromArgb(19, 25, 30),
                    "saving Original dark applies the charcoal palette immediately");
                CheckNativeTitleBar(form, true);
                CheckNativeTitleBar(table, true);
                using (var about = new AboutDialog())
                {
                    about.Show(form); Application.DoEvents();
                    CheckNativeTitleBar(about, true);
                    Assert(object.ReferenceEquals(form.Icon, about.Icon), "new About window uses the app icon");
                }
                Assert(grid.DefaultCellStyle.BackColor == GuiTheme.Surface && grid.DefaultCellStyle.ForeColor == GuiTheme.Ink &&
                    ((ComboBox)table.Controls.Find("PreviewFilter", true)[0]).BackColor == GuiTheme.Surface,
                    "open dialog tables and native inputs use dark surfaces and readable text");
                WithUi(() => loaded = LoadConfig(settingsPath));
                Assert(loaded.Theme == "original-dark", "dark theme persists in the INI");
                using (var restarted = new MainForm(false, loaded))
                {
                    restarted.Show();
                    Assert(restarted.BackColor == Color.FromArgb(19, 25, 30), "startup restores the dark palette");
                    CheckNativeTitleBar(restarted, true);
                    typeof(Control).GetMethod("RecreateHandle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(restarted, null);
                    CheckNativeTitleBar(restarted, true);
                }
                string themedManual = Path.Combine(settingsPath, "Manual-original-dark.html");
                File.WriteAllText(themedManual, "test");
                Assert(MainForm.ManualPath(settingsPath, "original-dark") == themedManual, "Help selects the matching dark manual");
                Assert(MainForm.ManualPath(settingsPath, "original") == Path.Combine(settingsPath, "Manual.html"), "Help falls back when a themed manual is absent");
                SetGuiTheme(form, 3, true);
                CheckNativeTitleBar(form, false);
                CheckNativeTitleBar(table, false);
                Assert(form.BackColor == Color.FromArgb(0, 128, 128) && grid.BackgroundColor == Color.FromArgb(255, 255, 240),
                    "switching back to Retro restores the desktop and paper colors");
                Assert(((Label)form.Controls.Find("DetectedDevice", true)[0]).ForeColor == GuiTheme.AccentDark,
                    "switching themes restores the detected device highlight");
            }
        }
        finally { GuiTheme.ApplyTheme("original"); }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    static void CheckNativeTitleBar(Form form, bool dark)
    {
        int value;
        if (DwmGetWindowAttribute(form.Handle, 20, out value, sizeof(int)) == 0)
            Assert(value == (dark ? 1 : 0), "native title bar follows theme for " + form.GetType().Name);
        if (DwmGetWindowAttribute(form.Handle, 35, out value, sizeof(int)) == 0)
            Assert(value == (GuiTheme.ThemeName == "windows" ? -1 : ColorTranslator.ToWin32(GuiTheme.ThemeName == "retro" ? GuiTheme.Accent : GuiTheme.Background)),
                "native caption exactly matches the selected palette");
        if (DwmGetWindowAttribute(form.Handle, 36, out value, sizeof(int)) == 0)
            Assert(value == (GuiTheme.ThemeName == "windows" ? -1 : ColorTranslator.ToWin32(GuiTheme.ThemeName == "retro" ? Color.White : GuiTheme.Ink)),
                "native caption text matches the selected palette");
    }

    static double ThemeContrast(Color a, Color b)
    {
        Func<Color, double> luminance = delegate(Color c) {
            Func<int, double> channel = delegate(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); };
            return 0.2126 * channel(c.R) + 0.7152 * channel(c.G) + 0.0722 * channel(c.B);
        };
        double x = luminance(a), y = luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    // Every theme's text colours stay readable on the surfaces they are drawn on.
    [Test("Windows appearance", Gui = true, Folder = "windows-theme")]
    static void TestWindowsTheme(string root)
    {
        Directory.CreateDirectory(root);
        var provider = GuiTheme.ReadWindowsAppearance;
        var appearance = new GuiTheme.WindowsAppearance { Accent = Color.FromArgb(0, 103, 192) };
        GuiTheme.ReadWindowsAppearance = () => appearance;
        try
        {
            var config = new Config { SettingsDirectory = root, BackupFolder = root };
            using (var form = new MainForm(false, config))
            using (var browser = new RestoreBrowserDialog(form, new Snapshot { Name = "Theme test", Folders = new List<string> { "Songs" } }, new[] { "Songs" }))
            {
                form.Show(); browser.Show(form); Application.DoEvents();
                SetGuiTheme(form, 2, true);
                Assert(GuiTheme.ThemeName == "windows" && !GuiTheme.IsDark && GuiTheme.Accent == appearance.Accent && GuiTheme.Surface == Color.White,
                    "third appearance option follows the Windows light mode and accent");
                Config loaded = null; WithUi(() => loaded = LoadConfig(root));
                Assert(loaded.Theme == "windows", "Windows preference persists as Theme=windows");
                File.WriteAllText(Path.Combine(root, "Manual-windows.html"), "fixture");
                Assert(MainForm.ManualPath(root, loaded.Theme) == Path.Combine(root, "Manual-windows.html"), "Windows appearance opens the system manual");
                var tree = (TreeView)browser.Controls.Find("RestoreFileTree", true)[0];
                tree.Nodes[0].Checked = true;
                appearance = new GuiTheme.WindowsAppearance { Dark = true, Accent = Color.FromArgb(255, 185, 0) };
                // Simulate Windows' notification without changing the user's registry or system theme.
                var message = Message.Create(form.Handle, 0x001A, IntPtr.Zero, IntPtr.Zero);
                typeof(MainForm).GetMethod("WndProc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(form, new object[] { message });
                Application.DoEvents();
                Assert(GuiTheme.ThemeName == "windows" && GuiTheme.IsDark && GuiTheme.Accent == appearance.Accent && form.BackColor == GuiTheme.Desktop && tree.BackColor == GuiTheme.Surface,
                    "Windows preference changes recolor the main window and open browsers");
                CheckNativeTitleBar(form, true); CheckNativeTitleBar(browser, true);
                Assert(tree.Nodes[0].Checked && browser.SelectedPaths.Length == 1 && loaded.Theme == "windows",
                    "system changes preserve browser checks and the saved automatic preference");
                foreach (bool darkMode in new[] { false, true })
                foreach (Color accent in new[] { Color.Black, Color.White, Color.Yellow, Color.Blue, Color.FromArgb(0, 103, 192) })
                {
                    appearance = new GuiTheme.WindowsAppearance { Dark = darkMode, Accent = accent };
                    GuiTheme.RefreshWindowsTheme();
                    Assert(ThemeContrast(GuiTheme.PrimaryInk, GuiTheme.Accent) >= 4.5 && ThemeContrast(GuiTheme.AccentDark, GuiTheme.Surface) >= 4.5,
                        "Windows buttons and text accents stay readable with light and dark system accents");
                }
                appearance = new GuiTheme.WindowsAppearance { HighContrast = true };
                GuiTheme.RefreshWindowsTheme();
                Assert(GuiTheme.Surface == SystemColors.Window && GuiTheme.Ink == SystemColors.WindowText && GuiTheme.PrimaryInk == SystemColors.HighlightText,
                    "Windows appearance honors system high-contrast colors");
                GuiTheme.ApplyTheme("original"); appearance = new GuiTheme.WindowsAppearance { Dark = true };
                GuiTheme.RefreshWindowsTheme();
                Assert(GuiTheme.ThemeName == "original" && !GuiTheme.IsDark, "system changes leave explicit themes unchanged");
            }
        }
        finally { GuiTheme.ReadWindowsAppearance = provider; GuiTheme.ApplyTheme("original"); }
    }

    [Test("Theme readability", Gui = true)]
    static void TestThemeReadability()
    {
        try
        {
            foreach (string theme in new[] { "original", "original-dark", "windows", "retro" })
            {
                GuiTheme.ApplyTheme(theme);
                foreach (Color surface in new[] { GuiTheme.Background, GuiTheme.Surface })
                    foreach (Color ink in new[] { GuiTheme.Ink, GuiTheme.Muted, GuiTheme.AccentDark })
                        Assert(ThemeContrast(ink, surface) >= 4.5, "text is readable in " + theme + ": " + ink.Name + " on " + surface.Name);
                Assert(ThemeContrast(GuiTheme.PrimaryInk, GuiTheme.Accent) >= 4.5, "primary button text is readable in " + theme);
            }
            // A disabled check box keeps readable text in the dark theme (Windows would draw it dark grey on dark).
            GuiTheme.ApplyTheme("original-dark");
            using (var panel = new Panel { Size = new Size(260, 30), BackColor = GuiTheme.Surface })
            using (var box = new CheckBox { Text = "Remove extra target files", AutoSize = true, Enabled = false })
            {
                panel.Controls.Add(box); GuiTheme.ApplyTo(panel);
                panel.CreateControl(); box.CreateControl();
                using (var bitmap = new Bitmap(box.Width, box.Height))
                {
                    box.DrawToBitmap(bitmap, new Rectangle(Point.Empty, box.Size));
                    double best = 0;
                    for (int x = 24; x < bitmap.Width; x++)
                        for (int y = 0; y < bitmap.Height; y++) best = Math.Max(best, ThemeContrast(bitmap.GetPixel(x, y), GuiTheme.Surface));
                    Assert(best >= 2.0, "disabled check box text stays visible in the dark theme");
                }
            }
            using (var dialog = new SelectionDialog("Choose", new List<string> { "One" }))
                Assert(dialog.List.DrawMode == DrawMode.OwnerDrawFixed, "choice list draws its selection in the theme's colours");
        }
        finally { GuiTheme.ApplyTheme("original"); }
    }

    [Test("All window themes", Gui = true, Folder = "window-themes")]
    static void TestAllWindowThemes(string root)
    {
        Config config; BackupCatalog catalog = ManagementFixture(root, out config);
        try
        {
            foreach (string initial in new[] { "original", "original-dark", "windows", "retro" })
            using (var owner = new MainForm(true, new Config { Theme = initial }))
            using (var profiles = new ProfileSettingsDialog(owner, config))
            {
                owner.Show();
                var windows = new List<Form> {
                    new AboutDialog(), new SettingsDialog(config),
                    new PreviewDialog(new OperationPlan { Label = "Review changes", Actions = new List<string> { "New: song.txt" } }),
                    new SelectionDialog("Choose backup", new List<string> { "Backup" }),
                    new ChoiceDialog("Continue?", new List<Option>()), new SearchDialog("Search", "Enter a name"),
                    new SampleRestoreDialog("Restore Samples/kick.wav from the selected backup?"),
                    new CatalogDialog(owner, config, false, catalog), new CatalogDialog(owner, config, true, catalog),
                    new RecipeViewerDialog(owner, null), new RestoreBrowserDialog(owner, new Snapshot { Name = "Theme test" }), profiles.CreateProfilePrompt()
                };
                try
                {
                    foreach (Form window in windows)
                    {
                        CheckNativeTitleBar(window, GuiTheme.IsDark);
                        Assert(object.ReferenceEquals(owner.Icon, window.Icon), "window has app icon before opening: " + window.Text);
                        window.Show(owner);
                    }
                    profiles.Show(owner);
                    foreach (string theme in new[] { "original-dark", "windows", "retro", "original" })
                    {
                        GuiTheme.ApplyTheme(theme);
                        CheckNativeTitleBar(owner, GuiTheme.IsDark);
                        CheckNativeTitleBar(profiles, GuiTheme.IsDark);
                        foreach (Form window in windows) CheckNativeTitleBar(window, GuiTheme.IsDark);
                        var preferences = (SettingsDialog)windows[1];
                        foreach (string name in new[] { "VerifyCopies", "CompareContents", "ReviewChanges", "RecoveryCopies" })
                            Assert(preferences.Controls.Find(name, true)[0].ForeColor == GuiTheme.Ink,
                                "settings checkbox label follows the palette: " + theme + " / " + name);
                    }
                }
                finally { foreach (Form window in windows) window.Dispose(); }
            }
        }
        finally { GuiTheme.ApplyTheme("original"); }
    }

    static void SetGuiTheme(MainForm form, int selected, bool save)
    {
        Exception failure = null;
        using (var automation = new System.Windows.Forms.Timer { Interval = 20 })
        {
            automation.Tick += delegate {
                foreach (Form owned in form.OwnedForms)
                {
                    if (!(owned is SettingsDialog)) continue;
                    automation.Stop();
                    try
                    {
                        var tabs = FindGuiControl<TabControl>(owned); tabs.SelectedIndex = 3;
                        var theme = (ComboBox)owned.Controls.Find("ColorTheme", true)[0];
                        Assert(theme.Items.Count == 4 && ((string)theme.Items[0]).StartsWith("Original —") && ((string)theme.Items[1]).StartsWith("Original (dark)") &&
                            ((string)theme.Items[2]).StartsWith("Windows") && ((string)theme.Items[3]).StartsWith("Retro"),
                            "Settings lists Original, Original (dark), Windows and Retro, in that order");
                        theme.SelectedIndex = selected;
                        if (save) ((Button)owned.Controls.Find("SaveSettings", true)[0]).PerformClick();
                        else ((Button)owned.CancelButton).PerformClick();
                    }
                    catch (Exception ex) { failure = ex; owned.Close(); }
                    break;
                }
            };
            automation.Start(); ((Button)form.Controls.Find("OpenSettings", true)[0]).PerformClick();
            if (failure != null) throw failure;
        }
    }
}
