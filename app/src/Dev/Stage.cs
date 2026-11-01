using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Winawdl.Model;
using Winawdl.Shell;
using Winawdl.Theme;
using Winawdl.Views;

namespace Winawdl.Dev;

/// <summary>An engine that says what a test tells it to and keeps what it was told.</summary>
public sealed class ScriptedEngine : IEngine
{
    public event Action<JsonElement>? Said;
    public event Action<string, string>? Stopped;
    public List<string> Told { get; } = [];
    public bool StartedSenderFolders { get; private set; }

    public void Start(string name, string folder, bool ask, bool contactsOnly,
                      IReadOnlyList<string> knownSenders, bool senderFolders = false)
    {
        StartedSenderFolders = senderFolders;
        Say(new
        {
            @event = "ready", protocol = 1, name, folder, ask,
            contacts_only = contactsOnly, known_senders = knownSenders,
            sender_folders = senderFolders,
        });
    }

    public void Tell(Dictionary<string, object?> command) => Told.Add(JsonSerializer.Serialize(command));

    public void Stop()
    {
    }

    public void Say(object said)
    {
        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(said));
        Said?.Invoke(parsed.RootElement.Clone());
    }

    public void End(string why, string detail) => Stopped?.Invoke(why, detail);
}

public sealed class ScriptedRadio : IRadio
{
    public RadioPhase Phase { get; private set; }
    public string LastLine { get; private set; } = "";
    public string LogFolder => Path.GetTempPath();
    public bool FromSource { get; set; } = true;
    public event Action? Changed;
    public int Starts { get; private set; }

    public void Start()
    {
        Starts++;
        Become(RadioPhase.Starting);
    }

    public void Stop() => Become(RadioPhase.Off);

    public void Linked(bool up)
    {
        if (up && Phase == RadioPhase.Starting) Become(RadioPhase.Running);
    }

    public void Become(RadioPhase phase, string lastLine = "")
    {
        (Phase, LastLine) = (phase, lastLine);
        Changed?.Invoke();
    }
}

public sealed class ScriptedStartup : IStartup
{
    public bool Stored { get; set; }
    public bool RefuseRead { get; set; }
    public bool RefuseWrite { get; set; }
    public bool IgnoreWrite { get; set; }
    public int Writes { get; private set; }
    public bool Enabled
    {
        get => RefuseRead ? throw new UnauthorizedAccessException() : Stored;
        set
        {
            Writes++;
            if (RefuseWrite) throw new UnauthorizedAccessException();
            if (!IgnoreWrite) Stored = value;
        }
    }
}

public sealed class ScriptedExplorer : IExplorerIntegration
{
    public bool Stored { get; set; }
    public bool RefuseRead { get; set; }
    public bool RefuseWrite { get; set; }
    public bool IgnoreWrite { get; set; }
    public bool Enabled
    {
        get => RefuseRead ? throw new UnauthorizedAccessException() : Stored;
        set
        {
            if (RefuseWrite) throw new UnauthorizedAccessException();
            if (!IgnoreWrite) Stored = value;
        }
    }
}

/// <summary>Windows with the dialogs answered in advance and nothing opened.</summary>
public sealed class ScriptedDesk(Dispatcher dispatcher) : IDesk
{
    public IReadOnlyList<string> Picked { get; set; } = [];
    public string? PickedFolder { get; set; }
    public ClipboardContent? Clipboard { get; set; }
    public List<string> Opened { get; } = [];
    public double Now { get; set; }
    public DateTimeOffset UtcNow { get; set; } = new(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public IReadOnlyList<string> PickFiles() => Picked;
    public string? PickFolder(string from) => PickedFolder;
    public ClipboardContent ReadClipboard() => Clipboard ?? new(Picked);
    public void Open(string path) => Opened.Add("open " + path);
    public void Reveal(string path) => Opened.Add("reveal " + path);
    public Task<ImageSource?> Thumbnail(string path) => Thumbnails.Load(path);

    /// <summary>Off for a render, which is one moment: what would happen later does not.</summary>
    public bool RunsLater { get; init; }

    public void Later(TimeSpan delay, Action action)
    {
        if (!RunsLater) return;
        DispatcherTimer? timer = null;
        timer = new DispatcherTimer(delay, DispatcherPriority.Normal, (_, _) =>
        {
            timer!.Stop();
            action();
        }, dispatcher);
    }
}

public sealed class ScriptedHistoryStore : IHistoryStore
{
    public List<TransferRecord> Saved { get; private set; } = [];
    public IReadOnlyList<TransferRecord> Load() => [.. Saved];
    public void Save(IReadOnlyList<TransferRecord> entries) => Saved = [.. entries];
}

/// <summary>Draws the app's screens to image files without opening a window, one
/// file per state, theme and size, to judge them by. Nothing here ships as data:
/// the devices and files are stand-ins that exist only for these pictures.</summary>
public static class Stage
{
    public const string Flag = "--render";
    private const double Scale = 2;

    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            return 2;
        }
        if (args is ["sheet", var into, .. var pictures])
        {
            Sheet(pictures, into);
            return 0;
        }
        if (args is ["icon", var icon, var preview])
        {
            WriteIcon(icon);
            IconPreview(preview);
            return 0;
        }
        if (args is ["brand", var docs])
        {
            Brand(docs);
            return 0;
        }
        if (args is ["readme", var shown])
        {
            Readme(shown);
            return 0;
        }
        var folder = Directory.CreateDirectory(args[0]).FullName;
        var only = args.Skip(1).ToHashSet();
        var app = new Application();
        var theme = Program.Dress(app);
        Motion.Reduced = true;
        var files = SampleFiles(Path.Combine(folder, "files"));

