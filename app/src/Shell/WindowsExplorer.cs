using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using Winawdl.Model;

namespace Winawdl.Shell;

/// <summary>Only this user's SendTo shortcut and two classic verbs. Windows 11
/// shows classic verbs under Show more options, not its packaged top-level menu.</summary>
public sealed class WindowsExplorer(string executable) : IExplorerIntegration
{
    public const string InstallFlag = "--explorer-install", RemoveFlag = "--explorer-remove";
    public const string SelectionModel = "Document";
    private const string Label = "Send with AirDrop";
    private const string OwnerValue = "winawdl-owner";
    private const string OwnerTag = "winawdl Explorer integration v1";
    private const string ShortcutHashValue = "winawdl-shortcut-sha256";
    private static readonly string[] Keys =
        [@"Software\Classes\*\shell\winawdl", @"Software\Classes\Directory\shell\winawdl"];
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), Label + ".lnk");

    public static string CommandFor(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Any(char.IsControl) || executable.Contains('"'))
            throw new ArgumentException("Explorer needs an absolute executable path.");
        return $"\"{executable}\" \"%1\"";
    }

    public bool Enabled
    {
        get
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            foreach (var path in Keys)
            {
                using var key = user.OpenSubKey(path);
                if (!PristineVerb(key, executable)) return false;
            }
            using var first = user.OpenSubKey(Keys[0]);
            return ShortcutMatches(Shortcut, executable)
                && MatchesHash(Shortcut, first?.GetValue(ShortcutHashValue) as string);
        }
        set
        {
            var command = CommandFor(executable);
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            if (value)
            {
                if (Enabled) return;
                string? existingHash;
                using (var first = user.OpenSubKey(Keys[0]))
                    existingHash = first?.GetValue(ShortcutHashValue) as string;
                if (File.Exists(Shortcut) && (!ShortcutMatches(Shortcut, executable)
                    || !MatchesHash(Shortcut, existingHash)))
                    throw new IOException("Another or customized Send with AirDrop shortcut already exists.");
                foreach (var path in Keys)
                {
                    using var existing = user.OpenSubKey(path);
                    if (existing != null && !PristineVerb(existing, executable))
                        throw new IOException("Another or customized Explorer verb already uses winawdl's key.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Shortcut)!);
                WithShortcutRegistration(Shortcut, executable, hash =>
                {
                    foreach (var path in Keys)
                    {
                        using var key = user.CreateSubKey(path);
                        key.SetValue(OwnerValue, OwnerTag);
                        key.SetValue("", Label);
                        key.SetValue("Icon", $"\"{executable}\",0");
                        // A static Document verb invokes once per item. Each joins the same app.
                        key.SetValue("MultiSelectModel", SelectionModel);
                        if (path == Keys[0]) key.SetValue(ShortcutHashValue, hash);
                        using var verb = key.CreateSubKey("command");
                        verb.SetValue("", command);
                    }
                }, () => Enabled = false);
            }
            else
            {
                string? hash;
                bool ownsShortcut;
                using (var first = user.OpenSubKey(Keys[0]))
                {
                    hash = first?.GetValue(ShortcutHashValue) as string;
                    ownsShortcut = first?.GetValue(OwnerValue) as string == OwnerTag;
                }
                if (ownsShortcut && MatchesHash(Shortcut, hash)) File.Delete(Shortcut);
                foreach (var path in Keys)
                {
                    using var key = user.OpenSubKey(path, writable: true);
                    if (key == null) continue;
                    var snapshot = Snapshot(key);
                    var removal = PlanVerbRemoval(snapshot, executable);
                    if (removal == null) continue;
                    using (var verb = key.OpenSubKey("command", writable: true))
                    {
                        foreach (var name in removal.CommandValues) verb?.DeleteValue(name, false);
                    }
                    using (var verb = key.OpenSubKey("command"))
                    {
                        if (verb is { ValueCount: 0, SubKeyCount: 0 }) key.DeleteSubKey("command", false);
                    }
                    foreach (var name in removal.Values) key.DeleteValue(name, false);
                    if (key.ValueCount == 0 && key.SubKeyCount == 0)
                    {
                        key.Dispose();
                        user.DeleteSubKey(path, false);
                    }
                }
            }
        }
    }

    internal sealed record VerbSnapshot(
        IReadOnlyDictionary<string, object?> Values,
        IReadOnlyDictionary<string, object?> CommandValues,
        IReadOnlyList<string> Subkeys,
        IReadOnlyList<string> CommandSubkeys);

    internal sealed record VerbRemoval(string[] Values, string[] CommandValues);

    private static VerbSnapshot Snapshot(RegistryKey key)
    {
        using var verb = key.OpenSubKey("command");
        return new(
            key.GetValueNames().ToDictionary(name => name, key.GetValue, StringComparer.OrdinalIgnoreCase),
            verb?.GetValueNames().ToDictionary(name => name, verb.GetValue, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, object?>(),
            key.GetSubKeyNames(), verb?.GetSubKeyNames() ?? []);
    }

    internal static bool PristineVerb(VerbSnapshot data, string executable)
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = Label, ["Icon"] = $"\"{executable}\",0",
            ["MultiSelectModel"] = SelectionModel, [OwnerValue] = OwnerTag
        };
        if (data.Values.ContainsKey(ShortcutHashValue))
            expected[ShortcutHashValue] = data.Values[ShortcutHashValue] as string ?? "";
        return data.Subkeys.Count == 1 && data.Subkeys[0].Equals("command", StringComparison.OrdinalIgnoreCase)
            && data.CommandSubkeys.Count == 0 && data.Values.Count == expected.Count
            && expected.All(pair => data.Values.TryGetValue(pair.Key, out var actual)
                && string.Equals(actual as string, pair.Value, StringComparison.Ordinal))
            && data.CommandValues.Count == 1
            && data.CommandValues.TryGetValue("", out var command)
            && string.Equals(command as string, CommandFor(executable), StringComparison.OrdinalIgnoreCase);
    }

    private static bool PristineVerb(RegistryKey? key, string executable) =>
        key != null && PristineVerb(Snapshot(key), executable);

    internal static VerbRemoval? PlanVerbRemoval(VerbSnapshot data, string executable)
    {
        if (data.Values.GetValueOrDefault(OwnerValue) as string != OwnerTag) return null;
        var values = new List<string> { OwnerValue };
        if (data.Values.GetValueOrDefault("") as string == Label) values.Add("");
        if (data.Values.GetValueOrDefault("Icon") as string == $"\"{executable}\",0") values.Add("Icon");
        if (data.Values.GetValueOrDefault("MultiSelectModel") as string == SelectionModel)
            values.Add("MultiSelectModel");
        if (data.Values.ContainsKey(ShortcutHashValue)) values.Add(ShortcutHashValue);
        var command = string.Equals(data.CommandValues.GetValueOrDefault("") as string,
            CommandFor(executable), StringComparison.OrdinalIgnoreCase) ? new[] { "" } : [];
        return new(values.ToArray(), command);
    }

    private static bool MatchesHash(string path, string? expected) =>
        expected != null && File.Exists(path)
        && string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), expected,
            StringComparison.OrdinalIgnoreCase);

    internal static void WithShortcutRegistration(
        string path, string executable, Action<string> writeRegistry, Action rollbackRegistry)
    {
        var created = !File.Exists(path);
        string? hash = null;
        try
        {
            if (created) WriteShortcut(path, executable);
            hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            writeRegistry(hash);
        }
        catch
        {
            try { rollbackRegistry(); }
            finally
            {
                if (created && MatchesHash(path, hash)) File.Delete(path);
            }
            throw;
        }
    }

    internal static void WriteShortcut(string path, string executable)
    {
        _ = CommandFor(executable);
        WithShortcut(path, link =>
        {
            link.TargetPath = executable;
            // Explorer appends the entire selected list to an executable SendTo link.
            link.Arguments = "";
            link.WorkingDirectory = Path.GetDirectoryName(executable);
            link.Description = Label;
            link.IconLocation = executable + ",0";
            link.Hotkey = "";
            link.WindowStyle = 1;
            link.Save();
        });
    }

    internal static bool ShortcutMatches(string path, string executable)
    {
        if (!File.Exists(path)) return false;
        var matches = false;
        WithShortcut(path, link => matches = string.Equals((string)link.TargetPath, executable,
            StringComparison.OrdinalIgnoreCase) && (string)link.Arguments == ""
            && string.Equals((string)link.WorkingDirectory, Path.GetDirectoryName(executable),
                StringComparison.OrdinalIgnoreCase)
            && (string)link.Description == Label
            && string.Equals((string)link.IconLocation, executable + ",0", StringComparison.OrdinalIgnoreCase)
            && (string)link.Hotkey == "" && (int)link.WindowStyle == 1);
        return matches;
    }

    private static void WithShortcut(string path, Action<dynamic> action)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        object? link = null;
        try
        {
            link = ((dynamic)shell).CreateShortcut(path);
            action(link);
        }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    public static int Register(bool enabled)
    {
        try
        {
            var integration = new WindowsExplorer(Environment.ProcessPath!);
            integration.Enabled = enabled;
            return integration.Enabled == enabled ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                     or System.Security.SecurityException or COMException) { return 1; }
    }
}
