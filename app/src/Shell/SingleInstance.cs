using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace Winawdl.Shell;

public sealed record LaunchRequest(string[] Paths, bool Surface)
{
    public const int MaxBytes = 256 * 1024;
    public const int MaxPaths = 1024;

    public static LaunchRequest FromArguments(string[] args)
    {
        var own = args.TakeWhile(arg => arg != "--engine").ToArray();
        var paths = Sanitize(own.Where(arg => arg != WindowsStartup.TrayFlag));
        return new(paths, paths.Length > 0 || !own.Contains(WindowsStartup.TrayFlag));
    }

    public static string[] Sanitize(IEnumerable<string> paths)
    {
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var path in paths)
        {
            if (++count > MaxPaths) throw new ArgumentException("Too many selected paths.");
            if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path.Any(char.IsControl)
                || path.IndexOfAny(['"', '<', '>', '|', '*', '?']) >= 0
                || path.StartsWith(@"\\.\") || path.StartsWith(@"\\?\")
                || !Path.IsPathFullyQualified(path)) continue;
            try
            {
                var full = Path.GetFullPath(path);
                // Do not accept alternate data streams or DOS device paths.
                if (full.AsSpan(2).Contains(':')) continue;
                if (File.Exists(full) || Directory.Exists(full)) kept.Add(full);
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        return kept.ToArray();
    }

    public byte[] Encode()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this);
        if (bytes.Length > MaxBytes) throw new ArgumentException("The selected paths are too large to open together.");
        return bytes;
    }

    public static LaunchRequest? Decode(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) return null;
        try
        {
            var request = JsonSerializer.Deserialize<LaunchRequest>(bytes);
            if (request?.Paths == null || request.Paths.Length > MaxPaths) return null;
            var paths = Sanitize(request.Paths);
            return new(paths, request.Surface || paths.Length > 0);
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return null; }
    }
}

/// <summary>One owner for this Windows user across sessions. No UI-thread pipe waits.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    public bool IsFirst { get; }
    internal string PipeName { get; }
    internal string MutexName { get; }

    public SingleInstance(string name = "winawdl-app")
    {
        var user = WindowsIdentity.GetCurrent().User!.Value;
        PipeName = name + "-" + user;
        MutexName = name + "-" + user;
        _mutex = new Mutex(false, MutexName,
            new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out var first);
        IsFirst = first;
    }

    public void Listen(Action<LaunchRequest> deliver)
    {
        if (!IsFirst || _listener != null) throw new InvalidOperationException("Only the owner may listen once.");
        // Create the endpoint synchronously; a second process may already be connecting.
        var pipe = NewPipe();
        _listener = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                using (pipe)
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(_stop.Token);
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(3));
                        var header = new byte[4];
                        await pipe.ReadExactlyAsync(header, deadline.Token);
                        var length = BitConverter.ToInt32(header);
                        if (length is > 0 and <= LaunchRequest.MaxBytes)
                        {
                            var bytes = new byte[length];
                            await pipe.ReadExactlyAsync(bytes, deadline.Token);
                            if (LaunchRequest.Decode(bytes) is { } request)
                            {
                                deliver(request);
                                await pipe.WriteAsync(new byte[] { 1 }, deadline.Token);
                            }
                        }
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException) { }
                }
                if (!_stop.IsCancellationRequested) pipe = NewPipe();
            }
        });
    }

    private NamedPipeServerStream NewPipe() => new(PipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);

    public async Task<bool> Forward(LaunchRequest request)
    {
        byte[] bytes;
        try { bytes = request.Encode(); }
        catch (ArgumentException) { return false; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(deadline.Token);
            await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), deadline.Token);
            await pipe.WriteAsync(bytes, deadline.Token);
            var reply = new byte[1];
            await pipe.ReadExactlyAsync(reply, deadline.Token);
            return reply[0] == 1;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.GetAwaiter().GetResult();
        _stop.Dispose();
        _mutex.Dispose();
    }
}
