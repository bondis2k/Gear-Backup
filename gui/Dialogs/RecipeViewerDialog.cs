using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using static PathSafety;
using static FujiRecipes;

// Shows saved X100VI recipes (C1–C7) and shooting settings from a settings backup folder or one XML
// export, with readable values next to the protocol numbers and raw bytes. Reads only; the camera
// isn't needed.
internal sealed class RecipeViewerDialog : ToolsDialog
{
    readonly ComboBox recipe = new ComboBox { Name = "RecipeSelection", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top,
        AccessibleName = "Recipe or shooting settings" };
    readonly DataGridView grid = ToolGrid("RecipeValues");
    readonly List<FujiRecipe> recipes = new List<FujiRecipe>();

    // `folder` opens a settings backup straight away; it must contain the exports.
    public RecipeViewerDialog(MainForm owner, string folder = null)
        : base(owner, "X100VI recipes", "Read saved C1–C7 recipes and shooting settings. Open a backup folder or an exported XML file; " +
            "the camera is not needed.")
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = GuiTheme.Surface };
        GuiTheme.StyleComboBox(recipe);
        recipe.Margin = new Padding(0, 0, 0, 10);
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(recipe, 0, 0);
        content.Controls.Add(grid, 0, 1);
        SetContent(content);
        foreach (string column in new[] { "Setting", "Value", "Protocol number", "Raw bytes" }) grid.Columns.Add(column, column);
        recipe.SelectedIndexChanged += delegate { DisplayRecipe(); };

        ToolButton("Open backup folder", "OpenRecipeFolder", delegate {
            using (var picker = new FolderPickerDialog("Choose an X100VI settings backup folder", null, false))
                if (picker.ShowDialog(this) == DialogResult.OK) LoadFolder(picker.SelectedPath);
        }, true);
        ToolButton("Open XML", "OpenRecipeXml", delegate {
            using (var picker = new OpenFileDialog { Filter = "Gear Backup settings (*.xml)|*.xml", CheckFileExists = true })
                if (picker.ShowDialog(this) == DialogResult.OK) LoadFiles(new[] { picker.FileName });
        });
        ToolButton("Copy recipe", "CopyRecipe", delegate {
            FujiRecipe selected = recipe.SelectedItem as FujiRecipe;
            if (selected != null) Clipboard.SetText(FujiRecipeReport(selected));
        });
        ToolButton("Export text", "ExportRecipe", ExportRecipe);
        if (folder != null) LoadFolder(folder);
    }

    // C1–C7 and the shooting settings, whichever of them the folder has.
    internal void LoadFolder(string folder)
    {
        RejectRootLinks(folder);
        List<string> files = RecipeFiles(folder);
        if (files.Count == 0) throw new IOException("No Gear Backup recipe exports found in this folder.");
        LoadFiles(files.ToArray());
    }

    // The recipe exports a settings backup folder holds (C1–C7.xml, Shooting settings.xml).
    static List<string> RecipeFiles(string folder)
    {
        var files = new List<string>();
        if (ContentStore.IsContentStore(folder))
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Shooting settings.xml" };
            for (int slot = 1; slot <= 7; slot++) names.Add("C" + slot + ".xml");
            foreach (var item in ContentStore.Current(folder).Files)
                if (names.Contains(item.Relative) && File.Exists(item.StoredPath)) files.Add(item.StoredPath);
            return files;
        }
        for (int i = 1; i <= 7; i++)
        {
            string path = Path.Combine(folder, "C" + i + ".xml");
            if (File.Exists(path)) files.Add(path);
        }
        string shooting = Path.Combine(folder, "Shooting settings.xml");
        if (File.Exists(shooting)) files.Add(shooting);
        return files;
    }

    // Whether a backup folder has recipes to show, so Dashboard and Storage can offer View recipes only
    // for settings backups. Only checks that the files exist; the viewer validates them when opening.
    internal static bool HasRecipes(string folder)
    {
        try { return Directory.Exists(folder) && RecipeFiles(folder).Count > 0; }
        catch (Exception) { return false; }
    }

    // Reads every file first, so one invalid file leaves what's shown unchanged.
    internal void LoadFiles(string[] files)
    {
        var loaded = new List<FujiRecipe>();
        foreach (string file in files) loaded.Add(ReadFujiRecipe(file));
        recipes.Clear();
        recipes.AddRange(loaded);
        recipe.Items.Clear();
        recipe.Items.AddRange(recipes.ToArray());
        recipe.SelectedIndex = 0;
    }

    void DisplayRecipe()
    {
        grid.Rows.Clear();
        var selected = recipe.SelectedItem as FujiRecipe;
        if (selected == null) return;
        foreach (FujiRecipeValue value in selected.Values)
            grid.Rows.Add(value.Name, value.Display, value.Number.HasValue ? value.Number.ToString() : "—", value.Raw);
        Notice.Text = selected.Model + " · firmware " + selected.Firmware + " · serial " + selected.Serial +
            ". Values marked ‘Protocol value’ have no confirmed display mapping. This viewer does not change exports or camera settings.";
    }

    void ExportRecipe()
    {
        var selected = recipe.SelectedItem as FujiRecipe;
        if (selected == null) return;
        using (var picker = new SaveFileDialog { Filter = "Text file (*.txt)|*.txt", FileName = selected.Section + ".txt" })
            if (picker.ShowDialog(this) == DialogResult.OK)
                File.WriteAllText(picker.FileName, FujiRecipeReport(selected), new System.Text.UTF8Encoding(false));
    }
}
