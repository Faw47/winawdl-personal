using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Winawdl.Model;

namespace Winawdl.Radio;

/// <summary>Starts and stops the radio from the app, which itself has no
/// administrator rights: a second copy of itself has them, started by the task
/// install.ps1 registers (RadioTask), or else by asking Windows. The app signals
/// that copy through a named event.</summary>
public sealed class RadioControl : IRadio
{
    private const int CancelledByUser = 1223;
    private const int TailBytes = 4096;
    private const int MostShownChars = 280;
    /// <summary>How long the task's copy has to show itself before Windows is asked instead.</summary>
    private static readonly TimeSpan TaskGrace = TimeSpan.FromSeconds(10);

    private readonly string? _repo;
    private readonly DispatcherTimer _watch;
    private Process? _supervisor;
    private EventWaitHandle? _stop;
    private int _starts;
    private string _stopName = "";
    private DateTime? _viaTaskSince;

    public RadioControl(Dispatcher dispatcher, string? repo)
    {
        _repo = repo;
        _watch = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background,
                                     (_, _) => Look(), dispatcher) { IsEnabled = false };
    }

    public RadioPhase Phase { get; private set; }
    public string LastLine { get; private set; } = "";
    public string LogFolder => RadioSupervisor.LogFolder;
    public bool FromSource => _repo is not null && Engine.Repo.IsSourceTree(_repo);
    public event Action? Changed;

    private void Become(RadioPhase phase, string lastLine = "")
    {
        if (Phase == phase && LastLine == lastLine) return;
        Phase = phase;
        LastLine = lastLine;
        Changed?.Invoke();
    }

    public void Start()
    {
        if (_supervisor is not null || _viaTaskSince is not null) return;
        if (_repo is null || !File.Exists(RadioSupervisor.RadioProgram(_repo))
                          || !File.Exists(RadioSupervisor.AwdlProgram(_repo)))
        {
            Become(RadioPhase.NotBuilt);
            return;
        }
        _stopName = $"winawdl-radio-stop-{Environment.ProcessId}-{++_starts}";
        _stop = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\" + _stopName);
        if (RadioTask.Run(Environment.ProcessId, _stopName))
        {
            _viaTaskSince = DateTime.UtcNow;
            Become(RadioPhase.Starting);
            _watch.Start();
            return;
        }
        Elevate();
    }

    /// <summary>Without the task, or when it did not start: Windows asks.</summary>
    private void Elevate()
    {
        try
        {
            _supervisor = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas",
                Arguments = $"{RadioSupervisor.Flag} {Environment.ProcessId} {_stopName}",
            });
        }
        catch (Win32Exception error) when (error.NativeErrorCode == CancelledByUser)
        {
            Become(RadioPhase.Refused);
            return;
        }
        catch (Win32Exception error)
        {
            Become(RadioPhase.Failed, error.Message);
            return;
        }
        Become(RadioPhase.Starting);
        _watch.Start();
    }

    public void Stop()
    {
        if (_supervisor is null && _viaTaskSince is null) return;
        _stop?.Set();
        Become(RadioPhase.Stopping);
    }

    /// <summary>The engine saw the AWDL adapter come up or go away.</summary>
    public void Linked(bool up)
    {
        if (up && Phase == RadioPhase.Starting) Become(RadioPhase.Running);
    }

    private void Look()
    {
        if (_supervisor is null && _viaTaskSince is { } since)
        {
            if (TaskCopy() is { } found)
            {
                _supervisor = found;
                _viaTaskSince = null;
            }
            else if (DateTime.UtcNow - since > TaskGrace)
            {
                _viaTaskSince = null;
                _watch.Stop();
                if (Phase == RadioPhase.Stopping) Become(RadioPhase.Off);
                else Elevate();
                return;
            }
            else return;
        }
        if (_supervisor is null) return;
        bool ended;
        try
        {
            ended = _supervisor.WaitForExit(0);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            ended = true;
        }
        if (!ended)
        {
            if (Phase == RadioPhase.Starting) Become(RadioPhase.Starting, Tail("radio.log"));
            return;
        }
        _watch.Stop();
        _supervisor.Dispose();
        _supervisor = null;
        _stop?.Dispose();
        _stop = null;
        var by = EndedBy();
        if (Phase == RadioPhase.Stopping || by is "stopped" or "owner")
            Become(RadioPhase.Off);
        else
            Become(RadioPhase.Failed, Tail(by == "awdld" ? "awdld.log" : "radio.log"));
    }

    /// <summary>The copy the task started, once it has said which process it is.</summary>
    private Process? TaskCopy()
    {
        try
        {
            var pid = int.Parse(File.ReadAllText(RadioSupervisor.PidFile(LogFolder, _stopName)).Trim());
            return Process.GetProcessById(pid);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException
                                       or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    private string EndedBy()
    {
        try
        {
            using var status = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(LogFolder, RadioSupervisor.StatusFile)));
            return status.RootElement.GetProperty("ended").GetString() ?? "";
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException)
        {
            return "";
        }
    }

    /// <summary>The last line a daemon wrote, which while it starts says what it
    /// is doing and after a failure says why.</summary>
    private string Tail(string logName)
    {
        try
        {
            using var log = new FileStream(Path.Combine(LogFolder, logName), FileMode.Open, FileAccess.Read,
                                           FileShare.ReadWrite | FileShare.Delete);
            log.Seek(-Math.Min(TailBytes, log.Length), SeekOrigin.End);
            var text = new StreamReader(log, Encoding.UTF8).ReadToEnd();
            var line = text.Split('\n').Select(part => part.Trim()).LastOrDefault(part => part.Length > 0) ?? "";
            return line.Length > MostShownChars ? line[..MostShownChars] + "…" : line;
        }
        catch (IOException)
        {
            return "";
        }
    }
}
