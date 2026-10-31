using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Winawdl.Engine;
using Winawdl.Model;
using Winawdl.Radio;
using Winawdl.Shell;
using Winawdl.Theme;
using Winawdl.Views;

namespace Winawdl.Dev;

/// <summary>The checks that need neither a radio nor the engine: the colors'
/// contrast for any accent, the wording, what each state of the radio and the
/// engine puts on screen, and the supervisor that runs the two daemons.</summary>
public static class Checks
{
    public const string Flag = "--check";

    private static StreamWriter _log = null!;
    private static int _passed, _failed;

    public static int Run(string[] args)
    {
        if (args.Length < 1) return 2;
        var folder = Directory.CreateDirectory(args[0]).FullName;
        using var log = new StreamWriter(Path.Combine(folder, "check.log")) { AutoFlush = true };
        _log = log;
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Program.Dress(app).Apply(Palette.Light(Rgb.Hex("#0078D4")));
            Motion.Reduced = true;
            if (args.Skip(1).Contains("compact"))
            {
                Compact(app);
                log.WriteLine(_failed == 0 ? $"ALL {_passed} PASSED" : $"{_failed} FAILED, {_passed} passed");
                return _failed == 0 ? 0 : 1;
            }
            if (args.Skip(1).Contains("selection"))
            {
                Selection(app, folder);
                log.WriteLine(_failed == 0 ? $"ALL {_passed} PASSED" : $"{_failed} FAILED, {_passed} passed");
                return _failed == 0 ? 0 : 1;
            }
            if (args.Skip(1).Contains("explorer"))
            {
                Explorer(app, folder);
                log.WriteLine(_failed == 0 ? $"ALL {_passed} PASSED" : $"{_failed} FAILED, {_passed} passed");
                return _failed == 0 ? 0 : 1;
            }
            if (args.Skip(1).Contains("startup"))
            {
                Startup(app);
                log.WriteLine(_failed == 0 ? $"ALL {_passed} PASSED" : $"{_failed} FAILED, {_passed} passed");
                return _failed == 0 ? 0 : 1;
            }
            Colors();
            Wording();
            Identities(Path.Combine(folder, "identities-" + Guid.NewGuid().ToString("N")));
            States(app);
            Startup(app);
            Explorer(app, folder);
            Selection(app, folder);
            Compact(app);
            Supervisor(Path.Combine(folder, "supervised"));
        }
        catch (Exception error)
        {
            log.WriteLine($"STOPPED {error}");
            return 1;
        }
        log.WriteLine(_failed == 0 ? $"ALL {_passed} PASSED" : $"{_failed} FAILED, {_passed} passed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Compact(Application app)
    {
        var world = Stage.NewWorld(app.Dispatcher);
        Stage.Scenes([]).First(scene => scene.Name == "incoming-ask").Build(world);
        var view = new MainView { DataContext = world.Model };
        // Stage's 380 x 560 smallest window includes its 32-DIP caption strip.
        view.Measure(new Size(380, 528));
        view.Arrange(new Rect(0, 0, 380, 528));
        view.UpdateLayout();
        var across = (ScrollViewer)view.FindName("AcrossScroll");
        var hand = (FrameworkElement)view.FindName("Hand");
        var acrossBounds = across.TransformToAncestor(view).TransformBounds(new Rect(across.RenderSize));
        var handBounds = hand.TransformToAncestor(view).TransformBounds(new Rect(hand.RenderSize));
        _log.WriteLine($"Compact across={acrossBounds}, hand={handBounds}, viewport={across.ViewportHeight}, extent={across.ExtentHeight}");
        Expect(handBounds.Top - acrossBounds.Bottom >= 16 && across.ViewportHeight > 0,
               "the compact peer viewport keeps 16 DIPs clear above the drop well and retains scroll space");
        var belowViewport = new Point(across.ActualWidth / 2, across.ActualHeight + 8);
        Expect(VisualTreeHelper.HitTest(across, belowViewport) is null,
               "compact peer content cannot receive input beyond its scroll viewport");
        across.ScrollToEnd();
        view.UpdateLayout();
        Expect(across.VerticalOffset > 0 && across.VerticalOffset + across.ViewportHeight >= across.ExtentHeight - 1,
               "the compact peer region scrolls to its final device controls");
        var lastPeer = Stage.Controls(view).First(item => item.Line.Contains("\"Stand-in MacBook\"")).Control;
        var peerBounds = lastPeer.TransformToAncestor(view).TransformBounds(new Rect(lastPeer.RenderSize));
        Expect(peerBounds.Top >= acrossBounds.Top && peerBounds.Bottom <= acrossBounds.Bottom,
               "the final peer tile is fully accessible after scrolling the compact viewport");
        Expect(Stage.Controls(view).Any(item => item.Line.Contains("\"Choose Files…\""))
               && Stage.Controls(view).Any(item => item.Line.Contains("\"Paste\"")),
               "the compact drop well retains Choose Files and Paste controls");
        world.Model.Dispose();
    }

    private static void Selection(Application app, string folder)
    {
        var root = Directory.CreateDirectory(Path.Combine(folder, "selection-" + Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "nested", "b.txt"), "b");
        var world = Stage.NewWorld(app.Dispatcher);
        var selectionView = Show(world);
        bool NoticeUses(string token)
        {
            selectionView.UpdateLayout();
            return ((TextBlock)selectionView.FindName("HeldNoticeText")).Foreground is SolidColorBrush brush
                && brush.Color == ((SolidColorBrush)selectionView.FindResource(token)).Color;
        }
        world.Model.Hold([root]);
        Expect(world.Model.Held.Select(file => file.Name).SequenceEqual(["a.txt", "b.txt"]),
               "folders expand into a deterministic flat file list");
        Expect(NoticeUses("InkSoft"), "successful flat-folder explanation uses informational ink");
        world.Model.Hold([Path.Combine(root, "a.txt")]);
        Expect(world.Model.Held.Count == 2, "a file selected directly and through a folder counts once");
        var twin = Directory.CreateDirectory(Path.Combine(root, "twins")).FullName;
        File.WriteAllText(Path.Combine(twin, "a.txt"), "second");
        world.Model.Hold([twin]);
        Expect(world.Model.Held.Count == 2 && world.Model.HeldNotice.Contains("second was left out"),
               "flat basename collisions keep the first file and explain the omission");
        Expect(NoticeUses("Ember"), "basename omissions retain failure ink");

        var many = Directory.CreateDirectory(Path.Combine(root, "many")).FullName;
        for (var index = 0; index < TableModel.MaxSendFiles; index++)
            File.WriteAllText(Path.Combine(many, $"{index:D3}.txt"), "");
        world.Model.Hold([many]);
        Expect(world.Model.Held.Count == 2 && world.Model.HeldNotice.Contains("Nothing was added"),
               "combined existing files and folder expansion cannot exceed 500; the batch is refused intact");
        Expect(NoticeUses("Ember"), "an exceeded selection budget retains failure ink");
        world.Engine.Say(new { @event = "peer", id = "limit-peer", name = "Stand-in iPhone", model = "iPhone" });
        var limitedPeer = world.Model.Peers.Single();
        world.Model.SendTo(limitedPeer, [many]);
        Expect(!limitedPeer.IsBusy && !world.Engine.Told.Last().Contains("\"do\":\"send\""),
               "a refused drop on a device cannot send the files already held by mistake");
        world.Engine.Say(new { @event = "peer_gone", id = "limit-peer" });
        world.Model.ClearHeld.Execute(null);
        world.Model.Hold([many]);
        Expect(world.Model.Held.Count == 500, "exactly 500 files fit the staging budget");
        world.Model.Hold([Path.Combine(root, "a.txt")]);
        Expect(world.Model.Held.Count == 500 && world.Model.HasHeldNotice, "a 501st file cannot enter the hand");
        Expect(TableModel.FitsSelection(0, 0, TableModel.MaxSendBytes)
               && !TableModel.FitsSelection(0, 0, TableModel.MaxSendBytes + 1)
               && TableModel.FitsSelection(499, TableModel.MaxSendBytes - 1, 1)
               && !TableModel.FitsSelection(499, TableModel.MaxSendBytes, 1),
               "the staging byte predicate includes the exact 2 GiB boundary and combines existing bytes");
        world.Model.ClearHeld.Execute(null);

        var chosen = Directory.CreateDirectory(Path.Combine(root, "chosen")).FullName;
        File.WriteAllText(Path.Combine(chosen, "safe.txt"), "safe");
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "private.txt"), "private");
        var junction = Path.Combine(chosen, "escape");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", junction, outside }) start.ArgumentList.Add(argument);
        using (var process = Process.Start(start)!)
        {
            Expect(process.WaitForExit(10000) && process.ExitCode == 0, "a native junction fixture is created in this check's GUID folder");
        }
        world.Model.Hold([chosen]);
        Expect(world.Model.Held.Select(file => file.Name).SequenceEqual(["safe.txt"])
               && world.Model.HeldNotice.Contains("junctions were left out"),
               "the native junction cannot export a file outside the chosen folder");
        world.Model.ClearHeld.Execute(null);