        var blue = Rgb.Hex("#0078D4");
        (string Name, Palette Palette)[] themes =
        [
            ("light", Palette.Light(blue)), ("dark", Palette.Night(blue)),
        ];
        foreach (var (name, build) in Scenes(files))
        {
            if (only.Count > 0 && !only.Contains(name)) continue;
            foreach (var (themeName, palette) in themes)
            {
                theme.Apply(palette);
                var view = Build(build, app.Dispatcher, name.StartsWith("setup-"));
                if (name.StartsWith("settings-startup-") || name.StartsWith("settings-explorer-"))
                {
                    view.Measure(new Size(460, 688));
                    view.Arrange(new Rect(0, 0, 460, 688));
                    view.UpdateLayout();
                    ((ScrollViewer)view.FindName("SettingsScroll")).ScrollToEnd();
                    view.UpdateLayout();
                }
                if (name.StartsWith("settings-sender-folders-"))
                {
                    view.Measure(new Size(460, 688));
                    view.Arrange(new Rect(0, 0, 460, 688));
                    view.UpdateLayout();
                    ((FrameworkElement)view.FindName("SenderFoldersSwitch")).BringIntoView();
                    view.UpdateLayout();
                }
                Save(view, 460, 720, Path.Combine(folder, $"{name}-{themeName}.png"));
                File.WriteAllLines(Path.Combine(folder, $"{name}.txt"), Controls(view).Select(one => one.Line));
                if (view.DataContext is TableModel model) model.Dispose();
            }
        }
        if (only.Count == 0 || only.Contains("request-card"))
        {
            // A pale and a dark desktop behind each theme: the card does not choose what it lies on.
            foreach (var (themeName, palette) in themes)
                foreach (var (ground, backdrop) in new[] { ("pale", "#C9D6E2"), ("deep", "#20262E") })
                {
                    theme.Apply(palette);
                    SaveRequestCard(app.Dispatcher, files, backdrop, Path.Combine(folder, $"request-card-{themeName}-on-{ground}.png"));
                }
        }
        if (only.Count == 0 || only.Contains("notification-fallback"))
        {
            foreach (var (themeName, palette) in themes)
            {
                theme.Apply(palette);
                SaveRequestCard(app.Dispatcher, files, "#20262E",
                    Path.Combine(folder, $"notification-fallback-{themeName}.png"));
            }
        }
        if (only.Count == 0 || only.Contains("sizes"))
        {
            var busy = Scenes(files).First(scene => scene.Name == "incoming-ask").Build;
            foreach (var (themeName, palette) in themes)
            {
                theme.Apply(palette);
                Save(Build(busy, app.Dispatcher), 380, 560, Path.Combine(folder, $"size-smallest-{themeName}.png"));
            }
            theme.Apply(Palette.Night(blue));
            Save(Build(busy, app.Dispatcher), 900, 820, Path.Combine(folder, "size-wide-dark.png"));
        }
        if (only.Count == 0 || only.Contains("accents"))
        {
            var holding = Scenes(files).First(scene => scene.Name == "holding").Build;
            foreach (var (accentName, accent) in new[]
                     {
                         ("yellow", "#FFB900"), ("rose", "#E3008C"), ("mint", "#00CC6A"), ("slate", "#515C6B"),
                     })
            {
                theme.Apply(Palette.Light(Rgb.Hex(accent)));
                Save(Build(holding, app.Dispatcher), 460, 720, Path.Combine(folder, $"accent-{accentName}-light.png"));
                theme.Apply(Palette.Night(Rgb.Hex(accent)));
                Save(Build(holding, app.Dispatcher), 460, 720, Path.Combine(folder, $"accent-{accentName}-dark.png"));
            }
            theme.Apply(Palette.System(Rgb.Black, Rgb.White, Rgb.Hex("#1AEBFF"), Rgb.Black, Rgb.Hex("#FFFF00")));
            Save(Build(holding, app.Dispatcher), 460, 720, Path.Combine(folder, "contrast-theme.png"));
        }
        if (only.Count == 0 || only.Contains("accessibility"))
        {
            foreach (var dark in new[] { true, false })
            {
                theme.Apply(dark
                    ? Palette.System(Rgb.Black, Rgb.White, Rgb.Hex("#1AEBFF"), Rgb.Black, Rgb.Hex("#FFFF00"))
                    : Palette.System(Rgb.White, Rgb.Black, Rgb.Hex("#000080"), Rgb.White, Rgb.Hex("#0000FF")));
                SaveRequestCard(app.Dispatcher, files, "#20262E", Path.Combine(folder, $"contrast-cards-{(dark ? "dark" : "light")}.png"));
            }
            foreach (var sceneName in new[] { "incoming-ask", "settings", "history-received", "setup-1" })
            {
                var scene = Scenes(files).First(scene => scene.Name == sceneName).Build;
                foreach (var (themeName, palette) in themes)
                {
                    theme.Apply(palette);
                    var view = Build(scene, app.Dispatcher, sceneName.StartsWith("setup-"));
                    view.LayoutTransform = new ScaleTransform(2, 2);
                    Save(view, 920, 1080, Path.Combine(folder, $"large-text-{sceneName}-{themeName}.png"));
                    ((TableModel)view.DataContext).Dispose();
                }
                foreach (var dark in new[] { true, false })
                {
                    theme.Apply(dark
                        ? Palette.System(Rgb.Black, Rgb.White, Rgb.Hex("#1AEBFF"), Rgb.Black, Rgb.Hex("#FFFF00"))
                        : Palette.System(Rgb.White, Rgb.Black, Rgb.Hex("#000080"), Rgb.White, Rgb.Hex("#0000FF")));
                    var view = Build(scene, app.Dispatcher, sceneName.StartsWith("setup-"));
                    Save(view, 460, 720, Path.Combine(folder, $"contrast-{sceneName}-{(dark ? "dark" : "light")}.png"));
                    ((TableModel)view.DataContext).Dispose();
                }
            }
            foreach (var (themeName, palette) in themes)
            {
                theme.Apply(palette);
                SaveRequestCard(app.Dispatcher, files, "#20262E", Path.Combine(folder, $"large-text-cards-{themeName}.png"), textScale: 2);
                var view = Build(Scenes(files).First(scene => scene.Name == "sending").Build, app.Dispatcher);
                view.Measure(new Size(460, 688)); view.Arrange(new Rect(0, 0, 460, 688)); view.UpdateLayout();
                var host = new AdornerDecorator { Child = view };
                host.Measure(new Size(460, 688)); host.Arrange(new Rect(0, 0, 460, 688)); host.UpdateLayout();
                var layer = AdornerLayer.GetAdornerLayer(view)!;
                foreach (var control in Controls(view).Select(one => one.Control).Where(control => control.IsEnabled))
                    layer.Add(new RingAdorner(control));
                Save(host, 460, 720, Path.Combine(folder, $"keyboard-focus-{themeName}.png"));
                ((TableModel)view.DataContext).Dispose();
            }
        }
        return 0;
    }

    public sealed record World(ScriptedEngine Engine, ScriptedRadio Radio, ScriptedDesk Desk, TableModel Model,
                               AppSettings Settings, ScriptedStartup Startup, ScriptedExplorer Explorer,
                               ScriptedHistoryStore HistoryStore);

    public static World NewWorld(Dispatcher dispatcher, string? folder = null, bool setup = false)
    {
        var engine = new ScriptedEngine();
        var radio = new ScriptedRadio();
        var desk = new ScriptedDesk(dispatcher);
        var settings = new AppSettings { Name = "STUDY-PC", Folder = folder ?? Path.GetTempPath(), SetupPending = setup };
        var startup = new ScriptedStartup();
        var explorer = new ScriptedExplorer();
        var history = new ScriptedHistoryStore();
        var model = new TableModel(engine, radio, desk, settings, startup, explorer, history);
        model.StartTheEngine();
        model.SetVisible(true);
        return new World(engine, radio, desk, model, settings, startup, explorer, history);
    }

    private static MainView Build(Action<World> scene, Dispatcher dispatcher, bool setup = false)
    {
        var world = NewWorld(dispatcher, setup: setup);
        scene(world);
        return new MainView { DataContext = world.Model };
    }

    private static void Up(World world)
    {
        world.Engine.Say(new { @event = "link", up = true });
        world.Engine.Say(new { @event = "receiving", on = true, port = 8771 });
        world.Engine.Say(new { @event = "looking", on = true, beacon = true, why = "" });
    }

    private static void Devices(World world)
    {
        Up(world);
        world.Engine.Say(new { @event = "peer", id = "a", name = "Stand-in iPhone", model = "iPhone14,2" });
        world.Engine.Say(new { @event = "peer", id = "b", name = "Stand-in MacBook", model = "MacBookPro18,3" });
    }

    private static void Sending(World world, string[] files, object said)
    {
        Devices(world);
        world.Model.Hold(files[..3]);
        world.Model.SendTo(world.Model.Peers[0]);
        world.Engine.Say(said);
    }

    private static void ShowHistory(World world)
    {
        world.Model.HistoryOpen = true;
        if (world.Model.HistoryRows.Count > 0) world.Model.HistoryRows[0].Expanded = true;
    }

    private static void Asked(World world, bool waits) => world.Engine.Say(new
    {
        @event = "asked", id = "r1", sender = "Stand-in iPhone", model = "iPhone14,2",
        files = new[]
        {
            new { name = "IMG_0001.jpg", size = 4_200_000, type = "public.jpeg" },
            new { name = "IMG_0002.jpg", size = 3_900_000, type = "public.jpeg" },
            new { name = "IMG_0003.jpg", size = 4_900_000, type = "public.jpeg" },
        },
        links = 0, total = 13_000_000, waits,
    });

    public static IEnumerable<(string Name, Action<World> Build)> Scenes(string[] files)
    {
        for (var step = 0; step < 5; step++)
        {
            var current = step;
            yield return ($"setup-{step + 1}", world =>
            {
                for (var index = 0; index < current; index++) world.Model.SkipSetupStep.Execute(null);
            });
        }
        yield return ("setup-name-error", world =>
        {
            world.Model.NameDraft = "bad/name";
            world.Model.NextSetup.Execute(null);
            world.Engine.Say(new { @event = "settings", name = "STUDY-PC", folder = world.Model.Folder,
                ask = true, refused = new { name = "invalid" } });
        });
        yield return ("setup-contacts", world =>
        {
            world.Model.SkipSetupStep.Execute(null);
            world.Model.OnlyKnownSenders = true;
        });
        yield return ("setup-ten-minutes", world =>
        {
            world.Model.SkipSetupStep.Execute(null);
            world.Model.EveryoneForAWhile = true;
        });
        yield return ("setup-startup-error", world =>
        {
            for (var index = 0; index < 3; index++) world.Model.SkipSetupStep.Execute(null);
            world.Startup.RefuseWrite = true;
            world.Model.StartWithWindows = true;
        });
        yield return ("setup-startup-on", world =>
        {
            for (var index = 0; index < 3; index++) world.Model.SkipSetupStep.Execute(null);
            world.Model.StartWithWindows = true;
        });
        yield return ("setup-explorer-on", world =>
        {
            for (var index = 0; index < 4; index++) world.Model.SkipSetupStep.Execute(null);
            world.Model.ExplorerEnabled = true;
        });
        yield return ("setup-folder-error", world =>
        {
            for (var index = 0; index < 2; index++) world.Model.SkipSetupStep.Execute(null);
            world.Engine.Say(new { @event = "settings", name = world.Model.Name, folder = world.Model.Folder,
                ask = true, refused = new { folder = "invalid" } });
        });
        yield return ("setup-explorer-error", world =>
        {
            for (var index = 0; index < 4; index++) world.Model.SkipSetupStep.Execute(null);
            world.Explorer.RefuseWrite = true;
            world.Model.ExplorerEnabled = true;
        });
        yield return ("radio-off", _ => { });
        yield return ("radio-starting", world =>
        {
            world.Radio.Start();
            world.Radio.Become(RadioPhase.Starting, "radio: firmware loaded, bringing the card up on channel 44");
        });
        yield return ("radio-failed", world => world.Radio.Become(
            RadioPhase.Failed, "radio: could not get 6 locked 2 MiB blocks below 4 GiB"));
        yield return ("radio-refused", world => world.Radio.Become(RadioPhase.Refused));
        yield return ("engine-down", world => world.Engine.End("no_python", ""));
        yield return ("nobody", world =>
        {
            Up(world);
            world.Engine.Say(new { @event = "unwilling", count = 1 });
        });
        yield return ("idle", Devices);
        yield return ("holding", world =>
        {
            Devices(world);
            world.Model.Hold(files);
        });
        yield return ("folder-flat", world =>
        {
            Devices(world);
            world.Model.Hold([Path.GetDirectoryName(files[0])!]);
        });
        yield return ("folder-limit", world =>
        {
            Devices(world);
            var many = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(files[0])!, "limit")).FullName;
            for (var index = 0; index <= TableModel.MaxSendFiles; index++)
                File.WriteAllText(Path.Combine(many, $"{index:D3}.txt"), "");
            world.Model.Hold([many]);
        });
        yield return ("clipboard-files", world =>
        {
            Devices(world);
            world.Desk.Clipboard = new(files[..2]);
            world.Model.Paste.Execute(null);
        });
        yield return ("clipboard-image", world =>
        {
            Devices(world);
            world.Desk.Clipboard = new([], Image: BitmapFrame.Create(new Uri(files[0]), BitmapCreateOptions.None, BitmapCacheOption.OnLoad));
            world.Model.Paste.Execute(null);
        });
        yield return ("clipboard-link", world =>
        {
            Devices(world);
            world.Desk.Clipboard = new([], Text: "https://example.org/photos");
            world.Model.Paste.Execute(null);
        });
        yield return ("clipboard-link-held", world =>
        {
            Devices(world);
            world.Model.Hold(files[..2]);
            world.Desk.Clipboard = new([], Text: "https://example.org/photos");
            world.Model.Paste.Execute(null);
        });
        yield return ("clipboard-rejected", world =>
        {
            Devices(world);
            world.Desk.Clipboard = new([], Text: "file:///C:/private");
            world.Model.Paste.Execute(null);
        });
        yield return ("clipboard-busy", world =>
        {
            Devices(world);
            world.Desk.Clipboard = new([], Problem: "The clipboard is busy. Try Paste again.");
            world.Model.Paste.Execute(null);
        });
        yield return ("asking-out", world => Sending(world, files, new { @event = "send", id = "s1", state = "asking" }));
        yield return ("sending", world =>
        {
            Sending(world, files, new { @event = "send", id = "s1", state = "sending", done = 0, total = 151_500_000 });
            world.Desk.Now = 1;
            world.Engine.Say(new { @event = "send", id = "s1", state = "sending", done = 94_000_000, total = 151_500_000 });
        });
        yield return ("sent", world => Sending(world, files, new { @event = "send", id = "s1", state = "sent", done = 9, total = 9 }));
        yield return ("declined", world => Sending(world, files, new { @event = "send", id = "s1", state = "declined" }));
        yield return ("failed-out", world => Sending(world, files, new { @event = "send", id = "s1", state = "failed", reason = "broken" }));
        yield return ("incoming-ask", world =>
        {
            Devices(world);
            Asked(world, waits: true);
        });
        yield return ("incoming-receiving", world =>
        {
            Devices(world);
            Asked(world, waits: true);
            world.Engine.Say(new { @event = "answered", id = "r1", how = "accepted" });
            world.Engine.Say(new { @event = "incoming", id = "r1", done = 0, total = 13_000_000 });
            world.Desk.Now = 1;
            world.Engine.Say(new { @event = "incoming", id = "r1", done = 5_900_000, total = 13_000_000 });
        });
        yield return ("incoming-lost", world =>
        {
            Devices(world);
            Asked(world, waits: true);
            world.Engine.Say(new { @event = "answered", id = "r1", how = "accepted" });
            world.Engine.Say(new { @event = "lost", id = "r1", reason = "nothing arrived" });
        });
        yield return ("received", world =>
        {
            Devices(world);
            world.Engine.Say(new
            {
                @event = "stored", id = "r1", folder = Path.GetDirectoryName(files[0]),
                names = files.Select(Path.GetFileName).ToArray(),
            });
        });
        yield return ("history-empty", ShowHistory);
        yield return ("history-received", world =>
        {
            Asked(world, waits: true);
            world.Engine.Say(new { @event = "answered", id = "r1", how = "accepted" });
            world.Engine.Say(new
            {
                @event = "stored", id = "r1", folder = Path.GetDirectoryName(files[0]),
                names = files[..2].Select(Path.GetFileName).ToArray(),
            });
            ShowHistory(world);
        });
        yield return ("history-receive-failed", world =>
        {
            Asked(world, waits: true);
            world.Engine.Say(new { @event = "answered", id = "r1", how = "accepted" });
            world.Engine.Say(new { @event = "lost", id = "r1", reason = "nothing arrived" });
            ShowHistory(world);
        });
        yield return ("history-receive-declined", world =>
        {
            Asked(world, waits: true);
            world.Engine.Say(new { @event = "answered", id = "r1", how = "declined" });
            ShowHistory(world);
        });
        yield return ("history-sent", world =>
        {
            Sending(world, files, new { @event = "send", id = "s1", state = "sent" });
            ShowHistory(world);
        });
        yield return ("history-send-failed", world =>
        {
            Sending(world, files, new { @event = "send", id = "s1", state = "failed", reason = "unreachable" });
            ShowHistory(world);
        });
        yield return ("history-send-declined", world =>
        {
            Sending(world, files, new { @event = "send", id = "s1", state = "declined" });
            ShowHistory(world);
        });
        yield return ("settings", world =>
        {
            Devices(world);
            world.Model.SettingsOpen = true;
        });
        yield return ("settings-startup-off", world => world.Model.SettingsOpen = true);
        yield return ("settings-sender-folders-off", world => world.Model.SettingsOpen = true);
        yield return ("settings-sender-folders-on", world =>
        {
            world.Model.SenderFolders = true;
            world.Model.SettingsOpen = true;
        });
        yield return ("settings-startup-on", world =>
        {
            world.Startup.Stored = true;
            world.Model.SettingsOpen = true;
        });
        yield return ("settings-startup-error", world =>
        {
            world.Startup.RefuseWrite = true;
            world.Model.SettingsOpen = true;
            world.Model.StartWithWindows = true;
        });
        yield return ("settings-startup-unreadable", world =>
        {
            world.Startup.RefuseRead = true;
            world.Model.SettingsOpen = true;
        });
        yield return ("settings-explorer-off", world => world.Model.SettingsOpen = true);
        yield return ("settings-explorer-on", world =>
        {
            world.Explorer.Stored = true;
            world.Model.SettingsOpen = true;
        });
        yield return ("settings-explorer-error", world =>
        {
            world.Explorer.RefuseWrite = true;
            world.Model.SettingsOpen = true;
            world.Model.ExplorerEnabled = true;
        });
        yield return ("settings-explorer-unreadable", world =>
        {
            world.Explorer.RefuseRead = true;
            world.Model.SettingsOpen = true;
        });
        yield return ("settings-always", world =>
        {
            Devices(world);
            Asked(world, waits: true);
            world.Model.Incoming[0].Always!.Execute(null);
            world.Engine.Say(new { @event = "answered", id = "r1", how = "declined" });
            world.Engine.Say(new
            {
                @event = "asked", id = "r2", sender = "Stand-in MacBook", model = "MacBookPro18,3",
                files = Array.Empty<object>(), links = 1, waits = true,
            });
            world.Model.Incoming[0].Always!.Execute(null);
            world.Engine.Say(new { @event = "answered", id = "r2", how = "declined" });
            world.Model.SettingsOpen = true;
        });
    }

    /// <summary>The card that shows a request outside the window, on a stand-in
    /// for whatever is on the screen behind it.</summary>
    private static void SaveRequestCard(Dispatcher dispatcher, string[] files, string backdrop, string path,
                                        bool listed = true, double textScale = 1)
    {
        var world = NewWorld(dispatcher);
        Devices(world);
        world.Model.Arrived += world.Model.Arrivals.Add;
        world.Engine.Say(new
        {
            @event = "asked", id = "r0", sender = "Stand-in iPhone", model = "iPhone14,2", links = 0, waits = false,
            files = new[] { new { name = Path.GetFileName(files[0]), size = 58_706, type = "public.png" } },
        });
        world.Engine.Say(new
        {
            @event = "stored", id = "r0", folder = Path.GetDirectoryName(files[0]),
            names = new[] { Path.GetFileName(files[0]) },
        });
        Asked(world, waits: true);
        world.Engine.Say(new
        {
            @event = "asked", id = "r2", sender = "Stand-in MacBook", model = "MacBookPro18,3",
            files = new[] { new { name = "IMG_0042.pvt", size = 0, type = "com.apple.private.live-photo-bundle" } },
            links = 0, total = 0, waits = true,
        });
        var card = new RequestsView { DataContext = world.Model, LayoutTransform = new ScaleTransform(textScale, textScale) };
        var screen = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(backdrop)),
            Child = new AdornerDecorator { Child = card }, Padding = new Thickness(0, 24, 0, 0),
        };
        // The cards are made by the first pass over the layout and measured by the second.
        for (var pass = 0; pass < 2; pass++)
        {
            screen.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            screen.Arrange(new Rect(screen.DesiredSize));
            screen.UpdateLayout();
        }
        WritePng(Bitmap(screen, screen.DesiredSize.Width, screen.DesiredSize.Height, Scale, dispatcher), path);
        if (listed) File.WriteAllLines(Path.ChangeExtension(path, ".txt"), Controls(card).Select(one => one.Line));
    }

    /// <summary>The two pictures the README shows of the app, both of stand-in
    /// devices and files: the window with its cards beside it on a stand-in for
    /// a desktop, and the cards alone in the light theme.</summary>
    private static void Readme(string folder)
    {
        Directory.CreateDirectory(folder);
        var app = new Application();
        var theme = Program.Dress(app);
        Motion.Reduced = true;
        var blue = Rgb.Hex("#0078D4");
        var files = SampleFiles(Path.Combine(Path.GetTempPath(), "winawdl-readme-files"));

        theme.Apply(Palette.Night(blue));
        var world = NewWorld(app.Dispatcher);
        Showcase(world, files);
        WritePng(Desktop(world, app.Dispatcher), Path.Combine(folder, "screenshot.png"));

        theme.Apply(Palette.Light(blue));
        SaveRequestCard(app.Dispatcher, files, "#C9D6E2", Path.Combine(folder, "cards.png"), listed: false);
    }

    /// <summary>A little of everything at once: files received earlier, one
    /// that has just arrived, a transfer on its way out, and a request waiting.</summary>
    private static void Showcase(World world, string[] files)
    {
        var folder = Path.GetDirectoryName(files[0]);
        Devices(world);
        world.Engine.Say(new
        {
            @event = "stored", id = "earlier", folder, names = new[] { Path.GetFileName(files[2]), Path.GetFileName(files[3]) },
        });
        world.Model.Arrived += world.Model.Arrivals.Add;
        world.Engine.Say(new
        {
            @event = "asked", id = "r0", sender = "Stand-in iPhone", model = "iPhone14,2", links = 0, waits = false,
            files = new[] { new { name = Path.GetFileName(files[0]), size = 58_706, type = "public.png" } },
        });
        world.Engine.Say(new { @event = "stored", id = "r0", folder, names = new[] { Path.GetFileName(files[0]) } });
        world.Model.Hold([files[1], files[4]]);
        world.Model.SendTo(world.Model.Peers[0]);
        world.Engine.Say(new { @event = "send", id = "s1", state = "sending", done = 0, total = 96_100_000 });
        world.Desk.Now = 1;
        world.Engine.Say(new { @event = "send", id = "s1", state = "sending", done = 12_900_000, total = 96_100_000 });
        world.Engine.Say(new
        {
            @event = "asked", id = "r1", sender = "Stand-in MacBook", model = "MacBookPro18,3",
            files = new[]
            {
                new { name = "IMG_0001.jpg", size = 4_200_000, type = "public.jpeg" },
                new { name = "IMG_0002.jpg", size = 3_900_000, type = "public.jpeg" },
                new { name = "IMG_0003.jpg", size = 4_900_000, type = "public.jpeg" },
            },
            links = 0, total = 13_000_000, waits = true,
        });
    }

    /// <summary>The window and the cards it shows outside itself, placed as on
    /// a screen: the cards in the corner, above a strip that stands in for the
    /// taskbar. Nothing of Windows is drawn but that strip and a plain ground.</summary>
    private static RenderTargetBitmap Desktop(World world, Dispatcher dispatcher)
    {
        const double width = 940, height = 916, taskbar = 44, windowWidth = 460, windowHeight = 816;
        var window = Windowed(new MainView { DataContext = world.Model });
        window.Width = windowWidth;
        window.Height = windowHeight;
        window.Clip = new RectangleGeometry(new Rect(0, 0, windowWidth, windowHeight), 8, 8);
        var frame = new Border
        {
            Child = window, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(36, 28, 0, 0),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 44, ShadowDepth = 14, Direction = 270, Opacity = 0.55, Color = Colors.Black,
            },
        };
        // Where the card window puts itself: 4 from the edge of the screen and from the taskbar.
        var cards = new AdornerDecorator
        {
            Child = new RequestsView { DataContext = world.Model },
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 4, taskbar + 4),
        };
        var strip = new Border
        {
            Height = taskbar, VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1B, 0x21)),
        };
        var screen = new Grid
        {
            Background = new LinearGradientBrush(Color.FromRgb(0x2A, 0x33, 0x40), Color.FromRgb(0x1C, 0x22, 0x2B), 90),
            Children = { strip, frame, cards },
        };
        // The cards are made by the first pass over the layout and measured by the second.
        for (var pass = 0; pass < 2; pass++)
        {
            screen.Measure(new Size(width, height));
            screen.Arrange(new Rect(0, 0, width, height));
            screen.UpdateLayout();
        }
        return Bitmap(screen, width, height, Scale, dispatcher);
    }

    /// <summary>Files to hold and receive in the pictures: three drawn images, so
    /// that Windows has thumbnails for them, and two that have none.</summary>
    public static string[] SampleFiles(string folder)
    {
        Directory.CreateDirectory(folder);
        (string Name, string From, string To)[] pictures =
        [
            ("stand-in-dune.png", "#E9B872", "#A63D40"), ("stand-in-sea.png", "#90A959", "#1F4E5F"),
            ("stand-in-dusk.png", "#6494AA", "#2B2D42"),
        ];
        var paths = new List<string>();
        foreach (var (name, from, to) in pictures)
        {
            var path = Path.Combine(folder, name);
            paths.Add(path);
            if (File.Exists(path)) continue;
            var picture = new Border
            {
                Width = 480, Height = 360,
                Background = new LinearGradientBrush(Rgb.Hex(from).Color, Rgb.Hex(to).Color, 60),
                Child = new System.Windows.Shapes.Ellipse
                {
                    Width = 120, Height = 120, Fill = Brushes.White, Opacity = 0.75,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(60),
                },
            };
            WritePng(Bitmap(picture, 480, 360, 1), path);
        }
        foreach (var name in new[] { "stand-in-notes.txt", "stand-in-plan.pdf" })
        {
            var path = Path.Combine(folder, name);
            paths.Add(path);
            if (!File.Exists(path)) File.WriteAllText(path, "A stand-in file for the app's own pictures.\n");
        }
        return [.. paths];
    }

    public static void Pump(Dispatcher dispatcher, int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background,
                                        (_, _) => frame.Continue = false, dispatcher);
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>The view as its window would show it, under a strip that stands
    /// in for the title bar Windows draws.</summary>
    public static void Save(FrameworkElement view, double width, double height, string path) =>
        WritePng(Bitmap(Windowed(view), width, height, Scale, view.Dispatcher), path);

    private static FrameworkElement Windowed(FrameworkElement view)
    {
        var caption = new TextBlock
        {
            Text = "winawdl", FontSize = 12, Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var strip = new Border { Height = 32, Child = caption };
        strip.SetResourceReference(Border.BackgroundProperty, "Table");
        DockPanel.SetDock(strip, Dock.Top);
        var window = new DockPanel { Children = { strip, view } };
        return new AdornerDecorator { Child = window };
    }

    public static RenderTargetBitmap Bitmap(FrameworkElement element, double width, double height, double scale,
                                            Dispatcher? pump = null)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        if (pump is not null)
        {
            // Thumbnails arrive from another thread.
            Pump(pump, 350);
            element.UpdateLayout();
        }
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale,
                                            PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    /// <summary>Every control under `root` that can be seen, in the order Tab
    /// reaches them: its kind, the name a screen reader says, and its state.</summary>
    public static List<(Control Control, string Line)> Controls(DependencyObject root)
    {
        var found = new List<(Control, string)>();
        void Walk(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                // IsVisible would say no to everything: nothing drawn here is in a window.
                if (child is UIElement { Visibility: not Visibility.Visible }) continue;
                // A scroll bar's own parts are for the pointer: the keys scroll by moving focus.
                if (child is System.Windows.Controls.Primitives.ScrollBar) continue;
                if (child is Control control and (System.Windows.Controls.Primitives.ButtonBase or TextBox))
                {
                    var name = System.Windows.Automation.AutomationProperties.GetName(control);
                    if (name.Length == 0)
                        name = new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(control).GetName();
                    if (name.Length == 0 && control is ContentControl { Content: string said }) name = said;
                    var state = control switch
                    {
                        System.Windows.Controls.Primitives.ToggleButton toggle => toggle.IsChecked == true ? " [on]" : " [off]",
                        TextBox box => $" [{box.Text}]",
                        _ => "",
                    };
                    var reach = !control.IsEnabled ? " (out of reach)"
                        : control.Focusable && control.IsTabStop ? "" : " (not a tab stop)";
                    var ring = control.FocusVisualStyle is null ? " (NO FOCUS RING)" : "";
                    found.Add((control, $"{control.GetType().Name} \"{name}\"{state}{reach}{ring}"));
                }
                Walk(child);
            }
        }
        Walk(root);
        return found;
    }

    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    /// <summary>Writes app\winawdl.ico from the drawing in AppIcon: run again
    /// after the drawing changes, then rebuild.</summary>
    public static void WriteIcon(string path)
    {
        var images = IconSizes.Select(size =>
        {
            var drawn = AppIcon.Draw(size, Colors.White, AppIcon.Ground);
            return (Size: size, Data: size >= 256 ? PngBytes(drawn) : DibBytes(drawn));
        }).ToList();
        using var file = new BinaryWriter(File.Create(path));
        file.Write((short)0);
        file.Write((short)1);
        file.Write((short)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            // A side of 256 is written as 0.
            file.Write((byte)(size >= 256 ? 0 : size));
            file.Write((byte)(size >= 256 ? 0 : size));
            file.Write((short)0);
            file.Write((short)1);
            file.Write((short)32);
            file.Write(data.Length);
            file.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) file.Write(data);
    }

    private static byte[] PngBytes(BitmapSource drawn)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(drawn));
        using var data = new MemoryStream();
        encoder.Save(data);
        return data.ToArray();
    }

    /// <summary>An icon's older form, which the small sizes are kept in: the
    /// pixels from the bottom row up, then a mask of the fully clear ones.</summary>
    private static byte[] DibBytes(BitmapSource drawn)
    {
        var size = drawn.PixelWidth;
        var pixels = new byte[size * size * 4];
        new FormatConvertedBitmap(drawn, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, size * 4, 0);
        var maskRow = (size + 31) / 32 * 4;
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);
        writer.Write(pixels.Length + maskRow * size);
        writer.Write(0L);
        writer.Write(0L);
        for (var row = size - 1; row >= 0; row--) writer.Write(pixels, row * size * 4, size * 4);
        for (var row = size - 1; row >= 0; row--)
        {
            var clear = new byte[maskRow];
            for (var x = 0; x < size; x++)
                if (pixels[(row * size + x) * 4 + 3] == 0) clear[x / 8] |= (byte)(0x80 >> x % 8);
            writer.Write(clear);
        }
        return data.ToArray();
    }

    /// <summary>The two pictures the repository shows of the mark: the icon,
    /// large, and the card a link to the repository is drawn with.</summary>
    public static void Brand(string folder)
    {
        Directory.CreateDirectory(folder);
        WritePng(AppIcon.Draw(512, Colors.White, AppIcon.Ground), Path.Combine(folder, "logo.png"));

        const double width = 1280, height = 640;
        var light = Palette.Light(Rgb.Hex("#0078D4"));
        FormattedText Line(string text, string family, double size, FontWeight weight, Rgb color) => new(
            text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(family), FontStyles.Normal, weight, FontStretches.Normal), size,
            light.Brush(color), 1);
        var card = new DrawingVisual();
        using (var dc = card.RenderOpen())
        {
            dc.DrawRectangle(light.Brush(light.Table), null, new Rect(0, 0, width, height));
            dc.DrawImage(AppIcon.Draw(256, Colors.White, AppIcon.Ground), new Rect(128, 192, 256, 256));
            dc.DrawText(Line("winawdl", "Segoe UI Variable Display, Segoe UI", 116, FontWeights.SemiBold, light.Ink),
                        new Point(440, 176));
            dc.DrawText(Line("AirDrop and AWDL on Windows,", "Segoe UI Variable Text, Segoe UI", 40,
                             FontWeights.Normal, light.InkSoft), new Point(446, 326));
            dc.DrawText(Line("through the built-in Wi-Fi card", "Segoe UI Variable Text, Segoe UI", 40,
                             FontWeights.Normal, light.InkSoft), new Point(446, 378));
        }
        var picture = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        picture.Render(card);
        WritePng(picture, Path.Combine(folder, "social-preview.png"));
    }

    /// <summary>The icon at every size it is stored in, on a light and a dark
    /// taskbar, with the smallest ones enlarged pixel for pixel.</summary>
    public static void IconPreview(string path)
    {
        const double band = 300, width = 1500;
        var smooth = new DrawingVisual();
        using (var dc = smooth.RenderOpen())
        {
            // Each pixel as a square: scaling the picture would blur what is being judged.
            void Enlarged(BitmapSource small, double left, double top, int times)
            {
                var side = small.PixelWidth;
                var pixels = new byte[side * side * 4];
                new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, side * 4, 0);
                for (var y = 0; y < side; y++)
                    for (var x = 0; x < side; x++)
                    {
                        var at = (y * side + x) * 4;
                        var color = Color.FromArgb(pixels[at + 3], pixels[at + 2], pixels[at + 1], pixels[at]);
                        dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(left + x * times, top + y * times, times, times));
                    }
            }

            foreach (var (row, taskbar, single) in new[]
                     {
                         (0, Color.FromRgb(0xF3, 0xF3, 0xF3), Colors.Black), (1, Color.FromRgb(0x20, 0x20, 0x20), Colors.White),
                     })
            {
                var top = row * band;
                dc.DrawRectangle(new SolidColorBrush(taskbar), null, new Rect(0, top, width, band));
                double x = 22;
                foreach (var size in IconSizes.Reverse())
                {
                    dc.DrawImage(AppIcon.Draw(size, Colors.White, AppIcon.Ground), new Rect(x, top + 22, size, size));
                    x += size + 18;
                }
                foreach (var size in (int[])[16, 24, 32])
                {
                    Enlarged(AppIcon.Draw(size, Colors.White, AppIcon.Ground), x, top + 22, 7);
                    x += size * 7 + 18;
                }
                var tray = AppIcon.Draw(32, single, null);
                dc.DrawImage(tray, new Rect(x, top + 22, 32, 32));
                Enlarged(tray, x, top + 70, 5);
            }
        }
        var picture = new RenderTargetBitmap((int)width, (int)(band * 2), 96, 96, PixelFormats.Pbgra32);
        picture.Render(smooth);
        WritePng(picture, path);
    }

    /// <summary>Several pictures side by side in one file, to look over at once.</summary>
    public static void Sheet(IReadOnlyList<string> pictures, string path, double height = 720)
    {
        var drawing = new DrawingVisual();
        double x = 0;
        using (var dc = drawing.RenderOpen())
        {
            foreach (var picture in pictures)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(picture);
                image.EndInit();
                var width = height * image.PixelWidth / image.PixelHeight;
                dc.DrawImage(image, new Rect(x, 0, width, height));
                x += width + 12;
            }
        }
        var sheet = new RenderTargetBitmap((int)x, (int)height, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(drawing);
        WritePng(sheet, path);
    }

    public static void WritePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}

