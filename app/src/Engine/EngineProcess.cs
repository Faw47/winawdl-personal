using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Winawdl.Model;

namespace Winawdl.Engine;

/// <summary>Where the project's files are, seen from the running app.</summary>
public static class Repo
{
    /// <summary>The folder above the app that holds airdrop\engine.py, or null.</summary>
    public static string? Find()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "airdrop", "engine.py")))
                return folder.FullName;
        return null;
    }

    /// <summary>Whether `root` is a clone being worked in, whose programs are
    /// built there, and not an installed copy.</summary>
    public static bool IsSourceTree(string root) => File.Exists(Path.Combine(root, "scripts", "build-radio.ps1"));

    public static string DataFolder
    {
        get
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "winawdl");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}

/// <summary>Runs airdrop\engine.py and passes JSON lines both ways.</summary>
public sealed partial class EngineProcess(Dispatcher dispatcher, string? repo, IReadOnlyList<string> extraArguments) : IEngine
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d,\d+ (?<level>[A-Z]+) (?<said>.*)$")]
    private static partial System.Text.RegularExpressions.Regex LogLine();

    private const long LogLimitBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private Process? _process;
    private string _lastError = "";
    private long _lastErrorAt;

    public event Action<JsonElement>? Said;
    public event Action<string, string>? Stopped;

    public static string LogPath => Path.Combine(Repo.DataFolder, "engine.log");

    public int? ProcessId => _process?.Id;

    private static string? OnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(folder => folder.Length > 0)
            .Select(folder => Path.Combine(folder.Trim('"'), program))
            .FirstOrDefault(File.Exists);

    public void Start(string name, string folder, bool ask, bool contactsOnly,
                      IReadOnlyList<string> knownSenders)
    {
        Stop();
        var script = repo is null ? null : Path.Combine(repo, "airdrop", "engine.py");
        if (script is null || !File.Exists(script))
        {
            Stopped?.Invoke("no_engine", "");
            return;
        }
        // The same choice the project's scripts make: the launcher first.
        var python = OnPath("py.exe") ?? OnPath("python.exe");
        if (python is null)
        {
            Stopped?.Invoke("no_python", "");
            return;
        }
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = repo,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
        };
        foreach (var argument in (string[])["-u", script, "--name", name, "--outdir", folder])
            start.ArgumentList.Add(argument);
        var keys = extraArguments.SkipWhile(argument => argument != "--keys").Skip(1).FirstOrDefault();
        var log = OpenLog();
        Identity.Ensure(keys ?? Identity.DefaultFolder, message =>
        {
            lock (log) log.WriteLine(message);
        });
        if (!ask) start.ArgumentList.Add("--take-all");
        if (contactsOnly) start.ArgumentList.Add("--contacts-only");
        // In one argument, so that an entry starting with a dash is not
        // taken for an option.
        foreach (var sender in knownSenders) start.ArgumentList.Add($"--known-sender={sender}");
        foreach (var argument in extraArguments) start.ArgumentList.Add(argument);

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, line) =>
        {
            if (string.IsNullOrWhiteSpace(line.Data)) return;
            // Its log is mostly routine. What explains an exit is an error, or
            // the unlogged lines Python prints as it gives up.
            var logged = LogLine().Match(line.Data);
            if (!logged.Success || logged.Groups["level"].Value is "ERROR" or "CRITICAL")
            {
                _lastError = logged.Success ? logged.Groups["said"].Value : line.Data.Trim();
                _lastErrorAt = Stopwatch.GetTimestamp();
            }
            lock (log) log.WriteLine(line.Data);
        };
        process.Exited += (_, _) => dispatcher.BeginInvoke(() =>
        {
            lock (log) log.Dispose();
            if (!ReferenceEquals(_process, process)) return;
            _process = null;
            // An error from long before the exit is not its cause.
            var recent = Stopwatch.GetElapsedTime(_lastErrorAt) < TimeSpan.FromSeconds(3);
            Stopped?.Invoke("ended", recent ? _lastError : "");
        });
        _lastError = "";
        try
        {
            process.Start();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
            log.Dispose();
            Stopped?.Invoke("ended", error.Message);
            return;
        }
        _process = process;
        process.BeginErrorReadLine();
        _ = Task.Run(() => Read(process));
    }

    private static StreamWriter OpenLog()
    {
        var old = new FileInfo(LogPath);
        var keep = old.Exists && old.Length < LogLimitBytes;
        return new StreamWriter(new FileStream(LogPath, keep ? FileMode.Append : FileMode.Create,
                                               FileAccess.Write, FileShare.ReadWrite), Utf8)
        {
            AutoFlush = true,
        };
    }

    private async Task Read(Process process)
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            JsonElement said;
            try
            {
                using var parsed = JsonDocument.Parse(line);
                said = parsed.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }
            _ = dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_process, process)) Said?.Invoke(said);
            });
        }
    }

    public void Tell(Dictionary<string, object?> command)
    {
        var line = JsonSerializer.Serialize(command);
        lock (_gate)
        {
            try
            {
                _process?.StandardInput.WriteLine(line);
                _process?.StandardInput.Flush();
            }
            catch (IOException)
            {
                // The engine is ending; its exit is reported on its own.
            }
        }
    }

    public void Stop()
    {
        var process = _process;
        if (process is null) return;
        _process = null;
        try
        {
            // The end of its input is the engine's cue to leave cleanly.
            lock (_gate) process.StandardInput.Close();
            if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
        }
    }
}
