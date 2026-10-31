using System.Globalization;
using System.Windows.Media;

namespace Winawdl.Theme;

/// <summary>An opaque color, with the arithmetic contrast rules are written in.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb Black = new(0, 0, 0);

    public static Rgb Hex(string hex)
    {
        hex = hex.TrimStart('#');
        return new Rgb(byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber),
                       byte.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber),
                       byte.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber));
    }

    public static Rgb From(Color color) => new(color.R, color.G, color.B);

    public Color Color => Color.FromRgb(R, G, B);

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>Relative luminance as WCAG 2 defines it.</summary>
    public double Luminance
    {
        get
        {
            static double Linear(byte channel)
            {
                var c = channel / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);
        }
    }

    public static double Contrast(Rgb a, Rgb b)
    {
        var (light, dark) = (Math.Max(a.Luminance, b.Luminance), Math.Min(a.Luminance, b.Luminance));
        return (light + 0.05) / (dark + 0.05);
    }

    /// <summary>This color with `amount` of `other` laid over it.</summary>
    public Rgb Mix(Rgb other, double amount)
    {
        byte Channel(byte from, byte to) => (byte)Math.Round(from + (to - from) * amount);
        return new Rgb(Channel(R, other.R), Channel(G, other.G), Channel(B, other.B));
    }

    /// <summary>The same hue and saturation at another lightness, 0 to 1.</summary>
    public Rgb WithLightness(double lightness)
    {
        var (hue, saturation, _) = Hsl;
        return FromHsl(hue, saturation, Math.Clamp(lightness, 0, 1));
    }

    public (double Hue, double Saturation, double Lightness) Hsl
    {
        get
        {
            double r = R / 255.0, g = G / 255.0, b = B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            var lightness = (max + min) / 2;
            if (max == min) return (0, 0, lightness);
            var spread = max - min;
            var saturation = lightness > 0.5 ? spread / (2 - max - min) : spread / (max + min);
            double hue;
            if (max == r) hue = (g - b) / spread + (g < b ? 6 : 0);
            else if (max == g) hue = (b - r) / spread + 2;
            else hue = (r - g) / spread + 4;
            return (hue / 6, saturation, lightness);
        }
    }

    private static Rgb FromHsl(double hue, double saturation, double lightness)
    {
        if (saturation == 0)
        {
            var grey = (byte)Math.Round(lightness * 255);
            return new Rgb(grey, grey, grey);
        }
        var q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
        var p = 2 * lightness - q;
        double Channel(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        return new Rgb((byte)Math.Round(Channel(hue + 1.0 / 3) * 255),
                       (byte)Math.Round(Channel(hue) * 255),
                       (byte)Math.Round(Channel(hue - 1.0 / 3) * 255));
    }
}

/// <summary>Every color the app draws with, for one Windows setting.</summary>
public sealed class Palette
{
    public const double TextContrast = 4.5;
    public const double ShapeContrast = 3.0;

    public bool Dark { get; private init; }
    /// <summary>A Windows contrast theme is on: the system's colors, outlines for depth.</summary>
    public bool Plain { get; private init; }

    public Rgb Table { get; private init; }
    public Rgb Raised { get; private init; }
    public Rgb Well { get; private init; }
    public Rgb Ink { get; private init; }
    public Rgb InkSoft { get; private init; }
    public Rgb Line { get; private init; }
    public Rgb Ember { get; private init; }
    /// <summary>The accent as a fill: a button, a switch that is on, the bar of a transfer.</summary>
    public Rgb Accent { get; private init; }
    public Rgb OnAccent { get; private init; }
    /// <summary>The accent as text on any surface.</summary>
    public Rgb AccentText { get; private init; }
    /// <summary>The tint a raised surface takes where a transfer has filled it.</summary>
    public Rgb AccentSoft { get; private init; }
    /// <summary>How strong the shadow under a raised thing is, 0 to 1, and its
    /// color: on paper a shadow is a darker paper, not grey.</summary>
    public double Shadow { get; private init; }
    public Rgb ShadowTone { get; private init; }
    /// <summary>The lit upper edge of a raised thing, and how visible it is.</summary>
    public Rgb Edge { get; private init; }
    public double EdgeOpacity { get; private init; }
    /// <summary>How dark the shaded upper lip of a well is.</summary>
    public double Shade { get; private init; }

