using System;
using System.Drawing;
using System.Windows.Forms;

partial class M8Backup
{
    sealed class SettingsDialog : Form
    {
        public SettingsDialog(Config config)
        {
            Text = "Settings · M8 Backup"; Font = new Font("Segoe UI", 10); BackColor = GuiTheme.Background;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false; MaximizeBox = false; ClientSize = new Size(640, 520); AutoScaleMode = AutoScaleMode.Dpi;
            if (GuiHeadless) Opacity = 0;
            var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), FlowDirection = FlowDirection.TopDown, WrapContents = false };
            Controls.Add(layout); layout.Controls.Add(GuiTheme.Label("Preferences", 20, GuiTheme.Ink));
            layout.Controls.Add(GuiTheme.Label("Saved settings are shared with the command-line program.", 10, GuiTheme.Muted));
            var verify = new CheckBox { Text = "Verify copied files", Checked = config.Verify, AutoSize = true };
            var thorough = new CheckBox { Text = "Compare file contents instead of size and time", Checked = config.Thorough, AutoSize = true };
            var review = new CheckBox { Text = "Review changes before copying", Checked = config.Preview, AutoSize = true };
            var recovery = new CheckBox { Text = "Keep recovery copies before restoring", Checked = config.Recovery, AutoSize = true };
            layout.Controls.Add(verify); layout.Controls.Add(thorough); layout.Controls.Add(review); layout.Controls.Add(recovery);
            layout.Controls.Add(GuiTheme.Label("Recovery location  ·  blank uses the default", 9, GuiTheme.Muted));
            var recoveryRow = new FlowLayoutPanel { Width = 580, Height = 38 };
            var recoveryPath = new TextBox { Text = config.RecoveryFolder, Width = 450 };
            var browse = GuiTheme.Button("Browse…", false); browse.MinimumSize = new Size(100, 28); browse.Padding = Padding.Empty;
            browse.Click += delegate { BrowseFolder(this, recoveryPath, true); };
            recoveryRow.Controls.Add(recoveryPath); recoveryRow.Controls.Add(browse); layout.Controls.Add(recoveryRow);
            var retention = new FlowLayoutPanel { Width = 580, Height = 62 };
            var backups = NumberField(retention, "Backups to keep", config.KeepBackups);
            var versions = NumberField(retention, "Old versions to keep", config.KeepOldVersions);
            var length = NumberField(retention, "Name warning length", config.MaxNameLength); layout.Controls.Add(retention);
            layout.Controls.Add(GuiTheme.Label("Zero keeps all backups/versions or disables name warnings.", 9, GuiTheme.Muted));
            var eject = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
            eject.Items.AddRange(new object[] { "Leave M8 connected", "Ask before ejecting M8", "Eject M8 after completion" });
            eject.SelectedIndex = config.Eject == "yes" ? 2 : config.Eject == "ask" ? 1 : 0; layout.Controls.Add(eject);
            var actions = new FlowLayoutPanel { Width = 580, Height = 50, Padding = new Padding(0, 12, 0, 0) };
            var save = GuiTheme.Button("Save settings", true); var cancel = GuiTheme.Button("Cancel", false); cancel.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(save); actions.Controls.Add(cancel); layout.Controls.Add(actions); AcceptButton = save; CancelButton = cancel;
            save.Click += delegate {
                try
                {
                    config.RecoveryFolder = ExpandPath(recoveryPath.Text); config.Verify = verify.Checked; config.Thorough = thorough.Checked;
                    config.Recovery = recovery.Checked; config.Preview = review.Checked; config.KeepBackups = (int)backups.Value;
                    config.KeepOldVersions = (int)versions.Value; config.MaxNameLength = (int)length.Value;
                    config.Eject = eject.SelectedIndex == 2 ? "yes" : eject.SelectedIndex == 1 ? "ask" : "no";
                    DialogResult = DialogResult.OK; Close();
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Check recovery location"); }
            };
        }
        static NumericUpDown NumberField(FlowLayoutPanel parent, string label, int value)
        {
            var column = new FlowLayoutPanel { Width = 183, Height = 62, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            column.Controls.Add(GuiTheme.Label(label, 9, GuiTheme.Muted));
            var number = new NumericUpDown { Minimum = 0, Maximum = int.MaxValue, Value = value, Width = 164 };
            column.Controls.Add(number); parent.Controls.Add(column); return number;
        }
    }}
