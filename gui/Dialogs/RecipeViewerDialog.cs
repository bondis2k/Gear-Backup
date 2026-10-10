using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

partial class GearBackup
{
    sealed class RecipeViewerDialog : ToolsDialog
    {
        readonly ComboBox recipe = new ComboBox { Name = "RecipeSelection", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, AccessibleName = "Recipe or shooting settings" };
        readonly DataGridView grid = ToolGrid("RecipeValues");
        readonly List<FujiRecipe> recipes = new List<FujiRecipe>();
        public RecipeViewerDialog(MainForm owner, string folder = null) : base(owner, "X100VI recipes", "Read saved C1–C7 recipes and shooting settings. Open a backup folder or an exported XML file; the camera is not needed.")
        {
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.Controls.Add(recipe, 0, 0); content.Controls.Add(grid, 0, 1); ToolLayout.Controls.Add(content, 0, 1);
            foreach (string column in new[] { "Setting", "Value", "Protocol number", "Raw bytes" }) grid.Columns.Add(column, column);
            recipe.SelectedIndexChanged += delegate { DisplayRecipe(); };
            ToolButton("Open backup folder", "OpenRecipeFolder", delegate {
                using (var picker = new FolderBrowserDialog { Description = "Choose an X100VI settings backup folder" })
                    if (picker.ShowDialog(this) == DialogResult.OK) LoadFolder(picker.SelectedPath);
            });
            ToolButton("Open XML", "OpenRecipeXml", delegate {
                using (var picker = new OpenFileDialog { Filter = "Gear Backup settings (*.xml)|*.xml", CheckFileExists = true })
                    if (picker.ShowDialog(this) == DialogResult.OK) LoadFiles(new[] { picker.FileName });
            });
            ToolButton("Copy recipe", "CopyRecipe", delegate { if (recipe.SelectedItem != null) Clipboard.SetText(FujiRecipeReport((FujiRecipe)recipe.SelectedItem)); });
            ToolButton("Export text", "ExportRecipe", delegate {
                if (recipe.SelectedItem == null) return;
                using (var picker = new SaveFileDialog { Filter = "Text file (*.txt)|*.txt", FileName = ((FujiRecipe)recipe.SelectedItem).Section + ".txt" })
                    if (picker.ShowDialog(this) == DialogResult.OK) File.WriteAllText(picker.FileName, FujiRecipeReport((FujiRecipe)recipe.SelectedItem), new System.Text.UTF8Encoding(false));
            });
            if (folder != null) LoadFolder(folder);
        }
        internal void LoadFolder(string folder)
        {
            RejectRootLinks(folder);
            var files = new List<string>();
            for (int i = 1; i <= 7; i++) if (File.Exists(Path.Combine(folder, "C" + i + ".xml"))) files.Add(Path.Combine(folder, "C" + i + ".xml"));
            if (File.Exists(Path.Combine(folder, "Shooting settings.xml"))) files.Add(Path.Combine(folder, "Shooting settings.xml"));
            if (files.Count == 0) throw new IOException("No Gear Backup recipe exports found in this folder.");
            LoadFiles(files.ToArray());
        }
        internal void LoadFiles(string[] files)
        {
            var loaded = new List<FujiRecipe>(); foreach (string file in files) loaded.Add(ReadFujiRecipe(file));
            recipes.Clear(); recipes.AddRange(loaded); recipe.Items.Clear(); recipe.Items.AddRange(recipes.ToArray()); recipe.SelectedIndex = 0;
        }
        void DisplayRecipe()
        {
            grid.Rows.Clear(); var selected = recipe.SelectedItem as FujiRecipe; if (selected == null) return;
            foreach (FujiRecipeValue value in selected.Values) grid.Rows.Add(value.Name, value.Display, value.Number.HasValue ? value.Number.ToString() : "—", value.Raw);
            Notice.Text = selected.Model + " · firmware " + selected.Firmware + " · serial " + selected.Serial +
                ". Values marked ‘Protocol value’ have no confirmed display mapping. This viewer does not change exports or camera settings.";
        }
    }
}
