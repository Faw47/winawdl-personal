using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Winawdl.Shell;

/// <summary>What Windows' Share sheet hands winawdl, and how it reaches the app.
/// Compiled into winawdl-share.exe too (app\share), which is what the sheet starts.</summary>
public static class Share
{
    /// <summary>Opens the window when winawdl is not running. A notice no toast
    /// issued has no action, so the app only starts and surfaces.</summary>
    public const string OpenUri = "winawdl-notice://action/0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>Where copies of shared files that would not outlive the share are kept,
    /// for this Windows user. The app deletes them once nothing needs them. Named
    /// outright rather than from %TEMP%, which a packaged process may see moved.</summary>
    public static string StashRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "winawdl", "shared");

    public interface ISource
    {
        Task<IReadOnlyList<Item>> Items();
        Task<string?> Link();
    }

    /// <param name="Path">Where the item is on disk, or empty for one that has no file of its own.</param>
    /// <param name="CopyInto">Copies the item into a folder and returns the copy's path.</param>
    public sealed record Item(string Path, bool IsFolder, Func<string, Task<string>> CopyInto);

    public sealed record Shared(string[] Paths, string Link);

    /// <summary>Files where they are, unless they sit somewhere the sharing app may
    /// delete them once the share is over; else a link.</summary>
    public static async Task<Shared> Gather(ISource source, string stash)
    {
        var paths = new List<string>();
        foreach (var item in await source.Items())
        {
            if (item.Path.Length > 0 && (item.IsFolder || !Temporary(item.Path)))
            {
                paths.Add(item.Path);
                continue;
            }
            if (item.IsFolder) continue;
            Directory.CreateDirectory(stash);
            paths.Add(await item.CopyInto(stash));
        }
        if (paths.Count > 0) return new(paths.ToArray(), "");
        return new([], await source.Link() is { } link && ValidLink(link) ? link : "");
    }

    /// <summary>Folders an app shares a file from only for as long as the share lasts.</summary>
    public static bool Temporary(string path)
    {
        var full = Full(path);
        return full is null || Inside(full, Path.GetTempPath())
            || full.Contains(@"\AppData\Local\Packages\", StringComparison.OrdinalIgnoreCase)
            || full.Contains(@"\INetCache\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes shared copies left from a share the app never took in.</summary>
    public static void Sweep(DateTime before, string? root = null)
    {
        root ??= StashRoot;
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var folder in new DirectoryInfo(root).EnumerateDirectories())
                if (folder.LastWriteTimeUtc < before && !folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    folder.Delete(recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static bool InStash(string path) => Full(path) is { } full && Inside(full, StashRoot);

    public static bool ValidLink(string url) =>
        url.Length > 0 && Encoding.UTF8.GetByteCount(url) <= 8192
            && (url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal))
            && !url.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)
                || char.GetUnicodeCategory(ch) is UnicodeCategory.Format or UnicodeCategory.Surrogate);

    /// <summary>The single-instance channel of winawdl.exe for this Windows user.</summary>
    public static string PipeName(string name) => name + "-" + WindowsIdentity.GetCurrent().User!.Value;

    /// <summary>Hands what was shared to the running app, as a second winawdl.exe
    /// would (SingleInstance.Listen reads it). False when no app answers.</summary>
    public static async Task<bool> Forward(Shared shared, string pipeName, TimeSpan wait)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { shared.Paths, Surface = true, Notice = "", shared.Link });
        using var deadline = new CancellationTokenSource(wait);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(deadline.Token);
            await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), deadline.Token);
            await pipe.WriteAsync(bytes, deadline.Token);
            var reply = new byte[1];
            await pipe.ReadExactlyAsync(reply, deadline.Token);
            return reply[0] == 1;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? Full(string path)
    {
        try { return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool Inside(string full, string folder) =>
        full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);
}
