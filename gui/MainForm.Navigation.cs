using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

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
        foreach (string title in new[] { "Device", "Backup", "Restore" })
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
        var toBackup = GuiTheme.Button("Go to Backup", false); toBackup.Name = "GoToBackup";
        var toRestore = GuiTheme.Button("Go to Restore", false); toRestore.Name = "GoToRestore";
        toBackup.Click += delegate { workflowTabs.SelectedIndex = 1; };
        toRestore.Click += delegate { workflowTabs.SelectedIndex = 2; };
        detect.Name = "DetectDevice";
        shortcuts.Controls.Add(detect); shortcuts.Controls.Add(toBackup); shortcuts.Controls.Add(toRestore);
        information.Controls.Add(shortcuts);
        deviceCard.Controls.Add(information, 1, 0);
        workflowTabs.TabPages[0].Controls.Add(deviceCard);
        workflowTabs.TabPages[1].Controls.Add(inputs);
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
                if (workflowTabs.SelectedIndex != 0)
                {
                    workflowTabs.SelectedTab.Controls.Add(inputs);
                    SelectOperation(workflowTabs.SelectedIndex == 2 ? (restoreMode.SelectedIndex == 2 ? GuiOperation.Check : GuiOperation.Restore)
                        : (backupMode.SelectedIndex == 2 ? GuiOperation.CameraSettings : backupMode.SelectedIndex == 1 ? GuiOperation.IncrementalBackup : GuiOperation.FullBackup), false);
                    backupMode.Visible = workflowTabs.SelectedIndex == 1;
                    restoreMode.Visible = workflowTabs.SelectedIndex == 2;
                }
                RefreshWorkflowLayout();
            }
            finally { ReleaseTabSwitch(); }
        };
        workflowHost = GuiTheme.Tabbed(workflowTabs);
        workflowHost.Dock = DockStyle.Top; workflowHost.Margin = Padding.Empty;
        Resize += delegate { RefreshWorkflowLayout(); };
        return workflowHost;
    }

    Control BuildOperationSelectors()
    {
        GuiTheme.StyleComboBox(backupMode); GuiTheme.StyleComboBox(restoreMode);
        backupMode.AccessibleName = "Backup type"; restoreMode.AccessibleName = "Restore or check";
        backupMode.Items.AddRange(new object[] { "Full backup", "Incremental backup" });
        restoreMode.Items.AddRange(new object[] { "Restore full backup", "Restore individual files", "Check backups" });
        backupMode.SelectedIndex = restoreMode.SelectedIndex = 0;
        backupMode.SelectedIndexChanged += delegate {
            if (!synchronizingNavigation) SelectOperation(backupMode.SelectedIndex == 2 ? GuiOperation.CameraSettings : backupMode.SelectedIndex == 1 ? GuiOperation.IncrementalBackup : GuiOperation.FullBackup);
        };
        restoreMode.SelectedIndexChanged += delegate {
            if (!synchronizingNavigation)
            {
                if (restoreMode.SelectedIndex < 2) rememberedRestoreScope = restoreMode.SelectedIndex;
                SelectOperation(restoreMode.SelectedIndex == 2 ? GuiOperation.Check : GuiOperation.Restore);
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
        if (synchronizingNavigation) return;
        synchronizingNavigation = true;
        try
        {
            bool restore = Selected == GuiOperation.Restore || Selected == GuiOperation.Check;
            backupMode.Visible = !restore; restoreMode.Visible = restore;
            if (restore) restoreMode.SelectedIndex = Selected == GuiOperation.Check ? 2 : rememberedRestoreScope;
            else backupMode.SelectedIndex = Selected == GuiOperation.CameraSettings ? 2 : (int)Selected;
            workflowTabs.SelectedIndex = restore ? 2 : 1;
            workflowTabs.SelectedTab.Controls.Add(inputs);
        }
        finally { synchronizingNavigation = false; }
        RefreshWorkflowLayout();
    }

    internal void SelectOperation(GuiOperation value, bool navigate = true)
    {
        if (value == GuiOperation.CameraSettings && !cameraSettingsAvailable) value = GuiOperation.FullBackup;
        using (new RedrawFreeze(this))
        {
            selectedOperation = value;
            if (navigate) SynchronizeOperationTab();
            RefreshOperationControls();
        }
    }

    void UpdateCameraSettingsAvailability(bool available)
    {
        cameraSettingsAvailable = available;
        bool previous = synchronizingNavigation; synchronizingNavigation = true;
        try
        {
            if (available && backupMode.Items.Count == 2) backupMode.Items.Add("Back up X100VI settings");
            else if (!available && backupMode.Items.Count == 3)
            {
                if (backupMode.SelectedIndex == 2) backupMode.SelectedIndex = 0;
                backupMode.Items.RemoveAt(2);
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
                : Math.Max(Font.Height * 13, inputs.Height + Font.Height * 5);
            int reserved = workflowTabs.SelectedIndex == 2 && Selected == GuiOperation.Restore ? 20 : 23;
            workflowHost.Height = Math.Min(desired, Math.Max(Font.Height * 14, ClientSize.Height - Font.Height * reserved));
        }
        finally { sizingWorkflow = false; }
    }

    internal void ShowDeviceInformation(string text) { deviceDetails.Text = text; RefreshWorkflowLayout(); }
}
