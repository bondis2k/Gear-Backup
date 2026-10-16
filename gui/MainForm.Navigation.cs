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
        readonly ComboBox backupMode = new ComboBox { Name = "BackupOperation", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill },
            restoreMode = new ComboBox { Name = "RestoreOperation", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly TextBox restoreDestination = new TextBox { Name = "RestoreDestination", AccessibleName = "Restore destination" };
        readonly Label deviceDetails = GuiTheme.Label("", 10, GuiTheme.Ink);
        FieldPanel sourceField, restoreField;
        Control workflowHost, deviceOverview;
        Button goToBackup, goToRestore;
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
            deviceDetails.Name = "DeviceStorageDetails";
            deviceDetails.MaximumSize = new Size(650, 0);
            deviceDetails.Margin = new Padding(0, 12, 0, 10);
            var deviceCard = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            deviceOverview = deviceCard;
            deviceCard.SizeChanged += delegate { RefreshWorkflowLayout(); };
            inputs.SizeChanged += delegate { RefreshWorkflowLayout(); };
            deviceCard.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            deviceCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            deviceCard.Controls.Add(deviceImage, 0, 0);
            var information = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            information.Controls.Add(deviceName); information.Controls.Add(deviceConnection); information.Controls.Add(deviceDetails);
            var shortcuts = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            var toBackup = goToBackup = GuiTheme.Button("Go to Backup", false); toBackup.Name = "GoToBackup";
            var toRestore = goToRestore = GuiTheme.Button("Go to Restore", false); toRestore.Name = "GoToRestore";
            toBackup.Click += delegate { workflowTabs.SelectedIndex = 1; };
            toRestore.Click += delegate { workflowTabs.SelectedIndex = 2; };
            detect.Name = "DetectDevice";
            shortcuts.Controls.Add(detect); shortcuts.Controls.Add(toBackup); shortcuts.Controls.Add(toRestore);
            information.Controls.Add(shortcuts);
            deviceCard.Controls.Add(information, 1, 0);
            workflowTabs.TabPages[0].Controls.Add(deviceCard);
            workflowTabs.TabPages[1].Controls.Add(inputs);
            workflowTabs.TabPages[SongTabIndex].Controls.Add(songTab.Build());
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
        // OP-1 Field) keeps Device but swaps Backup and Restore for placeholders until its own tools exist.
        bool InDeviceMode { get { return gearIcons != null && gearIcons.Active.Device != null; } }
        const string FirstDeviceTab = "Coming soon", SecondDeviceTab = "Planned";

        void ApplyGearMode()
        {
            if (workflowTabs.TabPages.Count < 3 || goToBackup == null) return;
            bool device = InDeviceMode;
            using (new RedrawFreeze(this))
            {
                bool previous = synchronizingNavigation; synchronizingNavigation = true;
                try
                {
                    goToBackup.Visible = goToRestore.Visible = !device;
                    TabPage first = workflowTabs.TabPages[1], second = workflowTabs.TabPages[2];
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
            if (chooseArchive && IsHandleCreated) BeginInvoke((Action)delegate { if (!IsDisposed && !Disposing && Selected == GuiOperation.RestoreZip && !runner.IsBusy) ChooseRestoreArchive(); });
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
                int desired = workflowTabs.SelectedIndex == 0 ? Math.Max(Font.Height * 17, deviceOverview.Height + Font.Height * 5)
                    : workflowTabs.SelectedIndex == SongTabIndex ? Math.Max(Font.Height * 13, songTab.ContentHeight + Font.Height * 5)
                    : InDeviceMode ? Font.Height * 13 : Math.Max(Font.Height * 13, inputs.Height + Font.Height * 5);
                int reserved = workflowTabs.SelectedIndex == 2 && Selected == GuiOperation.Restore ? 20 : 23;
                workflowHost.Height = Math.Min(desired, Math.Max(Font.Height * 14, ClientSize.Height - Font.Height * reserved));
            }
            finally { sizingWorkflow = false; }
        }

        internal void ShowDeviceInformation(string text) { deviceDetails.Text = text; RefreshWorkflowLayout(); }
    }

}
