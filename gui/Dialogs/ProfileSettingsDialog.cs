using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

partial class GearBackup
{
    // Edits the device and profile preferences in GearBackup.Profiles.ini. One row per preference (the
    // Settings with a Label); a checked row overrides the inherited value, an unchecked one inherits it.
    sealed class ProfileSettingsDialog : ToolsDialog
    {
        // One entry of the scope list: "device:m8" shown as "M8 · device defaults".
        sealed class ScopeChoice
        {
            public string Key, Label;
            public override string ToString() { return Label; }
        }

        readonly Config config;
        readonly ComboBox scope = new ComboBox { Name = "SettingsScope", Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDown,
            AccessibleName = "Device or profile settings scope" };
        readonly DataGridView grid = ToolGrid("ProfilePreferences");
        readonly Dictionary<string, Dictionary<string, string>> saved;

        public ProfileSettingsDialog(MainForm owner, Config options)
            : base(owner, "Profiles", "Choose a device or profile. Checked preferences override defaults; unchecked preferences inherit. " +
                "Profile preferences take priority over device preferences.")
        {
            config = options;
            saved = ReadProfileSettings(config);

            var content = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.Controls.Add(scope, 0, 0);
            content.Controls.Add(grid, 0, 1);
            ToolLayout.Controls.Add(content, 0, 1);

            grid.ReadOnly = false;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Override", HeaderText = "Override", FillWeight = 25 });
            grid.Columns.Add("Preference", "Preference");
            grid.Columns.Add("Value", "Value");
            grid.Columns.Add("Default", "Inherited value");
            grid.Columns[1].ReadOnly = true;
            grid.Columns[3].ReadOnly = true;

            foreach (string name in KnownScopes())
                scope.Items.Add(new ScopeChoice { Key = name, Label = name.StartsWith("device:", StringComparison.OrdinalIgnoreCase)
                    ? Devices.Find(name.Substring(7)).Name + " · device defaults" : name.Substring(8) + " · profile" });
            scope.SelectedIndexChanged += delegate { if (scope.SelectedIndex >= 0) LoadScope(); };
            scope.Validated += delegate { try { LoadScope(); } catch (Exception ex) { ShowToolError(ex); } };

            ToolButton("Save overrides", "SaveProfilePreferences", SaveScope);
            ToolButton("Browse location", "BrowseProfileLocation", BrowseLocation);
            ToolButton("Use inherited settings", "ResetProfilePreferences", delegate {
                string name = ScopeKey();
                SaveProfileSettings(config, name, new Dictionary<string, string>());
                saved.Remove(name);
                LoadScope();
                Notice.Text = "Overrides removed for this scope. Global settings are unchanged.";
            });
            scope.SelectedIndex = 0;
            Notice.Text = "Preferences are shared by the GUI and CLI. To add a named profile, type its name above.";
        }

        // Every device, every scope already saved, the profile in the main window, and every history found
        // in the configured backup locations; each once.
        List<string> KnownScopes()
        {
            var scopes = new List<string>();
            foreach (DeviceProfile device in Devices.All) scopes.Add("device:" + device.Id);
            scopes.AddRange(saved.Keys);
            if (config.Profile.Length > 0) scopes.Add("profile:" + config.Profile);
            foreach (string location in ConfiguredBackupLocations(config))
            {
                string profiles = Path.Combine(HistoriesRoot(new Config { BackupFolder = location }), "profiles");
                if (!Directory.Exists(profiles)) continue;
                RejectRootLinks(profiles);
                foreach (string path in Directory.GetDirectories(profiles)) scopes.Add("profile:" + Path.GetFileName(path));
            }
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return scopes.FindAll(name => unique.Add(name));
        }

        // The selected scope, or a new profile scope for a name typed into the list.
        string ScopeKey()
        {
            var selected = scope.SelectedItem as ScopeChoice;
            string name = selected != null && scope.Text == selected.Label ? selected.Key : "profile:" + scope.Text.Trim();
            ValidateSettingsScope(name);
            return name;
        }

        // What a scope inherits: the global settings, plus the device's preferences for a profile whose
        // device is known (from the Device setting, or the profile's automatic name such as "m8-card-...").
        Config InheritedSettings(string name)
        {
            if (!name.StartsWith("profile:", StringComparison.OrdinalIgnoreCase)) return config;
            string profileName = name.Substring(8);
            DeviceProfile device = Devices.Find(config.Device) ??
                Array.Find(Devices.All, d => profileName.StartsWith(d.Id + "-", StringComparison.OrdinalIgnoreCase));
            return ResolveProfileSettings(config, device == null ? "auto" : device.Id, "");
        }

        void LoadScope()
        {
            string name = ScopeKey();
            grid.Rows.Clear();
            Dictionary<string, string> values;
            saved.TryGetValue(name, out values);
            Config inherited = InheritedSettings(name);
            foreach (Setting setting in Settings)
            {
                if (!setting.IsPreference) continue;
                string value;
                bool overrides = values != null && values.TryGetValue(setting.Key, out value);
                value = overrides ? values[setting.Key] : setting.Get(inherited);
                int i = grid.Rows.Add(overrides, setting.Label, value, setting.Get(inherited));
                grid.Rows[i].Tag = setting.Key;
                if (setting.Choices == null) continue;
                // A list of the allowed values, showing the saved value in its normal form ("On" -> "yes").
                Config normalized = inherited.Clone();
                ApplySetting(normalized, setting.Key, value);
                grid.Rows[i].Cells[2] = new DataGridViewComboBoxCell { DataSource = setting.Choices, Value = setting.Get(normalized) };
            }
        }

        void BrowseLocation()
        {
            if (grid.CurrentRow == null) return;
            Setting setting = FindSetting((string)grid.CurrentRow.Tag);
            if (!setting.Location) { Notice.Text = "Select the backup or recovery location row first."; return; }
            using (var picker = new FolderBrowserDialog { Description = "Choose the " + setting.Label.ToLowerInvariant() })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                grid.CurrentRow.Cells[0].Value = true;
                grid.CurrentRow.Cells[2].Value = picker.SelectedPath;
            }
        }

        // Saves the checked rows; SaveProfileSettings checks each value and refuses the whole save if one
        // isn't valid.
        void SaveScope()
        {
            grid.EndEdit();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (!Equals(row.Cells[0].Value, true)) continue;
                Setting setting = FindSetting((string)row.Tag);
                string value = Convert.ToString(row.Cells[2].Value).Trim();
                values[setting.Key] = setting.Location ? ExpandPath(value) : value;
            }
            string name = ScopeKey();
            SaveProfileSettings(config, name, values);
            saved[name] = values;
            Notice.Text = "Preferences saved. They apply when the next operation identifies this device or profile.";
        }
    }
}
