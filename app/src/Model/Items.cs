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
    /// <summary>Set on the still of a Live Photo: opens the video stored beside it.</summary>
    public ICommand? PlayVideo { get; init; }
    public bool HasVideo => PlayVideo is not null;
    public string Hint => HasVideo ? $"{Name}, a Live Photo" : Name;
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

/// <summary>A device whose requests are accepted without asking, as settings lists it.</summary>
public sealed class TrustedModel(KnownDevice device)
{
    public KnownDevice Device { get; } = device;
    public string Name => Device.Name;
    public string Glyph => Words.Glyph(Words.KindOfDevice(Device.Model.Length > 0 ? Device.Model : null));
    public ICommand? Forget { get; init; }
    public string ForgetName => $"Forget inactive saved name {Name}";
}

/// <summary>Someone allowed to send while only known senders may: an Apple
/// ID email address or phone number, as it was typed.</summary>
public sealed class SenderModel(string address)
{
    public string Address { get; } = address;
    public ICommand? Forget { get; init; }
    public string ForgetName => $"Remove {Address} from the people who can send";
}

/// <summary>What a device sent, once it is stored: a notice that leads to it.</summary>
public sealed class ArrivalModel
{
    public required string Headline { get; init; }
    public required string Detail { get; init; }
    /// <summary>The first of the files, for its picture.</summary>
    public required FileItem File { get; init; }
    /// <summary>One file can be opened; several are shown in their folder.</summary>
    public required bool Single { get; init; }
    public ICommand? Press { get; init; }
    public ICommand? Reveal { get; init; }
    public ICommand? Dismiss { get; init; }
    public string PressLabel => Single ? "Open" : "Show in folder";
    public string PressName => Single ? $"Open {File.Name}" : "Show what arrived in its folder";
}

public enum IncomingState { Asking, Receiving, Lost }

/// <summary>Something a device wants to send here, from its request to its last byte.</summary>
public sealed class IncomingModel : Observable
{
    private IncomingState _state;
    private double _progress;
    private string _status = "";

    private bool _canAlways;

    public required string Id { get; init; }
    public required string Sender { get; init; }
    public required string What { get; init; }
    public required string Detail { get; init; }
    public string Model { get; init; } = "";
    public string Glyph => Words.Glyph(Words.KindOfDevice(Model.Length > 0 ? Model : null));
    public long? Total { get; set; }
    public RateMeter Rate { get; } = new();
    public ICommand? Accept { get; init; }
    public ICommand? Decline { get; init; }
    public ICommand? Dismiss { get; init; }
    /// <summary>Accepts this request and every later one from its device.</summary>
    public ICommand? Always { get; init; }
    public string AlwaysLabel => $"Always accept from {Sender}";

    /// <summary>The device gave a name and is not always accepted yet.</summary>
    public bool CanAlways
    {
        get => _canAlways;
        set
        {
            if (Set(ref _canAlways, value)) Changed(nameof(ShowsAlways));
        }
    }

    public bool ShowsAlways => IsAsking && CanAlways;

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
