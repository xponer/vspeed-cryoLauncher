using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Application = System.Windows.Application;

namespace VSpeedLauncher.Core;

public enum InstanceState { Stopped, Loading, Ready, Hibernated, Waking, Crashed }

/// <summary>
/// One <see cref="RunningInstance"/> per slot in the UI.  Tracks the PrismLauncher
/// process and (later) the JVM PID once the mod reports it via the pipe.
/// Notifies the UI through <see cref="StateChanged"/>; runs entirely on the
/// background — the UI marshals through the Dispatcher.
/// </summary>
public sealed class RunningInstance
{
    public required InstanceEntry Entry { get; init; }

    public InstanceState State        { get; set; } = InstanceState.Stopped;
    public Process?      PrismProcess { get; set; }
    public int           JvmPid       { get; set; } = 0;
    public long          LoadSeconds  { get; set; } = 0;
    public long          ResidentMB   { get; set; } = 0;

    public DateTime?     ReadyAt      { get; set; }
    public string?       LastError    { get; set; }

    public event Action<RunningInstance>? Changed;

    public void Notify()
    {
        Changed?.Invoke(this);
    }
}

/// <summary>
/// Owns the list of running instances.  Public methods are safe to call from
/// any thread; they marshal to a background TaskScheduler so the UI thread
/// stays responsive while we spawn child processes.
/// </summary>
public sealed class InstanceManager
{
    private readonly ConfigStore _config;

    public ObservableCollection<RunningInstance> Instances { get; } = new();

    /// <summary>Called after OnReady sets state to Ready — used by App to record history.</summary>
    public Action<string, long>? OnReadyCallback { get; set; }

    // ── Benchmark support: await the next READY for a given instance ───────────
    private TaskCompletionSource<long>? _readyWaiter;
    private string? _readyWaiterId;

    /// <summary>Returns a task that completes with loadSeconds when the given
    /// instance next reaches Ready (or faults on timeout, handled by caller).</summary>
    public Task<long> AwaitReadyAsync(string id)
    {
        _readyWaiterId = id;
        _readyWaiter   = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _readyWaiter.Task;
    }

    public InstanceManager(ConfigStore config)
    {
        _config = config;
        foreach (var entry in _config.Data.Instances)
            Instances.Add(new RunningInstance { Entry = entry });
    }

    public RunningInstance? FindById(string id)
        => Instances.FirstOrDefault(i => i.Entry.Id == id);

