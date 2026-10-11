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
    readonly TreeView tree = new BrowserTree { Name = "FolderTree", Dock = DockStyle.Fill };
    readonly TextBox path = new TextBox { Name = "FolderPath", AccessibleName = "Folder" };
    readonly Label message = GuiTheme.Label("", 9, GuiTheme.Muted);
    readonly Button up = GuiTheme.Button("↑ Up", false);
    bool syncing;
    const string Placeholder = "…";

    internal string SelectedPath { get; private set; }

    internal FolderPickerDialog(string description, string initial, bool allowNew) : base(description)
    {
        ClientSize = new Size(820, 600); MinimumSize = new Size(620, 460);
        Text = "Choose a folder · " + AppIdentity.Name;
        Accept.Text = "Select folder";
        BuildHeader(description);
        message.Name = "FolderPickerMessage"; message.Margin = new Padding(0, 6, 0, 0);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(BuildAddressBar(), 0, 0);
        var browser = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        browser.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        browser.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        browser.Controls.Add(BuildShortcuts(), 0, 0);
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(10), Margin = Padding.Empty };
        tree.BackColor = GuiTheme.Surface; tree.ForeColor = GuiTheme.Ink; tree.Font = GuiTheme.Font(10);
        card.Controls.Add(tree);
        browser.Controls.Add(card, 1, 0);
        content.Controls.Add(browser, 0, 1);
        content.Controls.Add(message, 0, 2);
        layout.Controls.Add(content, 0, 1);

        AddRoots();
        tree.BeforeExpand += delegate(object sender, TreeViewCancelEventArgs e) { LoadChildren(e.Node); };
        tree.AfterSelect += delegate(object sender, TreeViewEventArgs e) {
            if (syncing || e.Node.Tag == null) return;
            syncing = true; path.Text = (string)e.Node.Tag; syncing = false;
            ClearMessage();
        };
        path.KeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.KeyCode != Keys.Enter) return;
            string wanted = path.Text.Trim();
            var selected = tree.SelectedNode;
            if (selected != null && selected.Tag != null && PathsEqual(wanted, (string)selected.Tag)) return; // already there: Enter accepts
            e.SuppressKeyPress = true;
            Navigate(wanted);
        };
        path.TextChanged += delegate { UpdateUpButton(); };
        up.Click += delegate {
            try { var parent = Directory.GetParent(path.Text.Trim()); if (parent != null) Navigate(parent.FullName); }
            catch (ArgumentException) { ShowError("Enter a valid folder path first."); }
            catch (IOException) { ShowError("That folder is not available."); }
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
            if (chosen.Length == 0 || !Directory.Exists(chosen)) { e.Cancel = true; ShowError("Choose a folder that exists."); return; }
            SelectedPath = Path.GetFullPath(chosen);
        };
        Shown += delegate { if (!string.IsNullOrEmpty(initial)) GoTo(initial); if (tree.SelectedNode == null && tree.Nodes.Count > 0) tree.SelectedNode = tree.Nodes[0]; tree.Focus(); };
        // The tree needs a handle before it can be navigated; headless windows never show.
        if (!string.IsNullOrEmpty(initial)) GoTo(initial);
        ClearMessage(); UpdateUpButton();
    }

    void BuildHeader(string description)
    {
        Control old = layout.GetControlFromPosition(0, 0);
        layout.Controls.Remove(old); old.Dispose();
        layout.RowStyles[0] = new RowStyle(SizeType.AutoSize);
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 18) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Controls.Add(new BrowserIcon { Size = new Size(48, 48), Margin = new Padding(0, 0, 16, 0) }, 0, 0);
        var words = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        var heading = GuiTheme.Label("Choose a folder", 17, GuiTheme.Ink); heading.Font = GuiTheme.Heading(17); heading.Margin = new Padding(0, 0, 0, 4);
        words.Controls.Add(heading);
        var subtitle = GuiTheme.Label(description, 9.5f, GuiTheme.Muted); subtitle.Margin = Padding.Empty;
        words.SizeChanged += delegate { subtitle.MaximumSize = new Size(Math.Max(100, words.ClientSize.Width), 0); };
        words.Controls.Add(subtitle); header.Controls.Add(words, 1, 0);
        layout.Controls.Add(header, 0, 0);
    }

    Control BuildAddressBar()
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 14) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        up.Name = "FolderUp"; up.AccessibleName = "Go to parent folder"; up.MinimumSize = new Size(76, 36); up.Margin = new Padding(0, 0, 10, 0);
        var go = GuiTheme.Button("Go", false); go.Name = "FolderGo"; go.AccessibleName = "Go to entered folder"; go.MinimumSize = new Size(64, 36); go.Margin = new Padding(10, 0, 0, 0);
        go.Click += delegate { Navigate(path.Text.Trim()); };
        bar.Controls.Add(up, 0, 0);
        bar.Controls.Add(new FieldPanel(path) { Dock = DockStyle.Fill, Margin = Padding.Empty }, 1, 0);
        bar.Controls.Add(go, 2, 0);
        return bar;
    }

    Control BuildShortcuts()
    {
        var places = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = new Padding(0, 0, 14, 0) };
        places.Controls.Add(GuiTheme.SectionTitle("Quick access"));
        AddShortcut(places, "Home", "FolderHome", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        AddShortcut(places, "Desktop", "FolderDesktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddShortcut(places, "Documents", "FolderDocuments", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        var hint = GuiTheme.Label("Expand a drive or folder to browse its contents.", 9, GuiTheme.Muted);
        hint.MaximumSize = new Size(120, 0); hint.Margin = new Padding(0, 18, 0, 0); places.Controls.Add(hint);
        return places;
    }

    void AddShortcut(Control parent, string label, string name, string folder)
    {
        if (folder.Length == 0 || !Directory.Exists(folder)) return;
        var shortcut = GuiTheme.Button(label, false); shortcut.Name = name; shortcut.AccessibleName = "Open " + label + " folder";
        shortcut.AutoSize = false; shortcut.Size = new Size(120, 36); shortcut.MinimumSize = new Size(0, 36);
        shortcut.Padding = new Padding(10, 4, 10, 4); shortcut.Margin = new Padding(0, 0, 0, 8);
        shortcut.Click += delegate { Navigate(folder); }; parent.Controls.Add(shortcut);
    }

    void Navigate(string wanted)
    {
        if (!GoTo(wanted)) { ShowError("That folder was not found. Check the path and try again."); return; }
        tree.Focus();
    }
    void ClearMessage() { message.ForeColor = GuiTheme.Muted; message.Text = "Select a folder, or paste a path above and choose Go."; }
    void ShowError(string text) { message.ForeColor = GuiTheme.Danger; message.Text = text; }
    void UpdateUpButton()
    {
        try { up.Enabled = Directory.Exists(path.Text.Trim()) && Directory.GetParent(path.Text.Trim()) != null; }
        catch (ArgumentException) { up.Enabled = false; }
        catch (IOException) { up.Enabled = false; }
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
        int icon = folder.Length == 3 && folder[1] == ':' ? 2 : 0;
        var node = new TreeNode(text) { Tag = folder, ImageIndex = icon, SelectedImageIndex = icon };
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
        ClearMessage();
        return true;
    }

    void MakeFolder()
    {
        string parent = path.Text.Trim();
        if (!Directory.Exists(parent)) { ShowError("Choose the folder to create it in first."); return; }
        using (var ask = new SearchDialog("Name for the new folder", ""))
        {
            if (ask.ShowDialog(this) != DialogResult.OK) return;
            string name = ask.Input.Text.Trim();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == ".." || name.EndsWith("."))
            { ShowError("That is not a valid folder name."); return; }
            try
            {
                string created = Path.Combine(parent, name);
                Directory.CreateDirectory(created);
                var selected = tree.SelectedNode;
                if (selected != null) { selected.Nodes.Clear(); selected.Nodes.Add(new TreeNode(Placeholder)); }
                GoTo(created);
            }
            catch (Exception ex) { ShowError("Could not create the folder: " + ex.Message); }
        }
    }
}
