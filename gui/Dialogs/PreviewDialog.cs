using System.Windows.Forms;

partial class GearBackup
{
    sealed class PreviewDialog : SelectionDialog
    {
        public PreviewDialog(OperationPlan plan)
            : base(plan.Label + " · " + plan.Actions.Count + " actions · " + FormatSize(plan.Bytes), plan.Actions)
        {
            Text = plan.PreviewOnly ? "Preview · no files will change" : "Review changes";
            Accept.Text = plan.PreviewOnly ? "Close preview" : "Proceed";
            Accept.Enabled = true;
            List.SelectionMode = SelectionMode.None;
        }
    }
}
