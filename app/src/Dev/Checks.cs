using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Input;
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
            if (args.Skip(1).Contains("portraits"))
            {
                Portraits(app, folder);
                log.WriteLine(_failed == 0 ? $"ALL {_passed} PASSED" : $"{_failed} FAILED, {_passed} passed");
                return _failed == 0 ? 0 : 1;
            }
            if (args.Skip(1).Contains("accessibility"))
            {
                Colors();
                Accessibility(app, folder);
                log.WriteLine(_failed == 0 ? $"ALL {_passed} PASSED" : $"{_failed} FAILED, {_passed} passed");
                return _failed == 0 ? 0 : 1;
            }
            if (args.Skip(1).Contains("setup"))
            {
                Setup(app, folder);
                log.WriteLine(_failed == 0 ? $"ALL {_passed} PASSED" : $"{_failed} FAILED, {_passed} passed");
                return _failed == 0 ? 0 : 1;
            }
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
            if (args.Skip(1).Contains("sender-folders"))
            {
                SenderFolders(app, folder);
                log.WriteLine(_failed == 0 ? $"ALL {_passed} PASSED" : $"{_failed} FAILED, {_passed} passed");
                return _failed == 0 ? 0 : 1;
            }
            if (args.Skip(1).Contains("history"))
            {
                History(app, folder);
                log.WriteLine(_failed == 0 ? $"ALL {_passed} PASSED" : $"{_failed} FAILED, {_passed} passed");
                return _failed == 0 ? 0 : 1;
            }
            if (args.Skip(1).Contains("notifications"))
            {
                Notifications(app, folder);
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
            SenderFolders(app, folder);
            History(app, folder);
            Notifications(app, folder);
            Setup(app, folder);
            Accessibility(app, folder);
            Portraits(app, folder);
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

    private static void Portraits(Application app, string folder)
    {
        var world = Stage.NewWorld(app.Dispatcher);
        Stage.Scenes([]).First(scene => scene.Name == "device-portraits").Build(world);
        var view = Show(world);
        var portraits = Descendants(view).OfType<DevicePortrait>().ToList();
        Expect(portraits.Select(portrait => portrait.Kind).Order().SequenceEqual(Enum.GetValues<DeviceKind>().Order()),
            "each recognised device kind has its own portrait, including the unknown fallback");
        Expect(portraits.All(portrait => !portrait.IsHitTestVisible && UIElementAutomationPeer.CreatePeerForElement(portrait) is null),
            "device art is decorative; the named device button remains the accessible action");
        var hashes = new List<string>();
        foreach (var portrait in portraits)
        {
            var bitmap = Stage.Bitmap(portrait, 64, 64, 2);
            var pixels = new byte[bitmap.PixelHeight * bitmap.PixelWidth * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
        }
        Expect(hashes.Distinct().Count() == 4, "phone, tablet, computer and unknown art are visually distinct");
        var target = Stage.Controls(view).First(one => one.Control.DataContext is PeerModel).Control;
        Expect(view.TargetPeer(target, true) && Plate.GetDropTarget(target), "a file drag identifies its specific device destination");
        Expect(!view.TargetPeer(target, false) && !Plate.GetDropTarget(target), "non-file drags clear the destination ring");
        ((PeerModel)target.DataContext).State = PeerState.Sending;
        Expect(!view.TargetPeer(target, true) && !Plate.GetDropTarget(target), "a busy device refuses the drop highlight");
        ((PeerModel)target.DataContext).State = PeerState.Idle;
        world.Model.SettingsOpen = true;
        Expect(!view.TargetPeer(target, true), "a sheet prevents a drop onto devices behind it");
        world.Model.SettingsOpen = false;
        world.Model.Hold([Stage.SampleFiles(Path.Combine(folder, "portrait-files"))[0]]);
        Expect(KeyboardPress(view, ((PeerModel)target.DataContext).Name) && world.Engine.Told.Last().Contains("\"do\":\"send\""),
            "the illustrated device still sends directly using the keyboard");
        world.Model.Dispose();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    // Routes WPF key events without creating a window or taking the user's keyboard.
    private sealed class KeySource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private static bool KeyboardPress(FrameworkElement view, string name, Key key = Key.Enter)
    {
        view.UpdateLayout();
        var control = Stage.Controls(view).FirstOrDefault(one => one.Control.IsEnabled
            && AutomationProperties.GetName(one.Control) == name).Control;
        if (control is null) return false;
        var source = new KeySource { RootVisual = view };
        foreach (var routed in new[] { Keyboard.KeyDownEvent, Keyboard.KeyUpEvent })
            control.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = routed });
        Stage.Pump(view.Dispatcher, 80);
        return true;
    }

    private static void Accessibility(Application app, string folder)
    {
        var files = Stage.SampleFiles(Path.Combine(folder, "accessible-files"));
        foreach (var scene in Stage.Scenes(files))
        {
            var world = Stage.NewWorld(app.Dispatcher, setup: scene.Name.StartsWith("setup-"));
            scene.Build(world);
            var view = Show(world);
            var controls = Stage.Controls(view).Where(one => one.Control.IsEnabled).ToList();
            var missing = controls.Where(one => string.IsNullOrWhiteSpace(
                System.Windows.Automation.AutomationProperties.GetName(one.Control))
                || one.Control.FocusVisualStyle is null || !one.Control.IsTabStop).Select(one => one.Line).ToList();
            Expect(missing.Count == 0, $"{scene.Name}: named tab stops with focus indicators: {string.Join("; ", missing)}");
            var menus = Descendants(view).OfType<FrameworkElement>().Select(element => element.ContextMenu)
                .OfType<ContextMenu>().SelectMany(menu => menu.Items.OfType<MenuItem>());
            Expect(menus.All(item => !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(item))),
                $"{scene.Name}: context-menu commands have automation names");
            Expect(Descendants(view).OfType<Image>().All(image => UIElementAutomationPeer.CreatePeerForElement(image) is null)
                && Descendants(view).OfType<GlyphText>().All(glyph => UIElementAutomationPeer.CreatePeerForElement(glyph) is null),
                $"{scene.Name}: decorative pictures and glyphs stay out of the automation tree");
            var parts = Descendants(view).OfType<Control>().Where(control => control is
                System.Windows.Controls.Primitives.Thumb or System.Windows.Controls.Primitives.RepeatButton
                or System.Windows.Controls.Primitives.ScrollBar);
            Expect(parts.All(part => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(part))
                   && part.FocusVisualStyle is not null), $"{scene.Name}: scroll parts have names and focus styles");
            Expect(Descendants(view).OfType<Ring>().All(progress => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(progress))),
                $"{scene.Name}: progress ranges name their device");
            world.Model.Dispose();
        }
        var ring = new Ring { Progress = .42 };
        var range = UIElementAutomationPeer.CreatePeerForElement(ring)?.GetPattern(PatternInterface.RangeValue) as IRangeValueProvider;
        Expect(range is { IsReadOnly: true, Minimum: 0, Maximum: 100, Value: 42 }, "transfer rings expose a read-only percentage range");
        ring.Progress = 2;
        Expect(range?.Value == 100, "ring automation clamps the same value it draws");
        ring.Progress = double.NaN;
        Expect(range?.Value == 0, "non-finite ring progress cannot escape to automation");
        var readOnly = false;
        try { range?.SetValue(80); } catch (InvalidOperationException) { readOnly = true; }
        Expect(readOnly, "a screen reader cannot change transfer progress");
        var asking = Stage.NewWorld(app.Dispatcher);
        asking.Engine.Say(Ask("keyboard-accept", "Phone", "iPhone"));
        var main = Show(asking);
        var headlines = Descendants(main).OfType<TextBlock>().Where(text => text.Text == asking.Model.Incoming[0].Headline);
        Expect(headlines.Any(text => System.Windows.Automation.AutomationProperties.GetLiveSetting(text)
                   == AutomationLiveSetting.Assertive), "incoming requests are assertive live regions");
        Expect(headlines.OfType<LiveText>().Any(), "request live regions also raise WPF change events");
        var order = Stage.Controls(main).Where(one => one.Control.IsEnabled).Select(one => AutomationProperties.GetName(one.Control)).ToList();
        Expect(order.IndexOf("Decline") >= 0 && order.IndexOf("Decline") < order.IndexOf("Accept"), "request tab order follows Decline then Accept as drawn");
        Expect(KeyboardPress(main, "Accept") && asking.Engine.Told.Last().Contains("\"accept\":true"), "Enter accepts a request through WPF's keyboard handler");
        asking.Engine.Say(new { @event = "answered", id = "keyboard-accept", how = "accepted" });
        asking.Engine.Say(Ask("keyboard-decline", "Phone", "iPhone"));
        Expect(KeyboardPress(main, "Decline") && asking.Engine.Told.Last().Contains("\"accept\":false"), "Enter declines a request through WPF's keyboard handler");
        var cards = new RequestsView { DataContext = asking.Model };
        cards.Measure(new Size(380, 720)); cards.Arrange(new Rect(0, 0, 380, 720)); cards.UpdateLayout();
        Expect(Stage.Controls(cards).All(one => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(one.Control))
                   && one.Control.FocusVisualStyle is not null && one.Control.IsTabStop), "corner card actions are named tab stops with focus indicators");
        Expect(KeyboardPress(cards, "Decline") && asking.Engine.Told.Last().Contains("\"accept\":false"), "the corner card also accepts a keyboard Decline");
        asking.Engine.Say(Ask("keyboard-third", "Another Phone", "iPhone"));
        cards.LayoutTransform = new ScaleTransform(2, 2);
        cards.Measure(new Size(760, 600)); cards.Arrange(new Rect(0, 0, 760, 600)); cards.UpdateLayout();
        var cardScroll = Descendants(cards).OfType<ScrollViewer>().First();
        Expect(cardScroll.ScrollableHeight > 0, "enlarged corner cards can scroll within a limited desktop height");
        asking.Engine.Say(new { @event = "link", up = true });
        asking.Engine.Say(new { @event = "peer", id = "key-peer", name = "Keyboard Phone", model = "iPhone" });
        asking.Model.Hold([files[0]]);
        Expect(KeyboardPress(main, "Keyboard Phone") && asking.Engine.Told.Last().Contains("\"do\":\"send\""), "Enter on a chosen device sends the staged file");
        var progress = Descendants(main).OfType<LiveText>().Where(text => AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite);
        Expect(progress.Any(), "transfer status has polite live regions");
        Expect(KeyboardPress(main, "Settings") && asking.Model.SettingsOpen, "settings can open using Enter");
        Expect(KeyboardNavigation.GetTabNavigation((DependencyObject)main.FindName("SheetPanel")) == KeyboardNavigationMode.Cycle,
            "Tab and Shift+Tab stay in the settings sheet");
        Expect(KeyboardPress(main, "Keep receiving when the window is closed", Key.Space) && !asking.Settings.KeepInTray,
            "Space toggles a settings switch through WPF's keyboard handler");
        Expect(KeyboardPress(main, "Contacts Only", Key.Space) && asking.Settings.ContactsOnly,
            "Space selects Contacts Only through WPF's radio-button handler");
        Expect(KeyboardPress(main, "Everyone", Key.Space) && !asking.Settings.ContactsOnly,
            "Space can select Everyone again");
        Expect(KeyboardPress(main, "Close settings") && !asking.Model.SettingsOpen, "settings can close using Enter");
        asking.Model.Dispose();
        foreach (var sceneName in new[] { "incoming-ask", "settings", "history-received", "setup-1" })
        {
            var world = Stage.NewWorld(app.Dispatcher, setup: sceneName.StartsWith("setup-"));
            Stage.Scenes(files).First(scene => scene.Name == sceneName).Build(world);
            var scaled = new MainView { DataContext = world.Model, LayoutTransform = new ScaleTransform(2, 2) };
            var host = new AdornerDecorator { Child = scaled };
            host.Measure(new Size(760, 960)); host.Arrange(new Rect(0, 0, 760, 960)); host.UpdateLayout();
            var table = (ScrollViewer)scaled.FindName("TableScroll");
            Expect(scaled.ActualWidth <= 380.1 && table.ExtentHeight >= 528 && table.ScrollableHeight > 0,
                $"{sceneName}: at 200% text in a 760 by 960 viewport the whole table can scroll");
            table.ScrollToEnd(); host.UpdateLayout();
            Expect(table.VerticalOffset > 0, $"{sceneName}: the bottom of the enlarged table remains reachable");
            Expect(Stage.Controls(scaled).Where(one => one.Control.IsEnabled).All(one => one.Control.ActualWidth <= 380),
                $"{sceneName}: enlarged controls fit the logical window width");
            world.Model.Dispose();
        }
    }

    private static void Setup(Application app, string folder)
    {
        var path = Path.Combine(folder, "setup-settings.json");
        var settings = SettingsStore.Load(path);
        Expect(settings.SetupPending, "no app.json means first-run setup");
        settings.Save();
        Expect(SettingsStore.Load(path).SetupPending, "automatic saves cannot mark unfinished setup complete");
        var world = Stage.NewWorld(app.Dispatcher, setup: true);
        settings.Name = world.Model.Name;
        settings.Folder = world.Model.Folder;
        using var model = new TableModel(world.Engine, world.Radio, world.Desk, settings, world.Startup,
                                        world.Explorer, world.HistoryStore);
        var view = new MainView { DataContext = model };
        view.Measure(new Size(460, 688)); view.Arrange(new Rect(0, 0, 460, 688)); view.UpdateLayout();
        Expect(model.SetupOpen && model.SetupName && !model.TableInReach, "setup opens over an unreachable table");
        model.NameDraft = "bad/name";
        Expect(Press(view, "Continue") && model.SetupName, "Continue waits for the engine to validate a changed name");
        world.Engine.Say(new { @event = "settings", name = settings.Name, folder = settings.Folder,
            ask = true, refused = new { name = "invalid" } });
        Expect(model.HasNameError && model.SetupName, "a refused name stays on its setup step");
        Expect(Press(view, "Skip this step") && model.SetupVisibility, "a refused field can still be skipped");
        model.OnlyKnownSenders = true;
        Expect(settings.ContactsOnly && world.Engine.Told.Last().Contains("contacts_only"), "setup uses the existing visibility command");
        model.EveryoneForAWhile = true;
        Expect(model.EveryoneForAWhile, "setup offers Everyone for 10 Minutes");
        model.AnyoneCanSend = true;
        Expect(Press(view, "Continue") && model.SetupFolder && model.AnyoneCanSend, "Everyone remains available");
        world.Desk.PickedFolder = folder;
        Expect(Press(view, "Change folder") && world.Engine.Told.Last().Contains("folder"), "setup uses the existing folder picker command");
        Expect(Press(view, "Skip this step") && model.SetupStartup, "the receive folder step can be skipped");
        Expect(Press(view, "Start with Windows") && world.Startup.Stored, "setup writes startup through its seam");
        Expect(Press(view, "Continue") && model.SetupExplorer, "startup leads to Explorer integration");
        Expect(Press(view, "Send with AirDrop in Explorer") && world.Explorer.Stored, "setup writes Explorer integration through its seam");
        Expect(Press(view, "Done") && !model.SetupOpen && model.TableInReach, "Done completes setup and uncovers the table");
        Expect(!SettingsStore.Load(path).SetupPending, "finished setup stays finished after restart");
        var renamed = Stage.NewWorld(app.Dispatcher, setup: true);
        renamed.Model.NameDraft = "New PC";
        renamed.Model.NextSetup.Execute(null);
        renamed.Engine.Say(new { @event = "settings", name = "New PC", folder = renamed.Model.Folder,
            ask = true, refused = new { } });
        Expect(renamed.Model.SetupVisibility && renamed.Model.Name == "New PC", "an accepted name advances only after engine confirmation");
        renamed.Model.Dispose();
        settings.SetupPending = true;
        settings.Save();
        using (var skipStored = new TableModel(world.Engine, world.Radio, world.Desk, settings, world.Startup,
                                              world.Explorer, world.HistoryStore)) skipStored.SkipSetup.Execute(null);
        Expect(!SettingsStore.Load(path).SetupPending, "Skip Setup is remembered on disk");
        using var skipped = Stage.NewWorld(app.Dispatcher, setup: true).Model;
        skipped.SkipSetup.Execute(null);
        Expect(!skipped.SetupOpen, "Skip Setup closes the whole sheet");
        var allSkipped = Stage.NewWorld(app.Dispatcher, setup: true);
        for (var index = 0; index < 5; index++) allSkipped.Model.SkipSetupStep.Execute(null);
        Expect(!allSkipped.Model.SetupOpen && !allSkipped.Settings.SetupPending, "all five steps can be skipped");
        File.WriteAllText(path, "{\"Name\":\"Existing PC\"}");
        Expect(!SettingsStore.Load(path).SetupPending, "existing installs without the new flag never enter setup");
        File.WriteAllText(path, "broken");
        Expect(!SettingsStore.Load(path).SetupPending, "an unreadable existing settings file does not restart setup");
        foreach (var scene in Stage.Scenes([]).Where(scene => scene.Name.StartsWith("setup-")))
        {
            var staged = Stage.NewWorld(app.Dispatcher, setup: true);
            scene.Build(staged);
            Expect(staged.Model.SetupOpen, $"{scene.Name} has a light and dark render state");
            staged.Model.Dispose();
        }
        allSkipped.Model.Dispose();
        world.Model.Dispose();
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
        var retryName = "winawdl-check-retry-" + Guid.NewGuid().ToString("N");
        var attempts = 0;
        SingleInstance? retryOwner = null;
        using (retryOwner = new SingleInstance(retryName)
        {
            PipeFactory = () =>
            {
                if (Interlocked.Increment(ref attempts) == 2) throw new IOException("test recreation failure");
                return new System.IO.Pipes.NamedPipeServerStream(retryOwner!.PipeName,
                    System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
                    System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly
                    | System.IO.Pipes.PipeOptions.FirstPipeInstance);
            },
        })
        using (var retryClient = new SingleInstance(retryName))
        {
            var delivered = 0;
            retryOwner.Listen(_ => Interlocked.Increment(ref delivered));
            Expect(await retryClient.Forward(new LaunchRequest([], true)), "the retry fixture accepts its first activation");
            Expect(await retryClient.Forward(new LaunchRequest([], true)) && delivered == 2 && attempts >= 3,
                   "the activation listener recovers after pipe recreation fails and disposes normally");
        }
    }

    private static void SenderFolders(Application app, string folder)
    {
        var world = Stage.NewWorld(app.Dispatcher);
        world.Model.SettingsOpen = true;
        var view = Show(world);
        var toggle = (Winawdl.Theme.Switch)view.FindName("SenderFoldersSwitch");
        toggle.BringIntoView();
        view.UpdateLayout();
        Expect(!world.Model.SenderFolders && !world.Settings.SenderFolders && toggle.IsChecked == false,
               "sender folders default off in the model, persisted settings and switch");
        var saves = 0;
        world.Settings.Save = () => saves++;
        Expect(Press(view, "A Folder for Each Sender") && world.Model.SenderFolders
               && world.Settings.SenderFolders && saves == 1
               && world.Engine.Told.Last().Contains("\"sender_folders\":true"),
               "the receive-settings switch saves and sends sender_folders through set");
        var serialized = System.Text.Json.JsonSerializer.Serialize(world.Settings);
        var kept = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(serialized)!;
        var engine = new ScriptedEngine();
        using var model = new TableModel(engine, world.Radio, world.Desk, kept, world.Startup, world.Explorer,
                                         world.HistoryStore);
        model.StartTheEngine();
        Expect(engine.StartedSenderFolders && model.SenderFolders,
               "sender folder persistence reaches the startup engine seam before ready");
        Expect(Press(view, "A Folder for Each Sender") && !world.Model.SenderFolders && saves == 2
               && world.Engine.Told.Last().Contains("\"sender_folders\":false"),
               "turning sender folders off saves and sends false");
        foreach (var enabled in new[] { false, true })
        {
            var scene = Stage.Scenes([]).First(one => one.Name == $"settings-sender-folders-{(enabled ? "on" : "off")}");
            scene.Build(world);
            view.UpdateLayout();
            Expect(world.Model.SenderFolders == enabled && toggle.IsChecked == enabled,
                   $"the sender folder {(enabled ? "on" : "off")} render scene reflects its bound state");
        }
        var actual = Directory.CreateDirectory(Path.Combine(folder, "Sender PC")).FullName;
        File.WriteAllText(Path.Combine(actual, "note.txt"), "test");
        world.Engine.Say(new { @event = "stored", id = "folder-test", folder = actual, names = new[] { "note.txt" } });
        world.Model.Received[0].Reveal!.Execute(null);
        Expect(world.Model.Received[0].Path == Path.Combine(actual, "note.txt")
               && world.Desk.Opened.Last() == "reveal " + Path.Combine(actual, "note.txt"),
               "Show in Folder uses the actual sender folder from stored");
        world.Model.Dispose();

        var received = new List<System.Text.Json.JsonElement>();
        var failure = "";
        var native = new EngineProcess(app.Dispatcher, Repo.Find(),
            ["--loopback", "--port", "0", "--keys", Path.Combine(folder, "sender-folder-keys")]);
        native.Said += said => received.Add(said);
        native.Stopped += (why, detail) => failure = why + " " + detail;
        bool WaitFor(string kind, bool enabled)
        {
            var until = Stopwatch.StartNew();
            while (until.Elapsed.TotalSeconds < 15 && failure.Length == 0)
            {
                if (received.Any(said => said.TryGetProperty("event", out var name) && name.GetString() == kind
                    && said.TryGetProperty("sender_folders", out var value) && value.GetBoolean() == enabled)) return true;
                Stage.Pump(app.Dispatcher, 20);
            }
            _log.WriteLine("sender folder engine detail: " + failure);
            return false;
        }
        try
        {
            native.Start("Test PC", folder, true, false, [], senderFolders: true);
            Expect(WaitFor("ready", true), "native EngineProcess carries the persisted sender folder startup flag");
            native.Tell(new() { ["do"] = "set", ["sender_folders"] = false });
            Expect(WaitFor("settings", false), "native engine protocol acknowledges sender_folders false");
        }
        finally
        {
            native.Stop();
        }
    }

    private sealed class CheckToast : IToast
    {
        public List<Notice> Shown { get; } = [];
        public Action? Failed { get; private set; }
        public bool Available { get; set; } = true;
        public bool Cleared { get; private set; }
        public bool Show(Notice notice, Action failed) { Shown.Add(notice); Failed = failed; return Available; }
        public void Clear() => Cleared = true;
    }

    private static void Notifications(Application app, string folder)
    {
        var world = Stage.NewWorld(app.Dispatcher);
        var toast = new CheckToast();
        var front = false;
        var cards = 0;
        var arrivalCards = 0;
        var now = DateTimeOffset.UtcNow;
        using var notices = new NoticeActivation(world.Model, () => front, toast, () => cards++,
            _ => arrivalCards++, () => now);
        world.Engine.Say(Ask("n1", "Phone <&>\u202e", "iPhone"));
        notices.Request();
        var shown = toast.Shown.Single();
        Expect(NoticeActivation.Valid(new Uri(shown.Accept).AbsoluteUri),
               "URI parsing preserves notification action capabilities");
        var xml = System.Xml.Linq.XElement.Parse(WindowsToast.Xml(shown));
        Expect(xml.Descendants("action").Select(action => (string?)action.Attribute("content"))
               .SequenceEqual(["Accept", "Decline"]) && !xml.Value.Contains('\u202e'),
               "native toast XML escapes hostile text and has Accept and Decline actions");
        var request = LaunchRequest.FromArguments([shown.Accept]);
        Expect(LaunchRequest.Decode(request.Encode()) is { Notice: var uri, Paths.Length: 0 }
               && uri == shown.Accept, "toast activation survives the bounded single-instance protocol");
        Expect(notices.Activate(shown.Accept) && world.Engine.Told.Last().Contains("\"accept\":true")
               && !notices.Activate(shown.Accept), "Accept targets the pending request once using a random capability");
        notices.Request();
        Expect(notices.Activate(toast.Shown.Last().Decline)
               && world.Engine.Told.Last().Contains("\"accept\":false"), "Decline targets the pending request");
        notices.Request();
        now = now.AddMinutes(3);
        Expect(!notices.Activate(toast.Shown.Last().Accept)
               && !NoticeActivation.Valid("winawdl-notice://../accept")
               && !notices.Activate(NoticeActivation.Prefix + new string('a', 64)),
               "expired, malformed and unknown notification actions cannot accept a transfer");
        toast.Available = false;
        notices.Request();
        Expect(cards == 1, "unavailable toasts fall back to request cards");
        toast.Available = true;
        notices.Request();
        toast.Failed!();
        Expect(cards == 2, "an asynchronous toast failure falls back to request cards");
        var stale = toast.Shown.Last().Accept;
        world.Engine.Say(new { @event = "answered", id = "n1", how = "declined" });
        world.Engine.Say(Ask("n1", "Another Phone", "iPhone"));
        var commands = world.Engine.Told.Count;
        notices.Activate(stale);
        Expect(world.Engine.Told.Count == commands, "an old toast cannot accept a new request that reused an engine id");
        var count = toast.Shown.Count;
        front = true;
        notices.Request();
        front = false;
        world.Settings.ShowRequests = false;
        notices.Request();
        Expect(toast.Shown.Count == count && cards == 2, "the front window and Show Requests setting suppress both routes");
        world.Settings.ShowRequests = true;
        var file = Path.Combine(folder, "notification-note.txt");
        File.WriteAllText(file, "test");
        var arrival = new ArrivalModel { Headline = "Files Received", Detail = "note.txt",
            File = new FileItem(file, world.Desk), Single = true };
        notices.Arrival(arrival);
        Expect(toast.Shown.Last().Title == "Files Received" && toast.Shown.Last().Accept.Length == 0,
               "files received uses a native toast with an activation back to the app");
        toast.Available = false;
        notices.Arrival(arrival);
        Expect(arrivalCards == 1, "unavailable arrival toasts fall back to arrival cards");
        var shortcut = Path.Combine(folder, "notification-test.lnk");
        WindowsExplorer.WriteShortcut(shortcut, Environment.ProcessPath!);
        Expect(ToastRegistration.Stamp(shortcut) == 0 && ToastRegistration.ShortcutAppId(shortcut) == WindowsToast.AppId,
               "a throwaway Start Menu shortcut stores the native AppUserModelID");
        notices.Dispose();
        Expect(toast.Cleared, "shutdown clears notification capabilities and native toast history");
        world.Model.Dispose();
    }

    private static void History(Application app, string folder)
    {
        var path = Path.Combine(folder, "history.json");
        File.WriteAllText(path, "{broken");
        var store = new HistoryStore(path);
        Expect(store.Load().Count == 0, "an unreadable history file does not stop startup");
        File.WriteAllText(path, "[{\"Direction\":\"Received\",\"Who\":null,\"Device\":null,"
            + "\"When\":\"2025-01-02T03:04:05Z\",\"Files\":[{\"Name\":null,\"Path\":null,\"Size\":-3}],"
            + "\"TotalSize\":-3,\"Result\":null}]");
        Expect(store.Load().Count == 0, "malformed history records are ignored before they reach the view");
        File.WriteAllText(path, "[{\"Direction\":\"Sent\",\"Who\":\"Phone\",\"Device\":\"iPhone\","
            + "\"When\":\"2025-01-02T03:04:05Z\",\"Files\":[{\"Name\":\"note.txt\","
            + "\"Path\":\"\\u0000\",\"Size\":3}],\"TotalSize\":3,\"Result\":\"Finished\"}]");
        Expect(store.Load().Count == 0, "a malformed saved path is ignored before Show in Folder can use it");
        var entries = Enumerable.Range(0, 502).Select(index => new TransferRecord
        {
            Direction = "Received", Who = $"Device {index}", Device = "iPhone",
            When = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Files = [new TransferFile { Name = "photo.jpg", Path = Path.Combine(folder, "photo.jpg"), Size = 7 }],
            TotalSize = 7, Result = "Finished",
        }).ToList();
        store.Save(entries);
        var kept = store.Load();
        Expect(kept.Count == 500 && kept[0].Who == "Device 2" && kept[^1].Who == "Device 501",
               "history atomically keeps the newest 500 entries in order");
        Expect(!File.Exists(path + ".new") && File.Exists(path),
               "history writes through the same temporary-file replacement as app.json");

        var world = Stage.NewWorld(app.Dispatcher, folder);
        var view = Show(world);
        Expect(!world.Model.ShowRecent, "an empty install does not crowd the smallest window with Recent");
        var file = Path.Combine(folder, "history-note.txt");
        File.WriteAllText(file, "only the name and path belong in history");
        world.Engine.Say(new { @event = "link", up = true });
        world.Engine.Say(new { @event = "peer", id = "p1", name = "Sam's iPhone", model = "iPhone14,2" });
        world.Model.Hold([file]);
        world.Model.SendTo(world.Model.Peers[0]);
        world.Engine.Say(new { @event = "send", id = "s1", state = "sent" });
        Expect(world.HistoryStore.Saved.Count == 1
               && world.HistoryStore.Saved[0] is { Direction: "Sent", Who: "Sam's iPhone",
                    Device: "iPhone14,2", Result: "Finished" }
               && world.HistoryStore.Saved[0].Files[0].Path == file,
               "a finished send keeps its peer, device, file path and result");
        Expect(world.Model.ShowRecent, "saved transfers expose History through the Recent header");
        world.Engine.Say(new { @event = "asked", id = "r1", sender = "Pat's iPad", model = "iPad13,1",
                               files = new[] { new { name = "note.txt", size = 12, type = "" } },
                               links = 0, total = 12, waits = true });
        world.Engine.Say(new { @event = "answered", id = "r1", how = "declined" });
        Expect(world.HistoryStore.Saved.Count == 2
               && world.HistoryStore.Saved[1] is { Direction: "Received", Result: "Declined", TotalSize: 12 }
               && world.HistoryStore.Saved[1].Files[0] is { Name: "note.txt", Path: "" },
               "a declined incoming request keeps announced names and size without inventing paths");
        Expect(Press(view, "History") && world.Model.HistoryOpen && world.Model.HistoryRows.Count == 2,
               "History opens from Recent and shows prior transfers");
        var historyRow = world.Model.HistoryRows.First();
        historyRow.Toggle.Execute(null);
        Expect(historyRow.Expanded && historyRow.DetailsLabel == "Hide Files",
               "a history row reveals its files and names the action");
        world.Model.HistoryRows.Last().Reveal.Execute(null);
        Expect(world.Desk.Opened.Last() == "reveal " + file,
               "Show in Folder uses an existing file from a history entry");
        world.Model.ClearHistory.Execute(null);
        Expect(world.Model.HistoryRows.Count == 0 && world.HistoryStore.Saved.Count == 0
               && world.Model.HistoryOpen && !world.Model.ShowRecent,
               "Clear History writes an empty history and keeps its empty view open");
        world.Model.Hold([file]);
        world.Model.SendTo(world.Model.Peers[0]);
        world.Engine.Say(new { @event = "send", id = "s2", state = "declined" });
        world.Model.SendTo(world.Model.Peers[0]);
        world.Engine.Say(new { @event = "send", id = "s3", state = "failed", reason = "unreachable" });
        Expect(world.HistoryStore.Saved.Select(entry => entry.Result).SequenceEqual(["Declined", "Failed"])
               && world.HistoryStore.Saved.All(entry => entry.Direction == "Sent" && entry.When == world.Desk.UtcNow),
               "declined and failed sends are recorded once with a completion time");
        world.Engine.Say(new { @event = "asked", id = "r2", sender = "Pat's iPad", model = "iPad13,1",
                               files = new[] { new { name = "history-note.txt", size = 3, type = "" } },
                               links = 0, total = 3, waits = true });
        world.Engine.Say(new { @event = "answered", id = "r2", how = "accepted" });
        world.Engine.Say(new { @event = "stored", id = "r2", folder, names = new[] { "history-note.txt" } });
        var received = world.HistoryStore.Saved.Last();
        Expect(received is { Direction: "Received", Result: "Finished", Who: "Pat's iPad",
                             Device: "iPad13,1" }
               && received.Files.Single().Path == file
               && received.TotalSize == new FileInfo(file).Length,
               "a finished receive records the actual path and file size");
        world.Engine.Say(new { @event = "asked", id = "r3", sender = "../CON .txt", model = "iPhone",
                               files = new[] { new { name = "../CON .txt", size = 5, type = "" } },
                               links = 0, total = 5, waits = true });
        world.Engine.Say(new { @event = "lost", id = "r3", reason = "nothing arrived" });
        Expect(world.HistoryStore.Saved.Last() is { Direction: "Received", Result: "Failed" }
               && world.HistoryStore.Saved.Last().Files[0] is { Name: "_CON .txt", Path: "" },
               "a failed receive retains a safe name but no invented file path");
        world.Engine.Say(new { @event = "asked", id = "r4", sender = "Phone", model = "iPhone",
                               files = new[] { new { name = "safe.txt", size = 2, type = "" } },
                               links = 0, total = 2, waits = false });
        world.Engine.Say(new { @event = "stored", id = "r4", folder, names = new[] { "../outside.txt" } });
        Expect(world.HistoryStore.Saved.Last().Files.Count == 0,
               "a hostile stored path cannot escape the receive folder through history");
        world.Engine.Say(new { @event = "asked", id = "r5", sender = "../CON\u202e", model = "iPhone\u200b",
                               files = new[] { new { name = "safe.txt", size = 2, type = "" } },
                               links = 0, total = 2, waits = true });
        world.Engine.Say(new { @event = "answered", id = "r5", how = "declined" });
        Expect(world.HistoryStore.Saved.Last() is { Who: "_CON", Device: "iPhone" },
               "history cleans hostile sender and device labels before saving them");
        using (var huge = System.Text.Json.JsonDocument.Parse(
                   "{\"event\":\"asked\",\"id\":\"r6\",\"sender\":\"Phone\",\"model\":\"iPhone\","
                   + "\"files\":[{\"name\":\"a.txt\",\"size\":9223372036854775807},"
                   + "{\"name\":\"b.txt\",\"size\":9223372036854775807}],"
                   + "\"total\":18446744073709551614,\"links\":0,\"waits\":true}"))
            world.Engine.Say(huge.RootElement);
        world.Engine.Say(new { @event = "answered", id = "r6", how = "declined" });
        Expect(world.HistoryStore.Saved.Last().TotalSize == long.MaxValue,
               "oversized announced sizes cannot crash history or overflow its total");
        var count = world.HistoryStore.Saved.Count;
        world.Engine.Say(new { @event = "stored", id = "r4", folder, names = new[] { "history-note.txt" } });
        Expect(world.HistoryStore.Saved.Count == count, "a duplicate terminal event cannot duplicate history");
        store.Save(world.HistoryStore.Saved);
        Expect(!File.ReadAllText(path).Contains("only the name and path belong in history"),
               "history saves names and paths, never file contents");
        using (var reloaded = new TableModel(new ScriptedEngine(), new ScriptedRadio(),
                                             new ScriptedDesk(app.Dispatcher), new AppSettings(),
                                             new ScriptedStartup(), new ScriptedExplorer(), store))
            Expect(reloaded.HistoryRows.Count == world.HistoryStore.Saved.Count
                   && reloaded.HistoryRows[0].Record.Result == world.HistoryStore.Saved.Last().Result,
                   "a new app model shows saved history with newest entries first");
        var missing = new HistoryRow(new TransferRecord
        {
            Direction = "Sent", Who = "Phone", Device = "iPhone", Result = "Finished",
            Files = [new TransferFile { Name = "gone.txt", Path = Path.Combine(folder, "gone.txt") }],
        }, world.Desk);
        var opened = world.Desk.Opened.Count;
        missing.Reveal.Execute(null);
        Expect(!missing.CanReveal && world.Desk.Opened.Count == opened,
               "Show in Folder is unavailable after a history file is gone");
        world.Model.Dispose();

        var samples = Stage.SampleFiles(Path.Combine(folder, "history-stage-files"));
        foreach (var (name, direction, result) in new[]
                 {
                     ("history-received", "Received", "Finished"),
                     ("history-receive-failed", "Received", "Failed"),
                     ("history-receive-declined", "Received", "Declined"),
                     ("history-sent", "Sent", "Finished"),
                     ("history-send-failed", "Sent", "Failed"),
                     ("history-send-declined", "Sent", "Declined"),
                 })
        {
            var staged = Stage.NewWorld(app.Dispatcher, folder);
            Stage.Scenes(samples).First(scene => scene.Name == name).Build(staged);
            Expect(staged.Model.HistoryOpen && staged.Model.HistoryRows.Single().Record is { } shown
                   && shown.Direction == direction && shown.Result == result,
                   $"{name} renders the {direction.ToLowerInvariant()} {result.ToLowerInvariant()} state");
            staged.Model.Dispose();
        }
        var empty = Stage.NewWorld(app.Dispatcher, folder);
        Stage.Scenes(samples).First(scene => scene.Name == "history-empty").Build(empty);
        Expect(empty.Model.HistoryOpen && empty.Model.HistoryRows.Count == 0,
               "the empty History view has its own render state");
        empty.Model.Dispose();
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
        var boundary = Accents.Select(Rgb.Hex).SelectMany(accent => new[] { Palette.Light(accent), Palette.Night(accent) })
            .SelectMany(palette => palette.Surfaces.Select(surface => Rgb.Contrast(palette.ControlLine, surface))).Min();
        Expect(boundary >= Palette.ShapeContrast, $"interactive boundaries on every surface: lowest {boundary:0.00} to 1");
        foreach (var palette in new[]
        {
            Palette.System(Rgb.Black, Rgb.White, Rgb.Hex("#1AEBFF"), Rgb.Black, Rgb.Hex("#FFFF00")),
            Palette.System(Rgb.White, Rgb.Black, Rgb.Hex("#000080"), Rgb.White, Rgb.Hex("#0000FF")),
        })
            Expect(palette.Surfaces.All(surface => Rgb.Contrast(palette.Ink, surface) >= Palette.TextContrast
                    && Rgb.Contrast(palette.AccentText, surface) >= Palette.TextContrast
                    && Rgb.Contrast(palette.ControlLine, surface) >= Palette.ShapeContrast)
                    && Rgb.Contrast(palette.OnAccent, palette.Accent) >= Palette.TextContrast,
                "light and dark Windows contrast themes keep system text, highlight and control colors readable");
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
        var again = new TableModel(world.Engine, world.Radio, world.Desk, kept, world.Startup, world.Explorer,
                                   world.HistoryStore);
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
        var again = new TableModel(engine, world.Radio, world.Desk, kept, world.Startup, world.Explorer,
                                   world.HistoryStore);
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
