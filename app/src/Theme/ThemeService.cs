using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Winawdl.Theme;

/// <summary>Reads light or dark, the accent and the contrast setting from Windows,
/// and keeps the app's resources in step with them.</summary>
public sealed class ThemeService
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Dwm = @"Software\Microsoft\Windows\DWM";
    private static readonly Rgb WindowsBlue = Rgb.Hex("#0078D4");

    private readonly ResourceDictionary _resources;
    private readonly Dispatcher _dispatcher;

    public ThemeService(ResourceDictionary resources, Dispatcher dispatcher)
    {
        _resources = resources;
        _dispatcher = dispatcher;
        Current = Palette.Light(WindowsBlue);
    }

    public Palette Current { get; private set; }
    public event Action? Changed;

    public static Rgb SystemAccent()
    {
        // The value is stored as alpha, blue, green, red.
        if (Registry.CurrentUser.OpenSubKey(Dwm)?.GetValue("AccentColor") is int stored)
            return new Rgb((byte)stored, (byte)(stored >> 8), (byte)(stored >> 16));
        return WindowsBlue;
    }

    public static bool AppsAreDark() =>
        Registry.CurrentUser.OpenSubKey(Personalize)?.GetValue("AppsUseLightTheme") is 0;

    public static Palette FromWindows()
    {
        if (SystemParameters.HighContrast)
            return Palette.System(Rgb.From(SystemColors.WindowColor), Rgb.From(SystemColors.WindowTextColor),
                                  Rgb.From(SystemColors.HighlightColor),
                                  Rgb.From(SystemColors.HighlightTextColor),
                                  Rgb.From(SystemColors.HotTrackColor));
        return AppsAreDark() ? Palette.Night(SystemAccent()) : Palette.Light(SystemAccent());
    }

    /// <summary>Follow Windows from now on.</summary>
    public void Watch()
    {
        Refresh();
        SystemEvents.UserPreferenceChanged += (_, _) => _dispatcher.BeginInvoke(Refresh);
        SystemParameters.StaticPropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(SystemParameters.HighContrast)
                or nameof(SystemParameters.ClientAreaAnimation))
                _dispatcher.BeginInvoke(Refresh);
        };
    }

    private void Refresh()
    {
        Motion.Reduced = !SystemParameters.ClientAreaAnimation;
        Apply(FromWindows());
    }

    public void Apply(Palette palette)
    {
        Current = palette;
        void Set(string key, Rgb color, double opacity = 1) => _resources[key] = palette.Brush(color, opacity);
        Set("Table", palette.Table);
        Set("Raised", palette.Raised);
        Set("Well", palette.Well);
        Set("Ink", palette.Ink);
        Set("InkSoft", palette.InkSoft);
        Set("Line", palette.Line);
        Set("ControlLine", palette.ControlLine);
        Set("Ember", palette.Ember);
        // Apple's system green for "this PC can be found": the darker one on light, as its contrast needs.
        Set("Available", palette.Plain ? palette.Ink : Rgb.Hex(palette.Dark ? "#30D158" : "#248A3D"));
        Set("Accent", palette.Accent);
        Set("OnAccent", palette.OnAccent);
        Set("AccentText", palette.AccentText);
        Set("AccentSoft", palette.AccentSoft);
        Set("Edge", palette.Edge, palette.EdgeOpacity);
        Set("Veil", palette.Dark ? Rgb.Black : palette.Ink, palette.Plain ? 0.75 : palette.Dark ? 0.5 : 0.28);
        Set("Hover", palette.Well);
        Set("Scroll", palette.InkSoft);
        Set("Outline", palette.Line, palette.Plain ? 1 : 0.6);
        _resources["ShadowStrength"] = palette.Shadow;
        _resources["ShadowColor"] = palette.ShadowTone.Color;
        _resources["ShadeStrength"] = palette.Shade;
        _resources["Plain"] = palette.Plain;
        _resources["TableColor"] = palette.Table.Color;
        // An avatar is a soft sphere of the well's grey, lit from above, as
        // Apple draws a contact without a picture.
        var avatar = new LinearGradientBrush(palette.Raised.Color, palette.Well.Color, 90);
        if (palette.Plain) avatar = new LinearGradientBrush(palette.Table.Color, palette.Table.Color, 90);
        avatar.Freeze();
        _resources["Avatar"] = avatar;
        Changed?.Invoke();
    }
}
