// Questions and output for the running operation, whichever front end answers them.
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using static GearBackup.ConsoleUi;

namespace GearBackup
{
    static class ConsoleUi
    {
        internal static readonly Option Yes = new Option('y', "Yes"), No = new Option('n', "No");

        // Asks a question with a fixed set of answers; returns the chosen option's key.
        internal static char Ask(string question, params Option[] options)
        {
            OperationContext.Current.CheckCancellation();
            char answer = OperationContext.Current.Interaction.Choose(question, options);
            OperationContext.Current.CheckCancellation();
            return answer;
        }

        internal static bool AskYesNo(string question)
        {
            return Ask(question, Yes, No) == 'y';
        }

        // Lets the user pick one of `items`; 1..items.Count, or 0 for noneLabel / Enter (see ChooseItem).
        internal static int AskItem(string question, IList<string> items, string noneLabel = null)
        {
            OperationContext.Current.CheckCancellation();
            int answer = OperationContext.Current.Interaction.ChooseItem(question, items, noneLabel);
            OperationContext.Current.CheckCancellation();
            return answer;
        }

        // Free text such as a search or a path; null when cancelled.
        internal static string AskText(string question)
        {
            OperationContext.Current.CheckCancellation();
            string answer = OperationContext.Current.Interaction.AskText(question);
            OperationContext.Current.CheckCancellation();
            return answer;
        }

        internal static void PrintProgress(long processedBytes, long totalBytes, int done, int total, long copiedBytes)
        {
            OperationContext.Current.Progress(Stage.BackupCopy, "", processedBytes, totalBytes, done, total);
        }

        internal static void WriteColored(string text, ConsoleColor color)
        {
            OperationContext.Current.Interaction.WriteColored(text, color);
        }
    }

}
