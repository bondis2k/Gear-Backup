using System.Collections.Generic;

// Previewing and approving what an operation is about to do.
static class PlanReview
{
    internal static bool PreviewOnly
    {
        get { return OperationContext.Current.PreviewOnly; }
        set { OperationContext.Current.PreviewOnly = value; }
    }

    // True when the operation should go ahead: approved, and not just a --preview.
    internal static bool ShowPlan(string label, List<string> lines, long bytes, bool showDetails, Dictionary<string, long> fileSizes = null)
    {
        OperationContext.Current.CheckCancellation();
        bool approved = OperationContext.Current.Interaction.ReviewPlan(new OperationPlan {
            Label = label, Actions = lines, Bytes = bytes, FileSizes = fileSizes, ShowDetails = showDetails, PreviewOnly = PreviewOnly });
        OperationContext.Current.CheckCancellation();
        return approved && !PreviewOnly;
    }
}
