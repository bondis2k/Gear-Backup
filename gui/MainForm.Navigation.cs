using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    // Device discovery is separate from the two workflows. The operation selectors share the runner's
    // operation state, while backup sources and restore destinations keep independent values.
    internal sealed partial class MainForm
    {
        readonly TabControl workflowTabs = new TabControl { Name = "WorkflowTabs", Dock = DockStyle.Fill };
        // Keep inactive device tools owned by the form so theme and DPI changes reach their inputs too.
        readonly Panel deviceToolParking = new Panel { Visible = false };
        readonly ComboBox backupMode = new ComboBox { Name = "BackupOperation", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill },
            restoreMode = new ComboBox { Name = "RestoreOperation", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly TextBox restoreDestination = new TextBox { Name = "RestoreDestination", AccessibleName = "Restore destination" };
        FieldPanel sourceField, restoreField;
        Control workflowHost, operationState, activityArea;
        readonly DeviceCard deviceCard;
        readonly Label[] devicePlaceholders = new Label[2];
        bool synchronizingNavigation, backupOptionsExpanded, sizingWorkflow;
        bool cameraSettingsAvailable;
        int redrawFreezes;
        RedrawFreeze tabSwitchFreeze;

        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        // Switching tabs moves panels, shows and hides controls and resizes the tab area, and each step would
        // repaint the window on its own, which shows as flicker. While this is held the window keeps its
        // current picture and lays everything out at once; it is painted a single time when released.
        sealed class RedrawFreeze : IDisposable
        {
            const int SetRedraw = 0x000B;
            readonly MainForm form;
            bool active;
            internal RedrawFreeze(MainForm form)
            {
                this.form = form;
                if (!form.IsHandleCreated) return;
                active = true;
                if (form.redrawFreezes++ != 0) return;
                form.SuspendLayout();
                SendMessage(form.Handle, SetRedraw, IntPtr.Zero, IntPtr.Zero);
            }
            public void Dispose()
            {
                if (!active) return;
                active = false;
                if (--form.redrawFreezes != 0) return;
                form.ResumeLayout(true);
                SendMessage(form.Handle, SetRedraw, (IntPtr)1, IntPtr.Zero);
                form.Invalidate(true);
            }
        }

        void ReleaseTabSwitch()
        {
            RedrawFreeze freeze = tabSwitchFreeze;
            tabSwitchFreeze = null;
            if (freeze != null) freeze.Dispose();
        }

        Control BuildWorkflowTabs()
        {
            foreach (string title in new[] { "Device", "Backup", "Restore", "Song" })
                workflowTabs.TabPages.Add(new TabPage(title) { BackColor = GuiTheme.Surface, Padding = new Padding(12), AutoScroll = true });
            inputs.SizeChanged += delegate { RefreshWorkflowLayout(); };
            detect.Name = "DetectDevice";
            workflowTabs.TabPages[0].Controls.Add(deviceCard.Build(detect, delegate { workflowTabs.SelectedIndex = 1; }, delegate { workflowTabs.SelectedIndex = 2; }));
            workflowTabs.TabPages[1].Controls.Add(inputs);
            songPage = workflowTabs.TabPages[SongTabIndex];
            songPage.Controls.Add(songTab.Build());
            // Song inspection belongs to the M8 view; the other views start without it (see ApplyGearMode).
            workflowTabs.TabPages.Remove(songPage);
            deviceToolParking.Controls.Add(samplesTab.Build());
            deviceToolParking.Controls.Add(migrationTab.Build());
            Controls.Add(deviceToolParking);
            // The tab control shows the new (still empty) page before our handler can move the inputs onto it, so the
            // window stops painting as soon as a switch is requested and paints once when the new page is ready.
            workflowTabs.Selecting += delegate {
                if (tabSwitchFreeze != null) return;
                tabSwitchFreeze = new RedrawFreeze(this);
                BeginInvoke(new Action(ReleaseTabSwitch)); // never leave the window frozen if no change follows
            };
            workflowTabs.SelectedIndexChanged += delegate {
                try
                {
                    if (synchronizingNavigation) return;
                    if ((workflowTabs.SelectedIndex == 1 || workflowTabs.SelectedIndex == 2) && !InDeviceMode) ShowOperationTab();
                    RefreshWorkflowLayout();
                }
                finally { ReleaseTabSwitch(); }
            };
            workflowHost = GuiTheme.Tabbed(workflowTabs);
            workflowHost.Dock = DockStyle.Top; workflowHost.Margin = Padding.Empty;
            Resize += delegate { RefreshWorkflowLayout(); };
            return workflowHost;
        }

        // The backup and restore operations live on the Backup and Restore tabs; this puts the shared controls on the
        // selected one and selects the operation that goes with it.
        void ShowOperationTab()
        {
            workflowTabs.SelectedTab.Controls.Add(inputs);
            SelectOperation(workflowTabs.SelectedIndex == 2 ? RestoreOperationForIndex(restoreMode.SelectedIndex)
                : BackupOperationForIndex(backupMode.SelectedIndex), false);
            backupMode.Visible = workflowTabs.SelectedIndex == 1;
            restoreMode.Visible = workflowTabs.SelectedIndex == 2;
        }

        // Gear Backup mode is the whole app: Device, Backup and Restore. A device mode (M8, Tracker Mini, OP-XY,
        // OP-1 Field) keeps Device and shows its own tools (or placeholders).
        bool InDeviceMode { get { return gearIcons != null && gearIcons.Active.Device != null; } }
        const string FirstDeviceTab = "Coming soon", SecondDeviceTab = "Planned";

        void ApplyGearMode()
        {
            if (workflowTabs.TabPages.Count < 3 || !deviceCard.Built) return;
            bool device = InDeviceMode;
            using (new RedrawFreeze(this))
            {
                bool previous = synchronizingNavigation; synchronizingNavigation = true;
                try
                {
                    deviceCard.SetShortcutsVisible(!device);
                    SetSongTabVisible(device && gearIcons.Active.Device == Devices.M8);
                    TabPage first = workflowTabs.TabPages[1], second = workflowTabs.TabPages[2];
                    Control sampleContent = samplesTab.BuildContent;
                    deviceToolParking.Controls.Add(sampleContent);
                    deviceToolParking.Controls.Add(migrationTab.Content);
                    for (int i = 0; i < 2; i++)
                    {
                        if (devicePlaceholders[i] == null)
                            devicePlaceholders[i] = new Label { Name = "GearPlaceholder" + (i + 1), AutoSize = true, Dock = DockStyle.Top,
                                ForeColor = GuiTheme.Muted, Font = GuiTheme.Font(10), Padding = new Padding(4, 12, 0, 0), MaximumSize = new Size(700, 0) };
                        TabPage page = i == 0 ? first : second;
                        if (devicePlaceholders[i].Parent != null) devicePlaceholders[i].Parent.Controls.Remove(devicePlaceholders[i]);
                        if (device) page.Controls.Add(devicePlaceholders[i]);
                    }
                    if (device)
                    {
                        if (inputs.Parent != null) inputs.Parent.Controls.Remove(inputs);
                        first.Text = FirstDeviceTab; second.Text = SecondDeviceTab;
                        string name = gearIcons.Active.Name;
                        devicePlaceholders[0].Text = "Tools for the " + name + " will be placed here.";
                        devicePlaceholders[1].Text = "More " + name + " features are planned for this tab.";
                        if (gearIcons.Active.Device == Devices.M8)
                        {
                            first.Controls.Remove(devicePlaceholders[0]);
                            first.Text = "Sample check";
                            first.Controls.Add(sampleContent);
                            samplesTab.Activate();
                            second.Controls.Remove(devicePlaceholders[1]);
                            second.Text = "Card migration";
                            second.Controls.Add(migrationTab.Content);
                            migrationTab.Activate();
                        }
                    }
                    else
                    {
                        first.Text = "Backup"; second.Text = "Restore";
                        (workflowTabs.SelectedIndex == 2 ? second : first).Controls.Add(inputs);
                    }
                }
                finally { synchronizingNavigation = previous; }
                if (!device && (workflowTabs.SelectedIndex == 1 || workflowTabs.SelectedIndex == 2)) ShowOperationTab();
                RefreshWorkflowLayout();
            }
        }

        TabPage songPage;

        // The Song tab (reading an M8 song) is shown only in the M8 view.
        void SetSongTabVisible(bool visible)
        {
            if (songPage == null || workflowTabs.TabPages.Contains(songPage) == visible) return;
            if (visible) workflowTabs.TabPages.Add(songPage);
            else
            {
                if (workflowTabs.SelectedTab == songPage) workflowTabs.SelectedIndex = 0;
                workflowTabs.TabPages.Remove(songPage);
            }
        }

        Control BuildOperationSelectors()
        {
            GuiTheme.StyleComboBox(backupMode); GuiTheme.StyleComboBox(restoreMode);
            backupMode.AccessibleName = "Backup type"; restoreMode.AccessibleName = "Restore or check";
            backupMode.Items.AddRange(new object[] { "Full backup", "Incremental backup", "Zip archive" });
            restoreMode.Items.AddRange(new object[] { "Restore full backup", "Restore individual files", "Restore ZIP archive", "Check backups" });
            backupMode.SelectedIndex = restoreMode.SelectedIndex = 0;
            backupMode.SelectedIndexChanged += delegate {
                if (!synchronizingNavigation) SelectOperation(BackupOperationForIndex(backupMode.SelectedIndex));
            };
            restoreMode.SelectedIndexChanged += delegate {
                if (!synchronizingNavigation)
                {
                    if (restoreMode.SelectedIndex < 2) rememberedRestoreScope = restoreMode.SelectedIndex;
                    SelectOperation(RestoreOperationForIndex(restoreMode.SelectedIndex));
                    UpdateRestoreSelectionNotice();
                }
            };
            var selector = new Panel { Dock = DockStyle.Fill, Height = 36, MinimumSize = new Size(230, 36), Margin = new Padding(0, 5, 16, 0) };
            selector.Controls.Add(backupMode); selector.Controls.Add(restoreMode); restoreMode.Visible = false;
            var field = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 16, 0) };
            field.Controls.Add(FieldLabel("Type"));
            selector.Margin = Padding.Empty;
            field.Controls.Add(selector);
            return field;
        }

        void InitializeWorkflow()
        {
            synchronizingNavigation = true;
            try { backupMode.SelectedIndex = Selected == GuiOperation.IncrementalBackup ? 1 : 0; workflowTabs.SelectedIndex = 0; }
            finally { synchronizingNavigation = false; }
        }

        void SynchronizeOperationTab()
        {
            if (synchronizingNavigation || InDeviceMode) return;
            synchronizingNavigation = true;
            try
            {
                bool restore = Restoring || Selected == GuiOperation.Check;
                backupMode.Visible = !restore; restoreMode.Visible = restore;
                if (restore) restoreMode.SelectedIndex = Selected == GuiOperation.RestoreZip ? 2 : Selected == GuiOperation.Check ? 3 : rememberedRestoreScope;
                else backupMode.SelectedIndex = Selected == GuiOperation.CameraSettings ? 3 : Selected == GuiOperation.ZipArchive ? 2 : (int)Selected;
                workflowTabs.SelectedIndex = restore ? 2 : 1;
                workflowTabs.SelectedTab.Controls.Add(inputs);
            }
            finally { synchronizingNavigation = false; }
            RefreshWorkflowLayout();
        }

        static GuiOperation BackupOperationForIndex(int index)
        {
            return index == 3 ? GuiOperation.CameraSettings : index == 2 ? GuiOperation.ZipArchive : index == 1 ? GuiOperation.IncrementalBackup : GuiOperation.FullBackup;
        }
        static GuiOperation RestoreOperationForIndex(int index)
        { return index == 2 ? GuiOperation.RestoreZip : index == 3 ? GuiOperation.Check : GuiOperation.Restore; }

        internal void SelectOperation(GuiOperation value, bool navigate = true)
        {
            bool chooseArchive = value == GuiOperation.RestoreZip && Selected != value;
            if (value == GuiOperation.CameraSettings && !cameraSettingsAvailable) value = GuiOperation.FullBackup;
            using (new RedrawFreeze(this))
            {
                selectedOperation = value;
                if (navigate) SynchronizeOperationTab();
                RefreshOperationControls();
            }
            if (chooseArchive && IsHandleCreated) BeginInvoke((Action)delegate { if (!IsDisposed && !Disposing && Selected == GuiOperation.RestoreZip && !runner.IsBusy) zipRestore.Choose(); });
        }

        void UpdateCameraSettingsAvailability(bool available)
        {
            cameraSettingsAvailable = available;
            bool previous = synchronizingNavigation; synchronizingNavigation = true;
            try
            {
                if (available && backupMode.Items.Count == 3) backupMode.Items.Add("Back up X100VI settings");
                else if (!available && backupMode.Items.Count == 4)
                {
                    if (backupMode.SelectedIndex == 3) backupMode.SelectedIndex = 0;
                    backupMode.Items.RemoveAt(3);
                    if (Selected == GuiOperation.CameraSettings) selectedOperation = GuiOperation.FullBackup;
                }
            }
            finally { synchronizingNavigation = previous; }
            if (config != null) RefreshOperationControls();
        }

        void RefreshSourceField(bool restoring)
        {
            if (sourceField == null) return;
            if (restoreField == null) restoreField = new FieldPanel(restoreDestination) { Dock = DockStyle.Fill };
            FieldPanel wanted = restoring ? restoreField : sourceField, other = restoring ? sourceField : restoreField;
            if (other.Parent == optionalFields) optionalFields.Controls.Remove(other);
            if (wanted.Parent != optionalFields) optionalFields.Controls.Add(wanted, 0, 1);
            // Both input panels remain owned by the form, so their values, scaling and disposal are preserved.
            if (other.Parent == null) { other.Visible = false; Controls.Add(other); }
            wanted.Visible = Selected != GuiOperation.Check;
        }

        void RefreshWorkflowLayout()
        {
            if (workflowHost == null || sizingWorkflow) return;
            sizingWorkflow = true;
            try
            {
                bool samplePage = InDeviceMode && workflowTabs.SelectedIndex == 1 && gearIcons.Active.Device == Devices.M8;
                bool migrationPage = InDeviceMode && workflowTabs.SelectedIndex == 2 && gearIcons.Active.Device == Devices.M8;
                // The read-only M8 tool owns its status and results; give it the space normally used by backup activity.
                if (operationState != null) operationState.Visible = !(samplePage || migrationPage) || runner.IsBusy;
                if (activityArea != null) activityArea.Visible = !(samplePage || migrationPage) || runner.IsBusy;
                int desired = workflowTabs.SelectedIndex == 0 ? Math.Max(Font.Height * 17, deviceCard.ContentHeight + Font.Height * 5)
                    : workflowTabs.SelectedIndex == SongTabIndex ? Math.Max(Font.Height * 13, songTab.ContentHeight + Font.Height * 5)
                    : InDeviceMode && workflowTabs.SelectedIndex == 1 && gearIcons.Active.Device == Devices.M8 ? samplesTab.ContentHeight + Font.Height * 5
                    : InDeviceMode ? Font.Height * 13 : Math.Max(Font.Height * 13, inputs.Height + Font.Height * 5);
                int reserved = workflowTabs.SelectedIndex == 2 && Selected == GuiOperation.Restore ? 20 : 23;
                if ((samplePage || migrationPage) && !runner.IsBusy)
                {
                    var layout = workflowHost.Parent as TableLayoutPanel;
                    Control header = layout == null ? null : layout.GetControlFromPosition(0, 0);
                    int available = ClientSize.Height - (header == null ? Font.Height * 7 : header.Height + header.Margin.Vertical) -
                        (layout == null ? 60 : layout.Padding.Vertical) - 8;
                    workflowHost.Height = Math.Max(Font.Height * 14, available);
                    return;
                }
                workflowHost.Height = Math.Min(desired, Math.Max(Font.Height * 14, ClientSize.Height - Font.Height * reserved));
            }
            finally { sizingWorkflow = false; }
        }

        internal void ShowDeviceInformation(string text) { deviceCard.ShowInformation(text); }
    }

}
