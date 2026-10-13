using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Winawdl.Model;
using Winawdl.Shell;
using Winawdl.Theme;

namespace Winawdl.Views;

/// <summary>The requests that wait for an answer, as cards with Accept and Decline.</summary>
public partial class RequestsView : UserControl
{
    private static ImageSource? _mark;

    public RequestsView() => InitializeComponent();

    public static ImageSource Mark
    {
        get
        {
            if (_mark is null)
            {
                // Twice the size it is shown at, for a screen scaled up to 200%.
                var drawn = AppIcon.Draw(32, Colors.White, AppIcon.Ground);
                drawn.Freeze();
                _mark = drawn;
            }
            return _mark;
        }
    }

    /// <summary>A card comes up from the edge of the screen it sits against.</summary>
    private void OnArrived(object sender, RoutedEventArgs e)
    {
        if (Motion.Reduced || sender is not FrameworkElement element) return;
        var move = new TranslateTransform();
        element.RenderTransform = move;
        Motion.From(move, TranslateTransform.YProperty, 8, Motion.Move);
        Motion.From(element, OpacityProperty, 0, Motion.Move);
    }
}

/// <summary>Shows waiting requests in the corner of the screen while the app's
/// own window is not the one in front. It never takes the keyboard.</summary>
public sealed class RequestWindow : Window
{
    private const double Gap = 4;
    private const int ExtendedStyle = -20, NoActivate = 0x08000000, ToolWindow = 0x00000080;
    private const int AcceptsNotifications = 5;

    private readonly TableModel _model;
    private readonly Func<bool> _inFront;
    private bool _announced;

    /// <summary>`inFront` says whether the app's own window is the active one.</summary>
    public RequestWindow(TableModel model, Func<bool> inFront)
    {
        _model = model;
        _inFront = inFront;
        Title = "winawdl requests";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        Content = new RequestsView { DataContext = model };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, ExtendedStyle, GetWindowLong(handle, ExtendedStyle) | NoActivate | ToolWindow);
        };
        SizeChanged += (_, _) => Place();
        model.PropertyChanged += OnModelChanged;
    }

    /// <summary>Where the window goes, for a test that keeps it out of sight.
    /// Left alone, it sits in the corner of the screen above the taskbar.</summary>
    public Point? Spot { get; init; }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Reconsider();

    /// <summary>A request has arrived. False when it will not be shown here,
    /// and has to be said some other way.</summary>
    public bool Announce()
    {
        if (!_model.ShowRequests || !WindowsTakesNotices()) return false;
        _announced = true;
        Reconsider();
        return true;
    }

    /// <summary>The app's window came to the front or left it, or a request changed.</summary>
    public void Reconsider()
    {
        if (!_model.HasRequests) _announced = false;
        var wanted = _announced && _model.ShowRequests && !_inFront();
        if (wanted && !IsVisible)
        {
            var scale = MainWindow.TextScaleInWindows();
            ((FrameworkElement)Content).LayoutTransform = new ScaleTransform(scale, scale);
            Place();
            Show();
        }
        else if (!wanted && IsVisible) Hide();
    }

    private void Place()
    {
        if (Spot is { } spot)
        {
            (Left, Top) = (spot.X, spot.Y);
            return;
        }
        // Before it is first shown the window has no size yet: its content says what it will be.
        var content = (FrameworkElement)Content;
        if (!IsVisible) content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = IsVisible ? new Size(ActualWidth, ActualHeight) : content.DesiredSize;
        var room = SystemParameters.WorkArea;
        (Left, Top) = (room.Right - size.Width - Gap, room.Bottom - size.Height - Gap);
    }

    /// <summary>Not while a full-screen app or a presentation has the screen.</summary>
    private static bool WindowsTakesNotices() =>
        SHQueryUserNotificationState(out var state) != 0 || state == AcceptsNotifications;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
