using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

// Choose a folder or drive. Windows' own folder picker ignores the app's colour theme, so this one is
// drawn with the app's controls: a tree of the folders and drives, an editable path, and (when the
// folder may be new) a button that makes a folder inside the selected one.
internal sealed class FolderPickerDialog : DialogForm
{
    readonly TreeView tree = new TreeView { Name = "FolderTree", HideSelection = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, ShowRootLines = true };
    readonly TextBox path = new TextBox { Name = "FolderPath", AccessibleName = "Folder" };
    readonly Label message = GuiTheme.Label("", 9, GuiTheme.Muted);
    bool syncing;
    const string Placeholder = "…";

    internal string SelectedPath { get; private set; }

    internal FolderPickerDialog(string description, string initial, bool allowNew) : base(description)
    {
        ClientSize = new Size(620, 560); MinimumSize = new Size(480, 420);
        Accept.Text = "Select folder";
        message.Name = "FolderPickerMessage"; message.Margin = new Padding(0, 6, 0, 0);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(new FieldPanel(path) { Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 10) }, 0, 0);
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(10), Margin = Padding.Empty };
        tree.BackColor = GuiTheme.Surface; tree.ForeColor = GuiTheme.Ink; tree.Font = GuiTheme.Font(10);
        card.Controls.Add(tree);
        content.Controls.Add(card, 0, 1);
        content.Controls.Add(message, 0, 2);
        layout.Controls.Add(content, 0, 1);

        AddRoots();
        tree.BeforeExpand += delegate(object sender, TreeViewCancelEventArgs e) { LoadChildren(e.Node); };
        tree.AfterSelect += delegate(object sender, TreeViewEventArgs e) {
            if (syncing || e.Node.Tag == null) return;
            syncing = true; path.Text = (string)e.Node.Tag; syncing = false;
            message.Text = "";
        };
        path.KeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.KeyCode != Keys.Enter) return;
            string wanted = path.Text.Trim();
            var selected = tree.SelectedNode;
            if (selected != null && selected.Tag != null && PathsEqual(wanted, (string)selected.Tag)) return; // already there: Enter accepts
            e.SuppressKeyPress = true;
            if (!GoTo(wanted)) message.Text = "That folder was not found.";
        };
        if (allowNew)
        {
            var make = GuiTheme.Button("New folder…", false);
            make.Name = "NewFolder";
            make.Click += delegate { MakeFolder(); };
            buttons.Controls.Add(make);
        }
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (DialogResult != DialogResult.OK) return;
            string chosen = path.Text.Trim();
            if (chosen.Length == 0 || !Directory.Exists(chosen)) { e.Cancel = true; message.Text = "Choose a folder that exists."; return; }
            SelectedPath = Path.GetFullPath(chosen);
        };
        Shown += delegate { if (!string.IsNullOrEmpty(initial)) GoTo(initial); if (tree.SelectedNode == null && tree.Nodes.Count > 0) tree.SelectedNode = tree.Nodes[0]; tree.Focus(); };
        // The tree needs a handle before it can be navigated; headless windows never show.
        if (!string.IsNullOrEmpty(initial)) GoTo(initial);
    }

    static bool PathsEqual(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    void AddRoots()
    {
        foreach (Environment.SpecialFolder known in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments })
        {
            string folder = Environment.GetFolderPath(known);
            if (folder.Length != 0 && Directory.Exists(folder))
                tree.Nodes.Add(NewNode(known == Environment.SpecialFolder.DesktopDirectory ? "Desktop" : "Documents", folder));
        }
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string label = "";
            try { if (drive.IsReady) label = drive.VolumeLabel; else continue; } catch (IOException) { continue; }
            string name = drive.Name.TrimEnd('\\');
            tree.Nodes.Add(NewNode((label.Length == 0 ? "Local Disk" : label) + " (" + name + ")", drive.Name));
        }
    }

    static TreeNode NewNode(string text, string folder)
    {
        var node = new TreeNode(text) { Tag = folder };
        node.Nodes.Add(new TreeNode(Placeholder)); // replaced with the real subfolders when it is first opened
        return node;
    }

    static void LoadChildren(TreeNode node)
    {
        if (node.Nodes.Count != 1 || node.Nodes[0].Text != Placeholder || node.Nodes[0].Tag != null) return;
        node.Nodes.Clear();
        var found = new List<string>();
        try
        {
            foreach (DirectoryInfo directory in new DirectoryInfo((string)node.Tag).EnumerateDirectories())
            {
                // Hidden, system and linked folders are left out: they are not places to keep backups and can loop.
                if ((directory.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                found.Add(directory.Name);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        found.Sort(StringComparer.CurrentCultureIgnoreCase);
        foreach (string name in found) node.Nodes.Add(NewNode(name, Path.Combine((string)node.Tag, name)));
    }

    // Opens the tree down to `folder` and selects it; false when it does not exist.
    bool GoTo(string folder)
    {
        string full;
        try { full = Path.GetFullPath(folder.Trim()); } catch (Exception) { return false; }
        if (!Directory.Exists(full)) return false;
        string root = Path.GetPathRoot(full);
        TreeNode node = null;
        foreach (TreeNode drive in tree.Nodes) if (PathsEqual((string)drive.Tag, root)) { node = drive; break; }
        if (node == null) return false;
        string rest = full.Substring(root.Length).Trim('\\');
        if (rest.Length != 0)
            foreach (string part in rest.Split('\\'))
            {
                LoadChildren(node);
                TreeNode next = null;
                foreach (TreeNode child in node.Nodes) if (string.Equals(child.Text, part, StringComparison.OrdinalIgnoreCase)) { next = child; break; }
                if (next == null) break; // a folder the tree leaves out (hidden, say): stop at its parent
                node.Expand(); node = next;
            }
        tree.SelectedNode = node;
        node.EnsureVisible();
        syncing = true; path.Text = full; syncing = false;
        message.Text = "";
        return true;
    }

    void MakeFolder()
    {
        string parent = path.Text.Trim();
        if (!Directory.Exists(parent)) { message.Text = "Choose the folder to create it in first."; return; }
        using (var ask = new SearchDialog("Name for the new folder", ""))
        {
            if (ask.ShowDialog(this) != DialogResult.OK) return;
            string name = ask.Input.Text.Trim();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == ".." || name.EndsWith("."))
            { message.Text = "That is not a valid folder name."; return; }
            try
            {
                string created = Path.Combine(parent, name);
                Directory.CreateDirectory(created);
                var selected = tree.SelectedNode;
                if (selected != null) { selected.Nodes.Clear(); selected.Nodes.Add(new TreeNode(Placeholder)); }
                GoTo(created);
            }
            catch (Exception ex) { message.Text = "Could not create the folder: " + ex.Message; }
        }
    }
}
