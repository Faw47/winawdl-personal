using System.Globalization;
using System.IO;

namespace Winawdl.Model;

public enum DeviceKind { Other, Phone, Tablet, Computer }

public enum FileKind { File, Photo, Video, Link }

/// <summary>How the app says sizes, counts and kinds of things.</summary>
public static class Words
{
    private const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
    private static readonly CultureInfo English = CultureInfo.InvariantCulture;

    private static readonly HashSet<string> PhotoEndings = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".webp", ".bmp", ".tif", ".tiff" };
    private static readonly HashSet<string> VideoEndings = new(StringComparer.OrdinalIgnoreCase)
        { ".mov", ".mp4", ".m4v", ".avi", ".mkv", ".webm" };

    /// <summary>Sizes in the units Windows uses, where a megabyte is 1024 kilobytes.</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => Count((int)bytes, "byte", "bytes"),
        < (long)Mb => string.Create(English, $"{bytes / Kb:0} KB"),
        < (long)Gb => string.Create(English, $"{bytes / Mb:0.0} MB"),
        _ => string.Create(English, $"{bytes / Gb:0.00} GB"),
    };

    public static string Rate(double bytesPerSecond) => Size((long)bytesPerSecond) + "/s";

    public static string Percent(double fraction) =>
        string.Create(English, $"{Math.Floor(Math.Clamp(fraction, 0, 1) * 100):0}%");

    public static string Count(int count, string one, string many) =>
        count == 1 ? $"1 {one}" : string.Create(English, $"{count} {many}");

    /// <summary>A sender announces a Live Photo as a folder named *.pvt.</summary>
    private static bool IsLivePhoto(string name) =>
        Path.GetExtension(name).Equals(".pvt", StringComparison.OrdinalIgnoreCase);

    /// <summary>A file's name as shown, which for a Live Photo is its photo's.</summary>
    public static string Title(string name) => IsLivePhoto(name) ? Path.GetFileNameWithoutExtension(name) : name;

    public static FileKind KindOf(string name)
    {
        var ending = Path.GetExtension(name);
        if (PhotoEndings.Contains(ending) || IsLivePhoto(name)) return FileKind.Photo;
        if (VideoEndings.Contains(ending)) return FileKind.Video;
        return ending.Equals(".url", StringComparison.OrdinalIgnoreCase) ? FileKind.Link : FileKind.File;
    }

    /// <summary>"a photo", "3 videos", "4 photos and videos", "2 files".</summary>
    public static string Things(IReadOnlyCollection<string> names, int links = 0)
    {
        if (names.Count == 0)
            return links == 0 ? "something" : links == 1 ? "a link" : Count(links, "link", "links");
        var kinds = names.Select(KindOf).Distinct().ToList();
        var (one, many) = kinds switch
        {
            [FileKind.Photo] => ("a photo", "photos"),
            [FileKind.Video] => ("a video", "videos"),
            _ when kinds.All(kind => kind is FileKind.Photo or FileKind.Video) => ("", "photos and videos"),
            _ => ("a file", "files"),
        };
        return names.Count == 1 ? one : string.Create(English, $"{names.Count} {many}");
    }

    /// <summary>What kind of device an Apple model identifier names.</summary>
    public static DeviceKind KindOfDevice(string? model) => model switch
    {
        null => DeviceKind.Other,
        _ when model.StartsWith("iPhone", StringComparison.Ordinal) => DeviceKind.Phone,
        _ when model.StartsWith("iPad", StringComparison.Ordinal) => DeviceKind.Tablet,
        _ when model.Contains("Mac", StringComparison.Ordinal) => DeviceKind.Computer,
        _ => DeviceKind.Other,
    };

    public static string Name(DeviceKind kind) => kind switch
    {
        DeviceKind.Phone => "iPhone",
        DeviceKind.Tablet => "iPad",
        DeviceKind.Computer => "Mac",
        _ => "",
    };

    /// <summary>Segoe Fluent Icons: a phone, a tablet, a laptop, and "devices" for the rest.</summary>
    public static string Glyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Phone => "",
        DeviceKind.Tablet => "",
        DeviceKind.Computer => "",
        _ => "",
    };

    /// <summary>A page for a file, a picture, a film strip, a globe for a link.</summary>
    public static string Glyph(FileKind kind) => kind switch
    {
        FileKind.Photo => "",
        FileKind.Video => "",
        FileKind.Link => "",
        _ => "",
    };
}
