using System.IO;
using System.Windows.Input;
using System.Windows.Media;

namespace Winawdl.Model;

/// <summary>Works out a transfer's speed from the last couple of seconds of it.</summary>
public sealed class RateMeter
{
    private const double WindowSeconds = 2;
    private readonly Queue<(double Time, long Done)> _samples = new();

    public double BytesPerSecond { get; private set; }

    public void Note(double now, long done)
    {
        _samples.Enqueue((now, done));
        while (_samples.Count > 2 && now - _samples.Peek().Time > WindowSeconds) _samples.Dequeue();
        var (firstTime, firstDone) = _samples.Peek();
        BytesPerSecond = now - firstTime >= 0.25 ? (done - firstDone) / (now - firstTime) : BytesPerSecond;
    }
}

/// <summary>A file on your side of the table: held to be sent, or just received.</summary>
public sealed class FileItem : Observable
{
    private ImageSource? _thumbnail;

    public FileItem(string path, IDesk desk)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Kind = Words.KindOf(Name);
        Glyph = Words.Glyph(Kind);
        try
        {
            Bytes = new FileInfo(path).Length;
        }
        catch (IOException)
        {
        }
        _ = Load(desk);
    }

    public string Path { get; }
    public string Name { get; }
    public FileKind Kind { get; }
    public string Glyph { get; }
    public long Bytes { get; }
    /// <summary>A slight turn, so that a row of files reads as loose things and not a grid.</summary>
    public double Turn { get; init; }
    public ICommand? Press { get; init; }
    public ICommand? Remove { get; init; }
    public ICommand? Reveal { get; init; }
    public string RemoveName => $"Remove {Name}";

    public ImageSource? Thumbnail { get => _thumbnail; private set => Set(ref _thumbnail, value); }

    private async Task Load(IDesk desk) => Thumbnail = await desk.Thumbnail(Path);
}

public enum PeerState { Idle, Asking, Sending, Finishing, Sent, Declined, Failed }

/// <summary>A device across the table.</summary>
public sealed class PeerModel(string id, string name, string? model) : Observable
{
    private PeerState _state;
    private double _progress;
    private string _status = "";
    private bool _canSend;

    public string Id { get; } = id;
    public string Name { get; } = name;
    public DeviceKind Kind { get; } = Words.KindOfDevice(model);
    public string Glyph => Words.Glyph(Kind);
    public RateMeter Rate { get; private set; } = new();
    public string? SendId { get; set; }
    public IReadOnlyList<string> Sending { get; set; } = [];
    /// <summary>It stopped announcing itself while a transfer to it was running.</summary>
    public bool Left { get; set; }
    public ICommand? Press { get; init; }
    public ICommand? Stop { get; init; }

    public PeerState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            if (value == PeerState.Asking) Rate = new RateMeter();
            Changed(nameof(IsBusy));
            Changed(nameof(IsProblem));
            Changed(nameof(IsSent));
            Changed(nameof(ShowsSend));
        }
    }

    public double Progress { get => _progress; set => Set(ref _progress, value); }
    /// <summary>The line under the name.</summary>
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool IsBusy => State is PeerState.Asking or PeerState.Sending or PeerState.Finishing;
    public bool IsProblem => State is PeerState.Declined or PeerState.Failed;
    public bool IsSent => State == PeerState.Sent;
    public string StopName => $"Stop sending to {Name}";
    /// <summary>There are files in hand and this device is free to take them.</summary>
    public bool CanSend
    {
        get => _canSend;
        set
        {
            if (Set(ref _canSend, value)) Changed(nameof(ShowsSend));
        }
    }
    public bool ShowsSend => CanSend && !IsBusy && !IsSent;
    public string Resting => Words.Name(Kind);
}

public enum IncomingState { Asking, Receiving, Lost }

/// <summary>Something a device wants to send here, from its request to its last byte.</summary>
public sealed class IncomingModel : Observable
{
    private IncomingState _state;
    private double _progress;
    private string _status = "";

    public required string Id { get; init; }
    public required string Sender { get; init; }
    public required string What { get; init; }
    public required string Detail { get; init; }
    public long? Total { get; set; }
    public RateMeter Rate { get; } = new();
    public ICommand? Accept { get; init; }
    public ICommand? Decline { get; init; }
    public ICommand? Dismiss { get; init; }

    public IncomingState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value)) ChangedAll();
        }
    }

    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool IsAsking => State == IncomingState.Asking;
    public bool IsReceiving => State == IncomingState.Receiving;
    public bool IsLost => State == IncomingState.Lost;
    public string Headline => State switch
    {
        IncomingState.Asking => $"{Sender} wants to send you {What}",
        IncomingState.Receiving => $"{Sender} is sending you {What}",
        _ => $"The transfer from {Sender} stopped",
    };
}
