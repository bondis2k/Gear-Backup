// Windows notifications, including the "Eject M8?" question.
using System;
using System.Threading;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using static UsbDevices;

partial class GearBackup
{
    // Shows the final notification and handles ejecting, depending on the Eject setting:
    // "no" = just the summary, "ask" = "Eject M8" / "Not now" buttons, "yes" = eject straight away.
    static void Finish(string pnpId, string title, string summary, EjectMode ejectMode)
    {
        if (ejectMode == EjectMode.No)
        {
            ShowToast(title, summary, false);
            Console.WriteLine("  " + CurrentDevice.Name + " left connected.");
            return;
        }

        if (ejectMode == EjectMode.Ask)
        {
            bool? answer = ShowToast(title, summary + " Eject the " + CurrentDevice.Name + "?", true);
            if (answer == null) answer = AskYesNo("Eject the " + CurrentDevice.Name + " now?");
            if (answer != true)
            {
                Console.WriteLine("  " + CurrentDevice.Name + " left connected.");
                return;
            }
        }

        string error = EjectDrive(pnpId);
        if (error == null)
            WriteColored("  " + CurrentDevice.Name + " ejected. It is safe to unplug it.", ConsoleColor.Green);
        else
            WriteColored("  Could not eject the " + CurrentDevice.Name + ": " + error, ConsoleColor.Yellow);

        // With Eject=yes this is the only notification, so it carries the summary too
        string result = error == null ? CurrentDevice.Name + " ejected; it is safe to unplug it." : "Could not eject the " + CurrentDevice.Name + ": " + error;
        if (ejectMode == EjectMode.Yes)
            ShowToast(title, summary + " " + result, false);
        else
            ShowToast(error == null ? CurrentDevice.Name + " ejected" : "Could not eject the " + CurrentDevice.Name,
                      error == null ? "It is safe to unplug it." : error, false);
    }

    // Registers the app name shown on notifications (per user, under HKCU).
    static void RegisterAppId()
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId))
            key.SetValue("DisplayName", AppIdentity.Name);
    }

    // Shows a toast. With askEject, it has "Eject M8" / "Not now" buttons and waits for the answer:
    // returns true/false, or null if notifications didn't work or nobody answered.
    static bool? ShowToast(string title, string message, bool askEject)
    {
        try
        {
            RegisterAppId();
            string xml =
                "<toast" + (askEject ? " scenario='reminder'" : "") + ">" +
                "<visual><binding template='ToastGeneric'>" +
                "<text>" + Escape(title) + "</text><text>" + Escape(message) + "</text>" +
                "</binding></visual>" +
                (askEject
                    ? "<actions>" +
                      "<action content='Eject " + Escape(CurrentDevice.Name) + "' arguments='eject' activationType='foreground'/>" +
                      "<action content='Not now' arguments='keep' activationType='foreground'/>" +
                      "</actions>"
                    : "") +
                "</toast>";
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var toast = new ToastNotification(doc);
            toast.Tag = askEject ? "ask-eject" : "status";
            toast.Group = AppIdentity.DirectoryName;
            if (!askEject)
            {
                ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
                return null;
            }

            bool? answer = null;
            bool failed = false;
            var done = new ManualResetEvent(false);
            toast.Activated += (sender, args) =>
            {
                var activated = args as ToastActivatedEventArgs;
                answer = activated != null && activated.Arguments == "eject";
                done.Set();
            };
            toast.Dismissed += (sender, args) =>
            {
                if (args.Reason == ToastDismissalReason.UserCanceled) answer = false;
                done.Set();
            };
            toast.Failed += (sender, args) => { failed = true; done.Set(); };

            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);

            // Wait for a click on the notification, or a Y/N key press in this window
            Console.Write("  Eject the " + CurrentDevice.Name + "? Answer in the notification, or press Y/N here: ");
            DateTime giveUp = DateTime.Now.AddMinutes(15);
            while (!done.WaitOne(200) && DateTime.Now < giveUp)
            {
                if (!Console.KeyAvailable) continue;
                char c = char.ToLowerInvariant(Console.ReadKey(true).KeyChar);
                if (c != 'y' && c != 'n') continue;
                answer = c == 'y';
                try { ToastNotificationManager.History.Remove(toast.Tag, toast.Group, AppId); } catch (Exception) { }
                break;
            }
            Console.WriteLine(answer == true ? "yes" : answer == false ? "no" : "");
            return failed ? null : answer;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static string Escape(string s)
    {
        return System.Security.SecurityElement.Escape(s);
    }
}
