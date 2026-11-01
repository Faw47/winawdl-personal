using System.IO;
using System.Windows;
using System.Windows.Threading;
using Winawdl.Engine;
using Winawdl.Model;
using Winawdl.Radio;
using Winawdl.Shell;
using Winawdl.Theme;
using Winawdl.Views;

namespace Winawdl;

public static class Program
{
    private const string OnlyOne = "winawdl-app";
    /// <summary>What follows this on the command line goes to the engine, for a
    /// test without a radio: --engine --loopback --peer [::1]:8772</summary>
    private const string EngineFlag = "--engine";

    [STAThread]
    public static int Main(string[] args) => args switch
    {
        [RadioSupervisor.Flag, .. var rest] => RadioSupervisor.Run(rest),
        [Dev.Stage.Flag, .. var rest] => Dev.Stage.Run(rest),
        [Dev.Drive.Flag, .. var rest] => Dev.Drive.Run(rest),
        [Dev.Checks.Flag, .. var rest] => Dev.Checks.Run(rest),
        [WindowsExplorer.InstallFlag] => WindowsExplorer.Register(true),
        [WindowsExplorer.RemoveFlag] => WindowsExplorer.Register(false),
        [ToastRegistration.InstallFlag] => ToastRegistration.Register(true),
        [ToastRegistration.RemoveFlag] => ToastRegistration.Register(false),
        [ToastRegistration.ShortcutFlag, var path] => ToastRegistration.Stamp(path),
        [ShareRegistration.SignFlag, var package, var thumbprint] => ShareRegistration.Sign(package, thumbprint),
        [ShareRegistration.InstallFlag] => ShareRegistration.Register(true),
        [ShareRegistration.RemoveFlag] => ShareRegistration.Register(false),
        _ => RunApp(args),
    };

    /// <summary>The styles every window and every render shares.</summary>
    public static ThemeService Dress(Application app)
    {
        app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/winawdl;component/src/theme/controls.xaml", UriKind.Relative)));
        app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/winawdl;component/src/theme/devices.xaml", UriKind.Relative)));
        return new ThemeService(app.Resources, app.Dispatcher);
    }

    private static int RunApp(string[] args)
    {
        LaunchRequest request;
        try { request = LaunchRequest.FromArguments(args); }
        catch (ArgumentException) { return 2; }
        using var only = new SingleInstance(OnlyOne);
        if (!only.IsFirst)
        {
            return only.Forward(request).GetAwaiter().GetResult() ? 0 : 1;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += OnCrash;
        var theme = Dress(app);
        theme.Watch();

        var engineArguments = args.SkipWhile(argument => argument != EngineFlag).Skip(1).ToList();
        var repo = Repo.Find();
        var settings = SettingsStore.Load();
        var engine = new EngineProcess(app.Dispatcher, repo, engineArguments);
        var radio = new RadioControl(app.Dispatcher, repo);
        var model = new TableModel(engine, radio, new Desk(app.Dispatcher), settings,
                                   new WindowsStartup(Environment.ProcessPath!),
                                   new WindowsExplorer(Environment.ProcessPath!),
                                   new HistoryStore(Path.Combine(Repo.DataFolder, "history.json")));
        var window = new MainWindow(model, theme, settings);
        using var tray = new Tray(window, () =>
        {
            window.Quitting = true;
            window.Close();
            app.Shutdown();
        }, model);
        window.Closed += (_, _) => app.Shutdown();
        window.WentToTray += () =>
        {
            if (settings.TrayNoticeShown) return;
            settings.TrayNoticeShown = true;
            settings.Save();
            tray.Say("winawdl is still receiving. Open it or quit it from this icon.");
        };
        var requests = new RequestWindow(model, () => window.IsActive);
        window.Activated += (_, _) => requests.Reconsider();
        window.Deactivated += (_, _) => requests.Reconsider();
        using var notices = new NoticeActivation(model, () => window.IsActive, new WindowsToast(),
            () => requests.Announce(toastFallback: true),
            arrival => requests.Announce(arrival, toastFallback: true), () => DateTimeOffset.UtcNow);
        model.Attention += _ => notices.Request();
        model.Arrived += notices.Arrival;

        only.Listen(next => app.Dispatcher.BeginInvoke(() =>
        {
            if (next.Notice.Length > 0) notices.Activate(next.Notice);
            Deliver(next, model, window.Surface);
        }));

        Share.Sweep(DateTime.UtcNow.AddDays(-1));
        Deliver(request, model, () => { });
        model.StartTheEngine();
        if (request.Surface || model.SetupOpen) window.Show();
        app.Run();
        engine.Stop();
        model.Dispose();
        radio.Stop();
        return 0;
    }

    internal static bool ShouldSurface(string[] args) =>
        LaunchRequest.FromArguments(args).Surface;

    internal static void Deliver(LaunchRequest request, TableModel model, Action surface)
    {
        model.Receive(request.Paths, request.Link);
        if (request.Surface) surface();
    }

    internal static void ShowAtStartup(MainWindow window, string[] args)
    {
        if (ShouldSurface(args)) window.Show();
    }

    private static void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var report = Path.Combine(Repo.DataFolder, "crash.log");
        File.AppendAllText(report, $"{DateTime.Now:u}\n{e.Exception}\n\n");
        MessageBox.Show($"winawdl has to close because of an error. What happened is written in\n{report}",
                        "winawdl", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