    /// <summary>Registers a newly created/imported instance at runtime so it appears
    /// in the UI list without a restart. Marshals to the UI thread because
    /// <see cref="Instances"/> is bound to WPF.</summary>
    public void AddEntry(InstanceEntry entry)
    {
        if (Instances.Any(i => i.Entry.Id == entry.Id)) return;
        void add() => Instances.Add(new RunningInstance { Entry = entry });
        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) disp.Invoke(add);
        else add();
    }

    public RunningInstance? FindByJvmPid(int pid)
        => Instances.FirstOrDefault(i => i.JvmPid == pid);

    // ── Launch ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Spawn PrismLauncher for the given instance.  We pass extra system
    /// properties via JAVA_TOOL_OPTIONS so they apply even though we don't
    /// control Prism's JVM arg construction directly:
    /// <list type="bullet">
    ///   <item><c>-Dvspeed.daemon=true</c> — tells our mod to send READY</item>
    ///   <item><c>-Dvspeed.instance=&lt;id&gt;</c> — labels the JVM in the pipe</item>
    /// </list>
    /// </summary>
    public async Task LaunchAsync(RunningInstance inst, bool vanilla = false)
    {
        if (inst.State is InstanceState.Loading or InstanceState.Ready or InstanceState.Hibernated)
        {
            Logger.Info($"Launch({inst.Entry.Id}): already running, waking instead");
            await WakeAsync(inst);
            return;
        }

        if (string.IsNullOrEmpty(_config.Data.PrismExe) || !File.Exists(_config.Data.PrismExe))
        {
            inst.LastError = "PrismLauncher.exe path not configured.";
            inst.State = InstanceState.Crashed;
            inst.Notify();
            return;
        }

        inst.State = InstanceState.Loading;
        inst.LastError = null;
        inst.Notify();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName  = _config.Data.PrismExe,
                Arguments = $"--launch \"{inst.Entry.Id}\"",
                UseShellExecute = false,
                CreateNoWindow  = false,
            };
            // JAVA_TOOL_OPTIONS is honoured by HotSpot before any -X args.
            // This is how we inject the daemon-mode signal into a JVM we
            // don't directly invoke — Prism builds the JVM command line.
            var jto = $"-Dvspeed.daemon=true -Dvspeed.instance={inst.Entry.Id}";
            if (vanilla)
            {
                jto += " -Dvspeed.cache.enabled=false";
                Logger.Info($"Launch({inst.Entry.Id}): VANILLA mode (cache + AppCDS disabled)");
            }
            else
            {
                // AppCDS — the only lever that can touch boot-to-menu time.
                // Delivered via JAVA_TOOL_OPTIONS (verified to reach the game JVM).
                // Archive lives at a space-free path so no quoting is needed.
                try
                {
                    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var cdsDir = Path.Combine(local, "VSpeedLauncher", "cds");
                    Directory.CreateDirectory(cdsDir);
                    var safe = new string(inst.Entry.Id.Where(char.IsLetterOrDigit).ToArray());
                    var jsa  = Path.Combine(cdsDir, (safe.Length > 0 ? safe : "inst") + ".jsa");
                    if (!jsa.Contains(' '))   // safety: only if truly space-free
                        jto += $" -XX:+AutoCreateSharedArchive -XX:SharedArchiveFile={jsa}";
                }
                catch (Exception ex) { Logger.Warn($"AppCDS setup skipped: {ex.Message}"); }
            }
            psi.Environment["JAVA_TOOL_OPTIONS"] = jto;

            var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
            inst.PrismProcess = p;
            inst.Notify();
            Logger.Info($"Launched Prism for '{inst.Entry.Id}', pid {p.Id}");

            // Background watcher: if Prism exits without the JVM ever
            // reporting READY, mark as crashed.
            _ = Task.Run(async () =>
            {
                await p.WaitForExitAsync();
                if (inst.State == InstanceState.Loading)
                {
                    inst.LastError = $"Prism exited with code {p.ExitCode} before game became ready.";
                    inst.State = InstanceState.Crashed;
                    Application.Current.Dispatcher.Invoke(inst.Notify);
                }
            });
        }
        catch (Exception e)
        {
            inst.LastError = e.Message;
            inst.State = InstanceState.Crashed;
            inst.Notify();
            Logger.Error($"Launch failed: {e}");
        }
    }

    // ── Pipe-driven callback ─────────────────────────────────────────────────

    /// <summary>
    /// Called by <see cref="PipeServer"/> when the in-game mod sends READY.
    /// At this point the JVM is at the main menu and has zero further work
    /// to do — perfect moment to hibernate.
    /// </summary>
    public void OnReady(string instanceId, int jvmPid, long loadSeconds)
    {
        // Match by ID if possible; otherwise the first Loading instance
        // (handles users who launched directly via Prism without the UI).
        var inst = FindById(instanceId)
                ?? Instances.FirstOrDefault(i => i.State == InstanceState.Loading);
        if (inst is null)
        {
            Logger.Warn($"OnReady: no matching instance for '{instanceId}' (pid {jvmPid})");
            return;
        }

        inst.JvmPid      = jvmPid;
        inst.LoadSeconds = loadSeconds;
        inst.ReadyAt     = DateTime.Now;
        inst.State       = InstanceState.Ready;

        Application.Current.Dispatcher.Invoke(inst.Notify);
        Logger.Info($"READY: instance='{inst.Entry.Id}' jvm={jvmPid} load={loadSeconds}s");
        // Record using the matched instance's real id (the pipe value may be the
        // Prism display name; the matched RunningInstance is the source of truth).
        OnReadyCallback?.Invoke(inst.Entry.Id, loadSeconds);

        // Unblock a benchmark run waiting on this instance's boot.
        if (_readyWaiter != null && _readyWaiterId == inst.Entry.Id)
        {
            var w = _readyWaiter;
            _readyWaiter = null;
            w.TrySetResult(loadSeconds);
        }

        // No auto-hibernation — the user runs the game normally.  We just watch
        // the JVM so the launcher returns to Stopped when the game is closed.
        WatchJvmExit(inst);
    }

    /// <summary>
    /// Watch the game JVM; when it exits, flip the instance back to Stopped so
    /// the launcher doesn't keep showing it as running.
    /// </summary>
    private void WatchJvmExit(RunningInstance inst)
    {
        var pid = inst.JvmPid;
        if (pid <= 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var p = System.Diagnostics.Process.GetProcessById(pid);
                await p.WaitForExitAsync();
            }
            catch { /* already gone */ }

            // Only reset if it's still the same session (pid unchanged, not re-launched)
            if (inst.JvmPid == pid && inst.State is InstanceState.Ready or InstanceState.Hibernated)
            {
                inst.State  = InstanceState.Stopped;
                inst.JvmPid = 0;
                Application.Current.Dispatcher.Invoke(inst.Notify);
                Logger.Info($"JVM {pid} exited -> '{inst.Entry.Id}' Stopped");
            }
        });
    }

    // ── Hibernate / Wake ─────────────────────────────────────────────────────

    public void Hibernate(RunningInstance inst)
    {
        if (inst.State != InstanceState.Ready) return;
        var bytes = ProcessHibernator.Hibernate(inst.JvmPid);
        inst.ResidentMB = bytes > 0 ? bytes / 1024 / 1024 : 0;
        inst.State = InstanceState.Hibernated;
        Application.Current.Dispatcher.Invoke(inst.Notify);
    }

    public Task WakeAsync(RunningInstance inst)
    {
        if (inst.State != InstanceState.Hibernated && inst.State != InstanceState.Ready)
            return Task.CompletedTask;
        inst.State = InstanceState.Waking;
        Application.Current.Dispatcher.Invoke(inst.Notify);

        return Task.Run(() =>
        {
            ProcessHibernator.Wake(inst.JvmPid);
            inst.State = InstanceState.Ready;
            Application.Current.Dispatcher.Invoke(inst.Notify);
        });
    }

    public void Kill(RunningInstance inst)
    {
        if (inst.JvmPid > 0)   ProcessHibernator.Terminate(inst.JvmPid);
        if (inst.PrismProcess is { HasExited: false } p) p.Kill(entireProcessTree: true);
        inst.State = InstanceState.Stopped;
        inst.JvmPid = 0;
        Application.Current.Dispatcher.Invoke(inst.Notify);
    }

    public void WakeAndCloseAll()
    {
        foreach (var inst in Instances.ToList())
        {
            if (inst.State == InstanceState.Hibernated)
                ProcessHibernator.Wake(inst.JvmPid);
            Kill(inst);
        }
    }
}
