using System.IO;
using System.IO.Pipes;
using System.Text;

namespace VSpeedLauncher.Core;

/// <summary>
/// Listens on <c>\\.\pipe\vspeed-daemon</c> for READY messages from in-game
/// <c>dev.vspeed.daemon.DaemonClient</c>.  The connection is one-shot per JVM:
/// the mod opens, sends one line, reads ACK, closes.  We loop accepting new
/// connections forever, one client at a time (no need for multi-stream since
/// at most a handful of JVMs are ever running).
///
/// Protocol (line, ASCII, key=value pairs, space-separated):
/// <code>
/// READY pid=12345 loadSeconds=68 instance=ATM10 hwnd=0
/// </code>
/// </summary>
public sealed class PipeServer
{
    public const string PipeName = "vspeed-daemon";

    private readonly InstanceManager _manager;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public PipeServer(InstanceManager manager) => _manager = manager;

    public void Start()
    {
        _loop = Task.Run(AcceptLoopAsync);
        Logger.Info($"Pipe server listening on \\\\.\\pipe\\{PipeName}");
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _loop?.Wait(500); } catch { /* swallow */ }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // PipeOptions.Asynchronous lets WaitForConnectionAsync respect
                // cancellation.  We allow only one client at a time — a fresh
                // server instance is created after each connection completes.
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cts.Token);
                await HandleClientAsync(server);
            }
            catch (OperationCanceledException)
            {
                // graceful shutdown
                break;
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe loop: {ex.Message}");
                await Task.Delay(250);
            }
        }
        Logger.Info("Pipe server stopped.");
    }

    private async Task HandleClientAsync(NamedPipeServerStream server)
    {
        try
        {
            // Read-only — the new DaemonClient (mod side) is fire-and-forget,
            // closes the pipe after a single write, doesn't wait for an ACK.
            // We accept whatever it pushed before close, parse it, react.
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);

            var line = await reader.ReadLineAsync(_cts.Token);
            if (line is null)
            {
                Logger.Warn("Pipe client connected but sent no data.");
                return;
            }
            Logger.Info($"PIPE <-- {line}");

            if (line.StartsWith("READY ", StringComparison.Ordinal))
            {
                ParseReady(line, out var pid, out var seconds, out var instance);
                _manager.OnReady(instance, pid, seconds);
            }
            else
            {
                Logger.Warn($"Unrecognised pipe message: {line}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Pipe client: {ex.Message}");
        }
        finally
        {
            // Without an explicit Disconnect the OS keeps the previous client
            // associated with this server instance, and the next accept loop
            // iteration immediately fires for nothing.  Disconnect resets so
            // the next WaitForConnectionAsync actually waits.
            if (server.IsConnected)
            {
                try { server.Disconnect(); } catch { /* already disposed */ }
            }
        }
    }

    private static void ParseReady(string line, out int pid, out long seconds, out string instance)
    {
        pid = 0; seconds = 0; instance = "";
        var body = line[6..];   // after "READY "

        // pid= and loadSeconds= are simple numeric tokens.
        pid     = (int) ExtractNum(body, "pid=");
        seconds = ExtractNum(body, "loadSeconds=");

        // instance= can contain spaces (e.g. "All the Mods 10 - ATM10").
        // It runs until " hwnd=" or end-of-line — so we must NOT split on spaces.
        var iIdx = body.IndexOf("instance=", StringComparison.Ordinal);
        if (iIdx >= 0)
        {
            var start = iIdx + "instance=".Length;
            var hwndIdx = body.IndexOf(" hwnd=", start, StringComparison.Ordinal);
            instance = (hwndIdx >= 0 ? body[start..hwndIdx] : body[start..]).Trim();
        }
    }

    private static long ExtractNum(string body, string key)
    {
        var i = body.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return 0;
        var start = i + key.Length;
        var end = start;
        while (end < body.Length && (char.IsDigit(body[end]) || body[end] == '-')) end++;
        return long.TryParse(body[start..end], out var v) ? v : 0;
    }
}