    public IEnumerable<Rgb> Surfaces => [Table, Raised, Well];

    // Neutral greys as Apple's system colors are, with every ink at 4.5:1 or
    // more on each surface. The window is the grouped background, a card is
    // the surface on it, and a well is the fill behind an avatar or a field.
    public static Palette Light(Rgb accent) => Build(dark: false, accent,
        table: "#F2F2F7", raised: "#FFFFFF", well: "#E8E8ED", ink: "#1D1D1F", inkSoft: "#5E5E63",
        line: "#D8D8DD", ember: "#C4111C", shadow: 0.16, shadowTone: "#000000", edge: Rgb.White,
        edgeOpacity: 0, shade: 0);

    public static Palette Night(Rgb accent) => Build(dark: true, accent,
        table: "#1C1C1E", raised: "#2C2C2E", well: "#38383B", ink: "#F5F5F7", inkSoft: "#AEAEB2",
        line: "#3A3A3C", ember: "#FF8A82", shadow: 0.5, shadowTone: "#000000", edge: Rgb.White,
        edgeOpacity: 0, shade: 0);

    /// <summary>The system's own colors, for a Windows contrast theme.</summary>
    public static Palette System(Rgb window, Rgb text, Rgb highlight, Rgb highlightText, Rgb hotTrack) => new()
    {
        Dark = window.Luminance < 0.5, Plain = true,
        Table = window, Raised = window, Well = window, Ink = text, InkSoft = text, Line = text,
        Ember = text, Accent = highlight, OnAccent = highlightText, AccentText = hotTrack,
        AccentSoft = window, Shadow = 0, Edge = text, EdgeOpacity = 1, Shade = 0,
    };

    private static Palette Build(bool dark, Rgb accent, string table, string raised, string well,
                                 string ink, string inkSoft, string line, string ember, double shadow,
                                 string shadowTone, Rgb edge, double edgeOpacity, double shade)
    {
        Rgb[] surfaces = [Rgb.Hex(table), Rgb.Hex(raised), Rgb.Hex(well)];
        var fill = Reaching(accent, surfaces, ShapeContrast, towardDark: !dark);
        // White where it is readable, as Windows itself letters its accent; black on the pale ones.
        var onFill = Rgb.Contrast(Rgb.White, fill) >= TextContrast ? Rgb.White : Rgb.Black;
        var text = Reaching(accent, surfaces, TextContrast, towardDark: !dark);
        Rgb[] inks = [Rgb.Hex(ink), Rgb.Hex(inkSoft)];
        var soft = Tint(Rgb.Hex(raised), fill, dark ? 0.30 : 0.20, inks);
        return new Palette
        {
            Dark = dark, Table = surfaces[0], Raised = surfaces[1], Well = surfaces[2],
            Ink = inks[0], InkSoft = inks[1], Line = Rgb.Hex(line), Ember = Rgb.Hex(ember),
            Accent = fill, OnAccent = onFill, AccentText = text, AccentSoft = soft,
            Shadow = shadow, ShadowTone = Rgb.Hex(shadowTone), Edge = edge, EdgeOpacity = edgeOpacity,
            Shade = shade,
        };
    }

    /// <summary>The color nearest to `color` in lightness that reaches `ratio` against
    /// each of `against`, keeping its hue.</summary>
    private static Rgb Reaching(Rgb color, Rgb[] against, double ratio, bool towardDark)
    {
        var lightness = color.Hsl.Lightness;
        var candidate = color;
        for (var step = 0; step <= 100; step++)
        {
            candidate = color.WithLightness(lightness + (towardDark ? -step : step) * 0.01);
            if (against.All(surface => Rgb.Contrast(candidate, surface) >= ratio)) break;
        }
        return candidate;
    }

    /// <summary>`surface` tinted with `accent` as far as `amount`, or less where
    /// the inks would stop being readable on it.</summary>
    private static Rgb Tint(Rgb surface, Rgb accent, double amount, Rgb[] inks)
    {
        for (; amount > 0; amount -= 0.02)
        {
            var tinted = surface.Mix(accent, amount);
            if (inks.All(ink => Rgb.Contrast(ink, tinted) >= TextContrast)) return tinted;
        }
        return surface;
    }

    public SolidColorBrush Brush(Rgb color, double opacity = 1)
    {
        var brush = new SolidColorBrush(color.Color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }
}
