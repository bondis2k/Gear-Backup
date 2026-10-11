using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using static PathSafety;
using static AppSettings;
using static DeviceSelection;
using static ProfileSettings;
using static Session;
using static Workflow;
using static GearGui;

internal sealed class ProfileSettingsDialog : ToolsDialog
{
    sealed class ScopeChoice
    {
        public string Key, Label;
        public override string ToString() { return Label; }
    }
    sealed class ValueChoice
    {
        public string Value, Label;
        public override string ToString() { return Label; }
    }
    sealed class PreferenceField
    {
        public Setting Setting;
        public CheckBox UseDefault;
        public Control Editor;
        public Panel EditorRow;
    }

    readonly Config config;
    readonly ComboBox kind = new ComboBox { Name = "ProfileEditKind", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Edit device settings or a backup profile" };
    readonly ComboBox scope = new ComboBox { Name = "SettingsScope", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Device or backup profile" };
    readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill, Name = "ProfileSettingsTabs" };
    readonly Label applies = GuiTheme.Label("", 9, GuiTheme.Muted);
    readonly List<PreferenceField> fields = new List<PreferenceField>();
    readonly Dictionary<string, Dictionary<string, string>> saved;
    readonly Dictionary<string, Dictionary<string, string>> drafts = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    readonly List<ScopeChoice> scopes = new List<ScopeChoice>();
    readonly Dictionary<string, DeviceProfile> profileDevices = new Dictionary<string, DeviceProfile>(StringComparer.OrdinalIgnoreCase);
    string current;
    bool loading;

    public ProfileSettingsDialog(MainForm owner, Config options)
        : base(owner, "Device & backup settings", "Set preferences for a device, or for one backup history. Leave “Use default” checked for settings you don't need to change.")
    {
        config = options;
        saved = ReadProfileSettings(config);
        foreach (var section in saved.Values)
            foreach (string key in new List<string>(section.Keys))
            {
                Setting setting = FindPreference(key);
                if (setting.Choices == null) continue;
                Config normalized = options.Clone(); ApplySetting(normalized, key, section[key]);
                section[key] = setting.Get(normalized);
            }
        ClientSize = new Size(900, 740);
        foreach (DeviceProfile device in Devices.All) scopes.Add(new ScopeChoice { Key = "device:" + device.Id, Label = device.Name });
        foreach (string name in saved.Keys) if (name.StartsWith("profile:", StringComparison.OrdinalIgnoreCase)) AddProfile(name.Substring(8));
        if (config.Profile.Length > 0) AddProfile(config.Profile);
        foreach (string location in ConfiguredBackupLocations(config))
        {
            string directory = Path.Combine(HistoriesRoot(new Config { BackupFolder = location }), "profiles");
            if (!Directory.Exists(directory)) continue;
            try { RejectRootLinks(directory); }
            catch (IOException) { continue; }
            foreach (string history in Directory.GetDirectories(directory))
            {
                AddProfile(Path.GetFileName(history), HistoryNames.DisplayName(history));
                // A history that can't be read (linked, damaged record) just has no known device.
                DeviceProfile device = null;
                try { string backup = LatestBackup(history); if (backup != null) device = BackupDevice(backup); }
                catch (Exception ex) { if (!(ex is IOException || ex is UnauthorizedAccessException)) throw; }
                if (device != null)
                {
                    string name = Path.GetFileName(history);
                    DeviceProfile existing;
                    profileDevices[name] = profileDevices.TryGetValue(name, out existing) && existing != device ? null : device;
                }
            }
        }

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var selector = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 8) };
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        selector.Controls.Add(GuiTheme.Label("What would you like to edit?", 9, GuiTheme.Muted), 0, 0);
        selector.Controls.Add(GuiTheme.Label("Choose a device or backup history", 9, GuiTheme.Muted), 1, 0);
        GuiTheme.StyleComboBox(kind); GuiTheme.StyleComboBox(scope);
        kind.Items.AddRange(new object[] { "Settings for a device", "Settings for one backup history" });
        selector.Controls.Add(kind, 0, 1); selector.Controls.Add(scope, 1, 1);
        var create = GuiTheme.Button("New backup name…", false); create.Name = "NewProfile";
        create.Click += delegate { CreateProfile(); };
        selector.Controls.Add(create, 2, 1);
        content.Controls.Add(selector, 0, 0);
        applies.MaximumSize = new Size(800, 0); applies.Margin = new Padding(0, 0, 0, 12);
        content.Controls.Add(applies, 0, 1);
        foreach (string title in new[] { "Backup", "Restore", "Advanced" })
            tabs.TabPages.Add(new TabPage(title) { AutoScroll = true, BackColor = GuiTheme.Surface, Padding = new Padding(12) });
        Control tabbed = GuiTheme.Tabbed(tabs);
        tabbed.Controls[1].Name = "ProfileCategoryButtons";
        content.Controls.Add(tabbed, 0, 2);
        SetContent(content);
        kind.SelectedIndexChanged += delegate { RememberDraft(); PopulateScopes(); };
        scope.SelectedIndexChanged += delegate { RememberDraft(); LoadScope(); };
        ToolButton("Save changes", "SaveProfilePreferences", SaveScope, true);
        ToolButton("Reset to defaults", "ResetProfilePreferences", delegate {
            foreach (PreferenceField field in fields) field.UseDefault.Checked = true;
            Notice.Text = "Default settings selected. Click Save changes to apply.";
        });
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            RememberDraft();
            if (GuiHeadless || !HasUnsavedChanges() || e.Cancel) return;
            if (MessageBox.Show(this, "Close without saving your changes?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                e.Cancel = true;
        };
        kind.SelectedIndex = config.Profile.Length > 0 ? 1 : 0;
    }