        world.Desk.Clipboard = new([chosen]);
        world.Model.Paste.Execute(null);
        Expect(world.Model.Held.Count == 1 && world.Model.Held[0].Name == "safe.txt", "Paste stages a copied folder through the clipboard seam");
        world.Model.ClearHeld.Execute(null);
        world.Desk.Clipboard = new([], Text: "https://example.org/page");
        world.Model.Paste.Execute(null);
        Expect(world.Model.LinkDraft == "https://example.org/page", "Paste puts a web URL in the existing link field");
        Expect(NoticeUses("InkSoft"), "successful link paste resets failure ink to informational ink");
        foreach (var unsafeText in new[] { "file:///C:/private", "javascript:alert(1)", "https://example.org/\u200bhidden", "https://example.org/\nhidden" })
        {
            world.Model.LinkDraft = "";
            world.Desk.Clipboard = new([], Text: unsafeText);
            world.Model.Paste.Execute(null);
            Expect(world.Model.LinkDraft.Length == 0 && world.Model.HasHeldNotice, "unsafe or hidden clipboard URL is rejected");
            Expect(NoticeUses("Ember"), "rejected clipboard URLs retain failure ink");
        }
        world.Desk.Clipboard = new([], Problem: "The clipboard is busy. Try Paste again.");
        world.Model.Paste.Execute(null);
        Expect(world.Model.HeldNotice.Contains("clipboard is busy"), "clipboard contention has a retry message");
        Expect(!MainView.ShouldPasteToTable(new TextBox()) && !MainView.ShouldPasteToTable(new RichTextBox())
               && !MainView.ShouldPasteToTable(new PasswordBox()) && MainView.ShouldPasteToTable(new Button()),
               "Ctrl+V retains text editor paste and handles the rest of the main window");

