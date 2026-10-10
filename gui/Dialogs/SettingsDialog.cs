using System;
using System.Drawing;
using System.Windows.Forms;

partial class GearBackup
{
    sealed class SettingsDialog : Form
    {
        public SettingsDialog(Config config)
        {
            Text = "Settings · " + AppIdentity.Name; Font = new Font("Segoe UI", 10); BackColor = GuiTheme.Background;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            MinimizeBox = false; MaximizeBox = false; ClientSize = new Size(680, 570); MinimumSize = new Size(580, 450);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            if (GuiHeadless) Opacity = 0;
            Shown += delegate { GuiTheme.FitToScreen(this); };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 3, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(layout);
            layout.Controls.Add(Explanation("Preferences are shared with the command-line program."), 0, 0);
            var tabs = new TabControl { Dock = DockStyle.Fill, AccessibleName = "Preference categories" };
            var tabsCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(8), Margin = Padding.Empty };
            layout.Controls.Add(tabsCard, 0, 1);
            var backup = PreferencePage(tabs, "Backup");
            var restore = PreferencePage(tabs, "Restore");
            var device = PreferencePage(tabs, "Device");
            var appearance = PreferencePage(tabs, "Appearance");
            appearance.Controls.Add(GuiTheme.Label("Color theme", 10, GuiTheme.Ink));
            var theme = new ComboBox { Name = "ColorTheme", AccessibleName = "Color theme", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
            GuiTheme.StyleComboBox(theme);
            theme.Items.AddRange(new object[] { "Original — light gray and teal", "Retro — teal, navy, gray and cream", "Original (dark) — charcoal and teal" });
            theme.SelectedIndex = config.Theme == "original-dark" ? 2 : config.Theme == "original" ? 0 : 1;
            appearance.Controls.Add(theme);
            appearance.Controls.Add(Explanation("Applies when you save. Help opens the manual matching your selected theme."));
            var verify = Check("Verify copied files", "VerifyCopies", config.Verify);
            var thorough = Check("Compare file contents", "CompareContents", config.Thorough);
            var review = Check("Review changes before copying", "ReviewChanges", config.Preview);
            var history = Check("Save point-in-time history", "PointInTimeHistory", config.PointInTimeHistory);
            backup.Controls.Add(verify);
            backup.Controls.Add(Explanation("Reads copied files back during backups and restores, checking SHA-256 integrity. Adds time; off by default."));
            backup.Controls.Add(thorough);
            backup.Controls.Add(Explanation("Uses SHA-256 to detect content changes even when size and date match. Adds reading time; off by default."));
            backup.Controls.Add(review);
            backup.Controls.Add(history);
            backup.Controls.Add(Explanation("Turn off to keep backup files and checksums without a second history copy: roughly half the space for a fresh backup. Old file versions and separate backups are still kept."));
            var retention = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            var backups = NumberField(retention, "Backups to keep", config.KeepBackups);
            var versions = NumberField(retention, "Old versions to keep", config.KeepOldVersions);
            backup.Controls.Add(retention); backup.Controls.Add(Explanation("Zero keeps all backups or versions."));
            var recovery = Check("Keep recovery copies before restoring", "RecoveryCopies", config.Recovery);
            restore.Controls.Add(recovery);
            restore.Controls.Add(Explanation("Saves files that will be overwritten or removed. Off by default."));
            restore.Controls.Add(GuiTheme.Label("Recovery location · blank uses the default", 9, GuiTheme.Muted));
            var recoveryRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            recoveryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); recoveryRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var recoveryPath = new TextBox { Name = "RecoveryLocation", Text = config.RecoveryFolder, Dock = DockStyle.Fill, AccessibleName = "Recovery location" };
            var browse = GuiTheme.Button("Browse…", false); browse.MinimumSize = new Size(100, 28); browse.Padding = Padding.Empty;
            browse.AccessibleName = "Browse recovery location";
            browse.Click += delegate { BrowseFolder(this, recoveryPath, true); };
            recoveryRow.Controls.Add(recoveryPath, 0, 0); recoveryRow.Controls.Add(browse, 1, 0); restore.Controls.Add(recoveryRow);
            recoveryRow.Enabled = recovery.Checked;
            recovery.CheckedChanged += delegate { recoveryRow.Enabled = recovery.Checked; };
            var warnings = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            var length = NumberField(warnings, "Name warning length", config.MaxNameLength); device.Controls.Add(warnings);
            device.Controls.Add(Explanation("Zero disables name-length warnings."));
            device.Controls.Add(GuiTheme.Label("After a successful operation", 9, GuiTheme.Muted));
            var eject = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, AccessibleName = "Device ejection preference" };
            eject.Items.AddRange(new object[] { "Leave the device connected", "Ask before ejecting the device", "Eject the device after completion" });
            eject.SelectedIndex = (int)config.Eject; device.Controls.Add(eject);
            tabsCard.Controls.Add(GuiTheme.Tabbed(tabs));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 14, 0, 0), FlowDirection = FlowDirection.RightToLeft };
            var save = GuiTheme.Button("Save settings", true); save.Name = "SaveSettings";
            var cancel = GuiTheme.Button("Cancel", false); cancel.DialogResult = DialogResult.Cancel;
            save.Margin = Padding.Empty; actions.Controls.Add(save); actions.Controls.Add(cancel); layout.Controls.Add(actions, 0, 2); AcceptButton = save; CancelButton = cancel;
            save.Click += delegate {
                try
                {
                    config.RecoveryFolder = ExpandPath(recoveryPath.Text); config.Verify = verify.Checked; config.Thorough = thorough.Checked;
                    config.Recovery = recovery.Checked; config.Preview = review.Checked; config.KeepBackups = (int)backups.Value;
                    config.PointInTimeHistory = history.Checked;
                    config.KeepOldVersions = (int)versions.Value; config.MaxNameLength = (int)length.Value;
                    config.Eject = (EjectMode)eject.SelectedIndex;
                    config.Theme = theme.SelectedIndex == 2 ? "original-dark" : theme.SelectedIndex == 0 ? "original" : "retro";
                    DialogResult = DialogResult.OK; Close();
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Check recovery location"); }
            };
        }
        static TableLayoutPanel PreferencePage(TabControl tabs, string title)
        {
            var page = new TabPage(title) { BackColor = GuiTheme.Surface, AutoScroll = true, Padding = new Padding(16) };
            var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(content); tabs.TabPages.Add(page); return content;
        }
        static CheckBox Check(string text, string name, bool value)
        {
            return new CheckBox { Text = text, Name = name, AccessibleName = text, Checked = value, AutoSize = true, Margin = new Padding(0, 8, 0, 6) };
        }
        static Label Explanation(string text)
        {
            var label = GuiTheme.Label(text, 9, GuiTheme.Muted); label.MaximumSize = new Size(520, 0); return label;
        }
        static NumericUpDown NumberField(FlowLayoutPanel parent, string label, int value)
        {
            var column = new FlowLayoutPanel { Width = 220, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            column.Controls.Add(GuiTheme.Label(label, 9, GuiTheme.Muted));
            var number = new NumericUpDown { Minimum = 0, Maximum = int.MaxValue, Value = value, Width = 185, AccessibleName = label };
            column.Controls.Add(number); parent.Controls.Add(column); return number;
        }
    }
}
