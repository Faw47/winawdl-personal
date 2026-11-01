using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Winawdl.Model;

/// <summary>What fills the far side of the table.</summary>
public enum Across { EngineDown, RadioOff, RadioStarting, RadioProblem, Nearby }

/// <summary>Everything the window shows: the devices across the table, what they
/// want to send, and the files on this side.</summary>
public sealed class TableModel : Observable, IDisposable
{
    public const string Project = "https://github.com/raph559/winawdl";
    private const int MostReceived = 24;
    public const int MostTrayText = 127;
    // Keep these in step with airdrop/sender.py. The archive also has one root member.
    public const int MaxSendFiles = 500;
    public const long MaxSendBytes = 2L * 1024 * 1024 * 1024;
    public const string SelectionLimits = "Up to 500 files · 2 GiB total. Folder contents are sent flat.";
    private static readonly TimeSpan SentShownFor = TimeSpan.FromSeconds(4);
    private static readonly double[] Turns = [-2.5, 1.5, -1, 2, -1.5];

    private readonly IEngine _engine;
    private readonly IRadio _radio;
    private readonly IDesk _desk;
    private readonly AppSettings _settings;
    private readonly IStartup _startup;
    private readonly IExplorerIntegration _explorer;
    private bool _startupEnabled, _startupAvailable;
    private string _startupError = "";
    private bool _explorerEnabled, _explorerAvailable;
    private string _explorerError = "";
    private int _sends;
    private bool _engineUp, _wantsLook, _linkUp, _looking, _beaconOn = true, _receiving, _settingsOpen;
    private bool _radioHasRun;
    private string _beaconWhy = "", _receivingProblem = "", _heldNotice = "", _radioNotice = "";
    private bool _heldNoticeIsProblem;
    private string _engineProblem = "", _engineDetail = "", _nameDraft = "", _nameError = "", _folderError = "";
    private string _linkDraft = "";
    private readonly HashSet<string> _temporaryImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _activeSends = new();
    private readonly string _imageFolder = Path.Combine(Path.GetTempPath(), "winawdl-paste-" + Guid.NewGuid().ToString("N"));
    private string _senderDraft = "", _senderError = "";
    /// <summary>While everyone may send for a while, when that ends.</summary>
    private DateTime? _everyoneUntil;
    private int _unwilling;

    public TableModel(IEngine engine, IRadio radio, IDesk desk, AppSettings settings, IStartup startup,
                      IExplorerIntegration explorer)
    {
        _engine = engine;
        _radio = radio;
        _desk = desk;
        _settings = settings;
        _startup = startup;
        _explorer = explorer;
        RefreshStartup();
        RefreshExplorer();
        _nameDraft = Name;
        _engine.Said += OnSaid;
        _engine.Stopped += OnEngineStopped;
        _radio.Changed += OnRadioChanged;

        TurnOnRadio = new Command(() => RadioOn = true);
        ChooseFiles = new Command(() => Hold(_desk.PickFiles()));
        Paste = new Command(PasteClipboard);
        ClearHeld = new Command(() =>
        {
            Held.Clear();
            HeldChanged("");
        });
        OpenSettings = new Command(() => SettingsOpen = true);
        CloseSettings = new Command(() =>
        {
            CommitName();
            SettingsOpen = false;
        });
        ChangeFolder = new Command(PickFolder);
        OpenFolder = new Command(() => _desk.Open(Folder));
        OpenLog = new Command(() => _desk.Open(_radio.LogFolder));
        OpenProject = new Command(() => _desk.Open(Project));
        StartEngine = new Command(StartTheEngine);
        PanelAction = new Command(RunPanelAction);
        foreach (var device in _settings.AlwaysAccept) Trusted.Add(Listed(device));
        AddSender = new Command(AddTheSender);
        TurnOff = new Command(() => RadioOn = false);
        FindByContacts = new Command(() => FindBy(() => OnlyKnownSenders = true));
        FindByEveryoneForAWhile = new Command(() => FindBy(() => EveryoneForAWhile = true));
        FindByEveryone = new Command(() => FindBy(() => AnyoneCanSend = true));
        ShowSenders();
    }

    public ObservableCollection<PeerModel> Peers { get; } = [];
    public ObservableCollection<IncomingModel> Incoming { get; } = [];
    public ObservableCollection<TrustedModel> Trusted { get; } = [];
    public ObservableCollection<SenderModel> KnownSenders { get; } = [];
    public ObservableCollection<FileItem> Held { get; } = [];
    public ObservableCollection<FileItem> Received { get; } = [];
    public ObservableCollection<string> Notices { get; } = [];

    public ICommand TurnOnRadio { get; }
    public ICommand ChooseFiles { get; }
    public ICommand Paste { get; }
    public ICommand ClearHeld { get; }
    public ICommand OpenSettings { get; }
    public ICommand CloseSettings { get; }
    public ICommand ChangeFolder { get; }
    public ICommand OpenFolder { get; }
    public ICommand OpenLog { get; }
    public ICommand OpenProject { get; }
    public ICommand StartEngine { get; }
    public ICommand AddSender { get; }
    public ICommand TurnOff { get; }
    public ICommand FindByContacts { get; }
    public ICommand FindByEveryoneForAWhile { get; }
    public ICommand FindByEveryone { get; }
    public ICommand PanelAction { get; }

    /// <summary>Something wants an answer; the window may be out of sight.</summary>
    public event Action<string>? Attention;
    /// <summary>Files have just been stored.</summary>
    public event Action? Landed;
    /// <summary>What a device sent has just been stored; the window may be out of sight.</summary>
    public event Action<ArrivalModel>? Arrived;

