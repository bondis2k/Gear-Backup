using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

internal sealed class RestoreBrowserDialog : ToolsDialog
{
    readonly TreeView tree = new BrowserTree { Name = "RestoreFileTree", CheckBoxes = true, AccessibleName = "Backup files and folders", Dock = DockStyle.Fill };
    // Selection belongs to the inventory, including branches that have never been opened.
    sealed class Entry
    {
        internal string Path;
        internal bool Folder, Checked, Loaded;
        internal Entry Parent;
        internal TreeNode Node;
        internal readonly List<Entry> Children = new List<Entry>();
    }
    readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    readonly List<Entry> roots = new List<Entry>();
    bool changingChecks;
    Button useSelection;
    internal string[] SelectedPaths { get; private set; }

    internal RestoreBrowserDialog(MainForm owner, Snapshot snapshot, string[] selected = null)
        : base(owner, "Choose files to restore", "Check folders or individual files. You can review the restore plan before copying.", true)
    {
        // A file tree is narrow and tall; the folders start collapsed so the whole backup is visible at a glance.
        ClientSize = new Size(720, 740); MinimumSize = new Size(600, 440);
        tree.BackColor = GuiTheme.Surface; tree.ForeColor = GuiTheme.Ink; tree.Font = GuiTheme.Font(10);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var inventory = GuiTheme.Label(string.Format("Backup contents · {0:N0} files · {1:N0} folders", snapshot.Files.Count, snapshot.Folders.Count), 9, GuiTheme.Muted);
        inventory.Name = "RestoreBrowserInventory"; content.Controls.Add(inventory, 0, 0);
        var toolbar = new FlowLayoutPanel { Name = "RestoreBrowserToolbar", Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 10) };
        content.Controls.Add(toolbar, 0, 1);
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(10), Margin = Padding.Empty };
        card.Controls.Add(tree); content.Controls.Add(card, 0, 2); ToolLayout.Controls.Add(content, 0, 1);
        // Keep a lightweight inventory; create native rows only when a folder is opened.
        foreach (string folder in snapshot.Folders) Add(folder, true);
        foreach (RestoreEntry file in snapshot.Files) Add(file.Relative, false);
        Comparison<Entry> order = (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Path, b.Path);
        roots.Sort(order);
        foreach (Entry entry in entries.Values) entry.Children.Sort(order);
        tree.BeforeExpand += delegate(object sender, TreeViewCancelEventArgs e) {
            Entry entry;
            if (e.Node.Tag is string && entries.TryGetValue((string)e.Node.Tag, out entry)) Populate(entry);
        };
        tree.AfterCheck += delegate(object sender, TreeViewEventArgs e) {
            if (changingChecks) return;
            changingChecks = true;
            try
            {
                Entry entry;
                if (!(e.Node.Tag is string) || !entries.TryGetValue((string)e.Node.Tag, out entry)) return;
                CheckSubtree(entry, e.Node.Checked);
                for (Entry parent = entry.Parent; parent != null; parent = parent.Parent)
                {
                    bool all = true; foreach (Entry child in parent.Children) if (!child.Checked) { all = false; break; }
                    SetChecked(parent, all);
                }
            }
            finally { changingChecks = false; }
            UpdateSelection();
        };
        changingChecks = true;
        if (selected != null) foreach (string path in selected) { Entry entry; if (entries.TryGetValue(path, out entry)) CheckSubtree(entry, true); }
        foreach (Entry entry in roots) Aggregate(entry);
        changingChecks = false;
        tree.BeginUpdate();
        try { foreach (Entry entry in roots) tree.Nodes.Add(CreateNode(entry)); }
        finally { tree.EndUpdate(); }
        toolbar.Controls.Add(ToolButton("Select all", "SelectAllRestoreFiles", delegate { CheckAll(true); }));
        toolbar.Controls.Add(ToolButton("Clear selection", "ClearRestoreFiles", delegate { CheckAll(false); }));
        toolbar.Controls.Add(ToolButton("Expand all", "ExpandRestoreFolders", delegate {
            tree.BeginUpdate();
            try { foreach (Entry entry in roots) Expand(entry); }
            finally { tree.EndUpdate(); }
        }));
        toolbar.Controls.Add(ToolButton("Collapse all", "CollapseRestoreFolders", delegate { tree.CollapseAll(); }));
        foreach (Control button in toolbar.Controls)
        {
            button.MinimumSize = new Size(72, 32); button.Padding = new Padding(8, 3, 8, 3);
            button.Margin = new Padding(0, 0, 8, 6);
        }
        useSelection = ToolButton("Use selection", "UseRestoreSelection", delegate {
            UpdateSelection();
            if (SelectedPaths.Length == 0) { Notice.Text = "Check at least one file or folder."; return; }
            DialogResult = DialogResult.OK; Close();
        }, true);
        AcceptButton = useSelection;
        UpdateSelection();
    }
    Entry Add(string path, bool folder)
    {
        Entry existing;
        if (entries.TryGetValue(path, out existing)) return existing;
        string parent = Path.GetDirectoryName(path);
        var entry = new Entry { Path = path, Folder = folder };
        entries.Add(path, entry);
        if (string.IsNullOrEmpty(parent)) roots.Add(entry);
        else { entry.Parent = Add(parent, true); entry.Parent.Children.Add(entry); }
        return entry;
    }
    TreeNode CreateNode(Entry entry)
    {
        int icon = entry.Folder ? 0 : 1;
        entry.Node = new TreeNode(Path.GetFileName(entry.Path) + (entry.Folder ? "\\" : "")) {
            Tag = entry.Path, ImageIndex = icon, SelectedImageIndex = icon, Checked = entry.Checked
        };
        // A single placeholder gives unopened folders their native expander.
        if (entry.Children.Count != 0) entry.Node.Nodes.Add(new TreeNode());
        return entry.Node;
    }
    void Populate(Entry entry)
    {
        if (entry.Loaded) return;
        bool previous = changingChecks;
        changingChecks = true;
        tree.BeginUpdate();
        try
        {
            entry.Node.Nodes.Clear();
            var children = new TreeNode[entry.Children.Count];
            for (int i = 0; i < children.Length; i++) children[i] = CreateNode(entry.Children[i]);
            entry.Node.Nodes.AddRange(children);
            entry.Loaded = true;
        }
        finally { changingChecks = previous; tree.EndUpdate(); }
    }
    void Expand(Entry entry)
    {
        if (entry.Children.Count == 0) return;
        Populate(entry); entry.Node.Expand();
        foreach (Entry child in entry.Children) Expand(child);
    }
    static void SetChecked(Entry entry, bool check)
    {
        entry.Checked = check;
        if (entry.Node != null && entry.Node.Checked != check) entry.Node.Checked = check;
    }
    static void CheckSubtree(Entry entry, bool check)
    {
        SetChecked(entry, check);
        foreach (Entry child in entry.Children) CheckSubtree(child, check);
    }
    void CheckAll(bool check)
    {
        changingChecks = true;
        try { foreach (Entry entry in roots) CheckSubtree(entry, check); }
        finally { changingChecks = false; }
        UpdateSelection();
    }
    static void Aggregate(Entry entry)
    {
        if (entry.Children.Count == 0) return;
        bool all = true;
        foreach (Entry child in entry.Children) { Aggregate(child); all &= child.Checked; }
        SetChecked(entry, all);
    }
    void UpdateSelection()
    {
        var selected = new List<string>();
        foreach (Entry entry in roots) Collect(entry, selected);
        SelectedPaths = selected.ToArray();
        int files = 0, folders = 0;
        foreach (Entry entry in entries.Values)
            if (entry.Checked) { if (entry.Folder) folders++; else files++; }
        Notice.Text = selected.Count == 0 ? "Nothing selected. Check the files or folders you want to restore."
            : string.Format("Selected · {0:N0} files · {1:N0} folders. Checked folders include their contents.", files, folders);
        if (useSelection != null) useSelection.Enabled = selected.Count != 0;
    }
    static void Collect(Entry entry, List<string> selected)
    {
        if (entry.Checked) selected.Add(entry.Path);
        else foreach (Entry child in entry.Children) Collect(child, selected);
    }
}
