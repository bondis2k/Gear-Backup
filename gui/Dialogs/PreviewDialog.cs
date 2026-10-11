using System;
using System.Drawing;
using System.Windows.Forms;
using static ConsoleUi;

internal sealed class PreviewDialog : DialogForm
{
    readonly TextBox search = new TextBox { Name = "PreviewSearch", Dock = DockStyle.Fill, AccessibleName = "Search changes" };
    readonly ComboBox filter = new ComboBox { Name = "PreviewFilter", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Filter changes" };
    readonly DataGridView grid = new DataGridView { Name = "PreviewChanges", Dock = DockStyle.Fill, ReadOnly = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = GuiTheme.Surface, BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AccessibleName = "Planned changes" };
    readonly Label count = GuiTheme.Label("", 9, GuiTheme.Muted);
    readonly OperationPlan plan;
    public PreviewDialog(OperationPlan value)
        : base(value.Label + " · " + value.Actions.Count + " actions · " + FormatSize(value.Bytes))
    {
        plan = value;
        Text = plan.PreviewOnly ? "Preview · no files will change" : "Review changes";
        ClientSize = new Size(860, 520);
        Accept.Text = plan.PreviewOnly ? "Close preview" : "Proceed";
        Accept.AccessibleName = Accept.Text;
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var tools = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 0, 0, 12) };
        tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65)); tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        tools.Controls.Add(GuiTheme.Label("Search paths (Ctrl+F)", 9, GuiTheme.Muted), 0, 0);
        tools.Controls.Add(GuiTheme.Label("Show", 9, GuiTheme.Muted), 1, 0);
        tools.Controls.Add(new FieldPanel(search) { Dock = DockStyle.Fill }, 0, 1); tools.Controls.Add(filter, 1, 1);
        filter.Items.AddRange(new object[] { "All changes", "Additions", "Replacements", "Removals", "Unchanged" });
        grid.Columns.Add("Action", "Action"); grid.Columns.Add("Path", "Path"); grid.Columns.Add("Size", "Size");
        grid.Columns[0].FillWeight = 28; grid.Columns[1].FillWeight = 62; grid.Columns[2].FillWeight = 10;
        grid.Columns[2].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        GuiTheme.StyleGrid(grid);
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        GuiTheme.StyleComboBox(filter);
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        content.Controls.Add(tools, 0, 0); content.Controls.Add(grid, 0, 1); content.Controls.Add(count, 0, 2);
        layout.Controls.Add(content, 0, 1);
        search.TextChanged += delegate { ApplyFilter(); }; filter.SelectedIndexChanged += delegate { ApplyFilter(); };
        filter.SelectedIndex = 0;
    }
    void ApplyFilter()
    {
        grid.Rows.Clear();
        foreach (string line in plan.Actions)
        {
            int split = line.IndexOf(": ", StringComparison.Ordinal);
            string action = split < 0 ? line : line.Substring(0, split);
            string path = split < 0 ? "" : line.Substring(split + 2);
            int category = action.StartsWith("Remove", StringComparison.OrdinalIgnoreCase) ? 3 :
                action == "Changed" || action == "Replace" ? 2 : action == "Skip unchanged" ? 4 : 1;
            if (filter.SelectedIndex > 0 && filter.SelectedIndex != category) continue;
            if (path.IndexOf(search.Text, StringComparison.OrdinalIgnoreCase) < 0) continue;
            long size;
            string displaySize = plan.FileSizes != null && plan.FileSizes.TryGetValue(path, out size) ? FormatSize(size) : "—";
            int row = grid.Rows.Add(action, path, displaySize);
            if (category == 3) grid.Rows[row].Cells[0].Style.ForeColor = GuiTheme.Danger;
        }
        count.Text = grid.Rows.Count + " of " + plan.Actions.Count + " actions shown. " +
            (plan.PreviewOnly ? "No files will change." : "Proceed applies the entire plan, including hidden rows.");
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F)) { search.Focus(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