    public string Version { get; } =
        "winawdl " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "");

    public string Name => _settings.Name ?? Environment.MachineName;
    public string Folder => _settings.Folder ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public void StartTheEngine()
    {
        _engineProblem = "";
        if (!Directory.Exists(Folder)) _settings.Folder = null;
        ChangedAll();
        _engine.Start(Name, Folder, _settings.Ask, _settings.ContactsOnly, _settings.KnownSenders,
                      _settings.SenderFolders);
    }

    /// <summary>The window came into view or went out of it. Devices are only
    /// looked for while someone can see them.</summary>
    public void SetVisible(bool visible)
    {
        _wantsLook = visible;
        if (_engineUp) _engine.Tell(new() { ["do"] = "look", ["on"] = visible });
    }

    public Across Across
    {
        get
        {
            if (_engineProblem.Length > 0) return Across.EngineDown;
            if (_linkUp) return Across.Nearby;
            return _radio.Phase switch
            {
                RadioPhase.Starting => Across.RadioStarting,
                RadioPhase.Refused or RadioPhase.NotBuilt or RadioPhase.Failed => Across.RadioProblem,
                _ => Across.RadioOff,
            };
        }
    }

    public bool ShowsPanel => Across != Across.Nearby;
    public bool ShowsNearby => Across == Across.Nearby;
    public bool ShowsPeers => ShowsNearby && Peers.Count > 0;
    public bool ShowsNobody => ShowsNearby && Peers.Count == 0;

    public string PanelTitle => Across switch
    {
        Across.EngineDown => _engineProblem,
        Across.RadioStarting => "Turning on AirDrop",
        Across.RadioProblem => _radio.Phase switch
        {
            RadioPhase.Refused => "AirDrop stayed off",
            RadioPhase.NotBuilt => _radio.FromSource
                ? "AirDrop has not been built yet"
                : "AirDrop's programs are missing",
            _ => _radioHasRun ? "AirDrop stopped" : "AirDrop did not start",
        },
        _ => _radio.Phase == RadioPhase.Stopping ? "Turning off AirDrop" : "AirDrop is off",
    };

    public string PanelBody => Across switch
    {
        Across.EngineDown => _engineDetail,
        Across.RadioStarting => _radio.LastLine.Length > 0
            ? _radio.LastLine
            : "The Wi-Fi card is being set up for AirDrop.",
        Across.RadioProblem => _radio.Phase switch
        {
            RadioPhase.Refused => "Windows asked for permission and the answer was no.",
            RadioPhase.NotBuilt => _radio.FromSource
                ? "Run scripts\\build-radio.ps1 and scripts\\build-awdl.ps1 in the repository, then try again."
                : "This copy of winawdl is incomplete. Install it again.",
            _ => _radio.LastLine.Length > 0 ? _radio.LastLine : "It left nothing in its log.",
        },
        _ => _radio.Phase == RadioPhase.Stopping
            ? ""
            : "Turn it on to see nearby iPhones, iPads and Macs, and to be seen by them. "
              + "Windows asks for permission first.",
    };

    public string PanelActionLabel => Across switch
    {
        Across.EngineDown => "Start Again",
        Across.RadioOff => _radio.Phase == RadioPhase.Stopping ? "" : "Turn On AirDrop",
        Across.RadioProblem => _radio.Phase == RadioPhase.Refused ? "Turn On AirDrop" : "Try Again",
        _ => "",
    };

    public bool PanelHasAction => PanelActionLabel.Length > 0;
    /// <summary>The press leads to a Windows permission prompt, which Windows marks with a shield.</summary>
    public bool PanelAsksWindows => PanelHasAction && Across != Across.EngineDown;
    public bool PanelHasLog => Across == Across.RadioProblem && _radio.Phase == RadioPhase.Failed;

    private void RunPanelAction()
    {
        if (Across == Across.EngineDown) StartTheEngine();
        else RadioOn = true;
    }

    public string NobodyTitle => _looking ? "No one nearby yet" : "Getting ready to look";
    // A device that will not answer has its own notice below, which says what to
    // change on it; the general advice would only repeat it.
    public string NobodyBody => _looking && _unwilling > 0
        ? "Keep the other device's screen on."
        : _looking
        ? "Set AirDrop to Everyone for 10 Minutes on the device (Settings, General, AirDrop) and keep "
          + "its screen on."
        : "Nearby devices are woken over Bluetooth first.";

    /// <summary>The radio switch. It also reads as on while the radio starts.</summary>
    public bool RadioOn
    {
        get => _linkUp || _radio.Phase is RadioPhase.Starting or RadioPhase.Running;
        set
        {
            _radioNotice = "";
            if (value && !RadioOn) _radio.Start();
            else if (!value && _radio.Phase is RadioPhase.Starting or RadioPhase.Running) _radio.Stop();
            else if (!value && _linkUp)
                _radioNotice = "The radio was started from a terminal. Stop it there with Ctrl+C.";
            Refresh();
        }
    }

    // Who can find this PC, as AirDrop's own setting puts it: off, contacts,
    // everyone for ten minutes, everyone. Choosing one also turns the radio on.

    public bool FindMeOff => !RadioOn;
    public bool FindMeContacts => RadioOn && OnlyKnownSenders;
    public bool FindMeForAWhile => RadioOn && EveryoneForAWhile;
    public bool FindMeEveryone => RadioOn && AnyoneCanSend;

    public string FindMeLabel => !RadioOn ? "Receiving Off"
        : EveryoneForAWhile ? "Everyone for 10 Minutes"
        : OnlyKnownSenders ? "Contacts Only"
        : "Everyone";

    public string FindMeName => $"Who can find this PC: {FindMeLabel}";

    private void FindBy(Action choose)
    {
        choose();
        if (!RadioOn) RadioOn = true;
        ChangedAll();
    }

    public string Presence => _linkUp && _receiving ? $"Visible as {Name}"
        : _linkUp ? "Not visible yet"
        : "Not visible";

    private void RefreshNotices()
    {
        var notices = new List<string>();
        if (_radioNotice.Length > 0) notices.Add(_radioNotice);
        if (_linkUp && !_receiving && _receivingProblem == "port_held")
            notices.Add("Another program is already receiving AirDrop on this PC. Close it "
                        + "(scripts\\airdrop-receive.ps1 in a terminal) and winawdl takes over.");
        else if (_linkUp && !_receiving && _receivingProblem.Length > 0)
            notices.Add($"Receiving could not start: {_receivingProblem}");
        if (_looking && !_beaconOn)
            notices.Add("Bluetooth could not wake nearby devices"
                        + (_beaconWhy.Length > 0 ? $" ({_beaconWhy})" : "")
                        + ". A device shows up here while its own share sheet is open.");
        // Said of what was seen: one that keeps its name to itself, or would not
        // have this PC's certificate. Contacts Only is the likely cause of both.
        if (_unwilling > 0)
            notices.Add(_unwilling == 1
                ? "A device nearby only answers devices it knows. Set its AirDrop to Everyone for 10 Minutes."
                : $"{_unwilling} devices nearby only answer devices they know. Set their AirDrop to "
                  + "Everyone for 10 Minutes.");
        if (!notices.SequenceEqual(Notices))
        {
            Notices.Clear();
            foreach (var notice in notices) Notices.Add(notice);
        }
    }

    private void Refresh()
    {
        RefreshNotices();
        foreach (var peer in Peers) peer.CanSend = Held.Count > 0 || ValidLink(LinkDraft);
        ChangedAll();
    }

    private void OnRadioChanged()
    {
        if (_radio.Phase == RadioPhase.Running) _radioHasRun = true;
        if (_radio.Phase == RadioPhase.Starting) _radioHasRun = false;
        Refresh();
    }

    private void OnEngineStopped(string why, string detail)
    {
        _engineUp = false;
        _linkUp = _looking = _receiving = false;
        Peers.Clear();
        _activeSends.Clear();
        CollectImages();
        Incoming.Clear();
        (_engineProblem, _engineDetail) = why switch
        {
            "no_python" => ("Python was not found",
                            "The part of winawdl that speaks AirDrop runs on Python 3.10 or newer. "
                            + "Install it from python.org, then start it again."),
            "no_engine" => ("winawdl's files are incomplete",
                            "The airdrop folder of the repository was not found above the folder "
                            + "this app runs from."),
            "no_certificate" => ("No certificate could be made", detail),
            _ => ("The AirDrop engine stopped",
                  detail.Length > 0 ? detail : "It ended without saying why."),
        };
        Refresh();
    }

    private static string Text(JsonElement said, string name) =>
        said.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long? Number(JsonElement said, string name) =>
        said.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    private static bool Flag(JsonElement said, string name) =>
        said.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private void OnSaid(JsonElement said)
    {
        switch (Text(said, "event"))
        {
            case "ready":
                _engineUp = true;
                AdoptSettings(said);
                _engine.Tell(new() { ["do"] = "set", ["original_media"] = _settings.OriginalMedia });
                _engine.Tell(new() { ["do"] = "look", ["on"] = _wantsLook });
                break;
            case "link":
                _linkUp = Flag(said, "up");
                _radio.Linked(_linkUp);
                if (!_linkUp)
                {
                    Peers.Clear();
                    Incoming.Clear();
                    _looking = _receiving = false;
                    _unwilling = 0;
                }
                break;
            case "receiving":
                _receiving = Flag(said, "on");
                _receivingProblem = Text(said, "reason");
                break;
            case "looking":
                _looking = Flag(said, "on");
                _beaconOn = !_looking || Flag(said, "beacon") || Text(said, "why") == "test receivers";
                _beaconWhy = Text(said, "why");
                break;
            case "unwilling":
                _unwilling = (int)(Number(said, "count") ?? 0);
                break;
            case "peer":
                AddPeer(Text(said, "id"), Text(said, "name"), Text(said, "model"));
                break;
            case "peer_gone":
                PeerGone(Text(said, "id"));
                break;
            case "asked":
                Asked(said);
                break;
            case "answered":
                Answered(Text(said, "id"), Text(said, "how"));
                break;
            case "incoming":
                Arriving(Text(said, "id"), Number(said, "done") ?? 0, Number(said, "total"));
                break;
            case "stored":
                Stored(said);
                break;
            case "lost":
                if (Find(Text(said, "id")) is { } lost)
                {
                    lost.State = IncomingState.Lost;
                    lost.Progress = 0;
                    lost.Status = "Nothing was kept. Ask for it to be sent again.";
                }
                break;
            case "send":
                Sent(said);
                break;
            case "settings":
                SettingsSaid(said);
                break;
            case "fatal":
                OnEngineStopped(Text(said, "reason"), Text(said, "detail"));
                return;
        }
        Refresh();
    }

    private void AdoptSettings(JsonElement said)
    {
        _settings.Name = Text(said, "name");
        _settings.Folder = Text(said, "folder");
        _settings.Ask = Flag(said, "ask");
        if (said.TryGetProperty("sender_folders", out var separate))
            _settings.SenderFolders = separate.GetBoolean();
        AdoptSenders(said);
        _settings.Save();
        _nameDraft = Name;
    }

    /// <summary>Who may send, as the engine says it has it: the engine keeps
    /// the old list when it refuses a new one.</summary>
    private void AdoptSenders(JsonElement said)
    {
        _everyoneUntil = said.TryGetProperty("everyone_until", out var until) && until.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds((long) (until.GetDouble() * 1000)).LocalDateTime
            : null;
        // While everyone may send for a while the engine says it takes
        // everyone, but what is kept is the list it goes back to: an app
        // that ends in the meantime starts again with the list.
        if (_everyoneUntil is null && said.TryGetProperty("contacts_only", out var only)
            && only.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _settings.ContactsOnly = only.GetBoolean();
        if (!said.TryGetProperty("known_senders", out var senders) || senders.ValueKind != JsonValueKind.Array)
            return;
        var listed = senders.EnumerateArray()
            .Where(sender => sender.ValueKind == JsonValueKind.String)
            .Select(sender => sender.GetString() ?? "").ToList();
        if (listed.SequenceEqual(_settings.KnownSenders)) return;
        _settings.KnownSenders = listed;
        ShowSenders();
    }

    private void ShowSenders()
    {
        KnownSenders.Clear();
        foreach (var address in _settings.KnownSenders)
            KnownSenders.Add(new SenderModel(address) { Forget = new Command(() => ForgetSender(address)) });
    }

    /// <summary>An identifier as Apple hashes it, for comparing two of them:
    /// an email address lowercased, a phone number as its digits. Empty for
    /// something that is neither.</summary>
    private static string Identifier(string text)
    {
        text = text.Trim();
        if (text.Contains('@'))
        {
            var at = text.IndexOf('@');
            return at > 0 && at < text.Length - 1 && !text.Any(char.IsWhiteSpace) ? text.ToLowerInvariant() : "";
        }
        // A phone number may be written with spaces, dashes, dots, brackets
        // and a leading plus, and nothing else.
        if (text.Any(c => !char.IsAsciiDigit(c) && " +-.()".IndexOf(c) < 0)) return "";
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 5 ? digits : "";
    }

    private void AddTheSender()
    {
        var wanted = SenderDraft.Trim();
        var identifier = Identifier(wanted);
        if (identifier.Length == 0)
        {
            _senderError = "Type an Apple ID email address or phone number.";
        }
        else if (_settings.KnownSenders.Any(listed => Identifier(listed) == identifier))
        {
            _senderError = "That one is on the list already.";
        }
        else
        {
            _senderError = "";
            _senderDraft = "";
            TellSenders([.. _settings.KnownSenders, wanted]);
        }
        ChangedAll();
    }

    private void ForgetSender(string address) =>
        TellSenders(_settings.KnownSenders.Where(listed => listed != address).ToList());

    private void TellSenders(List<string> senders)
    {
        _settings.KnownSenders = senders;
        _settings.Save();
        ShowSenders();
        _engine.Tell(new() { ["do"] = "set", ["known_senders"] = senders });
        ChangedAll();
    }

    private void AddPeer(string id, string name, string model)
    {
        if (Peers.FirstOrDefault(known => known.Id == id) is { } known)
        {
            // Announced again while a transfer to it runs: its plate carries on, and stays.
            if (known.IsBusy)
            {
                known.Left = false;
                return;
            }
            Peers.Remove(known);
        }
        PeerModel? peer = null;
        peer = new PeerModel(id, name, model.Length > 0 ? model : null)
        {
            Press = new Command(() => SendTo(peer!)),
            Stop = new Command(() => _engine.Tell(new() { ["do"] = "cancel", ["id"] = peer!.SendId })),
        };
        peer.Status = peer.Resting;
        Peers.Add(peer);
    }

    private void PeerGone(string id)
    {
        if (Peers.FirstOrDefault(peer => peer.Id == id) is not { } peer) return;
        if (peer.IsBusy) peer.Left = true;
        else Peers.Remove(peer);
    }

    private static bool ValidLink(string url) =>
        url.Length > 0 && Encoding.UTF8.GetByteCount(url) <= 8192
            && (url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal))
            && !url.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)
                || char.GetUnicodeCategory(ch) is UnicodeCategory.Format or UnicodeCategory.Surrogate)
            && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && parsed.Host.Length > 0
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    public void SendTo(PeerModel peer, IEnumerable<string>? dropped = null)
    {
        if (peer.IsBusy) return;
        if (dropped is not null && !Hold(dropped)) return;
        var link = dropped is null && Held.Count == 0 ? LinkDraft : "";
        if (link.Length > 0 && !ValidLink(link))
        {
            HeldChanged("Enter a valid HTTP or HTTPS link.", problem: true);
            return;
        }
        if (link.Length == 0 && dropped is null && Held.Count == 0) Hold(_desk.PickFiles());
        if (link.Length == 0 && Held.Count == 0) return;
        peer.SendId = $"s{++_sends}";
        peer.Sending = link.Length > 0 ? [] : Held.Select(file => file.Path).ToList();
        peer.SendingLink = link;
        _activeSends[peer.SendId] = peer.Sending;
        peer.Progress = 0;
        peer.State = PeerState.Asking;
        peer.Status = "Accept on the device to start";
        _engine.Tell(new()
        {
            ["do"] = "send", ["id"] = peer.SendId, ["peer"] = peer.Id,
            ["files"] = peer.Sending,
            ["urls"] = link.Length > 0 ? new[] { link } : Array.Empty<string>(),
        });
        Refresh();
    }

    private void Sent(JsonElement said)
    {
        var id = Text(said, "id");
        if (Text(said, "state") is "sent" or "declined" or "refused" or "cancelled" or "failed")
            _activeSends.Remove(id);
        if (Peers.FirstOrDefault(known => known.SendId == id) is not { } peer)
        {
            CollectImages();
            return;
        }
        var total = Number(said, "total") ?? 0;
        var done = Number(said, "done") ?? 0;
        switch (Text(said, "state"))
        {
            case "asking":
                break;
            case "sending":
                peer.State = PeerState.Sending;
                peer.Rate.Note(_desk.Now, done);
                peer.Progress = total > 0 ? (double)done / total : 0;
                peer.Status = $"{Words.Percent(peer.Progress)} of {Words.Size(total)}"
                              + (peer.Rate.BytesPerSecond > 0 ? $" · {Words.Rate(peer.Rate.BytesPerSecond)}" : "");
                break;
            case "finishing":
                peer.State = PeerState.Finishing;
                peer.Progress = 1;
                peer.Status = "Waiting for the device to confirm";
                break;
            case "sent":
                peer.State = PeerState.Sent;
                peer.Progress = 0;
                peer.Status = peer.SendingLink.Length > 0
                    ? "Sent link"
                    : $"Sent {Words.Count(peer.Sending.Count, "file", "files")}";
                if (peer.SendingLink.Length > 0)
                {
                    if (LinkDraft == peer.SendingLink) LinkDraft = "";
                }
                else if (peer.Sending.SequenceEqual(Held.Select(file => file.Path)))
                {
                    Held.Clear();
                    HeldChanged("");
                }
                _desk.Later(SentShownFor, () =>
                {
                    if (peer.State == PeerState.Sent) Rest(peer);
                });
                break;
            case "declined":
                Stopped(peer, PeerState.Declined, "Declined on the device");
                break;
            case "refused":
                Stopped(peer, PeerState.Failed, "The device did not take the files");
                break;
            case "cancelled":
                Rest(peer);
                break;
            case "failed":
                Stopped(peer, PeerState.Failed, Text(said, "reason") switch
                {
                    "no_answer" => "No answer. Check that its screen is still on, then send again.",
                    "unreachable" => "It could not be reached. Keep its screen on and send again.",
                    "gone" => "It is no longer nearby.",
                    "not_a_file" => "One of these is no longer a file. Remove it and send again.",
                    "same_name" => "Two of the files have the same name. Remove one and send again.",
                    "limit" => "Send at most 500 files and 2 GiB at once. Remove some files and send again.",
                    "unreadable" => "One of the files could not be read. Remove it and send again.",
                    _ => "The connection broke partway. Send again.",
                });
                break;
        }
        CollectImages();
    }

    private void Stopped(PeerModel peer, PeerState state, string status)
    {
        peer.State = state;
        peer.Progress = 0;
        peer.Status = status;
        if (peer.Left) Peers.Remove(peer);
    }

    private void Rest(PeerModel peer)
    {
        peer.State = PeerState.Idle;
        peer.Progress = 0;
        peer.Status = peer.Resting;
        if (peer.Left) Peers.Remove(peer);
        Refresh();
    }

    private IncomingModel? Find(string id) => Incoming.FirstOrDefault(item => item.Id == id);

    private void Asked(JsonElement said)
    {
        var id = Text(said, "id");
        var names = new List<string>();
        if (said.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            names.AddRange(files.EnumerateArray().Select(file => Text(file, "name")));
        var links = (int)(Number(said, "links") ?? 0);
        var total = Number(said, "total");
        var sender = Text(said, "sender");
        var model = Text(said, "model");
        // The sender controls these strings; they cannot authenticate a device.
        // Until there is cryptographic pairing, every Ask needs its own consent.
        var known = false;
        var waits = Flag(said, "waits") && !known;
        var detail = names.Count == 1 ? Words.Title(names[0]) : "";
        if (total is > 0) detail += (detail.Length > 0 ? " · " : "") + Words.Size(total.Value);
        var item = new IncomingModel
        {
            Id = id,
            Sender = sender.Length > 0 ? sender : "A device nearby",
            Model = model,
            What = Words.Things(names, links),
            Detail = detail,
            Total = total,
            Accept = new Command(() => Answer(id, true)),
            Decline = new Command(() => Answer(id, false)),
            Always = new Command(() =>
            {
                // No name-only persistent approval: it is trivially spoofable.
                Answer(id, true);
            }),
            Dismiss = new Command(() =>
            {
                if (Find(id) is { } shown) Incoming.Remove(shown);
            }),
            CanAlways = false,
            State = waits ? IncomingState.Asking : IncomingState.Receiving,
            Status = waits ? detail : "Waiting for the files",
        };
        Incoming.Insert(0, item);
        // The engine holds the request of a device that is always accepted, as it does any other.
        if (known && Flag(said, "waits")) Answer(id, true);
        if (waits) Attention?.Invoke(item.Headline);
    }

    private void Answer(string id, bool accept) =>
        _engine.Tell(new() { ["do"] = "answer", ["id"] = id, ["accept"] = accept });

    private bool IsTrusted(string name, string model) =>
        _settings.AlwaysAccept.Any(device => device.Name == name && device.Model == model);

    private TrustedModel Listed(KnownDevice device)
    {
        TrustedModel? listed = null;
        listed = new TrustedModel(device)
        {
            Forget = new Command(() =>
            {
                _settings.AlwaysAccept.Remove(device);
                _settings.Save();
                Trusted.Remove(listed!);
                Refresh();
            }),
        };
        return listed;
    }

    private void Trust(string name, string model)
    {
        if (name.Length == 0 || IsTrusted(name, model)) return;
        var device = new KnownDevice { Name = name, Model = model };
        _settings.AlwaysAccept.Add(device);
        _settings.Save();
        Trusted.Add(Listed(device));
        // Its other requests that wait no longer need the choice.
        foreach (var waiting in Incoming.Where(waiting => waiting.Sender == name && waiting.Model == model))
            waiting.CanAlways = false;
        Refresh();
    }

    public bool HasTrusted => Trusted.Count > 0;
    /// <summary>A request waits for an answer.</summary>
    public bool HasRequests => Incoming.Any(item => item.IsAsking);

    private void Answered(string id, string how)
    {
        if (Find(id) is not { } item) return;
        if (how != "accepted")
        {
            Incoming.Remove(item);
            return;
        }
        item.State = IncomingState.Receiving;
        item.Status = "Waiting for the files";
    }

    private void Arriving(string id, long done, long? total)
    {
        if (Find(id) is not { } item) return;
        item.Total = total ?? item.Total;
        item.Rate.Note(_desk.Now, done);
        if (item.Total is not > 0)
        {
            item.Status = Words.Size(done) + " so far";
            return;
        }
        item.Progress = (double)done / item.Total.Value;
        item.Status = done >= item.Total
            ? "Saving"
            : $"{Words.Percent(item.Progress)} of {Words.Size(item.Total.Value)}"
              + (item.Rate.BytesPerSecond > 0 ? $" · {Words.Rate(item.Rate.BytesPerSecond)}" : "");
    }

    private void Stored(JsonElement said)
    {
        var item = Find(Text(said, "id"));
        if (item is not null) Incoming.Remove(item);
        var folder = Text(said, "folder");
        if (!said.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array) return;
        // A Live Photo is stored as a still and a video, and is one thing here: its still.
        var videos = new Dictionary<string, string>();
        if (said.TryGetProperty("live", out var live) && live.ValueKind == JsonValueKind.Array)
            foreach (var pair in live.EnumerateArray())
                if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2)
                    videos[pair[0].GetString() ?? ""] = pair[1].GetString() ?? "";
        var landed = new List<FileItem>();
        foreach (var name in names.EnumerateArray().Select(name => name.GetString() ?? "").Reverse())
        {
            if (videos.ContainsValue(name)) continue;
            var path = Path.Combine(folder, name);
            var video = videos.TryGetValue(name, out var beside) ? Path.Combine(folder, beside) : null;
            landed.Insert(0, new FileItem(path, _desk)
            {
                Turn = Turns[Received.Count % Turns.Length],
                Press = new Command(() => _desk.Open(path)),
                Reveal = new Command(() => _desk.Reveal(path)),
                PlayVideo = video is null ? null : new Command(() => _desk.Open(video)),
            });
            Received.Insert(0, landed[0]);
        }
        while (Received.Count > MostReceived) Received.RemoveAt(Received.Count - 1);
        Landed?.Invoke();
        if (item is not null && landed.Count > 0) Arrived?.Invoke(Arrival(item, landed));
    }

    private ArrivalModel Arrival(IncomingModel from, List<FileItem> files)
    {
        var first = files[0];
        var size = Words.Size(files.Sum(file => file.Bytes));
        ArrivalModel? arrival = null;
        void Leave() => Arrivals.Remove(arrival!);
        arrival = new ArrivalModel
        {
            Headline = $"{from.Sender} sent you {from.What}",
            Detail = files.Count == 1 ? $"{first.Name} · {size}" : size,
            File = first,
            Single = files.Count == 1,
            Press = new Command(() =>
            {
                if (files.Count == 1) _desk.Open(first.Path);
                else _desk.Reveal(first.Path);
                Leave();
            }),
            Reveal = new Command(() =>
            {
                _desk.Reveal(first.Path);
                Leave();
            }),
            Dismiss = new Command(Leave),
        };
        return arrival;
    }

    /// <summary>What arrived and is being said on screen, outside the window.</summary>
    public ObservableCollection<ArrivalModel> Arrivals { get; } = [];

    /// <summary>The line the notification area shows under the pointer: what
    /// is moving and how far along, or how visible this PC is.</summary>
    public string TrayText
    {
        get
        {
            var lines = new List<string>();
            foreach (var item in Incoming)
                lines.Add(item.IsReceiving && item.Status.Length > 0 ? $"{item.Headline}: {item.Status}" : item.Headline);
            foreach (var peer in Peers.Where(peer => peer.IsBusy || peer.IsSent))
                lines.Add(peer.State switch
                {
                    PeerState.Asking => $"Waiting for {peer.Name} to accept",
                    PeerState.Sent => $"{peer.Status} to {peer.Name}",
                    _ => $"Sending to {peer.Name}: {peer.Status}",
                });
            if (lines.Count == 0) lines.Add(Presence);
            var text = "winawdl\n" + string.Join("\n", lines);
            // Windows cuts what it shows there at 127 characters.
            return text.Length <= MostTrayText ? text : text[..(MostTrayText - 1)] + "…";
        }
    }

    /// <summary>Choosing files is the next thing to do: devices can be seen and
    /// nothing else is waiting for an answer.</summary>
    public bool ChooseIsNext => Across == Across.Nearby && !Incoming.Any(item => item.IsAsking);
    public bool TableInReach => !SettingsOpen;

    public string LinkDraft
    {
        get => _linkDraft;
        set { if (Set(ref _linkDraft, value)) Refresh(); }
    }

    public bool HasHeld => Held.Count > 0;
    public bool HoldsNothing => Held.Count == 0;
    public bool HasReceived => Received.Count > 0;
    public string HeldNotice => _heldNotice;
    public bool HeldNoticeIsProblem => _heldNoticeIsProblem;
    public bool HasHeldNotice => _heldNotice.Length > 0;
    public string HeldSummary =>
        $"{Words.Count(Held.Count, "file", "files")} · {Words.Size(Held.Sum(file => file.Bytes))}";
    public string HeldHint => Peers.Count > 0
        ? "Press a device above to send them"
        : "They go to the device you press, once one shows up";

    /// <summary>Expand folders without following reparse points. Distinct files
    /// with the same basename keep the first, as the flat archive requires.</summary>
    public bool Hold(IEnumerable<string> paths)
    {
        bool folders = false, links = false, containsHeld = false;
        int clashes = 0;
        var added = new List<string>();
        var seen = new HashSet<string>(Held.Select(file => file.Path), StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(Held.Select(file => file.Name), StringComparer.OrdinalIgnoreCase);
        var bytes = Held.Sum(file => file.Bytes);
        IEnumerable<string> Expand(IEnumerable<string> selected)
        {
            var pending = new Stack<string>(selected.Reverse());
            while (pending.TryPop(out var selectedPath))
            {
                var path = Path.GetFullPath(selectedPath);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    links = true;
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    folders = true;
                    foreach (var child in Directory.GetFileSystemEntries(path)
                                 .OrderByDescending(item => item, StringComparer.OrdinalIgnoreCase)
                                 .ThenByDescending(item => item, StringComparer.Ordinal))
                        pending.Push(child);
                }
                else yield return path;
            }
        }
        try
        {
            foreach (var path in Expand(paths))
            {
                if (!seen.Add(path))
                {
                    containsHeld |= Held.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));
                    continue;
                }
                if (!names.Add(Path.GetFileName(path)))
                {
                    clashes++;
                    continue;
                }
                var size = new FileInfo(path).Length;
                if (!FitsSelection(Held.Count + added.Count, bytes, size))
                {
                    HeldChanged("Nothing was added. Send at most 500 files and 2 GiB at once.", problem: true);
                    return false;
                }
                bytes += size;
                added.Add(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            HeldChanged("Nothing was added. One of these items could not be read. Check it and try again.", problem: true);
            return false;
        }
        foreach (var path in added)
        {
            FileItem? item = null;
            item = new FileItem(path, _desk)
            {
                Turn = Turns[Held.Count % Turns.Length],
                Remove = new Command(() =>
                {
                    Held.Remove(item!);
                    HeldChanged("");
                }),
            };
            Held.Add(item);
        }
        var notices = new List<string>();
        if (folders) notices.Add("Folder contents are sent flat; subfolders are not kept.");
        if (links) notices.Add("Links and junctions were left out.");
        if (clashes > 0) notices.Add("Two files with the same name cannot go together. The second was left out.");
        if (added.Count == 0 && notices.Count == 0) notices.Add("No new files to add.");
        HeldChanged(string.Join(" ", notices), problem: clashes > 0);
        return added.Count > 0 || containsHeld;
    }

    internal static bool FitsSelection(int currentCount, long currentBytes, long nextBytes) =>
        currentCount < MaxSendFiles && nextBytes >= 0 && nextBytes <= MaxSendBytes - currentBytes;

    private void HeldChanged(string notice, bool problem = false)
    {
        _heldNotice = notice;
        _heldNoticeIsProblem = problem;
        CollectImages();
        Refresh();
    }

    private void PasteClipboard()
    {
        var content = _desk.ReadClipboard();
        if (content.Problem.Length > 0) { HeldChanged(content.Problem, problem: true); return; }
        if (content.Files.Count > 0) { Hold(content.Files); return; }
        if (content.Image is { } image)
        {
            try
            {
                Directory.CreateDirectory(_imageFolder);
                var path = Path.Combine(_imageFolder, "Clipboard-" + Guid.NewGuid().ToString("N")[..8] + ".png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                    _temporaryImages.Add(path);
                    encoder.Save(output);
                }
                Hold([path]);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                           or InvalidOperationException or NotSupportedException)
            {
                HeldChanged("The clipboard image could not be saved. Copy it again and try Paste.", problem: true);
            }
            CollectImages();
            return;
        }
        if (ValidLink(content.Text))
        {
            LinkDraft = content.Text;
            HeldChanged(HasHeld ? "Link added. Clear the files to send the link." : "Link added. Press a device to send it.");
            return;
        }
        HeldChanged("Copy files, an image, or a valid HTTP or HTTPS link, then try Paste.", problem: true);
    }

    private void CollectImages()
    {
        var needed = new HashSet<string>(Held.Select(file => file.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var paths in _activeSends.Values) needed.UnionWith(paths);
        foreach (var path in _temporaryImages.Where(path => !needed.Contains(path)).ToArray())
        {
            try { File.Delete(path); _temporaryImages.Remove(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (_temporaryImages.Count == 0)
        {
            try { if (Directory.Exists(_imageFolder)) Directory.Delete(_imageFolder); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>After the engine stops, release only images this model created.</summary>
    public void Dispose()
    {
        Held.Clear();
        _activeSends.Clear();
        CollectImages();
    }

    public bool SettingsOpen
    {
        get => _settingsOpen;
        set
        {
            if (!Set(ref _settingsOpen, value)) return;
            if (value)
            {
                _nameDraft = Name;
                _nameError = _folderError = "";
                RefreshStartup();
                RefreshExplorer();
            }
            ChangedAll();
        }
    }

    public string NameDraft { get => _nameDraft; set => Set(ref _nameDraft, value); }
    public string NameError => _nameError;
    public bool HasNameError => _nameError.Length > 0;
    public string FolderError => _folderError;
    public bool HasFolderError => _folderError.Length > 0;

    public bool AskFirst
    {
        get => _settings.Ask;
        set
        {
            if (_settings.Ask == value) return;
            _settings.Ask = value;
            _settings.Save();
            _engine.Tell(new() { ["do"] = "set", ["ask"] = value });
            ChangedAll();
        }
    }

    public bool TakeAll { get => !AskFirst; set => AskFirst = !value; }

    public bool SenderFolders
    {
        get => _settings.SenderFolders;
        set
        {
            if (_settings.SenderFolders == value) return;
            _settings.SenderFolders = value;
            _settings.Save();
            if (_engineUp)
                _engine.Tell(new() { ["do"] = "set", ["sender_folders"] = value });
            Changed();
        }
    }

    public bool OriginalMedia
    {
        get => _settings.OriginalMedia;
        set
        {
            if (_settings.OriginalMedia == value) return;
            _settings.OriginalMedia = value;
            _settings.Save();
            if (_engineUp)
                _engine.Tell(new() { ["do"] = "set", ["original_media"] = value });
            Changed();
        }
    }

    // Each of the three choices acts only when it is chosen: a radio button
    // that is let go of writes false, which says nothing.

    /// <summary>Only the people on the list may send, as Apple proves them.</summary>
    public bool OnlyKnownSenders
    {
        get => _settings.ContactsOnly && _everyoneUntil is null;
        set { if (value) ChooseSenders(contactsOnly: true); }
    }

    public bool AnyoneCanSend
    {
        get => !_settings.ContactsOnly && _everyoneUntil is null;
        set { if (value) ChooseSenders(contactsOnly: false); }
    }

    /// <summary>Everyone may send for ten minutes, then only the people on
    /// the list again, as an iPhone's "Everyone for 10 Minutes" does.</summary>
    public bool EveryoneForAWhile
    {
        get => _everyoneUntil is not null;
        set
        {
            if (!value || _everyoneUntil is not null) return;
            _settings.ContactsOnly = true;
            _settings.Save();
            _everyoneUntil = DateTime.Now.AddSeconds(EveryoneSeconds);
            _engine.Tell(new() { ["do"] = "set", ["everyone_for"] = EveryoneSeconds });
            ChangedAll();
        }
    }

    public const int EveryoneSeconds = 10 * 60;
    public string EveryoneUntil => _everyoneUntil is { } until
        ? $"Until {until:t}, then only the people listed here."
        : "Then only the people listed here.";

    private void ChooseSenders(bool contactsOnly)
    {
        if (_settings.ContactsOnly == contactsOnly && _everyoneUntil is null) return;
        _settings.ContactsOnly = contactsOnly;
        _everyoneUntil = null;
        _settings.Save();
        _engine.Tell(new() { ["do"] = "set", ["contacts_only"] = contactsOnly });
        ChangedAll();
    }
    public bool HasKnownSenders => KnownSenders.Count > 0;
    /// <summary>The list is in force and empty, so nobody can send.</summary>
    public bool NobodyCanSend => OnlyKnownSenders && !HasKnownSenders;
    public string SenderDraft { get => _senderDraft; set => Set(ref _senderDraft, value); }
    public string SenderError => _senderError;
    public bool HasSenderError => _senderError.Length > 0;

    public bool ShowRequests
    {
        get => _settings.ShowRequests;
        set
        {
            _settings.ShowRequests = value;
            _settings.Save();
            Changed();
        }
    }

    public bool KeepInTray
    {
        get => _settings.KeepInTray;
        set
        {
            _settings.KeepInTray = value;
            _settings.Save();
            Changed();
        }
    }

    public bool StartWithWindows
    {
        get => _startupEnabled;
        set
        {
            if (_startupEnabled == value) return;
            try
            {
                _startup.Enabled = value;
                RefreshStartup();
                if (_startupAvailable && _startupEnabled != value)
                    _startupError = "Windows did not keep that change. Try again.";
            }
            catch (Exception error) when (StartupError(error))
            {
                RefreshStartup();
                _startupError = "Start with Windows could not be changed. Check this account's permissions and try again.";
            }
            ChangedAll();
        }
    }

    public bool StartupAvailable => _startupAvailable;
    public string StartupProblem => _startupError;
    public bool HasStartupProblem => _startupError.Length > 0;

    private static bool StartupError(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or System.Runtime.InteropServices.COMException;

    private void RefreshStartup()
    {
        try
        {
            _startupEnabled = _startup.Enabled;
            _startupAvailable = true;
            _startupError = "";
        }
        catch (Exception error) when (StartupError(error))
        {
            _startupEnabled = false;
            _startupAvailable = false;
            _startupError = "Start with Windows could not be read. Check this account's permissions and reopen Settings.";
        }
    }

    public bool ExplorerEnabled
    {
        get => _explorerEnabled;
        set
        {
            if (_explorerEnabled == value) return;
            try
            {
                _explorer.Enabled = value;
                RefreshExplorer();
                if (_explorerAvailable && _explorerEnabled != value)
                    _explorerError = "Windows did not keep that change. Try again.";
            }
            catch (Exception error) when (StartupError(error))
            {
                RefreshExplorer();
                _explorerError = "Send with AirDrop could not be changed. Check this account's permissions and try again.";
            }
            ChangedAll();
        }
    }

    public bool ExplorerAvailable => _explorerAvailable;
    public string ExplorerProblem => _explorerError;
    public bool HasExplorerProblem => _explorerError.Length > 0;

    private void RefreshExplorer()
    {
        try
        {
            _explorerEnabled = _explorer.Enabled;
            _explorerAvailable = true;
            _explorerError = "";
        }
        catch (Exception error) when (StartupError(error))
        {
            _explorerEnabled = false;
            _explorerAvailable = false;
            _explorerError = "Send with AirDrop could not be read. Check this account's permissions and reopen Settings.";
        }
    }

    public void CommitName()
    {
        var wanted = NameDraft.Trim();
        if (wanted == Name) return;
        _engine.Tell(new() { ["do"] = "set", ["name"] = wanted });
    }

    private void PickFolder()
    {
        if (_desk.PickFolder(Folder) is { } folder)
            _engine.Tell(new() { ["do"] = "set", ["folder"] = folder });
    }

    private void SettingsSaid(JsonElement said)
    {
        var refused = said.TryGetProperty("refused", out var value) ? value : default;
        bool Refused(string name) =>
            refused.ValueKind == JsonValueKind.Object && refused.TryGetProperty(name, out _);
        _nameError = Refused("name")
            ? "Use 1 to 63 characters, and none of \\ / : * ? \" < > |"
            : "";
        _folderError = Refused("folder") ? "That folder could not be used. The old one stays." : "";
        _settings.Name = Text(said, "name");
        _settings.Folder = Text(said, "folder");
        _settings.Ask = Flag(said, "ask");
        if (said.TryGetProperty("sender_folders", out var separate))
            _settings.SenderFolders = separate.GetBoolean();
        if (Refused("known_senders")) _senderError = "The list could not be changed. The old one stays.";
        AdoptSenders(said);
        _settings.Save();
        if (!Refused("name")) _nameDraft = Name;
    }
}
