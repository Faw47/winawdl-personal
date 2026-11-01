using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Winawdl.Shell;

/// <summary>Installer-owned shortcut identity and per-user notification protocol.</summary>
public static class ToastRegistration
{
    public const string InstallFlag = "--notifications-install", RemoveFlag = "--notifications-remove";
    public const string ShortcutFlag = "--notification-shortcut";
    private const string ProtocolKey = @"Software\Classes\winawdl-notice";
    private static readonly PropertyKey AppIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private static string Command => $"\"{Environment.ProcessPath}\" \"%1\"";

    public static bool Installed
    {
        get
        {
            using var protocol = Registry.CurrentUser.OpenSubKey(ProtocolKey + @"\shell\open\command");
            if (!string.Equals(protocol?.GetValue("") as string, Command, StringComparison.OrdinalIgnoreCase)) return false;
            return new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms }
                .Select(where => Path.Combine(Environment.GetFolderPath(where), "winawdl.lnk"))
                .Any(path => File.Exists(path) && ShortcutAppId(path) == WindowsToast.AppId);
        }
    }

    public static int Register(bool enabled)
    {
        try
        {
            using var existing = Registry.CurrentUser.OpenSubKey(ProtocolKey);
            using var command = existing?.OpenSubKey(@"shell\open\command");
            if (existing is not null && (existing.GetValue("winawdl-owner") as string != WindowsToast.AppId
                || !string.Equals(command?.GetValue("") as string, Command, StringComparison.OrdinalIgnoreCase))) return 1;
            if (enabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(ProtocolKey);
                key.SetValue("", "URL:winawdl Notification");
                key.SetValue("URL Protocol", "");
                key.SetValue("winawdl-owner", WindowsToast.AppId);
                using var open = key.CreateSubKey(@"shell\open\command");
                open.SetValue("", Command);
            }
            else
            {
                new WindowsToast().Clear();
                if (existing is not null)
                {
                    using (var open = Registry.CurrentUser.OpenSubKey(ProtocolKey + @"\shell\open\command", true))
                        open?.DeleteValue("", false);
                    using (var key = Registry.CurrentUser.OpenSubKey(ProtocolKey, true))
                        foreach (var value in new[] { "", "URL Protocol", "winawdl-owner" }) key?.DeleteValue(value, false);
                    foreach (var suffix in new[] { @"\shell\open\command", @"\shell\open", @"\shell", "" })
                    {
                        var path = ProtocolKey + suffix;
                        using var key = Registry.CurrentUser.OpenSubKey(path);
                        if (key is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(path, false);
                    }
                }
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return 1; }
    }

    public static int Stamp(string path)
    {
        try { ShortcutAppId(path, WindowsToast.AppId); return 0; }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException) { return 1; }
    }

    internal static string ShortcutAppId(string path, string? set = null)
    {
        var iid = typeof(IPropertyStore).GUID;
        SHGetPropertyStoreFromParsingName(Path.GetFullPath(path), IntPtr.Zero, set is null ? 0 : 2, iid, out var store);
        try
        {
            var key = AppIdKey;
            if (set is not null)
            {
                var value = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(set) };
                try { store.SetValue(ref key, ref value); store.Commit(); }
                finally { PropVariantClear(ref value); }
            }
            store.GetValue(ref key, out var read);
            try { return read.Type == 31 ? Marshal.PtrToStringUni(read.Pointer) ?? "" : ""; }
            finally { PropVariantClear(ref read); }
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr bind, int flags,
        [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IPropertyStore store);
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
