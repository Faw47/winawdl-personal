using System.Text.Json.Serialization;
using System.IO;
using System.Windows.Input;

namespace Winawdl.Model;

/// <summary>A file's name and location in a transfer, never its contents.</summary>
public sealed class TransferFile
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }

    [JsonIgnore]
    public bool HasPath => Path.Length > 0;

    [JsonIgnore]
    public string SizeLabel => Words.Size(Math.Max(0, Size));
}

/// <summary>One transfer after it finished, failed or was declined.</summary>
public sealed class TransferRecord
{
    public string Direction { get; set; } = "";
    public string Who { get; set; } = "";
    public string Device { get; set; } = "";
    public DateTimeOffset When { get; set; }
    public List<TransferFile> Files { get; set; } = [];
    public long TotalSize { get; set; }
    public string Result { get; set; } = "";
}

/// <summary>A saved transfer as one expandable row in the history sheet.</summary>
public sealed class HistoryRow : Observable
{
    private bool _expanded;
    private readonly IDesk _desk;

    public HistoryRow(TransferRecord record, IDesk desk)
    {
        Record = record;
        _desk = desk;
        Toggle = new Command(() => Expanded = !Expanded);
        Reveal = new Command(() =>
        {
            if (FirstExistingFile() is { } path) _desk.Reveal(path);
        });
    }

    public TransferRecord Record { get; }
    public IReadOnlyList<TransferFile> Files => Record.Files;
    public string Title => Record.Direction == "Sent" ? $"Sent to {Record.Who}" : $"Received from {Record.Who}";
    public string Detail => $"{Record.Result} · {Record.When.ToLocalTime():g} · {Words.Size(Math.Max(0, Record.TotalSize))}";
    public string Device => Record.Device.Length > 0 ? Record.Device : "Unknown device";
    public string DetailsLabel => Expanded ? "Hide Files" : "Show Files";
    public string ToggleName => $"{Title}, {DetailsLabel}";
    public bool CanReveal => FirstExistingFile() is not null;
    public ICommand Toggle { get; }
    public ICommand Reveal { get; }
    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (!Set(ref _expanded, value)) return;
            Changed(nameof(DetailsLabel));
            Changed(nameof(ToggleName));
        }
    }

    private static bool Exists(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }

    private string? FirstExistingFile() => Record.Files.Select(file => file.Path).FirstOrDefault(Exists);
}
