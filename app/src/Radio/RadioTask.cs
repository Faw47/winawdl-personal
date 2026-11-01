using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Winawdl.Radio;

/// <summary>A scheduled task, registered by install.ps1 for the account that
/// installs winawdl, that runs `winawdl.exe --radio` with that account's full
/// rights. Started from the app it needs no prompt: the account agreed to it
/// once, at install. Where there is no such task, the app asks Windows instead.</summary>
public static partial class RadioTask
{
    public const string Name = "winawdl radio";

    /// <summary>The stop event's name, as RadioControl makes it; nothing else is
    /// passed through the task, so nothing else is taken.</summary>
    [GeneratedRegex(@"^winawdl-radio-stop-[0-9]{1,10}-[0-9]{1,10}$")]
    public static partial Regex StopName();

    internal static string[] Arguments(int owner, string stopName) => [owner.ToString(), stopName];

    /// <summary>Starts the supervisor through the task. False when there is no
    /// task this account may run, or Task Scheduler would not start it.</summary>
    public static bool Run(int owner, string stopName)
    {
        if (!StopName().IsMatch(stopName)) return false;
        object? service = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return false;
            service = Activator.CreateInstance(type)!;
            dynamic scheduler = service;
            scheduler.Connect();
            dynamic task = scheduler.GetFolder(@"\").GetTask(Name);
            // $(Arg0) and $(Arg1) in the task's arguments. Task Scheduler takes an
            // array of strings here and refuses an array of variants.
            task.Run(Arguments(owner, stopName));
            return true;
        }
        catch (Exception error)
        {
            // Whatever the task did, Windows can still be asked: this never stops the radio.
            System.Diagnostics.Trace.TraceWarning("The radio task did not run: {0}", error.Message);
            return false;
        }
        finally
        {
            if (service is not null) Marshal.FinalReleaseComObject(service);
        }
    }
}
