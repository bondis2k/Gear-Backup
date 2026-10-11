using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

internal sealed class RestoreBrowserDialog : ToolsDialog
{
    readonly TreeView tree = new TreeView { Name = "RestoreFileTree", CheckBoxes = true, HideSelection = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    readonly Dictionary<string, TreeNode> nodes = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
    bool changingChecks;
    internal string[] SelectedPaths { get; private set; }

    internal RestoreBrowserDialog(MainForm owner, Snapshot snapshot, string[] selected = null)
        : base(owner, "Choose files to restore", "Check folders to include everything below them, or check individual files. Nothing is restored until you review the restore preview.")
    {
        tree.BackColor = GuiTheme.Surface; tree.ForeColor = GuiTheme.Ink;
        SetContent(tree);
        tree.BeginUpdate();
        foreach (string folder in snapshot.Folders) Add(folder, true);
        foreach (RestoreEntry file in snapshot.Files) Add(file.Relative, false);
        tree.Sort();
        foreach (TreeNode node in tree.Nodes) node.Expand();
        tree.EndUpdate();
        tree.AfterCheck += delegate(object sender, TreeViewEventArgs e) {
            if (changingChecks) return;
            changingChecks = true;
            try
            {
                CheckChildren(e.Node, e.Node.Checked);
                for (TreeNode parent = e.Node.Parent; parent != null; parent = parent.Parent)
                {
                    bool all = true; foreach (TreeNode child in parent.Nodes) if (!child.Checked) { all = false; break; }
                    parent.Checked = all;
                }
            }
            finally { changingChecks = false; }
            UpdateSelection();
        };
        changingChecks = true;
        if (selected != null) foreach (string path in selected) { TreeNode node; if (nodes.TryGetValue(path, out node)) { node.Checked = true; CheckChildren(node, true); } }
        foreach (TreeNode node in tree.Nodes) Aggregate(node);
        changingChecks = false;
        ToolButton("Select all", "SelectAllRestoreFiles", delegate { CheckAll(true); });
        ToolButton("Clear selection", "ClearRestoreFiles", delegate { CheckAll(false); });
        var use = ToolButton("Use selection", "UseRestoreSelection", delegate {
            UpdateSelection();
            if (SelectedPaths.Length == 0) { Notice.Text = "Check at least one file or folder."; return; }
            DialogResult = DialogResult.OK; Close();
        }, true);
        AcceptButton = use;
        UpdateSelection();
    }
    TreeNode Add(string path, bool folder)
    {
        TreeNode existing;
        if (nodes.TryGetValue(path, out existing)) return existing;
        string parent = Path.GetDirectoryName(path);
        var node = new TreeNode(Path.GetFileName(path) + (folder ? "\\" : "")) { Tag = path };
        nodes.Add(path, node);
        if (string.IsNullOrEmpty(parent)) tree.Nodes.Add(node); else Add(parent, true).Nodes.Add(node);
        return node;
    }
    static void CheckChildren(TreeNode node, bool check)
    {
        foreach (TreeNode child in node.Nodes) { child.Checked = check; CheckChildren(child, check); }
    }
    void CheckAll(bool check)
    {
        changingChecks = true;
        try { foreach (TreeNode node in tree.Nodes) { node.Checked = check; CheckChildren(node, check); } }
        finally { changingChecks = false; }
        UpdateSelection();
    }
    static void Aggregate(TreeNode node)
    {
        if (node.Nodes.Count == 0) return;
        bool all = true;
        foreach (TreeNode child in node.Nodes) { Aggregate(child); all &= child.Checked; }
        node.Checked = all;
    }
    void UpdateSelection()
    {
        var selected = new List<string>();
        foreach (TreeNode node in tree.Nodes) Collect(node, selected);
        SelectedPaths = selected.ToArray();
        Notice.Text = selected.Count + " selected file(s) or folder(s). A checked folder includes its subfolders and files.";
    }
    static void Collect(TreeNode node, List<string> selected)
    {
        if (node.Checked) selected.Add((string)node.Tag);
        else foreach (TreeNode child in node.Nodes) Collect(child, selected);
    }
}
