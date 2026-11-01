using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Winawdl.Model;

/// <summary>The AirDrop engine as the model sees it. Both events arrive on the
/// thread the model lives on.</summary>
public interface IEngine
{
    event Action<JsonElement> Said;
    /// <summary>The engine is not running: "no_python", "no_engine", or "ended"
    /// with the last thing it wrote.</summary>
    event Action<string, string> Stopped;

    /// <summary>Starts the engine. With contactsOnly, only the senders in
    /// knownSenders are accepted from the first connection on.</summary>
    void Start(string name, string folder, bool ask, bool contactsOnly, IReadOnlyList<string> knownSenders,
               bool senderFolders = false);
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
    ClipboardContent ReadClipboard();
    /// <summary>Copy a stored arrival as an image, safe web link, or file drop; empty on success.</summary>
    string CopyReceived(string path);
    void Open(string path);
    void Reveal(string path);
    Task<ImageSource?> Thumbnail(string path);
    /// <summary>Runs `action` once, after `delay`, on the model's thread.</summary>
    void Later(TimeSpan delay, Action action);
    /// <summary>Seconds on a clock that only goes forward.</summary>
    double Now { get; }
    /// <summary>Wall-clock time for a finished transfer.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>A clipboard snapshot; checks provide this without touching Windows' clipboard.</summary>
public sealed record ClipboardContent(IReadOnlyList<string> Files, BitmapSource? Image = null,
                                      string Text = "", string Problem = "");

/// <summary>The current user's Windows startup entry. Checks supply a stand-in.</summary>
public interface IStartup
{
    bool Enabled { get; set; }
}

/// <summary>The current user's SendTo shortcut and classic Explorer verbs.</summary>
public interface IExplorerIntegration
{
    bool Enabled { get; set; }
}

/// <summary>Transfer history persistence. Checks and renders use a stand-in.</summary>
public interface IHistoryStore
{
    IReadOnlyList<TransferRecord> Load();
    void Save(IReadOnlyList<TransferRecord> entries);
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
    /// <summary>Only new installations enter setup; existing app.json files default to finished.</summary>
    public bool SetupPending { get; set; }
    public string? Name { get; set; }
    public string? Folder { get; set; }
    public bool Ask { get; set; } = true;
    /// <summary>Ask Apple devices to preserve high-efficiency photo and video formats.</summary>
    public bool OriginalMedia { get; set; }
    public bool SenderFolders { get; set; }
    public List<KnownDevice> AlwaysAccept { get; set; } = [];
    /// <summary>Only the people in KnownSenders may send, as Apple proves them.</summary>
    public bool ContactsOnly { get; set; }
    /// <summary>Apple ID emails and phone numbers, as typed.</summary>
    public List<string> KnownSenders { get; set; } = [];
    /// <summary>A request shows on screen while the window is not in front.</summary>
    public bool ShowRequests { get; set; } = true;
    public bool KeepInTray { get; set; } = true;
    public bool TrayNoticeShown { get; set; }
    /// <summary>The radio was on when last left: it comes back on when the app starts.</summary>
    public bool RadioWanted { get; set; }
    public double[]? Window { get; set; }

    /// <summary>Called after a change, to write it down.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Action Save { get; set; } = () => { };
}
