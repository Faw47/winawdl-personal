using System.Text.Json;
using System.Windows.Media;

namespace Winawdl.Model;

/// <summary>The AirDrop engine as the model sees it. Both events arrive on the
/// thread the model lives on.</summary>
public interface IEngine
{
    event Action<JsonElement> Said;
    /// <summary>The engine is not running: "no_python", "no_engine", or "ended"
    /// with the last thing it wrote.</summary>
    event Action<string, string> Stopped;

    void Start(string name, string folder, bool ask);
    void Tell(Dictionary<string, object?> command);
    void Stop();
}

public enum RadioPhase { Off, Starting, Running, Stopping, Refused, NotBuilt, Failed }

/// <summary>The radio and AWDL daemons, which run elevated and apart from the app.</summary>
public interface IRadio
{
    RadioPhase Phase { get; }
    /// <summary>What the daemons last wrote: progress while starting, the cause after a failure.</summary>
    string LastLine { get; }
    string LogFolder { get; }
    /// <summary>The app runs from a clone of the repository, whose programs are
    /// built there, and not from an installed copy.</summary>
    bool FromSource { get; }
    event Action Changed;

    void Start();
    void Stop();
    /// <summary>The AWDL adapter came up or went away.</summary>
    void Linked(bool up);
}

/// <summary>What the model asks of Windows.</summary>
public interface IDesk
{
    IReadOnlyList<string> PickFiles();
    string? PickFolder(string from);
    IReadOnlyList<string> ClipboardFiles();
    void Open(string path);
    void Reveal(string path);
    Task<ImageSource?> Thumbnail(string path);
    /// <summary>Runs `action` once, after `delay`, on the model's thread.</summary>
    void Later(TimeSpan delay, Action action);
    /// <summary>Seconds on a clock that only goes forward.</summary>
    double Now { get; }
}

/// <summary>A device whose requests are accepted without asking. The name and
/// the model are what it says of itself: AirDrop gives a PC nothing sturdier.</summary>
public sealed class KnownDevice
{
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class AppSettings
{
    public string? Name { get; set; }
    public string? Folder { get; set; }
    public bool Ask { get; set; } = true;
    public List<KnownDevice> AlwaysAccept { get; set; } = [];
    /// <summary>A request shows on screen while the window is not in front.</summary>
    public bool ShowRequests { get; set; } = true;
    public bool KeepInTray { get; set; } = true;
    public bool TrayNoticeShown { get; set; }
    public double[]? Window { get; set; }

    /// <summary>Called after a change, to write it down.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Action Save { get; set; } = () => { };
}
