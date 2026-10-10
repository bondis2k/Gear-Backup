using System;
using System.Collections.Generic;
using System.Windows.Forms;

partial class GearBackup
{
    // The GUI's front end: questions become dialogs, output goes to the activity log, and progress to the
    // status area. Dialogs are shown on the UI thread; the operation itself runs on a worker thread.
    sealed class GuiInteraction : IOperationInteraction
    {
        readonly MainForm form;
        public GuiInteraction(MainForm owner) { form = owner; }
        public void Write(string text) { form.AppendActivity(text); }
        public void WriteLine(string text) { form.AppendActivity(text + Environment.NewLine); }
        public void WriteColored(string text, ConsoleColor color) { WriteLine(text); }
        public void ReportProgress(OperationProgress progress) { form.UpdateProgress(progress); }

        // One button per option. Closing the dialog cancels the operation.
        public char Choose(string question, IList<Option> options)
        {
            return form.OnUi(delegate {
                using (var dialog = new ChoiceDialog(question, options))
                {
                    if (dialog.ShowDialog(form) != DialogResult.OK) throw new OperationCanceledException();
                    return dialog.Key;
                }
            });
        }

        // A list to pick from; noneLabel, if given, is offered as an extra last entry and returns 0.
        public int ChooseItem(string question, IList<string> items, string noneLabel)
        {
            var choices = new List<string>(items);
            if (noneLabel != null) choices.Add(noneLabel);
            return form.OnUi(delegate {
                using (var dialog = new SelectionDialog(question, choices))
                {
                    if (dialog.ShowDialog(form) != DialogResult.OK) throw new OperationCanceledException();
                    int selected = dialog.List.SelectedIndex + 1;
                    return selected > items.Count ? 0 : selected;
                }
            });
        }

        public string AskText(string question)
        {
            return form.OnUi(delegate {
                using (var dialog = new SearchDialog(question, ""))
                    return dialog.ShowDialog(form) == DialogResult.OK ? dialog.Input.Text : null;
            });
        }

        public bool ReviewPlan(OperationPlan plan)
        {
            if (!plan.ShowDetails && !plan.PreviewOnly) return true;
            return form.OnUi(delegate {
                using (var dialog = new PreviewDialog(plan))
                    return dialog.ShowDialog(form) == DialogResult.OK && !plan.PreviewOnly;
            });
        }
    }

}