    // A backup history's settings, listed by its readable name (or nickname); the key keeps the folder
    // name, which is what GearBackup.Profiles.ini and the CLI use.
    void AddProfile(string name, string label = null)
    {
        ScopeChoice existing = scopes.Find(item => item.Key.Equals("profile:" + name, StringComparison.OrdinalIgnoreCase));
        if (existing == null) scopes.Add(new ScopeChoice { Key = "profile:" + name, Label = label ?? HistoryNames.AutomaticName(name) });
        else if (label != null) existing.Label = label;
    }
    void PopulateScopes()
    {
        loading = true;
        current = null; scope.Items.Clear();
        string prefix = kind.SelectedIndex == 0 ? "device:" : "profile:";
        foreach (ScopeChoice item in scopes) if (item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) scope.Items.Add(item);
        loading = false;
        int selected = 0;
        if (kind.SelectedIndex == 1)
            for (int i = 0; i < scope.Items.Count; i++)
                if (((ScopeChoice)scope.Items[i]).Key.Equals("profile:" + config.Profile, StringComparison.OrdinalIgnoreCase)) selected = i;
        if (scope.Items.Count > 0) scope.SelectedIndex = selected;
        else { tabs.Enabled = false; applies.Text = "No backup histories yet. Back up a device first, or click New backup name."; Notice.Text = "Device settings apply to all backups from that device. Settings for one backup history change them for that history only."; }
    }
    Config InheritedSettings(string name)
    {
        if (name.StartsWith("device:", StringComparison.OrdinalIgnoreCase)) return config;
        string profileName = name.Substring(8);
        DeviceProfile device;
        if (!profileDevices.TryGetValue(profileName, out device))
            device = Array.Find(Devices.All, d => profileName.StartsWith(d.Id + "-", StringComparison.OrdinalIgnoreCase)) ?? Devices.Find(config.Device);
        return ResolveProfileSettings(config, device == null ? "auto" : device.Id, "");
    }
    void LoadScope()
    {
        if (loading || scope.SelectedItem == null) return;
        current = ((ScopeChoice)scope.SelectedItem).Key;
        loading = true;
        tabs.Enabled = true; fields.Clear();
        Dictionary<string, string> values;
        if (!drafts.TryGetValue(current, out values)) saved.TryGetValue(current, out values);
        Config inherited = InheritedSettings(current);
        var pages = new Dictionary<string, TableLayoutPanel>();
        foreach (TabPage page in tabs.TabPages)
        {
            while (page.Controls.Count > 0) page.Controls[0].Dispose();
            var rows = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(rows); pages[page.Text] = rows;
        }
        foreach (Setting setting in Settings)
        {
            if (!setting.IsPreference) continue;
            string value;
            bool custom = values != null && values.TryGetValue(setting.Key, out value);
            value = custom ? values[setting.Key] : setting.Get(inherited);
            string section = setting.Key == "Recovery" || setting.Key == "RecoveryFolder" ? "Restore" :
                setting.Key == "BackupFolder" || setting.Key == "PointInTimeHistory" || setting.Key == "KeepBackups" || setting.Key == "KeepOldVersions" ? "Backup" : "Advanced";
            AddField(pages[section], setting, inherited, value, custom);
        }
        applies.Text = current.StartsWith("device:", StringComparison.OrdinalIgnoreCase)
            ? "Applies to backups from " + ((ScopeChoice)scope.SelectedItem).Label + ". Defaults come from Settings. Settings for one backup history can change them further."
            : "Applies only to “" + ((ScopeChoice)scope.SelectedItem).Label + "”. Defaults come from the device when known, otherwise Settings.";
        loading = false;
        Notice.Text = "Change the settings you need, then click Save changes. Your choices are shared with the CLI.";
    }
    void AddField(TableLayoutPanel page, Setting setting, Config inherited, string value, bool custom)
    {
        string defaultValue = setting.Get(inherited);
        string displayDefault = defaultValue == "" ? (setting.Key == "RecoveryFolder" ? "automatic recovery location" : DefaultBackupFolder()) : FriendlyValue(setting, defaultValue);
        var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Margin = new Padding(0, 0, 0, 16) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(GuiTheme.Label(setting.Label, 10, GuiTheme.Ink));
        var useDefault = new CheckBox { Name = "ProfileUseDefault_" + setting.Key, Text = "Use default: " + displayDefault, Dock = DockStyle.Top, Height = 28, AutoEllipsis = true, Checked = !custom,
            AccessibleName = "Use default for " + setting.Label, Margin = new Padding(0, 5, 0, 5) };
        row.Controls.Add(useDefault);
        var editorRow = new Panel { Dock = DockStyle.Top, Height = 42, Enabled = custom };
        Control editor;
        if (setting.Choices != null)
        {
            var list = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            GuiTheme.StyleComboBox(list);
            Config normalized = inherited.Clone(); ApplySetting(normalized, setting.Key, value);
            foreach (string choice in setting.Choices) list.Items.Add(new ValueChoice { Value = choice, Label = FriendlyValue(setting, choice) });
            for (int i = 0; i < list.Items.Count; i++) if (((ValueChoice)list.Items[i]).Value == setting.Get(normalized)) list.SelectedIndex = i;
            editor = list;
        }
        else if (setting.Location)
        {
            var path = new TextBox { Text = value, Dock = DockStyle.Fill };
            var browse = GuiTheme.Button("Browse…", false); browse.Dock = DockStyle.Right; browse.Width = 100;
            browse.Click += delegate { using (var picker = new FolderBrowserDialog { Description = "Choose " + setting.Label.ToLowerInvariant() }) if (picker.ShowDialog(this) == DialogResult.OK) path.Text = picker.SelectedPath; };
            editorRow.Controls.Add(browse); editor = path;
        }
        else
        {
            int number; int.TryParse(value, out number);
            editor = new NumericUpDown { Minimum = 0, Maximum = int.MaxValue, Value = Math.Max(0, number), Dock = DockStyle.Fill };
        }
        editor.Name = "ProfileValue_" + setting.Key; editor.AccessibleName = setting.Label;
        editorRow.Controls.Add(editor); editor.BringToFront(); row.Controls.Add(editorRow);
        string help = setting.Key == "KeepBackups" || setting.Key == "KeepOldVersions" ? "0 keeps everything." :
            setting.Key == "PointInTimeHistory" ? "On keeps earlier states available for restore. Off uses less space." :
            setting.Key == "Recovery" ? "Save existing files before restore replaces or removes them." :
            setting.Key == "RecoveryFolder" ? "Leave blank to use the automatic recovery location." :
            setting.Key == "Verify" ? "Read copies back to check their integrity. Adds time." :
            setting.Key == "Thorough" ? "Compare contents instead of size and date. Adds reading time." :
            setting.Key == "HardLinks" ? "Save history space on NTFS; linked files share the same content." :
            setting.Key == "Preview" ? "Review the file plan before copying." :
            setting.Key == "MaxNameLength" ? "0 disables name-length warnings." : "";
        if (help.Length > 0) row.Controls.Add(GuiTheme.Label(help, 9, GuiTheme.Muted));
        var field = new PreferenceField { Setting = setting, UseDefault = useDefault, Editor = editor, EditorRow = editorRow };
        fields.Add(field); page.Controls.Add(row);
        useDefault.CheckedChanged += delegate {
            editorRow.Enabled = !useDefault.Checked;
            if (useDefault.Checked)
            {
                var list = editor as ComboBox;
                var count = editor as NumericUpDown;
                if (list != null)
                {
                    for (int i = 0; i < list.Items.Count; i++) if (((ValueChoice)list.Items[i]).Value == defaultValue) list.SelectedIndex = i;
                }
                else if (count != null) { int number; int.TryParse(defaultValue, out number); count.Value = Math.Max(0, number); }
                else editor.Text = defaultValue;
            }
            Changed();
        };
        editor.TextChanged += delegate { Changed(); };
        var combo = editor as ComboBox; if (combo != null) combo.SelectedIndexChanged += delegate { Changed(); };
        var numeric = editor as NumericUpDown; if (numeric != null) numeric.ValueChanged += delegate { Changed(); };
    }
    static string FriendlyValue(Setting setting, string value)
    {
        if (setting.Key == "Eject") return value == "yes" ? "Eject after completion" : value == "ask" ? "Ask before ejecting" : "Leave connected";
        if (value == "yes") return "On";
        if (value == "no") return "Off";
        if (value == "ask") return "Ask each time";
        return value;
    }
    void Changed() { if (!loading) Notice.Text = "Unsaved changes. Click Save changes to apply them."; }
    Dictionary<string, string> ReadFields()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (PreferenceField field in fields)
        {
            if (field.UseDefault.Checked) continue;
            var combo = field.Editor as ComboBox;
            var number = field.Editor as NumericUpDown;
            values[field.Setting.Key] = combo != null ? ((ValueChoice)combo.SelectedItem).Value :
                number != null ? number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : field.Editor.Text.Trim();
        }
        return values;
    }
    void RememberDraft() { if (!loading && current != null) drafts[current] = ReadFields(); }
    bool HasUnsavedChanges()
    {
        foreach (var draft in drafts)
        {
            Dictionary<string, string> original;
            if (!saved.TryGetValue(draft.Key, out original)) original = new Dictionary<string, string>();
            if (original.Count != draft.Value.Count) return true;
            foreach (var value in draft.Value) { string old; if (!original.TryGetValue(value.Key, out old) || old != value.Value) return true; }
        }
        return false;
    }
    void SaveScope()
    {
        if (current == null) return;
        Dictionary<string, string> values = ReadFields();
        SaveProfileSettings(config, current, values);
        saved[current] = values; drafts.Remove(current);
        Notice.Text = "Saved for “" + ((ScopeChoice)scope.SelectedItem).Label + "”. Changes apply to the next operation.";
    }
    void CreateProfile()
    {
        using (var dialog = CreateProfilePrompt())
        {
            var name = (TextBox)dialog.Controls.Find("NewProfileName", true)[0];
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            RememberDraft(); AddProfile(name.Text.Trim());
            if (kind.SelectedIndex != 1) kind.SelectedIndex = 1; else PopulateScopes();
            for (int i = 0; i < scope.Items.Count; i++)
                if (((ScopeChoice)scope.Items[i]).Key.Equals("profile:" + name.Text.Trim(), StringComparison.OrdinalIgnoreCase)) scope.SelectedIndex = i;
        }
    }
    internal Form CreateProfilePrompt()
    {
        var dialog = new Form { Text = "New backup name", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(430, 170),
            Font = Font, BackColor = GuiTheme.Background, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false };
        if (GuiHeadless) dialog.Opacity = 0;
        var label = GuiTheme.Label("Name for a separate backup history", 10, GuiTheme.Ink); label.Location = new Point(20, 20);
        var name = new TextBox { Name = "NewProfileName", Location = new Point(20, 50), Width = 390 };
        var add = GuiTheme.Button("Create", true); add.Location = new Point(250, 100);
        var cancel = GuiTheme.Button("Cancel", false); cancel.Location = new Point(120, 100); cancel.DialogResult = DialogResult.Cancel;
        add.Click += delegate { try { ValidateProfile(name.Text.Trim()); dialog.DialogResult = DialogResult.OK; } catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Choose a backup name"); } };
        dialog.Controls.AddRange(new Control[] { label, name, add, cancel }); dialog.AcceptButton = add; dialog.CancelButton = cancel;
        GuiTheme.ApplyTo(dialog);
        return dialog;
    }
}