        world.Desk.Clipboard = new([], Image: BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8));
        world.Model.Paste.Execute(null);
        var image = world.Model.Held.Single().Path;
        Expect(File.ReadAllBytes(image).Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
               "clipboard bitmap is encoded as a real PNG owned by this session");
        world.Model.Held[0].Remove!.Execute(null);
        Expect(!File.Exists(image), "removing a staged clipboard image deletes its temporary PNG");
        world.Model.Paste.Execute(null);
        image = world.Model.Held.Single().Path;
        world.Engine.Say(new { @event = "peer", id = "paste-peer", name = "Stand-in iPhone", model = "iPhone" });
        var peer = world.Model.Peers.Single();
        world.Model.SendTo(peer);
        world.Engine.Say(new { @event = "send", id = peer.SendId, state = "failed", reason = "no_answer" });
        Expect(File.Exists(image), "a failed send retains the staged PNG for retry");
        world.Model.SendTo(peer);
        world.Model.ClearHeld.Execute(null);
        Expect(File.Exists(image), "clearing the hand while a send is active retains the PNG");
        world.Engine.Say(new { @event = "send", id = peer.SendId, state = "sent" });
        Expect(!File.Exists(image), "the PNG is deleted when the active send finishes after a clear");
        world.Model.Paste.Execute(null);
        image = world.Model.Held.Single().Path;
        world.Model.SendTo(peer);
        world.Engine.Say(new { @event = "send", id = peer.SendId, state = "sent" });
        Expect(world.Model.Held.Count == 0 && !File.Exists(image), "a successful send clears and deletes its temporary clipboard image");
        world.Model.Paste.Execute(null);
        image = world.Model.Held.Single().Path;
        world.Model.ClearHeld.Execute(null);
        Expect(!File.Exists(image), "Clear deletes an idle staged PNG");
        world.Model.Paste.Execute(null);
        image = world.Model.Held.Single().Path;
        world.Engine.Say(new { @event = "peer", id = "second-paste-peer", name = "Stand-in iPad", model = "iPad" });
        var secondPeer = world.Model.Peers.Single(item => item.Id == "second-paste-peer");
        world.Model.SendTo(peer);
        world.Model.SendTo(secondPeer);
        world.Model.ClearHeld.Execute(null);
        world.Engine.Say(new { @event = "send", id = peer.SendId, state = "sent" });
        Expect(File.Exists(image), "one finished send cannot delete an image another device is still using");
        world.Engine.Say(new { @event = "send", id = secondPeer.SendId, state = "declined" });
        Expect(!File.Exists(image), "the final concurrent send releases its PNG after the hand was cleared");
        world.Model.Paste.Execute(null);
        image = world.Model.Held.Single().Path;
        world.Model.SendTo(peer);
        var lostId = peer.SendId;
        world.Model.ClearHeld.Execute(null);
        world.Engine.Say(new { @event = "link", up = false });
        Expect(File.Exists(image), "link loss cannot delete an image still used by the engine");
        world.Engine.Say(new { @event = "send", id = lostId, state = "failed", reason = "gone" });
        Expect(!File.Exists(image), "terminal send after peer removal releases the PNG");
        world.Model.Paste.Execute(null);
        image = world.Model.Held.Single().Path;
        world.Model.Dispose();
        Expect(!File.Exists(image) && File.Exists(Path.Combine(chosen, "safe.txt")), "shutdown deletes session PNGs and preserves selected user files");
    }

    private static void Explorer(Application app, string folder)
    {
        var first = Path.Combine(folder, "first file.txt");
        var second = Path.Combine(folder, "second & file.txt");
        File.WriteAllText(first, "one");
        File.WriteAllText(second, "two");
        var request = LaunchRequest.FromArguments(["--tray", first, second, "--engine", "--loopback", folder]);
        Expect(request.Paths.SequenceEqual(new[] { first, second }) && request.Surface,
               "Explorer keeps the complete selected list and files override tray-only launch");
        Expect(!LaunchRequest.FromArguments(["--tray"]).Surface && LaunchRequest.FromArguments([]).Surface,
               "second tray-only launch stays hidden and ordinary launch surfaces");
        Expect(LaunchRequest.Sanitize([first, "relative.txt", "C:relative", "\\rooted", first,
                   Path.Combine(folder, "absent"), "C:\\bad\0name", folder]).SequenceEqual(new[] { first, folder }),
               "initial and pipe paths require existing canonical absolute paths and discard duplicates");
        Expect(LaunchRequest.Decode("{}"u8.ToArray()) == null
               && LaunchRequest.Decode(new byte[LaunchRequest.MaxBytes + 1]) == null,
               "malformed and oversized launch messages are refused");
        var world = Stage.NewWorld(app.Dispatcher);
        Program.Deliver(request, world.Model, () =>
            Expect(world.Model.Held.Count == 2, "files reach the model before the window surfaces"));
        var view = Show(world);
        world.Model.SettingsOpen = true;
        Expect(Press(view, "Send with AirDrop in Explorer") && world.Explorer.Stored && world.Model.ExplorerEnabled,
               "Explorer switch enables registration through its isolated seam");
        world.Explorer.RefuseWrite = true;
        world.Model.ExplorerEnabled = false;
        Expect(world.Model.ExplorerEnabled && world.Model.HasExplorerProblem,
               "a denied Explorer removal preserves actual state and shows an error");
        world.Explorer.RefuseWrite = false;
        world.Model.ExplorerEnabled = false;
        Expect(!world.Model.ExplorerEnabled && !world.Model.HasExplorerProblem,
               "Explorer switch removes registration and clears a recovered error");
        world.Explorer.RefuseRead = true;
        world.Model.SettingsOpen = false;
        world.Model.SettingsOpen = true;
        Expect(!world.Model.ExplorerAvailable && world.Model.HasExplorerProblem,
               "unreadable Explorer registration disables the switch");
        ExplorerPipe(first, second).GetAwaiter().GetResult();
        var shortcut = Path.Combine(folder, "Send with AirDrop.lnk");
        WindowsExplorer.WriteShortcut(shortcut, Environment.ProcessPath!);
        Expect(WindowsExplorer.ShortcutMatches(shortcut, Environment.ProcessPath!),
               "native shell shortcut roundtrips in the test folder with no per-file placeholder");
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        try
        {
            dynamic link = ((dynamic)shell).CreateShortcut(shortcut);
            try { link.Description = "My own SendTo shortcut"; link.Save(); }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        Expect(!WindowsExplorer.ShortcutMatches(shortcut, Environment.ProcessPath!),
               "a customized SendTo shortcut is not treated as app-owned");
        var partial = Path.Combine(folder, "partial SendTo.lnk");
        var rolledBack = false;
        try { WindowsExplorer.WithShortcutRegistration(partial, Environment.ProcessPath!,
            _ => throw new IOException("fake registry denial"), () => rolledBack = true); }
        catch (IOException) { }
        Expect(rolledBack && !File.Exists(partial),
               "failed registry registration rolls back its partial values and new shortcut");
        WindowsExplorer.WriteShortcut(partial, Environment.ProcessPath!);
        var original = File.ReadAllBytes(partial);
        try { WindowsExplorer.WithShortcutRegistration(partial, Environment.ProcessPath!,
            _ => throw new IOException("fake registry denial"), () => { }); }
        catch (IOException) { }
        Expect(File.ReadAllBytes(partial).SequenceEqual(original),
               "failed registration preserves a pre-existing shortcut");
        var executable = Environment.ProcessPath!;
        var fakeValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = "Send with AirDrop", ["Icon"] = $"\"{executable}\",0",
            ["MultiSelectModel"] = WindowsExplorer.SelectionModel,
            ["winawdl-owner"] = "winawdl Explorer integration v1",
            ["UserValue"] = "keep me"
        };
        var fakeCommand = new Dictionary<string, object?>
        {
            [""] = WindowsExplorer.CommandFor(executable), ["UserCommandValue"] = "keep me"
        };
        var fakeVerb = new WindowsExplorer.VerbSnapshot(fakeValues, fakeCommand,
            ["command", "UserSubkey"], ["UserCommandSubkey"]);
        Expect(!WindowsExplorer.PristineVerb(fakeVerb, executable),
               "a customized classic verb cannot be overwritten as a pristine registration");
        var removal = WindowsExplorer.PlanVerbRemoval(fakeVerb, executable);
        Expect(removal != null && !removal.Values.Contains("UserValue")
               && !removal.CommandValues.Contains("UserCommandValue")
               && fakeVerb.Subkeys.Contains("UserSubkey"),
               "classic verb cleanup selects app values without deleting user values or subkeys");
        fakeValues.Remove("winawdl-owner");
        Expect(WindowsExplorer.PlanVerbRemoval(fakeVerb, executable) == null,
               "a matching command without the app ownership marker is not removed");
        Expect(WindowsExplorer.CommandFor(@"C:\Program Files\winawdl\winawdl.exe")
               == "\"C:\\Program Files\\winawdl\\winawdl.exe\" \"%1\""
               && WindowsExplorer.SelectionModel == "Document",
               "classic verbs quote one item per invocation; the pipe accumulates each selection");
    }

    private static async Task ExplorerPipe(string first, string second)
    {
        var name = "winawdl-check-" + Guid.NewGuid().ToString("N");
        using var owner = new SingleInstance(name);
        using var other = new SingleInstance(name);
        Expect(owner.IsFirst && !other.IsFirst, "a real per-user mutex chooses one owner");
        Expect(owner.MutexName.Contains(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value,
                   StringComparison.Ordinal), "the cross-session mutex name includes this user's SID");
        var received = new TaskCompletionSource<LaunchRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forwarding = other.Forward(LaunchRequest.FromArguments([first, second]));
        owner.Listen(request => received.TrySetResult(request));
        Expect(await forwarding && (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Paths.Length == 2,
               "real pipe forwards the full selection even when the listener starts later");
        using (var bad = new System.IO.Pipes.NamedPipeClientStream(".", owner.PipeName,
                   System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
        {
            await bad.ConnectAsync(3000);
            await bad.WriteAsync(BitConverter.GetBytes(LaunchRequest.MaxBytes + 1));
        }
        Expect(await other.Forward(LaunchRequest.FromArguments([first])),
               "the pipe server survives an oversized hostile frame");
    }

    private static void Startup(Application app)
    {
        var world = Stage.NewWorld(app.Dispatcher);
        world.Model.SettingsOpen = true;
        var view = Show(world);
        Expect(Stage.Controls(view).Any(one => one.Line.Contains("Start with Windows")),
               "Window settings offer Start with Windows");
        var model = world.Model;
        var saves = 0;
        world.Settings.Save = () => saves++;
        var commands = world.Engine.Told.Count;
        Expect(!model.StartWithWindows && model.StartupAvailable && !model.HasStartupProblem,
               "startup is off when the fake registry has no entry");
        Expect(Press(view, "Start with Windows") && world.Startup.Stored && model.StartWithWindows,
               "the startup switch writes the seam and reads back on");
        Expect(Press(view, "Start with Windows") && !world.Startup.Stored && !model.StartWithWindows,
               "turning startup off removes the fake entry");
        world.Startup.Stored = true;
        model.SettingsOpen = false;
        model.SettingsOpen = true;
        Expect(model.StartWithWindows, "reopening Settings reflects an entry enabled elsewhere");
        world.Startup.Stored = false;
        model.SettingsOpen = false;
        model.SettingsOpen = true;
        Expect(!model.StartWithWindows, "reopening Settings reflects an entry removed elsewhere");
        world.Startup.RefuseWrite = true;
        Expect(Press(view, "Start with Windows") && !model.StartWithWindows
               && ((Theme.Switch)view.FindName("StartupSwitch")).IsChecked == false
               && model.HasStartupProblem && !world.Startup.Stored,
               "a denied write leaves the switch off and explains the failure");
        world.Startup.RefuseWrite = false;
        model.StartWithWindows = true;
        Expect(model.StartWithWindows && !model.HasStartupProblem, "a later successful write clears the error");
        world.Startup.RefuseWrite = true;
        Expect(Press(view, "Start with Windows") && model.StartWithWindows
               && ((Theme.Switch)view.FindName("StartupSwitch")).IsChecked == true
               && model.HasStartupProblem && world.Startup.Stored,
               "a denied removal leaves startup on and explains the failure");
        world.Startup.RefuseWrite = false;
        model.StartWithWindows = false;
        world.Startup.IgnoreWrite = true;
        model.StartWithWindows = true;
        Expect(!model.StartWithWindows && model.HasStartupProblem,
               "a write that was not kept is never reported as enabled");
        world.Startup.RefuseRead = true;
        model.SettingsOpen = false;
        model.SettingsOpen = true;
        Expect(!model.StartupAvailable && model.HasStartupProblem,
               "a read error disables startup changes and explains how to retry");
        world.Startup.RefuseRead = false;
        model.SettingsOpen = false;
        model.SettingsOpen = true;
        Expect(model.StartupAvailable && !model.HasStartupProblem, "reopening Settings retries a denied read");
        var command = WindowsStartup.CommandFor(@"C:\Program Files\winawdl\winawdl.exe");
        Expect(command == "\"C:\\Program Files\\winawdl\\winawdl.exe\" --tray",
               "the Run command quotes the absolute executable and carries only the tray flag");
        foreach (var path in new[] { "winawdl.exe", "C:\\bad\"path\\winawdl.exe", "C:\\" + new string('a', 260) + ".exe" })
        {
            var refused = false;
            try { WindowsStartup.CommandFor(path); }
            catch (ArgumentException) { refused = true; }
            Expect(refused, "an invalid or overlong Run command is refused before registry access");
        }
        Expect(!Program.ShouldSurface([WindowsStartup.TrayFlag]) && Program.ShouldSurface([])
               && Program.ShouldSurface(["--engine", WindowsStartup.TrayFlag]),
               "tray startup stays hidden; ordinary launches surface and engine arguments stay separate");
        Expect(saves == 0 && world.Engine.Told.Count == commands,
               "startup state is never saved in app settings or sent to the engine");
        world.Settings.KeepInTray = false;
        var window = new MainWindow(model, new ThemeService(app.Resources, app.Dispatcher), world.Settings)
        {
            Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false,
        };
        Program.ShowAtStartup(window, [WindowsStartup.TrayFlag]);
        Expect(!window.IsVisible && !world.Settings.KeepInTray && world.Engine.Told.Count > 0,
               "tray startup never shows the window with close-to-tray off, while the engine is started");
        Program.ShowAtStartup(window, []);
        Expect(window.IsVisible, "an ordinary startup still shows the window");
        window.Quitting = true;
        window.Close();
    }

    private static void Expect(bool ok, string what)
    {
        if (ok) _passed++;
        else _failed++;
        _log.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    }

    private static void Identities(string folder)
    {
        Directory.CreateDirectory(folder);
        var certificate = Path.Combine(folder, Identity.Certificate);
        var key = Path.Combine(folder, Identity.Key);
        var secret = Path.Combine(folder, Identity.Secret);
        using var pair = RSA.Create(2048);
        var request = new CertificateRequest("CN=AirDrop receiver", pair, HashAlgorithmName.SHA256,
                                            RSASignaturePadding.Pkcs1);
        using var legacy = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                                    DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(certificate, legacy.ExportCertificatePem());
        File.WriteAllText(key, pair.ExportPkcs8PrivateKeyPem());
        Expect(Identity.Ensure(folder), "an existing plaintext identity migrates");
        Expect(File.ReadAllText(key).Contains("BEGIN ENCRYPTED PRIVATE KEY") && File.Exists(secret),
               "migration replaces the plaintext key with encrypted PEM and a protected secret");
        if (!File.Exists(secret)) return;
        string Password() => Encoding.ASCII.GetString(ProtectedData.Unprotect(File.ReadAllBytes(secret),
                                                       null, DataProtectionScope.CurrentUser));
        using (var migrated = X509Certificate2.CreateFromEncryptedPemFile(certificate, Password(), key))
        {
            using var migratedKey = migrated.GetRSAPrivateKey()!;
            Expect(migrated.RawData.SequenceEqual(legacy.RawData) &&
                   migratedKey.ExportSubjectPublicKeyInfo().SequenceEqual(pair.ExportSubjectPublicKeyInfo()),
                   "migration keeps the certificate and RSA identity");
        }
        var before = File.ReadAllBytes(key);
        Expect(Identity.Ensure(folder) && before.SequenceEqual(File.ReadAllBytes(key)),
               "an encrypted identity is reused across starts");
        var user = WindowsIdentity.GetCurrent().User!;
        var access = new DirectoryInfo(folder).GetAccessControl();
        Expect(access.AreAccessRulesProtected && access.GetAccessRules(true, true, typeof(SecurityIdentifier))
                   .Cast<FileSystemAccessRule>().All(rule => rule.IdentityReference.Equals(user)),
               "only the current user has access to the keys folder");
        foreach (var path in new[] { key, secret, certificate })
        {
            var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            Expect(rules.Cast<FileSystemAccessRule>().All(rule => rule.IdentityReference.Equals(user)),
                   "stored identity files grant access only to the current user");
        }
        foreach (var damage in new[] { "missing", "corrupt", "wrong" })
        {
            var old = File.ReadAllText(certificate);
            if (damage == "missing") File.Delete(secret);
            else File.WriteAllBytes(secret, damage == "corrupt" ? [1, 2, 3] :
                ProtectedData.Protect(Encoding.ASCII.GetBytes("wrong password"), null, DataProtectionScope.CurrentUser));
            var reasons = new List<string>();
            Expect(Identity.Ensure(folder, reasons.Add) && File.ReadAllText(certificate) != old,
                   $"a {damage} secret replaces the unusable identity");
            Expect(reasons.Any(reason => reason.Contains("cannot be loaded") && reason.Contains("new identity")),
                   "identity recovery logs why it made a replacement");
            using var recovered = X509Certificate2.CreateFromEncryptedPemFile(certificate, Password(), key);
            Expect(recovered.HasPrivateKey, "the replacement identity decrypts");
        }

        // Exercise both implementations with each other's files, not a stand-in DPAPI blob.
        var repo = Repo.Find() ?? throw new InvalidOperationException("the repository was not found");
        var pythonFolder = Path.Combine(folder, "python");
        var start = new ProcessStartInfo("python")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-c",
            "import sys; sys.path.insert(0, sys.argv[1]); import identity, receiver, sender; " +
            "receiver.tls_context(sys.argv[2]); sender.client_context(sys.argv[2]); " +
            "identity.ensure_self_signed(sys.argv[3])", Path.Combine(repo, "airdrop"), folder, pythonFolder })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            throw new TimeoutException("Python identity interoperability check did not finish");
        }
        Expect(process.ExitCode == 0, "Python loads the app's encrypted identity for both TLS roles");
        _log.Write(errors.GetAwaiter().GetResult());
        var pythonCertificate = Path.Combine(pythonFolder, Identity.Certificate);
        var original = File.ReadAllBytes(pythonCertificate);
        Expect(Identity.Ensure(pythonFolder) && original.SequenceEqual(File.ReadAllBytes(pythonCertificate)),
               "the app reuses Python's encrypted identity");
        IdentityOverlap(folder, repo, pair, legacy);
    }

    private static void IdentityOverlap(string root, string repo, RSA pair, X509Certificate2 legacy)
    {
        foreach (var mode in new[] { "migration", "recovery", "receiver", "sender" })
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "overlap-" + mode)).FullName;
            var certificate = Path.Combine(folder, Identity.Certificate);
            var key = Path.Combine(folder, Identity.Key);
            File.WriteAllText(certificate, legacy.ExportCertificatePem());
            File.WriteAllText(key, pair.ExportPkcs8PrivateKeyPem());
            if (mode != "migration")
                Expect(Identity.Ensure(folder), "prepare encrypted identity for overlap check");
            if (mode == "recovery") File.WriteAllBytes(Path.Combine(folder, Identity.Secret), [1, 2, 3]);
            var before = File.ReadAllBytes(certificate);
            var start = new ProcessStartInfo("python")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-c",
                "import sys; sys.path.insert(0, sys.argv[1]); from test_identity import overlap_worker; " +
                "overlap_worker(*sys.argv[2:])", Path.Combine(repo, "airdrop", "tests"), folder,
                mode is "migration" or "recovery" ? "publish" : mode }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            Task<bool>? ensure = null;
            try
            {
                var paused = process.StandardOutput.ReadLineAsync();
                if (!paused.Wait(15_000) || paused.Result != "paused")
                    throw new InvalidOperationException("Python did not reach the overlap boundary");
                using var attempting = new ManualResetEventSlim();
                ensure = Task.Run(() => { attempting.Set(); return Identity.Ensure(folder); });
                if (!attempting.Wait(5_000)) throw new TimeoutException("app identity check did not start");
                Expect(!ensure.Wait(400), $"app waits for Python {mode} transaction");
                process.StandardInput.WriteLine();
                process.StandardInput.Flush();
                if (!process.WaitForExit(15_000) || !ensure.Wait(15_000))
                    throw new TimeoutException("identity overlap check did not finish");
                Expect(process.ExitCode == 0 && ensure.Result, $"both {mode} transactions finish");
                _log.Write(errors.GetAwaiter().GetResult());
                var password = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(folder, Identity.Secret)),
                                                       null, DataProtectionScope.CurrentUser);
                try
                {
                    using var loaded = X509Certificate2.CreateFromEncryptedPemFile(certificate,
                        Encoding.ASCII.GetString(password), key);
                    Expect(loaded.HasPrivateKey && (mode == "recovery" ?
                        !before.SequenceEqual(File.ReadAllBytes(certificate)) :
                        before.SequenceEqual(File.ReadAllBytes(certificate))),
                        $"overlapping {mode} leaves the expected loadable identity");
                }
                finally { CryptographicOperations.ZeroMemory(password); }
            }
            finally
            {
                if (!process.HasExited) process.Kill();
                process.WaitForExit(5_000);
                if (ensure is not null && !ensure.Wait(15_000))
                    throw new TimeoutException("app identity transaction remained blocked");
            }
        }
    }

    /// <summary>The 48 accents Windows offers, and the extremes nobody offers.</summary>
    private static readonly string[] Accents =
    [
        "FFB900", "FF8C00", "F7630C", "CA5010", "DA3B01", "EF6950", "D13438", "FF4343", "E74856", "E81123",
        "EA005E", "C30052", "E3008C", "BF0077", "C239B3", "9A0089", "0078D4", "0063B1", "8E8CD8", "6B69D6",
        "8764B8", "744DA9", "B146C2", "881798", "0099BC", "2D7D9A", "00B7C3", "038387", "00B294", "018574",
        "00CC6A", "10893E", "7A7574", "5D5A58", "68768A", "515C6B", "567C73", "486860", "498205", "107C10",
        "767676", "4C4A48", "69797E", "4A5459", "647C64", "525E54", "847545", "7E735F",
        "FFFFFF", "000000", "FFFF00", "0000FF", "808080",
    ];

    private static void Colors()
    {
        double text = double.MaxValue, onFill = double.MaxValue, accentText = double.MaxValue,
               fill = double.MaxValue, tinted = double.MaxValue;
        foreach (var accent in Accents.Select(Rgb.Hex))
            foreach (var palette in (Palette[])[Palette.Light(accent), Palette.Night(accent)])
            {
                foreach (var surface in palette.Surfaces)
                {
                    foreach (var ink in (Rgb[])[palette.Ink, palette.InkSoft, palette.Ember])
                        text = Math.Min(text, Rgb.Contrast(ink, surface));
                    accentText = Math.Min(accentText, Rgb.Contrast(palette.AccentText, surface));
                    fill = Math.Min(fill, Rgb.Contrast(palette.Accent, surface));
                }
                onFill = Math.Min(onFill, Rgb.Contrast(palette.OnAccent, palette.Accent));
                tinted = Math.Min(tinted, Math.Min(Rgb.Contrast(palette.Ink, palette.AccentSoft),
                                                   Rgb.Contrast(palette.InkSoft, palette.AccentSoft)));
            }
        var cases = $"{Accents.Length} accents, light and dark";
        Expect(text >= Palette.TextContrast, $"ink, soft ink and ember on every surface: lowest {text:0.00} to 1");
        Expect(onFill >= Palette.TextContrast, $"text on an accent fill, {cases}: lowest {onFill:0.00} to 1");
        Expect(accentText >= Palette.TextContrast, $"accent as text or focus ring on every surface, {cases}: lowest {accentText:0.00} to 1");
        Expect(fill >= Palette.ShapeContrast, $"accent as a fill against every surface, {cases}: lowest {fill:0.00} to 1");
        Expect(tinted >= Palette.TextContrast, $"ink on the tint of a transfer in progress, {cases}: lowest {tinted:0.00} to 1");
    }

    private static void Wording()
    {
        Expect(Words.Size(512) == "512 bytes" && Words.Size(1) == "1 byte" && Words.Size(26_100_000) == "24.9 MB"
               && Words.Size(3 * 1024L * 1024 * 1024) == "3.00 GB" && Words.Size(20_000) == "20 KB",
               "sizes read as Windows writes them");
        Expect(Words.Things(["a.jpg"]) == "a photo" && Words.Things(["a.jpg", "b.HEIC"]) == "2 photos"
               && Words.Things(["a.mov"]) == "a video" && Words.Things(["a.jpg", "b.mov"]) == "2 photos and videos"
               && Words.Things(["a.pdf", "b.jpg"]) == "2 files" && Words.Things([], 1) == "a link"
               && Words.Things([], 3) == "3 links" && Words.Things([]) == "something",
               "what is being sent is named by its kind");
        Expect(Words.KindOfDevice("iPhone14,2") == DeviceKind.Phone && Words.KindOfDevice("iPad13,1") == DeviceKind.Tablet
               && Words.KindOfDevice("MacBookPro18,3") == DeviceKind.Computer && Words.KindOfDevice("Macmini9,1") == DeviceKind.Computer
               && Words.KindOfDevice(null) == DeviceKind.Other && Words.KindOfDevice("RealityDevice14,1") == DeviceKind.Other,
               "a device's kind comes from the model it reports");
        Expect(Words.Percent(0.999) == "99%" && Words.Percent(1) == "100%" && Words.Percent(-1) == "0%",
               "a percentage never shows 100 before the end");
    }

    private static MainView Show(Stage.World world)
    {
        var view = new MainView { DataContext = world.Model };
        var host = new AdornerDecorator { Child = view };
        host.Measure(new Size(460, 720));
        host.Arrange(new Rect(0, 0, 460, 720));
        host.UpdateLayout();
        return view;
    }

    private static bool Press(FrameworkElement view, string name)
    {
        view.UpdateLayout();
        var control = Stage.Controls(view).FirstOrDefault(one => one.Line.Contains($"\"{name}\"")).Control;
        if (control is null) return false;
        var peer = UIElementAutomationPeer.CreatePeerForElement(control);
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
        else if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
        else return false;
        Stage.Pump(view.Dispatcher, 80);
        return true;
    }

    private static void States(Application app)
    {
        // The radio, from off to on and through each way it can fail.
        var world = Stage.NewWorld(app.Dispatcher);
        var view = Show(world);
        var model = world.Model;
        Expect(model.Across == Across.RadioOff && model.Presence == "Not visible" && !model.RadioOn,
               $"no link: \"{model.PanelTitle}\", header \"{model.Presence}\"");
        Expect(Press(view, "Turn On AirDrop") && world.Radio.Starts == 1 && model.Across == Across.RadioStarting && model.RadioOn,
               $"Turn On AirDrop -> the radio is started once: \"{model.PanelTitle}\"");
        world.Radio.Become(RadioPhase.Starting, "radio: card ready");
        Expect(model.PanelBody == "radio: card ready", "while it starts, the panel shows what the radio last wrote");
        world.Engine.Say(new { @event = "link", up = true });
        world.Engine.Say(new { @event = "receiving", on = true });
        Expect(model.Across == Across.Nearby && world.Radio.Phase == RadioPhase.Running && model.Presence == "Visible as STUDY-PC",
               $"link up -> the devices' side, header \"{model.Presence}\", \"{model.NobodyTitle}\"");
        world.Engine.Say(new { @event = "looking", on = true, beacon = false, why = "the radio is off" });
        world.Engine.Say(new { @event = "unwilling", count = 2 });
        world.Engine.Say(new { @event = "receiving", on = false, reason = "port_held" });
        Expect(model.Notices.Count == 3 && model.Presence == "Not visible yet",
               $"no Bluetooth, a held port and shy devices -> three notices: {string.Join(" | ", model.Notices)}");
        world.Engine.Say(new { @event = "unwilling", count = 1 });
        Expect(model.Notices.Contains("A device nearby only answers devices it knows. Set its AirDrop to Everyone for 10 Minutes."),
               "one device that refuses this PC or keeps its name -> the notice says what to change on it");
        world.Engine.Say(new { @event = "unwilling", count = 0 });
        Expect(model.Notices.Count == 2, "it stops refusing, or leaves -> the notice goes");
        world.Engine.Say(new { @event = "link", up = false });
        world.Radio.Become(RadioPhase.Failed, "radio: could not get 6 locked 2 MiB blocks below 4 GiB");
        Expect(model.Across == Across.RadioProblem && model.PanelTitle == "AirDrop stopped" && model.PanelHasLog,
               $"the radio ends by itself -> \"{model.PanelTitle}\": \"{model.PanelBody}\"");
        Expect(Press(view, "Show Logs") && world.Desk.Opened.Last().StartsWith("open "), "Show Logs -> the log folder is opened (recorded)");
        Expect(Press(view, "Try Again") && world.Radio.Starts == 2, "Try Again -> the radio is started again");
        world.Radio.Become(RadioPhase.Failed, "");
        Expect(model.PanelTitle == "AirDrop did not start" && model.PanelBody == "It left nothing in its log.",
               $"it fails before running, silent -> \"{model.PanelTitle}\": \"{model.PanelBody}\"");
        world.Radio.Become(RadioPhase.Refused);
        Expect(model.PanelTitle == "AirDrop stayed off" && !model.PanelHasLog && !model.RadioOn,
               $"Windows was told no -> \"{model.PanelTitle}\": \"{model.PanelBody}\"");
        world.Radio.Become(RadioPhase.NotBuilt);
        Expect(model.PanelBody.Contains("build-radio.ps1"), $"nothing built -> \"{model.PanelTitle}\": \"{model.PanelBody}\"");

        // The engine, each way it can be missing.
        foreach (var (why, detail, title) in new[]
                 {
                     ("no_python", "", "Python was not found"), ("no_engine", "", "winawdl's files are incomplete"),
                     ("no_certificate", "OpenSSL not found", "No certificate could be made"),
                     ("ended", "", "The AirDrop engine stopped"),
                 })
        {
            world.Engine.End(why, detail);
            Expect(model.Across == Across.EngineDown && model.PanelTitle == title && model.PanelBody.Length > 20 || detail.Length > 0 && model.PanelBody == detail,
                   $"engine {why} -> \"{model.PanelTitle}\": \"{model.PanelBody}\"");
        }
        var before = world.Engine.Told.Count;
        Expect(Press(view, "Start Again") && model.Across != Across.EngineDown && world.Engine.Told.Count > before,
               "Start Again -> the engine is started and told whether to look");

        // The switch does what the panel's button does, and takes it back.
        world = Stage.NewWorld(app.Dispatcher);
        view = Show(world);
        model = world.Model;
        Expect(model.FindMeLabel == "Receiving Off" && model.FindMeOff, "the pill says receiving is off while the radio is");
        model.FindByEveryone.Execute(null);
        Expect(world.Radio.Starts == 1 && model.RadioOn && model.FindMeEveryone && model.FindMeLabel == "Everyone",
               "choosing Everyone in the pill -> the radio is started, and the pill says Everyone");
        model.TurnOff.Execute(null);
        Expect(world.Radio.Phase == RadioPhase.Off && !model.RadioOn && model.FindMeOff,
               "Receiving Off in the pill -> the radio the app started is stopped");

        // A transfer that the device walks away from, and one that is lost on the way in.
        world = Stage.NewWorld(app.Dispatcher);
        view = Show(world);
        model = world.Model;
        var copied = Stage.SampleFiles(Path.Combine(Path.GetTempPath(), "winawdl-check-files"));
        world.Desk.Picked = copied[..2];
        model.Paste.Execute(null);
        Expect(model.Held.Count == 2, "the command behind Ctrl+V takes the files on the clipboard in hand (the keys were not pressed)");
        model.ClearHeld.Execute(null);
        world.Engine.Say(new { @event = "link", up = true });
        world.Engine.Say(new { @event = "peer", id = "a", name = "Stand-in iPhone", model = "iPhone14,2" });
        var files = Stage.SampleFiles(Path.Combine(Path.GetTempPath(), "winawdl-check-files"));
        world.Desk.Picked = files[..2];
        Expect(Press(view, "Stand-in iPhone") && model.Held.Count == 2 && world.Engine.Told.Last().Contains("\"do\":\"send\""),
               "press a device with nothing in hand -> files are chosen and sent to it in one go");
        world.Engine.Say(new { @event = "peer_gone", id = "a" });
        Expect(model.Peers.Count == 1, "it stops announcing itself mid-transfer -> its plate stays until the transfer ends");
        world.Engine.Say(new { @event = "send", id = "s1", state = "failed", reason = "no_answer" });
        Expect(model.Peers.Count == 0, "the transfer fails -> the plate of the device that left goes too");
        world.Engine.Say(new { @event = "asked", id = "r1", sender = "", files = Array.Empty<object>(), links = 2, waits = true });
        Expect(model.Incoming[0].Headline == "A device nearby wants to send you 2 links", $"a nameless sender with links -> \"{model.Incoming[0].Headline}\"");
        world.Engine.Say(new { @event = "answered", id = "r1", how = "left" });
        Expect(model.Incoming.Count == 0, "the sender stops waiting -> its request goes by itself");
        world.Engine.Say(new { @event = "asked", id = "r2", sender = "Stand-in iPhone", files = new[] { new { name = "a.mov", size = 5, type = "" } }, links = 0, total = 5, waits = false });
        world.Engine.Say(new { @event = "lost", id = "r2", reason = "nothing arrived" });
        Expect(model.Incoming[0].IsLost && Press(view, "Dismiss") && model.Incoming.Count == 0,
               "a transfer lost on the way in says so, and Dismiss removes it");

        // A device that is announced again while a transfer to it runs.
        world.Engine.Say(new { @event = "peer", id = "b", name = "Stand-in iPad", model = "iPad13,1" });
        Press(view, "Stand-in iPad");
        world.Engine.Say(new { @event = "peer_gone", id = "b" });
        world.Engine.Say(new { @event = "peer", id = "b", name = "Stand-in iPad", model = "iPad13,1" });
        Expect(model.Peers.Count == 1 && model.Peers[0].IsBusy,
               "it is announced again mid-transfer -> the same plate, still sending");
        world.Engine.Say(new { @event = "send", id = "s2", state = "failed", reason = "no_answer" });
        Expect(model.Peers.Count == 1, "that transfer fails -> its plate stays, because the device is back");

        // A Live Photo is one photo, on the way in and on the shelf.
        world.Engine.Say(new { @event = "asked", id = "r3", sender = "Stand-in iPhone", files = new[] { new { name = "IMG_1.pvt", size = 0, type = "com.apple.private.live-photo-bundle" } }, links = 0, total = 0, waits = true });
        Expect(model.Incoming[0].Headline == "Stand-in iPhone wants to send you a photo" && model.Incoming[0].Detail == "IMG_1",
               $"a Live Photo is asked about -> \"{model.Incoming[0].Headline}\", \"{model.Incoming[0].Detail}\"");
        var shelf = Path.Combine(Path.GetTempPath(), "winawdl-check-files");
        world.Engine.Say(new
        {
            @event = "stored", id = "r3", folder = shelf, names = new[] { "IMG_1.MOV", "IMG_1.jpg", "note.txt" },
            live = new[] { new[] { "IMG_1.jpg", "IMG_1.MOV" } },
        });
        Expect(model.Received.Select(file => file.Name).SequenceEqual(["IMG_1.jpg", "note.txt"]),
               $"a Live Photo and a note are stored -> on the shelf: {string.Join(", ", model.Received.Select(file => file.Name))}");
        var live = model.Received.FirstOrDefault(file => file.Name == "IMG_1.jpg");
        live?.PlayVideo?.Execute(null);
        Expect(live is { HasVideo: true, Hint: "IMG_1.jpg, a Live Photo" } && world.Desk.Opened.Last() == "open " + Path.Combine(shelf, "IMG_1.MOV"),
               "the photo says it is a Live Photo, and Play the video opens its video (recorded)");
        Expect(model.Received.First(file => file.Name == "note.txt") is { HasVideo: false, Hint: "note.txt" }, "any other file has no video to play");

        Always(app);
        Requests(app);
        KnownSenders(app);
    }

    private static object Ask(string id, string sender, string model, bool waits = true) => new
    {
        @event = "asked", id, sender, model, links = 0, total = 5, waits,
        files = new[] { new { name = "a.jpg", size = 5, type = "public.jpeg" } },
    };

    /// <summary>A sender's name/model are not proof of identity.</summary>
    private static void Always(Application app)
    {
        var world = Stage.NewWorld(app.Dispatcher);
        var view = Show(world);
        var model = world.Model;
        var attention = new List<string>();
        model.Attention += attention.Add;
        world.Engine.Say(new { @event = "link", up = true });

        world.Engine.Say(Ask("r1", "Stand-in iPhone", "iPhone14,2"));
        Expect(model.Incoming[0].IsAsking && !model.Incoming[0].ShowsAlways
               && attention.Count == 1,
               "named senders require approval and have no Always button");
        Expect(Press(view, "Accept") && world.Engine.Told.Last().Contains("\"id\":\"r1\",\"accept\":true"),
               "a single request can still be accepted");
        world.Engine.Say(new { @event = "answered", id = "r1", how = "accepted" });

        var told = world.Engine.Told.Count;
        world.Engine.Say(Ask("r2", "Stand-in iPhone", "iPhone14,2"));
        Expect(model.Incoming[0].IsAsking && world.Engine.Told.Count == told
               && attention.Count == 2,
               "a repeat with the identical self-reported identity still asks");
        world.Engine.Say(new { @event = "answered", id = "r2", how = "declined" });

        var kept = new AppSettings { AlwaysAccept = [new KnownDevice { Name = "Stand-in iPad", Model = "iPad13,1" }] };
        var again = new TableModel(world.Engine, world.Radio, world.Desk, kept, world.Startup, world.Explorer);
        Expect(again.Trusted is [{ Name: "Stand-in iPad" }],
               "previously saved name-only entries remain visible to remove");
        told = world.Engine.Told.Count;
        world.Engine.Say(Ask("r3", "Stand-in iPad", "iPad13,1"));
        Expect(again.Incoming[0].IsAsking && world.Engine.Told.Count == told,
               "even a legacy saved sender is never automatically accepted");
    }

    /// <summary>Only the people listed may send: the setting reaches the engine
    /// when it starts and when it changes, and the list takes only what Apple
    /// can vouch for.</summary>
    private static void KnownSenders(Application app)
    {
        var world = Stage.NewWorld(app.Dispatcher);
        var model = world.Model;
        model.SenderDraft = "not a person";
        model.AddSender.Execute(null);
        Expect(model.HasSenderError && world.Settings.KnownSenders.Count == 0,
               "something that is neither an email address nor a phone number is not listed");
        model.SenderDraft = "Friend@Example.com";
        model.AddSender.Execute(null);
        Expect(!model.HasSenderError && model.SenderDraft.Length == 0
               && world.Engine.Told.Last().Contains("\"known_senders\":[\"Friend@Example.com\"]"),
               "an email address is listed and the engine told");
        model.SenderDraft = "friend@example.com";
        model.AddSender.Execute(null);
        Expect(model.HasSenderError && model.KnownSenders.Count == 1,
               "the same address in other letters is not listed twice");
        model.SenderDraft = "+1 (555) 010-9999";
        model.AddSender.Execute(null);
        Expect(model.KnownSenders.Count == 2, "a phone number is listed as typed");

        Expect(!model.NobodyCanSend, "anyone can send until asked otherwise");
        model.OnlyKnownSenders = true;
        Expect(world.Settings.ContactsOnly
               && world.Engine.Told.Last().Contains("\"contacts_only\":true"),
               "choosing only the people listed tells the engine and is kept");
        model.KnownSenders[0].Forget!.Execute(null);
        model.KnownSenders[0].Forget!.Execute(null);
        Expect(model.NobodyCanSend && world.Engine.Told.Last().Contains("\"known_senders\":[]"),
               "with the list emptied nobody can send, and the window says so");

        model.SenderDraft = "friend@example.com";
        model.AddSender.Execute(null);
        model.EveryoneForAWhile = true;
        Expect(model.EveryoneForAWhile && !model.OnlyKnownSenders && world.Settings.ContactsOnly
               && world.Engine.Told.Last().Contains("\"everyone_for\":600"),
               "everyone for 10 minutes tells the engine and keeps the list as what comes after");
        var until = DateTimeOffset.Now.AddMinutes(10).ToUnixTimeSeconds();
        world.Engine.Say(new
        {
            @event = "settings", refused = new { }, name = "STUDY-PC", folder = Path.GetTempPath(),
            ask = true, contacts_only = false, known_senders = new[] { "friend@example.com" },
            everyone_until = until,
        });
        Expect(model.EveryoneForAWhile && world.Settings.ContactsOnly,
               "while everyone may send, what is kept is still the list");
        world.Engine.Say(new
        {
            @event = "settings", refused = new { }, name = "STUDY-PC", folder = Path.GetTempPath(),
            ask = true, contacts_only = true, known_senders = new[] { "friend@example.com" },
            everyone_until = (double?) null,
        });
        Expect(model.OnlyKnownSenders && !model.EveryoneForAWhile,
               "when the ten minutes end the window shows the list in force again");
        model.AnyoneCanSend = true;
        Expect(!world.Settings.ContactsOnly && world.Engine.Told.Last().Contains("\"contacts_only\":false"),
               "everyone, for good, can still be chosen");

        var kept = new AppSettings
        {
            Folder = Path.GetTempPath(), ContactsOnly = true, KnownSenders = ["friend@example.com"],
        };
        var engine = new ScriptedEngine();
        var again = new TableModel(engine, world.Radio, world.Desk, kept, world.Startup, world.Explorer);
        again.StartTheEngine();
        Expect(again.OnlyKnownSenders && again.KnownSenders is [{ Address: "friend@example.com" }],
               "the choice and the list come back with the app, as the engine started with them");
    }

    /// <summary>The card that shows a request while the window is not in front.</summary>
    private static void Requests(Application app)
    {
        var world = Stage.NewWorld(app.Dispatcher);
        var model = world.Model;
        var card = new RequestsView { DataContext = model };
        var host = new AdornerDecorator { Child = card };
        void Lay()
        {
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
        }

        world.Engine.Say(new { @event = "link", up = true });
        world.Engine.Say(Ask("r1", "Stand-in iPhone", "iPhone14,2"));
        Lay();
        var controls = Stage.Controls(card).Select(one => one.Line).ToList();
        Expect(controls.Count == 2 && controls[0].Contains("\"Decline\"") && controls[1].Contains("\"Accept\"")
               && controls.All(line => !line.Contains("NO FOCUS RING")),
               $"a waiting request is a card with two presses: {string.Join(", ", controls)}");
        Expect(Press(card, "Accept") && world.Engine.Told.Last().Contains("\"id\":\"r1\",\"accept\":true"), "Accept on the card -> the request is accepted");
        world.Engine.Say(new { @event = "answered", id = "r1", how = "accepted" });
        Lay();
        Expect(Stage.Controls(card).Count == 0 && !model.HasRequests, "once answered, the card is gone while the transfer runs in the window");
        world.Engine.Say(Ask("r2", "Stand-in iPhone", "iPhone14,2"));
        Lay();
        Expect(Press(card, "Decline") && world.Engine.Told.Last().Contains("\"id\":\"r2\",\"accept\":false"), "Decline on the card -> the request is declined");

        // The window that holds the cards, kept off screen.
        var inFront = false;
        var window = new RequestWindow(model, () => inFront) { Spot = new Point(-30000, -30000) };
        world.Settings.ShowRequests = false;
        Expect(!window.Announce() && !window.IsVisible, "with the setting off, a request is not shown on screen and is said another way");
        world.Settings.ShowRequests = true;
        // What Announce answers also depends on Windows: a full-screen app would have it say no.
        var announced = window.Announce();
        Stage.Pump(app.Dispatcher, 80);
        Expect(!announced || window.IsVisible && !window.IsActive && window.Left <= -29000,
               announced ? "a request while the window is not in front -> the card window shows, without taking the keyboard"
                         : "Windows takes no notices right now (a full-screen app or a presentation): nothing is shown");
        inFront = true;
        window.Reconsider();
        Expect(!window.IsVisible, "the app's window comes to the front -> the card window goes: the request is there already");
        inFront = false;
        window.Reconsider();
        Expect(!announced || window.IsVisible, "it leaves the front with the request still waiting -> the card window is back");
        world.Engine.Say(new { @event = "answered", id = "r2", how = "left" });
        Stage.Pump(app.Dispatcher, 80);
        Expect(!window.IsVisible && !model.HasRequests, "the sender stops waiting -> the card window goes by itself");

        // What arrived, said on a card that leads to it.
        var shelf = Path.Combine(Path.GetTempPath(), "winawdl-check-files");
        world.Engine.Say(new { @event = "stored", id = "r1", folder = shelf, names = Array.Empty<string>() });
        var arrivals = new List<ArrivalModel>();
        model.Arrived += arrivals.Add;
        world.Engine.Say(Ask("r3", "Stand-in iPhone", "iPhone14,2", waits: false));
        world.Engine.Say(new { @event = "incoming", id = "r3", done = 2, total = 5 });
        Expect(model.TrayText == "winawdl\nStand-in iPhone is sending you a photo: 40% of 5 bytes",
               $"the notification area icon says what is arriving and how far along: \"{model.TrayText.Replace("\n", " / ")}\"");
        world.Engine.Say(new { @event = "stored", id = "r3", folder = shelf, names = new[] { "IMG_1.jpg" } });
        Expect(arrivals is [{ Headline: "Stand-in iPhone sent you a photo", Single: true, PressLabel: "Open" }]
               && arrivals[0].Detail.StartsWith("IMG_1.jpg"), $"one file stored -> \"{arrivals.LastOrDefault()?.Headline}\", \"{arrivals.LastOrDefault()?.Detail}\"");
        Expect(model.TrayText == "winawdl\nNot visible yet", $"with nothing moving the icon says how visible this PC is: \"{model.TrayText.Replace("\n", " / ")}\"");
        var said = window.Announce(arrivals[0]);
        Stage.Pump(app.Dispatcher, 80);
        Expect(said == announced && (!said || window.IsVisible && model.Arrivals.Count == 1),
               said ? "what arrived while the window is not in front -> a card for it" : "Windows takes no notices right now: no card");
        if (!said) model.Arrivals.Add(arrivals[0]);
        Lay();
        controls = Stage.Controls(card).Select(one => one.Line).ToList();
        Expect(controls.Count == 4 && controls[0].Contains("\"Open IMG_1.jpg\"") && controls[1].Contains("\"Dismiss\"")
               && controls[2].Contains("\"Show in Folder\"") && controls[3].Contains("\"Open\""),
               $"the card is itself the press, and holds three more: {string.Join(", ", controls)}");
        Expect(Press(card, "Open IMG_1.jpg") && world.Desk.Opened.Last() == "open " + Path.Combine(shelf, "IMG_1.jpg") && model.Arrivals.Count == 0,
               "press the card -> the file is opened (recorded), and the card goes");
        Stage.Pump(app.Dispatcher, 80);
        Expect(!window.IsVisible, "with no card left, the card window goes");

        world.Engine.Say(Ask("r4", "Stand-in iPhone", "iPhone14,2", waits: false));
        world.Engine.Say(new { @event = "stored", id = "r4", folder = shelf, names = new[] { "a.jpg", "b.jpg" } });
        Expect(arrivals.Last() is { Single: false, PressLabel: "Show in Folder" }, "several files stored -> the card shows them in their folder");
        model.Arrivals.Add(arrivals.Last());
        Lay();
        Expect(Press(card, "Show what arrived in its folder") && world.Desk.Opened.Last() == "reveal " + Path.Combine(shelf, "a.jpg"),
               "press it -> the folder is opened on the first of them (recorded)");
        model.Arrivals.Add(arrivals.Last());
        Lay();
        Expect(Press(card, "Dismiss") && model.Arrivals.Count == 0, "Dismiss -> the card goes, and nothing is opened");
        model.Arrivals.Add(arrivals.Last());
        inFront = true;
        window.Reconsider();
        Expect(model.Arrivals.Count == 0 && !window.IsVisible, "the app's window comes to the front -> the card goes: the files are on its shelf");

        // Sending, as the icon says it.
        var held = Stage.SampleFiles(shelf);
        world.Engine.Say(new { @event = "peer", id = "a", name = "Stand-in iPad", model = "iPad13,1" });
        model.Hold(held[..2]);
        model.SendTo(model.Peers[0]);
        Expect(model.TrayText == "winawdl\nWaiting for Stand-in iPad to accept", $"a send that waits: \"{model.TrayText.Replace("\n", " / ")}\"");
        world.Engine.Say(new { @event = "send", id = "s1", state = "sending", done = 50, total = 200 });
        Expect(model.TrayText == "winawdl\nSending to Stand-in iPad: 25% of 200 bytes", $"a send under way: \"{model.TrayText.Replace("\n", " / ")}\"");
        world.Engine.Say(new { @event = "send", id = "s1", state = "sent" });
        Expect(model.TrayText == "winawdl\nSent 2 files to Stand-in iPad", $"a send that got through: \"{model.TrayText.Replace("\n", " / ")}\"");
        world.Engine.Say(Ask("r5", new string('n', 200), "iPhone14,2"));
        Expect(model.TrayText.Length == TableModel.MostTrayText && model.TrayText.EndsWith('…'),
               "a line too long for Windows is cut where Windows would cut it");
        window.Close();
    }

    /// <summary>What a log holds so far, read while its writer still has it open.</summary>
    private static string Written(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static void Supervisor(string logs)
    {
        // Ping stands in for both daemons: it runs until interrupted, and says so when it is.
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        Daemon Forever(string name) => new(name, ping, ["-t", "127.0.0.1"]);

        (int Code, double Seconds, string Ended) Supervised(int owner, Daemon radio, Daemon awdl, Action<EventWaitHandle> during)
        {
            var name = $"winawdl-check-{Environment.ProcessId}-{Guid.NewGuid():N}";
            using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\" + name);
            var clock = Stopwatch.StartNew();
            var run = Task.Run(() => RadioSupervisor.Supervise(owner, name, radio, awdl, logs));
            during(stop);
            var code = run.Wait(TimeSpan.FromSeconds(30)) ? run.Result : -1;
            var status = Path.Combine(logs, RadioSupervisor.StatusFile);
            return (code, clock.Elapsed.TotalSeconds, File.Exists(status) ? File.ReadAllText(status) : "");
        }

        var asked = Supervised(Environment.ProcessId, Forever("radio"), Forever("awdld"), stop =>
        {
            Thread.Sleep(2500);
            stop.Set();
        });
        var said = File.ReadAllText(Path.Combine(logs, "awdld.log"));
        Expect(asked.Code == 0 && asked.Ended.Contains("stopped") && said.Contains("Reply from"),
               $"asked to stop -> both daemons end, exit {asked.Code}, their output is in the logs");
        Expect(said.Contains("Control-C") && asked.Seconds < 2.5 + 4,
               $"they are interrupted the way Ctrl+C does, not killed: stopped {asked.Seconds - 2.5:0.0} s after the request");
        Expect(Process.GetProcessesByName("PING").Length == 0, "no daemon is left running");

        // A daemon that says one line with a dash in it on its error stream, then stays.
        var dashed = $"up {(char)0x2014} ready";
        var speaker = new Daemon("radio", Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        [
            "-NoProfile", "-Command",
            "$e = [Console]::OpenStandardError(); $b = [Text.Encoding]::UTF8.GetBytes(\"" + dashed + "`n\"); "
            + "$e.Write($b, 0, $b.Length); $e.Flush(); Start-Sleep 30",
        ]);
        var spoken = Supervised(Environment.ProcessId, speaker, Forever("awdld"), stop =>
        {
            // PowerShell takes its time to start on a busy machine: wait for the line, not for a clock.
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed.TotalSeconds < 25 && !Written(Path.Combine(logs, "radio.log")).Contains(dashed))
                Thread.Sleep(200);
            stop.Set();
        });
        Expect(spoken.Code == 0 && File.ReadAllText(Path.Combine(logs, "radio.log")).Contains(dashed),
               "what a daemon writes to its error stream in UTF-8 is in its log as written");

        var died = Supervised(Environment.ProcessId, new Daemon("radio", ping, ["-n", "1", "127.0.0.1"]), Forever("awdld"), _ => { });
        Expect(died.Code == 3 && died.Ended.Contains("radio"), $"a daemon ends by itself -> exit {died.Code}, the status names it, the other is stopped");

        using var owner = Process.Start(new ProcessStartInfo(ping, "-n 3 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        var orphaned = Supervised(owner.Id, Forever("radio"), Forever("awdld"), _ => { });
        Expect(orphaned.Code == 0 && orphaned.Ended.Contains("owner"), $"the app is gone -> the daemons are stopped, exit {orphaned.Code}");

        var missing = Supervised(Environment.ProcessId, new Daemon("radio", Path.Combine(logs, "no-such.exe"), []), Forever("awdld"), _ => { });
        Expect(missing.Code == 3 && missing.Ended.Contains("launch"), $"a daemon that cannot be started -> exit {missing.Code}, nothing left running");
        Expect(Process.GetProcessesByName("PING").Length == 0, "and again no daemon is left running");

        var noRepo = new EngineProcess(System.Windows.Threading.Dispatcher.CurrentDispatcher, null, []);
        string? why = null;
        noRepo.Stopped += (reason, _) => why = reason;
        noRepo.Start("x", Path.GetTempPath(), true, false, []);
        Expect(why == "no_engine", "an app that cannot find its engine says so and starts nothing");
    }
}
