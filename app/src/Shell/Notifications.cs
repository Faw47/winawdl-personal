using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Windows.Data.Xml.Dom;
using Windows.Foundation.Metadata;
using Windows.UI.Notifications;

namespace Winawdl.Shell;

public sealed record Notice(string Title, string Detail, string Open, string Accept = "", string Decline = "", string Preview = "");

public interface IToast
{
    bool Show(Notice notice, Action failed);
    void Clear();
}

/// <summary>Inbox Windows toasts. URI activation returns through the existing per-user pipe.</summary>
public sealed class WindowsToast : IToast
{
    public const string AppId = "winawdl.AirDrop";
    public const string Scheme = "winawdl-notice";

    internal static string Xml(Notice notice)
    {
        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"),
            new XElement("text", Clean(notice.Title)), new XElement("text", Clean(notice.Detail)));
        // Desktop toast images use absolute file:/// URLs. Never accept network
        // images or arbitrary launch URLs as thumbnail sources.
        if (Uri.TryCreate(notice.Preview, UriKind.Absolute, out var image)
            && image.IsFile && image.LocalPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            binding.Add(new XElement("image", new XAttribute("placement", "appLogoOverride"),
                new XAttribute("src", image.AbsoluteUri), new XAttribute("alt", "Received photo")));
        var toast = new XElement("toast", new XAttribute("activationType", "protocol"),
            new XAttribute("launch", notice.Open), new XElement("visual", binding));
        if (notice.Accept.Length > 0 && notice.Decline.Length > 0)
            toast.Add(new XElement("actions",
                Button("Accept", notice.Accept), Button("Decline", notice.Decline)));
        return toast.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Button(string label, string uri) => new("action",
        new XAttribute("content", label), new XAttribute("arguments", uri), new XAttribute("activationType", "protocol"));

    private static string Clean(string text) => string.Concat(text.EnumerateRunes()
        .Where(rune => System.Text.Rune.GetUnicodeCategory(rune) is not
            (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format))
        .Take(256).Select(rune => rune.ToString()));

    public bool Show(Notice notice, Action failed)
    {
        try
        {
            if (!ToastRegistration.Installed || SHQueryUserNotificationState(out var state) != 0 || state != 5)
                return false;
            if (ApiInformation.IsPropertyPresent("Windows.UI.Notifications.ToastNotificationManagerForUser", "NotificationMode")
                && ToastNotificationManager.GetDefault().NotificationMode != ToastNotificationMode.Unrestricted)
                return false;
            var notifier = ToastNotificationManager.CreateToastNotifier(AppId);
            if (notifier.Setting != NotificationSetting.Enabled) return false;
            var document = new XmlDocument();
            document.LoadXml(Xml(notice));
            var toast = new ToastNotification(document) { ExpirationTime = DateTimeOffset.Now.AddMinutes(2) };
            toast.Failed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(failed);
            notifier.Show(toast);
            return true;
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or IOException
                                      or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Clear()
    {
        try { ToastNotificationManager.History.Clear(AppId); }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
