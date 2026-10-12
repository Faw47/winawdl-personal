using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Microsoft.Win32;
using Winawdl.Model;
using Winawdl.Shell;
using Winawdl.Theme;

namespace Winawdl.Views;

public sealed class MainWindow : Window
{
    private const double SmallestWidth = 380, SmallestHeight = 560;

    private readonly TableModel _model;
    private readonly ThemeService _theme;
    private readonly AppSettings _settings;
    private double _textScale = 1;

    public MainWindow(TableModel model, ThemeService theme, AppSettings settings)
    {
        _model = model;
        _theme = theme;
        _settings = settings;
        Title = "winawdl";
        Width = 460;
        Height = 720;
        MinWidth = SmallestWidth;
        MinHeight = SmallestHeight;
        SetResourceReference(BackgroundProperty, "Table");
        Content = new MainView { DataContext = model };
        Restore();
        Paint();
        theme.Changed += Paint;
        SourceInitialized += (_, _) => Paint();
        IsVisibleChanged += (_, _) => Seen();
        StateChanged += (_, _) => Seen();
    }

    /// <summary>The user chose Quit: the next close ends the app whatever the settings say.</summary>
    public bool Quitting { get; set; }

    /// <summary>The window was closed into the notification area for the first time.</summary>
    public event Action? WentToTray;

    private void Paint()
    {
        Icon = PlaceholderIcon.ForWindow(_theme.Current);
        TitleBar.Paint(this, _theme.Current);
        TextScale = TextScaleInWindows();
    }

    /// <summary>The text size set under Accessibility in Windows, as a factor.
    /// WPF does not follow it by itself.</summary>
    public static double TextScaleInWindows() =>
        Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility")?.GetValue("TextScaleFactor") is int percent
            ? Math.Clamp(percent / 100.0, 1, 2.25)
            : 1;

    /// <summary>Everything in the window grows with the text, so that nothing
    /// is cut off by what stayed small around it.</summary>
    public double TextScale
    {
        get => _textScale;
        set
        {
            if (value == _textScale) return;
            var grow = value / _textScale;
            _textScale = value;
            ((FrameworkElement)Content).LayoutTransform = new System.Windows.Media.ScaleTransform(value, value);
            var room = SystemParameters.WorkArea;
            (MinWidth, MinHeight) = (Math.Min(SmallestWidth * value, room.Width), Math.Min(SmallestHeight * value, room.Height));
            (Width, Height) = (Math.Min(Width * grow, room.Width), Math.Min(Height * grow, room.Height));
        }
    }

    private void Seen() => _model.SetVisible(IsVisible && WindowState != WindowState.Minimized);

    public void Surface()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Remember();
        if (!Quitting && _settings.KeepInTray)
        {
            e.Cancel = true;
            Hide();
            WentToTray?.Invoke();
        }
        base.OnClosing(e);
    }

    private void Remember()
    {
        var bounds = RestoreBounds;
        if (bounds.IsEmpty) return;
        _settings.Window = [bounds.Left, bounds.Top, bounds.Width, bounds.Height];
        _settings.Save();
    }

    private void Restore()
    {
        if (_settings.Window is not [var left, var top, var width, var height]) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        // A window remembered on a screen that is gone would open out of reach.
        if (!screen.Contains(new Rect(left, top, Math.Min(width, 200), 40))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (left, top, Math.Max(width, MinWidth), Math.Max(height, MinHeight));
    }
}

/// <summary>The icon in the notification area, which keeps the app reachable
/// while its window is closed.</summary>
public sealed class Tray : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly MainWindow _window;
    private readonly ContextMenu _menu;

    public Tray(MainWindow window, Action quit)
    {
        _window = window;
        _menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        _menu.Items.Add(Item("Open winawdl", window.Surface));
        _menu.Items.Add(Item("Quit", quit));
        _icon = new System.Windows.Forms.NotifyIcon { Text = "winawdl", Icon = Mark(), Visible = true };
        _icon.MouseClick += (_, click) =>
        {
            if (click.Button == System.Windows.Forms.MouseButtons.Left) window.Surface();
            else ShowMenu();
        };
        _icon.BalloonTipClicked += (_, _) => window.Surface();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    private static MenuItem Item(string header, Action run)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => run();
        return item;
    }

    private static System.Drawing.Icon Mark()
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        var darkTaskbar = Registry.CurrentUser.OpenSubKey(key)?.GetValue("SystemUsesLightTheme") is 0;
        return PlaceholderIcon.ForTray(darkTaskbar);
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        _window.Dispatcher.BeginInvoke(() =>
        {
            var old = _icon.Icon;
            _icon.Icon = Mark();
            old?.Dispose();
        });

    private void ShowMenu()
    {
        _menu.IsOpen = true;
        // A menu opened from the notification area closes on a click elsewhere
        // only if its window is the foreground one.
        if (PresentationSource.FromVisual(_menu) is HwndSource source)
            SetForegroundWindow(source.Handle);
    }

    public void Say(string text) => _icon.ShowBalloonTip(5000, "winawdl", text, System.Windows.Forms.ToolTipIcon.None);

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
