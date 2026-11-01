using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Winawdl.Engine;
using Winawdl.Model;

namespace Winawdl.Shell;

/// <summary>The picture Explorer shows for a file, when it has one.</summary>
public static class Thumbnails
{
    private const int Pixels = 192;
    private const int BiggerSizeOk = 0x1, ThumbnailOnly = 0x8;
    private static readonly Guid ImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private static readonly BlockingCollection<Action> Work = [];

    static Thumbnails()
    {
        // Shell extensions expect a single-threaded apartment.
        var thread = new Thread(() =>
        {
            foreach (var job in Work.GetConsumingEnumerable()) job();
        })
        {
            IsBackground = true, Name = "thumbnails",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static Task<ImageSource?> Load(string path)
    {
        var done = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Work.Add(() => done.SetResult(Read(path)));
        return done.Task;
    }

    private static ImageSource? Read(string path)
    {
        try
        {
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ImageFactory, out var factory) != 0) return null;
            if (factory.GetImage(new Size2 { Width = Pixels, Height = Pixels }, BiggerSizeOk | ThumbnailOnly,
                                 out var bitmap) != 0)
                return null;
            try
            {
                var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                                                                  BitmapSizeOptions.FromEmptyOptions());
                // A thumbnail is a whole picture; what its fourth channel holds is not opacity.
                var opaque = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
                opaque.Freeze();
                return opaque;
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        catch (Exception error) when (error is COMException or ArgumentException or IOException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size2
    {
        public int Width, Height;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(Size2 size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}

/// <summary>Windows as the model uses it: dialogs, Explorer, the clipboard.</summary>
public sealed class Desk(Dispatcher dispatcher) : IDesk
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public double Now => _clock.Elapsed.TotalSeconds;
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public IReadOnlyList<string> PickFiles()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Choose files to send" };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public string? PickFolder(string from)
    {
        var dialog = new OpenFolderDialog { Title = "Save received files to", InitialDirectory = from };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public ClipboardContent ReadClipboard()
    {
        try
        {
            if (Clipboard.ContainsFileDropList()) return new([.. Clipboard.GetFileDropList().Cast<string>()]);
            if (Clipboard.ContainsImage()) return new([], Image: Clipboard.GetImage());
            return new([], Text: Clipboard.ContainsText() ? Clipboard.GetText() : "");
        }
        catch (COMException)
        {
            // Another program holds the clipboard open.
            return new([], Problem: "The clipboard is busy. Try Paste again.");
        }
    }

    // Types Windows runs, installs, mounts or follows when opened. One of these
    // that someone sent is shown in Explorer, so one click cannot run it.
    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".cpl", ".msc", ".msi", ".msp", ".mst",
        ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller", ".application",
        ".appref-ms", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".ps1xml", ".vbs", ".vbe",
        ".js", ".jse", ".wsf", ".wsh", ".wsc", ".hta", ".jar", ".py", ".pyw", ".reg",
        ".inf", ".lnk", ".url", ".website", ".scf", ".library-ms", ".search-ms",
        ".searchconnector-ms", ".settingcontent-ms", ".chm", ".hlp", ".gadget", ".diagcab",
        ".iso", ".img", ".vhd", ".vhdx", ".dll", ".sys", ".ocx", ".xll", ".theme",
        ".themepack",
    };

    public void Open(string path)
    {
        if (File.Exists(path) && Runnable.Contains(Path.GetExtension(path)))
        {
            // A link the receiver stored itself still opens in the browser.
            if (StoredWebLink(path) is { } url)
            {
                path = url;
            }
            else
            {
                Reveal(path);
                return;
            }
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
        }
    }

    /// <summary>The web address in a .url exactly as airdrop\transfer.py writes
    /// one, two lines and an http or https URL; null for anything else.</summary>
    private static string? StoredWebLink(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".url", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            if (new FileInfo(path).Length > 8192) return null;
            var lines = File.ReadAllLines(path);
            if (lines.Length != 2 || lines[0] != "[InternetShortcut]"
                || !lines[1].StartsWith("URL=", StringComparison.Ordinal)) return null;
            var url = lines[1][4..];
            if (url.Any(char.IsControl) || url.Contains(' ')) return null;
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri.AbsoluteUri
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Reveal(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();

    public Task<ImageSource?> Thumbnail(string path) => Thumbnails.Load(path);

    public void Later(TimeSpan delay, Action action)
    {
        DispatcherTimer? timer = null;
        timer = new DispatcherTimer(delay, DispatcherPriority.Normal, (_, _) =>
        {
            timer!.Stop();
            action();
        }, dispatcher);
    }
}

/// <summary>The app's own settings, in a file under the user's local app data.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Readable = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(Repo.DataFolder, "app.json");

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            settings = new AppSettings();
        }
        settings.Save = () =>
        {
            try
            {
                var draft = FilePath + ".new";
                File.WriteAllText(draft, JsonSerializer.Serialize(settings, Readable));
                File.Move(draft, FilePath, overwrite: true);
            }
            catch (IOException)
            {
                // A setting that could not be written still holds until the app closes.
            }
        };
        return settings;
    }
}
