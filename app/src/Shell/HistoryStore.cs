using System.IO;
using System.Globalization;
using System.Text.Json;
using Winawdl.Model;

namespace Winawdl.Shell;

/// <summary>The last 500 transfers, saved beside app.json by atomic replacement.</summary>
public sealed class HistoryStore(string filePath) : IHistoryStore
{
    private const int Limit = 500;
    private static readonly JsonSerializerOptions Readable = new() { WriteIndented = true };

    public IReadOnlyList<TransferRecord> Load()
    {
        try
        {
            var entries = JsonSerializer.Deserialize<List<TransferRecord>>(File.ReadAllText(filePath));
            return entries?.Where(Valid)
                           .TakeLast(Limit).ToList() ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                                      or System.Security.SecurityException)
        {
            // A damaged or unavailable history must not prevent the app from opening.
            return [];
        }
    }

    private static bool SafeLabel(string? text) =>
        text is { Length: > 0 and <= 255 }
        && !text.Contains("..", StringComparison.Ordinal)
        && !text.Any(ch => char.IsControl(ch) || char.GetUnicodeCategory(ch) == UnicodeCategory.Format
                           || "/\\<>:\"|?*".Contains(ch));

    private static bool SafePath(string? path)
    {
        if (path is null || path.Length > 32767 || path.Any(char.IsControl)) return false;
        if (path.Length == 0) return true;
        try { return Path.IsPathFullyQualified(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }

    private static bool Valid(TransferRecord? entry) =>
        entry is { Direction: "Sent" or "Received", Result: "Finished" or "Failed" or "Declined",
                   Files: not null, TotalSize: >= 0 }
        && SafeLabel(entry.Who) && SafeLabel(entry.Device)
        && entry.When != default
        && entry.Files.Count <= 500
        && entry.Files.All(file => file is { Size: >= 0 } && SafeLabel(file.Name) && SafePath(file.Path));

    public void Save(IReadOnlyList<TransferRecord> entries)
    {
        try
        {
            var draft = filePath + ".new";
            File.WriteAllText(draft, JsonSerializer.Serialize(entries.TakeLast(Limit), Readable));
            File.Move(draft, filePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            // A transfer remains visible this session if its history cannot be saved.
        }
    }
}
