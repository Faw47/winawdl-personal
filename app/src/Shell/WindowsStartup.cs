using System.IO;
using Microsoft.Win32;
using Winawdl.Model;

namespace Winawdl.Shell;

/// <summary>A per-user Run entry, in the same explicit view the uninstaller removes.</summary>
public sealed class WindowsStartup(string executable) : IStartup
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "winawdl";
    public const string TrayFlag = "--tray";

    public static string CommandFor(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"'))
            throw new ArgumentException("Windows startup needs an absolute executable path.");
        var command = $"\"{executable}\" {TrayFlag}";
        if (command.Length > 260)
            throw new ArgumentException("The executable path is too long for Windows startup.");
        return command;
    }

    public bool Enabled
    {
        get
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var run = user.OpenSubKey(RunKey);
            return run?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command
                && string.Equals(command, CommandFor(executable), StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            if (value)
            {
                var command = CommandFor(executable);
                using var run = user.CreateSubKey(RunKey, writable: true);
                run.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                using var run = user.OpenSubKey(RunKey, writable: true);
                run?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }
}
