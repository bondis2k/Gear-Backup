using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

partial class GearBackup
{
    sealed class ProfileSettingsDialog : ToolsDialog
    {
        sealed class ScopeChoice
        {
            public string Key, Label;
            public override string ToString() { return Label; }
        }
        readonly Config config;
        readonly ComboBox scope = new ComboBox { Name = "SettingsScope", Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDown, AccessibleName = "Device or profile settings scope" };
        readonly DataGridView grid = ToolGrid("ProfilePreferences");
        readonly Dictionary<string, Dictionary<string, string>> saved;
        public ProfileSettingsDialog(MainForm owner, Config options) : base(owner, "Profiles", "Choose a device or profile. Checked preferences override defaults; unchecked preferences inherit. Profile preferences take priority over device preferences.")
        {
            config = options; saved = ReadProfileSettings(config);
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.Controls.Add(scope, 0, 0); content.Controls.Add(grid, 0, 1); ToolLayout.Controls.Add(content, 0, 1);
            grid.ReadOnly = false;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Override", HeaderText = "Override", FillWeight = 25 });
            grid.Columns.Add("Preference", "Preference"); grid.Columns[1].ReadOnly = true;
            grid.Columns.Add("Value", "Value"); grid.Columns.Add("Default", "Inherited value"); grid.Columns[3].ReadOnly = true;
            var scopes = new List<string>(); foreach (DeviceProfile device in Devices.All) scopes.Add("device:" + device.Id);
            scopes.AddRange(saved.Keys);
            if (config.Profile.Length > 0) scopes.Add("profile:" + config.Profile);
            foreach (string location in ConfiguredBackupLocations(config))
            {
                string profiles = Path.Combine(HistoriesRoot(new Config { BackupFolder = location }), "profiles");
                if (Directory.Exists(profiles)) { RejectRootLinks(profiles); foreach (string path in Directory.GetDirectories(profiles)) scopes.Add("profile:" + Path.GetFileName(path)); }
            }
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in scopes) if (unique.Add(name)) scope.Items.Add(new ScopeChoice { Key = name,
                Label = name.StartsWith("device:", StringComparison.OrdinalIgnoreCase) ? Devices.Find(name.Substring(7)).Name + " · device defaults" : name.Substring(8) + " · profile" });
            scope.SelectedIndexChanged += delegate { if (scope.SelectedIndex >= 0) LoadScope(); };
            scope.Validated += delegate { try { LoadScope(); } catch (Exception ex) { ShowToolError(ex); } };
            ToolButton("Save overrides", "SaveProfilePreferences", SaveScope);
            ToolButton("Browse location", "BrowseProfileLocation", delegate {
                if (grid.CurrentRow == null) return;
                string key = (string)grid.CurrentRow.Tag;
                if (key != "BackupFolder" && key != "RecoveryFolder") { Notice.Text = "Select the backup or recovery location row first."; return; }
                using (var picker = new FolderBrowserDialog { Description = "Choose the " + (key == "BackupFolder" ? "backup" : "recovery") + " location" })
                    if (picker.ShowDialog(this) == DialogResult.OK) { grid.CurrentRow.Cells[0].Value = true; grid.CurrentRow.Cells[2].Value = picker.SelectedPath; }
            });
            ToolButton("Use inherited settings", "ResetProfilePreferences", delegate {
                string name = ScopeKey(); ValidateSettingsScope(name); SaveProfileSettings(config, name, new Dictionary<string, string>());
                saved.Remove(name); LoadScope(); Notice.Text = "Overrides removed for this scope. Global settings are unchanged.";
            });
            scope.SelectedIndex = 0;
            Notice.Text = "Preferences are shared by the GUI and CLI. To add a named profile, type its name above.";
        }
        string ScopeKey()
        {
            var selected = scope.SelectedItem as ScopeChoice;
            return selected != null && scope.Text == selected.Label ? selected.Key : "profile:" + scope.Text.Trim();
        }
        static string PreferenceLabel(string key)
        {
            string[] labels = { "Backup location", "Point-in-time history", "Hard-linked history", "Verify copied files", "Compare file contents", "Review changes", "Backups to keep", "Old versions to keep", "Recovery copies", "Recovery location", "Name warning length", "Eject after completion" };
            return labels[Array.IndexOf(ProfileSettingKeys, key)];
        }
        static string PreferenceValue(Config config, string key)
        {
            object value = typeof(Config).GetField(key).GetValue(config);
            return value is bool ? ((bool)value ? "yes" : "no") : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
        void LoadScope()
        {
            string name = ScopeKey(); ValidateSettingsScope(name); grid.Rows.Clear();
            Dictionary<string, string> values; saved.TryGetValue(name, out values);
            Config inherited = config;
            if (name.StartsWith("profile:", StringComparison.OrdinalIgnoreCase))
            {
                string profileName = name.Substring(8); DeviceProfile device = Devices.Find(config.Device);
                if (device == null) device = Array.Find(Devices.All, d => profileName.StartsWith(d.Id + "-", StringComparison.OrdinalIgnoreCase));
                inherited = ResolveProfileSettings(config, device == null ? "auto" : device.Id, "");
            }
            foreach (string key in ProfileSettingKeys)
            {
                string value; bool overrides = values != null && values.TryGetValue(key, out value);
                value = overrides ? values[key] : PreferenceValue(inherited, key);
                int i = grid.Rows.Add(overrides, PreferenceLabel(key), value, PreferenceValue(inherited, key)); grid.Rows[i].Tag = key;
                if (typeof(Config).GetField(key).FieldType == typeof(bool))
                {
                    var normalized = inherited.Clone(); ApplySetting(normalized, key, value); value = PreferenceValue(normalized, key);
                    grid.Rows[i].Cells[2] = new DataGridViewComboBoxCell { DataSource = new[] { "yes", "no" }, Value = value };
                }
                if (key == "Eject")
                {
                    var normalized = inherited.Clone(); ApplySetting(normalized, key, value);
                    grid.Rows[i].Cells[2] = new DataGridViewComboBoxCell { DataSource = new[] { "no", "ask", "yes" }, Value = PreferenceValue(normalized, key).ToLowerInvariant() };
                }
            }
        }
        void SaveScope()
        {
            grid.EndEdit(); var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow row in grid.Rows) if (Equals(row.Cells[0].Value, true))
            {
                string key = (string)row.Tag, value = Convert.ToString(row.Cells[2].Value).Trim();
                Type type = typeof(Config).GetField(key).FieldType;
                int number;
                if (type == typeof(int) && (!int.TryParse(value, out number) || number < 0)) throw new ArgumentException(key + " must be zero or a positive integer.");
                if (type == typeof(bool) && value != "yes" && value != "no") throw new ArgumentException(key + " must be yes or no.");
                if (key == "Eject" && !Array.Exists(new[] { "no", "ask", "yes" }, v => v.Equals(value, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Eject must be no, ask or yes.");
                if (key == "BackupFolder" || key == "RecoveryFolder") value = ExpandPath(value);
                values[key] = value;
            }
            string name = ScopeKey(); SaveProfileSettings(config, name, values); saved[name] = values;
            Notice.Text = "Preferences saved. They apply when the next operation identifies this device or profile.";
        }
    }
}
