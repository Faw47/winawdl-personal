using System.IO;
using System.Windows.Media.Imaging;
using Winawdl.Engine;

namespace Winawdl.Shell;

/// <summary>Short-lived, locally rendered previews for completed photo toasts.
/// Files are capped and pruned; neither the source photo nor its format changes.</summary>
public static class ToastPreviews
{
    private const int MaxFiles = 10;
    private const int MaxBytes = 200 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private static readonly SemaphoreSlim Work = new(1, 1);
    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".avif",
        ".gif", ".webp", ".bmp", ".tif", ".tiff",
    };

    internal static bool Candidate(string path)
    {
        try { return Path.IsPathFullyQualified(path) && Pictures.Contains(Path.GetExtension(path)) && File.Exists(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    public static async Task<string> CreateAsync(string path)
    {
        if (!Candidate(path) || !await Work.WaitAsync(0)) return "";
        try
        {
            // Windows' existing shell image factory generates a 192px preview.
            // It uses installed Windows codecs for HEIC/HEIF/AVIF, and returns
            // null when no decoder is installed. Never block the toast for it.
            var load = Thumbnails.Load(path);
            if (await Task.WhenAny(load, Task.Delay(Timeout)) != load) return "";
            if (await load is not BitmapSource picture) return "";
            return Save(picture, Path.Combine(Repo.DataFolder, "toast-previews"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException
                                      or InvalidOperationException or System.Runtime.InteropServices.COMException
                                      or System.Security.SecurityException)
        {
            return "";
        }
        finally { Work.Release(); }
    }

    /// <summary>Shared with headless checks so previews need no camera or phone.</summary>
    internal static string Save(BitmapSource picture, string folder)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".jpg");
        // JPEG caps storage and is natively supported by ToastGeneric. The
        // thumbnail has already been downsampled by Explorer to 192 x 192.
        var encoder = new JpegBitmapEncoder { QualityLevel = 78 };
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            encoder.Save(output);
        if (new FileInfo(target).Length is <= 0 or > MaxBytes)
        {
            File.Delete(target);
            return "";
        }
        Prune(folder);
        return new Uri(target, UriKind.Absolute).AbsoluteUri;
    }

    private static void Prune(string folder)
    {
        // Product-owned JPEGs only, never the user's original received files.
        foreach (var old in new DirectoryInfo(folder).EnumerateFiles("*.jpg")
                     .OrderByDescending(file => file.LastWriteTimeUtc).ThenByDescending(file => file.Name)
                     .Skip(MaxFiles))
        {
            try { old.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
