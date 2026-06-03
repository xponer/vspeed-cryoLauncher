using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core.Auth;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WpfApp = System.Windows.Application;

namespace VSpeedLauncher.Core;

/// <summary>
/// Bidirectional JS ↔ C# bridge for the Cryo React UI running inside WebView2.
///
/// <h3>Protocol</h3>
/// JS → C# (request):
/// <code>window.chrome.webview.postMessage(JSON.stringify({id, method, args}))</code>
/// C# → JS (response):
/// <code>{"id": "r1", "result": ...}  |  {"id": "r1", "error": "..."}</code>
/// C# → JS (push event, no id):
/// <code>{"type": "instanceStateChanged", "data": {...}}</code>
///
/// All bridge calls are handled on a background thread; responses are posted
/// back to the WebView2 via <c>Dispatcher.Invoke</c>.
/// </summary>
public sealed class CryoBridge
{
    private readonly InstanceManager _manager;
    private readonly ConfigStore     _config;
    private readonly HistoryStore    _history;
    private readonly WebView2        _webView;
    private readonly string          _prismDataDir;

    private static readonly JsonSerializerOptions _jOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        WriteIndented               = false,
        DefaultIgnoreCondition      = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public CryoBridge(InstanceManager manager, ConfigStore config,
                      HistoryStore history, WebView2 webView)
    {
        _manager      = manager;
        _config       = config;
        _history      = history;
        _webView      = webView;
        _prismDataDir = config.Data.PrismDataDir;

        // Subscribe to runtime state changes and push them to the UI
        foreach (var inst in manager.Instances)
            inst.Changed += OnInstanceChanged;

        manager.Instances.CollectionChanged += (_, _) =>
        {
            foreach (var inst in manager.Instances)
            {
                inst.Changed -= OnInstanceChanged;
                inst.Changed += OnInstanceChanged;
            }
        };

        // Set initial Discord presence if configured (no-op otherwise).
        UpdateDiscordPresence();
    }

    // ── Push: C# → JS ────────────────────────────────────────────────────────

    private void OnInstanceChanged(RunningInstance inst)
    {
        Push("instanceStateChanged", BuildRuntimeState(inst));
        ManageLauncherWindow(inst);
        UpdateDiscordPresence();
    }

    // ── Discord Rich Presence ────────────────────────────────────────────────
    private static readonly DiscordRpc _discord = new();

    /// <summary>Connects/refreshes Discord presence based on config + running games.
    /// No-op when disabled or no client ID is set. Runs off-thread (pipe I/O).</summary>
    public void UpdateDiscordPresence()
    {
        var enabled  = _config.Data.DiscordEnabled;
        var clientId = (_config.Data.DiscordClientId ?? "").Trim();
        _ = Task.Run(() =>
        {
            try
            {
                if (!enabled || string.IsNullOrEmpty(clientId)) { _discord.Clear(); return; }
                if (!_discord.Connect(clientId)) return;

                var active = _manager.Instances.FirstOrDefault(i =>
                    i.State is InstanceState.Ready or InstanceState.Loading or InstanceState.Hibernated);
                if (active != null)
                {
                    var name = active.Entry.DisplayName;
                    var meta = ReadMetaSafe(active.Entry.Id);
                    _discord.SetPresence($"Playing {name}", meta != null ? $"{meta.Loader} {meta.Mc}".Trim() : "Modded Minecraft");
                }
                else
                {
                    _discord.SetPresence("In the launcher", "Browsing modpacks");
                }
            }
            catch (Exception e) { Logger.Info($"Discord presence: {e.Message}"); }
        });
    }

    private InstanceMeta? ReadMetaSafe(string id)
    {
        try { return InstanceMetaReader.Read(id, _prismDataDir); } catch { return null; }
    }

    // ── Auto-update (Velopack + GitHub Releases) ─────────────────────────────
    private static readonly UpdateService _updater = new();

    private object GetAppVersion() => new
    {
        version   = _updater.CurrentVersion,
        installed = _updater.IsInstalled,
    };

    private async Task<object?> CheckForUpdateAsync()
    {
        try
        {
            if (!_updater.IsInstalled)
                return new { ok = true, available = false, installed = false, current = _updater.CurrentVersion };
            var v = await _updater.CheckAsync();
            return new { ok = true, available = v != null, version = v, current = _updater.CurrentVersion, installed = true };
        }
        catch (Exception e)
        {
            Logger.Warn($"CheckForUpdate: {e.Message}");
            return new { ok = false, error = e.Message };
        }
    }

    /// <summary>Downloads the pending update (progress via updateProgress) then
    /// applies it and restarts. Push events: updateProgress / updateError.</summary>
    private object ApplyUpdate()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                Push("updateProgress", new { phase = "download", percent = 0 });
                await _updater.DownloadAsync(p => Push("updateProgress", new { phase = "download", percent = p }));
                Push("updateProgress", new { phase = "ready", percent = 100 });
                await Task.Delay(700);   // let the UI show "restarting…"
                _updater.ApplyAndRestart();   // exits the process
            }
            catch (Exception e)
            {
                Logger.Warn($"ApplyUpdate: {e.Message}");
                Push("updateError", new { error = e.Message });
            }
        });
        return new { ok = true };
    }

    /// <summary>
    /// Hide the launcher when a game is loading (avoids GPU conflict with
    /// Minecraft's GL context + gives the user a clean game launch).
    /// Show it again when the game exits or crashes.
    /// </summary>
    private void ManageLauncherWindow(RunningInstance inst)
    {
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            switch (inst.State)
            {
                case InstanceState.Crashed:
                    // Always show the launcher on crash so the user can see the error.
                    App.Current.OpenMainWindow();
                    break;

                case InstanceState.Stopped:
                    // If we hid the window when the game started, bring it back now.
                    if (_config.Data.AutoHideOnLaunch)
                        App.Current.OpenMainWindow();
                    break;
            }
        });
    }

    private void Push(string type, object data)
    {
        var json = JsonSerializer.Serialize(new { type, data }, _jOpts);
        try
        {
            WpfApp.Current?.Dispatcher.Invoke(() =>
                _webView.CoreWebView2?.PostWebMessageAsString(json));
        }
        catch (Exception e)
        {
            Logger.Warn($"Bridge push failed: {e.Message}");
        }
    }

    // ── Receive: JS → C# ─────────────────────────────────────────────────────

    public void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var raw = e.WebMessageAsJson;
        _ = Task.Run(async () =>
        {
            string? reqId = null;
            try
            {
                var req  = JsonNode.Parse(raw)!;
                reqId    = req["id"]?.GetValue<string>();
                var meth = req["method"]?.GetValue<string>() ?? "";
                var argsNode = req["args"];
                var args = (argsNode is JsonObject) ? argsNode : new JsonObject();

                Logger.Info($"Bridge → {meth}");
                var result = await DispatchAsync(meth, args);
                Reply(reqId, result, null);
            }
            catch (Exception ex)
            {
                Logger.Error($"Bridge({reqId}): {ex.Message}");
                Reply(reqId, null, ex.Message);
            }
        });
    }

    private void Reply(string? id, object? result, string? error)
    {
        object payload = error != null
            ? new { id, error }
            : (object)new { id, result };
        var json = JsonSerializer.Serialize(payload, _jOpts);
        try
        {
            WpfApp.Current?.Dispatcher.Invoke(() =>
                _webView.CoreWebView2?.PostWebMessageAsString(json));
        }
        catch (Exception e)
        {
            Logger.Warn($"Bridge reply failed: {e.Message}");
        }
    }

    // ── Method dispatcher ─────────────────────────────────────────────────────

    private async Task<object?> DispatchAsync(string method, JsonNode args)
    {
        // Async handlers that perform real I/O run before the sync switch.
        if (method == "aiChat")              return await AiChatAsync(args);
        if (method == "accountStatus")       return await AccountStatusAsync();
        if (method == "getNeoForgeVersions") return await GetNeoForgeVersionsAsync(args.Str("mcVersion"));
        if (method == "searchModrinth")      return await SearchModrinthAsync(args.Str("query"), args.Str("id"), args.Int("offset", 0), args.Str("kind"));
        if (method == "getModrinthVersions") return await GetModrinthVersionsAsync(args.Str("projectId"), args.Str("id"));
        if (method == "pingServer")          return await PingServerAsync(args.Str("ip"));
        if (method == "searchCurseForge")    return await SearchCurseForgeAsync(args.Str("query"), args.Str("id"), args.Int("offset", 0), args.Str("kind"));
        if (method == "getCurseForgeFiles")  return await GetCurseForgeFilesAsync(args.Str("projectId"), args.Str("id"));
        if (method == "checkForUpdate")      return await CheckForUpdateAsync();
        if (method == "getModpackInfo")      return await GetModpackInfoAsync(args.Str("id"));
#pragma warning disable CS8619
        object? result = method switch
        {
            "getInstances"    => GetInstances(),
            "getInstance"     => GetInstance(args.Str("id")),
            "getKpis"         => GetKpis(args.Str("id")),
            "getCache"        => GetCache(args.Str("id")),
            "getMods"         => GetMods(args.Str("id")),
            "setModEnabled"   => SetModEnabled(args.Str("id"), args.Str("file"), args.Bool("enabled", true)),
            "openUrl"         => OpenUrl(args.Str("url")),
            "openPrism"       => OpenPrism(),
            "setProfileNextLaunch" => SetProfileNextLaunch(args.Str("id"), args.Bool("on", false)),
            "getHistory"      => GetHistory(),
            "getLogs"         => GetLogs(args.Str("id"), args.Int("n", 3000)),
            "getBootTimeline" => GetBootTimeline(args.Str("id")),
            "rebuildCache"    => RebuildCache(args.Str("id")),
            "launchInstance"  => LaunchInstance(args.Str("id"), args.Bool("vanilla", false), args.Str("joinServer")),
            "startBenchmark"  => StartBenchmark(args.Str("id")),
            "cancelBenchmark" => CancelBenchmark(),
            "selfCheck"       => SelfCheck(),
            "getAppVersion"   => GetAppVersion(),
            "applyUpdate"     => ApplyUpdate(),
            "openLauncherLog" => OpenLauncherLog(),
            "scanMods"        => ScanMods(args.Str("id")),
            "analyzeModGraph" => AnalyzeModGraph(args.Str("id")),
            "coreTest"        => CoreTest(args.Str("version"), args.Int("ram", 0)),
            "accountLogin"    => AccountLogin(),
            "accountLogout"   => AccountLogout(),
            "aiChatStream"    => StartAiStream(args),
            "getStats"        => GetStats(args.Str("id")),
            "stopInstance"    => StopInstance(args.Str("id")),
            "hibernateInstance" => HibernateInstance(args.Str("id")),
            "wakeInstance"    => WakeInstance(args.Str("id")),
            "windowMinimize"  => WindowMinimize(),
            "windowMaximize"  => WindowMaximize(),
            "windowClose"     => WindowClose(),
            "windowDragStart" => WindowDragStart(),
            "getConfig"         => GetConfig(),
            "saveConfig"        => SaveConfig(args),
            // ── Instance config (instance.cfg) ─────────────────────────────────
            "getInstanceCfg"    => GetInstanceCfg(args.Str("id")),
            "saveInstanceCfg"   => SaveInstanceCfg(args.Str("id"), args),
            "detectJavas"       => DetectJavas(args.Str("id")),
            "getSystemRam"      => GetSystemRam(),
            // ── Shell / file actions ───────────────────────────────────────────
            "openFolder"        => OpenFolder(args.Str("id")),
            "openCrashReport"   => OpenCrashReport(args.Str("id")),
            "exportLogs"        => ExportLogs(args.Str("id"), args.Str("content")),
            "removeFromLauncher"  => RemoveFromLauncher(args.Str("id")),
            // ── Profiles ───────────────────────────────────────────────────────────
            "getProfiles"         => GetProfiles(),
            "saveProfile"         => SaveProfile(args),
            "deleteProfile"       => DeleteProfile(args.Str("profileId")),
            "applyProfile"        => ApplyProfile(args.Str("id"), args.Str("profileId")),
            // ── AI Memory ─────────────────────────────────────────────────────────
            "saveAiMemory"        => SaveAiMemory(args.Str("id"), args.Str("problem"), args.Str("solution"), args["actions"]),
            "getAiMemory"         => GetAiMemory(args.Str("id")),
            "clearAiMemory"       => ClearAiMemory(args.Str("id")),
            // ── Instance creation / modpack install (no Prism) ─────────────────────
            "createInstance"      => CreateInstance(args),
            "duplicateInstance"   => DuplicateInstance(args.Str("id")),
            "installModrinthModpack"   => InstallModrinthModpack(args.Str("projectId"), args.Str("versionId"), args.Str("name")),
            "installCurseForgeModpack" => InstallCurseForgeModpack(args.Str("projectId"), args.Str("fileId"), args.Str("name")),
            "updateModpack"            => UpdateModpack(args.Str("id")),
            // ── Modpack Export / Import ────────────────────────────────────────────
            "exportModpack"       => ExportModpack(args.Str("id")),
            "importModpack"       => ImportModpack(),
            // ── Modrinth ───────────────────────────────────────────────────────────
            "downloadMod"         => DownloadMod(args.Str("id"), args.Str("url"), args.Str("filename"), args.Str("sha512"), args.Str("projectTitle")),
            "downloadModrinthMod" => DownloadModrinthMod(args.Str("id"), args.Str("projectId"), args.Str("versionId"), args.Str("projectTitle")),
            "checkModUpdates"     => CheckModUpdates(args.Str("id")),
            "updateMod"           => UpdateMod(args.Str("id"), args.Str("oldFile"), args.Str("url"), args.Str("newFilename"), args.Str("sha512")),
            // ── Server list ────────────────────────────────────────────────────────
            "getServers"          => GetServers(args.Str("id")),
            "addServer"           => AddServer(args.Str("id"), args.Str("name"), args.Str("ip")),
            "removeServer"        => RemoveServer(args.Str("id"), args.Str("ip")),
            // ── World Backups ──────────────────────────────────────────────────────
            "getWorlds"           => GetWorlds(args.Str("id")),
            "backupWorld"         => BackupWorld(args.Str("id"), args.Str("worldName")),
            "getBackups"          => GetBackups(args.Str("id")),
            "restoreBackup"       => RestoreBackup(args.Str("id"), args.Str("file")),
            "deleteBackup"        => DeleteBackup(args.Str("id"), args.Str("file")),
            "openWorldsFolder"    => OpenWorldsFolder(args.Str("id")),
            // ── Cryo Engine (CmlLib.Core) ──────────────────────────────────────────
            "installNeoForge"     => InstallNeoForge(args.Str("id"), args.Str("neoForgeVersion")),
            "launchWithEngine"    => LaunchWithEngine(args.Str("id"), args.Str("joinServer")),
            "getEngineStatus"     => GetEngineStatus(args.Str("id")),
            "setEngineSource"     => SetEngineSource(args.Str("id"), args.Str("source")),
            _ => throw new InvalidOperationException($"Unknown method: {method}"),
        };
#pragma warning restore CS8619
        return result;
    }

    // ── Instance list ─────────────────────────────────────────────────────────

    private object GetInstances()
    {
        var result = new List<object>();
        foreach (var inst in _manager.Instances)
        {
            try
            {
                var meta = ReadMeta(inst.Entry.Id);
                Merge(meta, inst);
                result.Add(EnrichWithPhases(meta, inst.Entry.Id));
            }
            catch (Exception ex)
            {
                Logger.Warn($"GetInstances: skipping '{inst.Entry.Id}': {ex.Message}");
                // Return a minimal safe object so the UI still shows the instance
                result.Add(new
                {
                    id = inst.Entry.Id, name = inst.Entry.DisplayName,
                    loader = "", mc = "", loaderVer = "", java = "",
                    mods = 0, ramMin = 2048, ramMax = 8192,
                    lastPlayed = 0L, accent = "#38BDF8",
                    cacheState = "off", wallClock = 90,
                    phases = DefaultPhases(90),
                    state = inst.State.ToString().ToLowerInvariant(),
                    jvmPid = inst.JvmPid, loadSeconds = inst.LoadSeconds,
                    residentMB = inst.ResidentMB, lastError = ex.Message,
                });
            }
        }
        return result;
    }

    private object GetInstance(string id)
    {
        var inst = _manager.FindById(id);
        var meta = ReadMeta(id);
        if (inst != null) Merge(meta, inst);
        return EnrichWithPhases(meta, id);
    }

    /// <summary>
    /// Adds wallClock and phases — required by the React Overview tab.
    /// wallClock is the last known boot time from history (or a safe estimate).
    /// phases are computed from proportional timings (bootstrap 12%, construction 71%, setup 17%).
    /// </summary>
    private object EnrichWithPhases(InstanceMeta meta, string id)
    {
        var kpi    = _history.GetKpis(id);
        double wall = kpi.Avg > 0 ? kpi.Avg : EstimateWall(meta.ModCount);
        return new
        {
            id           = meta.Id,
            name         = meta.Name,
            loader       = meta.Loader,
            mc           = meta.Mc,
            loaderVer    = meta.LoaderVer,
            java         = meta.Java,
            mods         = meta.ModCount,
            ramMin       = meta.RamMin,
            ramMax       = meta.RamMax,
            lastPlayed   = meta.LastPlayed,
            accent       = meta.Accent,
            cacheState   = meta.CacheState,
            wallClock    = (int)Math.Round(wall),
            phases       = DefaultPhases(wall),
            state        = meta.State,
            jvmPid       = meta.JvmPid,
            loadSeconds  = meta.LoadSeconds,
            residentMB   = meta.ResidentMB,
            lastError    = meta.LastError,
        };
    }

    private static double EstimateWall(int modCount) =>
        modCount switch { < 50 => 15, < 150 => 35, < 300 => 60, _ => 90 };

    private static object[] DefaultPhases(double wall)
    {
        var boot  = Math.Round(wall * 0.12, 1);
        var cons  = Math.Round(wall * 0.71, 1);
        var setup = Math.Round(wall * 0.17, 1);
        return new object[]
        {
            new { key = "bootstrap",    start = 0.0,        dur = boot,  cacheable = false },
            new { key = "construction", start = boot,       dur = cons,  cacheable = false },
            new { key = "setup",        start = boot+cons-Math.Round(wall*0.06,1), dur = setup, cacheable = false },
        };
    }

    private InstanceMeta ReadMeta(string id)
        => InstanceMetaReader.Read(id, _prismDataDir);

    private static void Merge(InstanceMeta meta, RunningInstance inst)
    {
        meta.State       = inst.State.ToString().ToLowerInvariant();
        meta.JvmPid      = inst.JvmPid;
        meta.LoadSeconds = inst.LoadSeconds;
        meta.ResidentMB  = inst.ResidentMB;
        meta.LastError   = inst.LastError ?? "";
    }

    private static object BuildRuntimeState(RunningInstance inst) => new
    {
        id          = inst.Entry.Id,
        state       = inst.State.ToString().ToLowerInvariant(),
        jvmPid      = inst.JvmPid,
        loadSeconds = inst.LoadSeconds,
        residentMB  = inst.ResidentMB,
        lastError   = inst.LastError ?? "",
    };

    // ── KPIs ──────────────────────────────────────────────────────────────────

    private object GetKpis(string id)
        => _history.GetKpis(id);

    // ── Cache ─────────────────────────────────────────────────────────────────

    private object GetCache(string id)
    {
        var dir  = Path.Combine(_prismDataDir, "instances", id);
        var info = InstanceMetaReader.ReadCacheInfo(dir);
        var state = info.TotalSizeBytes > 0 ? "ready" : "off";

        // Real entry counts come from the mod's vspeed-stats.json (written on world load).
        int recipes = 0, advancements = 0;
        try
        {
            var sp = Path.Combine(dir, "minecraft", "vspeed-stats.json");
            if (File.Exists(sp))
            {
                var types = JsonNode.Parse(File.ReadAllText(sp))?["types"]?.AsObject();
                recipes      = types?["recipe"]?["entries"]?.GetValue<int>() ?? 0;
                advancements = types?["advancement"]?["entries"]?.GetValue<int>() ?? 0;
            }
        }
        catch { /* ignore */ }

        return new
        {
            enabled          = true,
            state,
            modsetHash       = info.Hash != null ? "sha256:" + info.Hash + "…" : "",
            recipes,
            advancements,
            sizeBytes        = info.TotalSizeBytes,
            builtAt          = info.BuiltAt.HasValue
                                 ? new DateTimeOffset(info.BuiltAt.Value).ToUnixTimeMilliseconds()
                                 : (long?)null,
            path             = ".vspeed-cache/json/<type>/<hash>.bin",
            worldEntryCold   = 8.5,
            worldEntryWarm   = 1.8,
        };
    }

    // ── Mods ──────────────────────────────────────────────────────────────────

    private object GetMods(string id)
    {
        var modsDir = Path.Combine(_prismDataDir, "instances", id, "minecraft", "mods");
        if (!Directory.Exists(modsDir)) return Array.Empty<object>();

        var knownOptim = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "modernfix", "ferritecore", "sodium", "embeddium", "rubidium",
            "immediatelyfast", "entityculling", "moreculling", "noisium",
            "canary", "radium", "lithium", "starlight", "smoothboot",
        };

        // Include both enabled (*.jar) and disabled (*.jar.disabled) mods.
        var files = Directory.GetFiles(modsDir, "*.jar")
            .Concat(Directory.GetFiles(modsDir, "*.jar.disabled"));

        return files.Select(path =>
            {
                var file    = Path.GetFileName(path);                 // real filename, incl. .disabled
                var enabled = !file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var baseN   = enabled ? file : file[..^".disabled".Length];   // strip .disabled
                var fn      = baseN.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? baseN[..^4] : baseN;
                var sizeMb  = new FileInfo(path).Length / 1024.0 / 1024.0;
                var dash    = fn.LastIndexOf('-');
                var name    = dash > 0 ? fn[..dash] : fn;
                var ver     = dash > 0 ? fn[(dash + 1)..] : "";
                var isOptim = knownOptim.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
                return new
                {
                    id           = id + "::" + file,   // id carries the real filename for toggling
                    file,
                    name         = name.Replace('_', ' ').Replace('-', ' '),
                    version      = ver,
                    enabled,
                    optimization = isOptim,
                    sizeMb       = Math.Round(sizeMb, 1),
                    update       = false,
                };
            })
            .OrderBy(m => m.name)
            .ToList<object>();
    }

    /// <summary>Enable/disable a mod by renaming its jar ↔ jar.disabled.</summary>
    private object SetModEnabled(string id, string file, bool enabled)
    {
        var modsDir = Path.Combine(_prismDataDir, "instances", id, "minecraft", "mods");
        var current = Path.Combine(modsDir, file);
        if (!File.Exists(current))
            return new { ok = false, error = "Mod file not found: " + file };

        var isDisabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        string target;
        if (enabled && isDisabled)        target = Path.Combine(modsDir, file[..^".disabled".Length]);
        else if (!enabled && !isDisabled) target = Path.Combine(modsDir, file + ".disabled");
        else return new { ok = true, file };   // already in desired state

        try
        {
            File.Move(current, target, overwrite: false);
            Logger.Info($"SetModEnabled({id}): {file} -> {Path.GetFileName(target)}");
            return new { ok = true, file = Path.GetFileName(target), enabled };
        }
        catch (Exception e)
        {
            return new { ok = false, error = e.Message };
        }
    }

    /// <summary>Open a URL or PrismLauncher (for New/Duplicate which Prism owns).</summary>
    private object OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            return new { ok = true };
        }
        catch (Exception e) { return new { ok = false, error = e.Message }; }
    }

    private object OpenPrism()
    {
        var exe = _config.Data.PrismExe;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            return new { ok = false, error = "PrismLauncher path not configured" };
        try { System.Diagnostics.Process.Start(exe); return new { ok = true }; }
        catch (Exception e) { return new { ok = false, error = e.Message }; }
    }

    /// <summary>
    /// Toggle JFR boot profiling for the next launch by adding/removing a single
    /// (comma-free, Prism-safe) flag in the instance's JvmArgs.
    /// </summary>
    private object SetProfileNextLaunch(string id, bool on)
    {
        var cfgPath = Path.Combine(_prismDataDir, "instances", id, "instance.cfg");
        if (!File.Exists(cfgPath)) return new { ok = false, error = "instance.cfg not found" };
        const string FLAG = "-XX:StartFlightRecording=filename=vspeed-boot.jfr";

        var lines = File.ReadAllLines(cfgPath).ToList();
        bool inGeneral = true; int jvmIdx = -1; string jvm = "";
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i].Trim();
            if (l.StartsWith("[")) { inGeneral = l.Equals("[General]", StringComparison.OrdinalIgnoreCase); continue; }
            if (inGeneral && l.StartsWith("JvmArgs=")) { jvmIdx = i; jvm = l[8..]; }
        }
        jvm = jvm.Replace(FLAG, "").Trim();
        if (on) jvm = (FLAG + " " + jvm).Trim();

        if (jvmIdx >= 0) lines[jvmIdx] = "JvmArgs=" + jvm;
        else
        {
            var ui = lines.FindIndex(l => l.Trim().Equals("[UI]", StringComparison.OrdinalIgnoreCase));
            var ins = "JvmArgs=" + jvm;
            if (ui >= 0) lines.Insert(ui, ins); else lines.Add(ins);
        }
        // ensure OverrideJavaArgs=true
        var ovrIdx = lines.FindIndex(l => l.Trim().StartsWith("OverrideJavaArgs="));
        if (ovrIdx >= 0) lines[ovrIdx] = "OverrideJavaArgs=true";

        File.WriteAllLines(cfgPath, lines);
        Logger.Info($"SetProfileNextLaunch({id}): {(on ? "ON" : "OFF")}");
        return new { ok = true, on };
    }

    // ── History ───────────────────────────────────────────────────────────────

    private object GetHistory()
        => _history.GetAll();

    // ── Logs ──────────────────────────────────────────────────────────────────

    private object GetLogs(string id, int n)
        => LogReader.Read(id, _prismDataDir, n);

    // ── Boot timeline (waterfall derived from real log timestamps) ──────────────

    private object GetBootTimeline(string id)
    {
        var entries = LogReader.Read(id, _prismDataDir, 30000);
        if (entries.Count == 0) return new { ok = false, error = "No log found for this instance yet." };

        long start = entries[0].T;

        // Ordered milestones; each is the FIRST line (at/after the previous one)
        // whose message contains any of the given needles (case-insensitive).
        var milestones = new (string name, string[] needles)[]
        {
            ("Bootstrap",       new[]{ "ModLauncher running", "ModLauncher", "Java is", "JVM" }),
            ("Mod discovery",   new[]{ "Found mod", "mods to load", "ModDiscoverer", "Scanning" }),
            ("Loading mods",    new[]{ "Constructing", "Loading ", "LOAD_REGISTRIES", "Processing mod" }),
            ("Mixins",          new[]{ "Mixin", "mixing" }),
            ("Registries",      new[]{ "Applying holder", "Registry", "registered" }),
            ("Resource reload", new[]{ "Reloading ResourceManager" }),
            ("Texture atlas",   new[]{ "Created: ", "atlas", "Stitching" }),
            ("Audio / render",  new[]{ "OpenAL", "Sound engine started", "Backend library", "Narrator" }),
        };

        var phases = new List<object>();
        long cursorTime = start;
        int cursorIdx = 0;
        string? prevName = null; long prevTime = start;

        foreach (var (name, needles) in milestones)
        {
            int found = -1;
            for (int i = cursorIdx; i < entries.Count; i++)
            {
                var msg = entries[i].Msg;
                if (needles.Any(n => msg.Contains(n, StringComparison.OrdinalIgnoreCase)))
                { found = i; break; }
            }
            if (found < 0) continue;

            var ts = entries[found].T;
            if (ts < prevTime) ts = prevTime;     // keep monotonic
            if (prevName != null)
                phases.Add(new { name = prevName, atMs = prevTime - start, durationMs = ts - prevTime });
            prevName = name; prevTime = ts;
            cursorIdx = found + 1;
            cursorTime = ts;
        }

        // Final phase up to the last log line (≈ main menu reached)
        long endTime = entries[^1].T;
        if (prevName != null && endTime > prevTime)
            phases.Add(new { name = prevName, atMs = prevTime - start, durationMs = endTime - prevTime });

        long totalMs = endTime - start;
        return new { ok = true, totalMs, phases, lineCount = entries.Count };
    }

    // ── Cache rebuild ─────────────────────────────────────────────────────────

    private object RebuildCache(string id)
    {
        var cacheDir = Path.Combine(_prismDataDir, "instances", id,
                                    "minecraft", ".vspeed-cache", "json");
        if (Directory.Exists(cacheDir))
        {
            Directory.Delete(cacheDir, recursive: true);
            Logger.Info($"RebuildCache({id}): deleted {cacheDir}");
        }
        return new { ok = true };
    }

    // ── Launch / Stop / Hibernate / Wake ──────────────────────────────────────

    private object LaunchInstance(string id, bool vanilla, string joinServer = "")
    {
        var inst = _manager.FindById(id)
            ?? throw new InvalidOperationException($"Instance not found: {id}");

        // Cryo-native instances skip Prism and go straight to CmlLib.
        if (inst.Entry.Source == "cryo" && MicrosoftAccount.Instance.LoggedIn
            && GetStoredEngineVersion(id) != null)
        {
            Logger.Info($"LaunchInstance({id}): routing to Cryo engine (source=cryo)"
                        + (string.IsNullOrWhiteSpace(joinServer) ? "" : $", joining {joinServer}"));
            return LaunchWithEngine(id, joinServer);
        }

        if (_config.Data.AutoHideOnLaunch)
            WpfApp.Current?.Dispatcher.Invoke(() => WpfApp.Current?.MainWindow?.Hide());

        _ = _manager.LaunchAsync(inst, vanilla);
        return new { ok = true, vanilla };
    }

    // ── Automated benchmark (boot-to-menu A/B: Vanilla vs Optimized) ───────────
    private CancellationTokenSource? _benchCts;
    private volatile bool _benchRunning;

    private object StartBenchmark(string id)
    {
        if (_benchRunning) return new { ok = false, error = "Benchmark already running" };
        var inst = _manager.FindById(id)
            ?? throw new InvalidOperationException($"Instance not found: {id}");

        _benchCts = new CancellationTokenSource();
        _benchRunning = true;
        _ = Task.Run(() => RunBenchmarkAsync(inst, _benchCts.Token));
        return new { ok = true };
    }

    private object CancelBenchmark()
    {
        _benchCts?.Cancel();
        return new { ok = true };
    }

    /// <summary>
    /// Launches the game three times unattended and measures boot-to-menu via the
    /// mod's READY pipe signal: (1) Vanilla baseline, (2) Optimized warm-up to
    /// build the AppCDS class archive, (3) Optimized measured run that uses it.
    /// No world entry required — this targets the "time to main menu" the user cares about.
    /// </summary>
    private async Task RunBenchmarkAsync(RunningInstance inst, CancellationToken ct)
    {
        long vanilla = -1, optimized = -1;
        try
        {
            // Make sure nothing is already running for this instance.
            if (inst.State != InstanceState.Stopped) { _manager.Kill(inst); await Task.Delay(3000, ct); }

            Push("benchmarkProgress", new {
                phase = "start", step = 0, totalSteps = 3,
                message = "Benchmark started — the game launches 3× (~5–8 min). Leave it alone until done.",
            });

            // 1) Vanilla baseline: data cache OFF, AppCDS OFF.
            vanilla = await BenchRunAsync(inst, vanilla: true,  "Vanilla baseline (no cache, no AppCDS)", 1, ct);
            Push("benchmarkProgress", new {
                phase = "vanilla", step = 1, totalSteps = 3, bootVanilla = vanilla,
                message = vanilla > 0 ? $"Vanilla boot-to-menu: {vanilla}s"
                                      : "Vanilla run timed out / no READY signal",
            });

            // 2) Optimized warm-up: builds the AppCDS archive (this run's time is discarded).
            await BenchRunAsync(inst, vanilla: false, "Optimized warm-up (building class cache)", 2, ct);
            Push("benchmarkProgress", new {
                phase = "warmup", step = 2, totalSteps = 3, bootVanilla = vanilla,
                message = "Class cache built — measuring optimized boot…",
            });

            // 3) Optimized measured: uses the AppCDS archive built in step 2.
            optimized = await BenchRunAsync(inst, vanilla: false, "Optimized (measured)", 3, ct);

            double delta = (vanilla > 0 && optimized > 0) ? vanilla - optimized : 0;
            double pct   = (vanilla > 0)                  ? delta / vanilla * 100.0 : 0;
            Push("benchmarkProgress", new {
                phase = "done", step = 3, totalSteps = 3, done = true,
                bootVanilla = vanilla, bootOptimized = optimized,
                deltaSeconds = Math.Round(delta, 1), deltaPercent = Math.Round(pct, 1),
                message = "Benchmark complete.",
            });
        }
        catch (OperationCanceledException)
        {
            Push("benchmarkProgress", new { phase = "cancelled", cancelled = true, message = "Benchmark cancelled." });
        }
        catch (Exception e)
        {
            Logger.Warn($"Benchmark failed: {e.Message}");
            Push("benchmarkProgress", new { phase = "error", error = true, message = $"Benchmark failed: {e.Message}" });
        }
        finally
        {
            try { if (inst.State != InstanceState.Stopped) _manager.Kill(inst); } catch { /* ignore */ }
            _benchRunning = false;
        }
    }

    private async Task<long> BenchRunAsync(RunningInstance inst, bool vanilla, string label, int step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Push("benchmarkProgress", new {
            phase = "launching", step, totalSteps = 3,
            mode = vanilla ? "vanilla" : "optimized",
            message = $"Launching: {label}…",
        });

        var ready = _manager.AwaitReadyAsync(inst.Entry.Id);
        await _manager.LaunchAsync(inst, vanilla);

        // Wait for the mod's READY signal (= reached main menu), with a hard timeout.
        var winner = await Task.WhenAny(ready, Task.Delay(TimeSpan.FromMinutes(7), ct));
        ct.ThrowIfCancellationRequested();
        long secs = (winner == ready && ready.IsCompletedSuccessfully) ? ready.Result : -1;

        await Task.Delay(2000, ct);                 // let the main menu settle
        try { _manager.Kill(inst); } catch { /* ignore */ }
        await Task.Delay(4000, ct);                 // let the process tree die before the next launch
        return secs;
    }

    // ── AI assistant (NVIDIA hosted / local NIM, OpenAI-compatible) ────────────
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private const string AiSystemPrompt =
@"You are Cryo Assistant, an expert built into the Cryo Minecraft modpack launcher (NeoForge/Forge/Fabric, Prism-based).
Help the user diagnose mod conflicts, crashes, launch failures, performance issues, and general modpack questions. Be concise and concrete; prefer short paragraphs and bullet points.

When a fix can be performed by the launcher, propose it on its own line beginning with @@ACTION followed by compact JSON, e.g.:
@@ACTION {""type"":""disableMod"",""label"":""Disable JEI (duplicate)"",""args"":{""file"":""jei-1.21.1.jar""}}

Allowed action types and args:
- disableMod {file}   — disable a mod (use the EXACT file name from the provided mod list)
- enableMod {file}    — re-enable a disabled mod
- rebuildCache {}     — rebuild the VSpeed data cache
- setRam {gb}         — set max RAM in GB (integer, e.g. 10) for the current instance
- openCrashReport {}  — open the latest crash report
- openModsFolder {}   — open the instance's mods folder

Rules: put a short human explanation BEFORE each @@ACTION line. Only propose an action you can clearly justify from the logs/crash/mods context. NEVER invent file names — only use files present in the context. If unsure, ask a clarifying question instead of guessing. You cannot edit the launcher's own source code; if you suspect a launcher bug, say so clearly and describe it.";

    /// <summary>
    /// Calls the configured OpenAI-compatible chat endpoint (NVIDIA hosted by default).
    /// args: { messages:[{role,content}], instanceId?, attach?:["logs","mods","crash"] }
    /// </summary>
    private async Task<object?> AiChatAsync(JsonNode args)
    {
        var key     = (_config.Data.AiApiKey  ?? "").Trim();
        var baseUrl = (_config.Data.AiBaseUrl ?? "").Trim().TrimEnd('/');
        var model   = string.IsNullOrWhiteSpace(_config.Data.AiModel) ? "microsoft/phi-4-mini-instruct" : _config.Data.AiModel.Trim();
        bool isLocal = baseUrl.Contains("localhost") || baseUrl.Contains("127.0.0.1") || baseUrl.Contains("0.0.0.0");

        if (string.IsNullOrWhiteSpace(baseUrl)) return new { ok = false, error = "No API base URL configured (Settings → Assistant)." };
        if (string.IsNullOrWhiteSpace(key) && !isLocal)
            return new { ok = false, error = "No NVIDIA API key set. Paste your key in Settings → Assistant." };

        var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = AiSystemPrompt } };
        var ctx = BuildAiContext(args);
        if (!string.IsNullOrEmpty(ctx)) msgs.Add(new JsonObject { ["role"] = "system", ["content"] = ctx });
        if (args["messages"] is JsonArray hist)
            foreach (var m in hist)
            {
                var role    = m?["role"]?.GetValue<string>() ?? "user";
                var content = m?["content"]?.GetValue<string>() ?? "";
                if (content.Length == 0) continue;
                msgs.Add(new JsonObject { ["role"] = role, ["content"] = content });
            }

        var body = new JsonObject
        {
            ["model"]       = model,
            ["messages"]    = msgs,
            ["max_tokens"]  = 1024,
            ["temperature"] = 0.2,   // NVIDIA-recommended for phi-4-mini; low = reliable diagnostics
            ["top_p"]       = 0.7,
            ["stream"]      = false,
        };

        // Build the chat-completions URL tolerantly: the configured base may be the
        // host ("…nvidia.com"), include "/v1", or already be the full endpoint.
        string chatUrl =
            baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? baseUrl :
            baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)               ? baseUrl + "/chat/completions" :
                                                                                        baseUrl + "/v1/chat/completions";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, chatUrl);
            if (!string.IsNullOrWhiteSpace(key)) req.Headers.Add("Authorization", "Bearer " + key);
            req.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                string detail = text;
                try
                {
                    var n = JsonNode.Parse(text);
                    detail = n?["detail"]?.ToString()
                          ?? n?["error"]?["message"]?.ToString()
                          ?? n?["message"]?.ToString() ?? text;
                }
                catch { /* keep raw */ }
                Logger.Warn($"AI chat HTTP {(int)resp.StatusCode}");
                return new { ok = false, error = $"AI error {(int)resp.StatusCode}: {Trunc(detail, 400)}" };
            }

            var node    = JsonNode.Parse(text);
            var reply   = node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            return new { ok = true, content = reply };
        }
        catch (TaskCanceledException)
        {
            return new { ok = false, error = "Request timed out. Check your internet (hosted) or the local NIM at " + baseUrl + "." };
        }
        catch (HttpRequestException e)
        {
            return new { ok = false, error = "Network error: " + e.Message };
        }
        catch (Exception e)
        {
            Logger.Warn($"AI chat failed: {e.Message}");
            return new { ok = false, error = e.Message };
        }
    }

    /// <summary>Kicks off a streaming chat completion; chunks arrive as push events
    /// (aiChunk / aiDone / aiError) keyed by streamId. Returns immediately.</summary>
    private object StartAiStream(JsonNode args)
    {
        var key      = (_config.Data.AiApiKey  ?? "").Trim();
        var baseUrl  = (_config.Data.AiBaseUrl ?? "").Trim().TrimEnd('/');
        var streamId = args["streamId"]?.GetValue<string>() ?? "";
        bool isLocal = baseUrl.Contains("localhost") || baseUrl.Contains("127.0.0.1") || baseUrl.Contains("0.0.0.0");

        if (string.IsNullOrWhiteSpace(baseUrl)) return new { ok = false, error = "No API base URL configured (Settings → Assistant)." };
        if (string.IsNullOrWhiteSpace(key) && !isLocal)
            return new { ok = false, error = "No NVIDIA API key set. Paste your key in Settings → Assistant." };

        _ = Task.Run(() => StreamAsync(args, key, baseUrl, streamId));
        return new { ok = true, streamId };
    }

    private async Task StreamAsync(JsonNode args, string key, string baseUrl, string streamId)
    {
        try
        {
            var model = string.IsNullOrWhiteSpace(_config.Data.AiModel) ? "microsoft/phi-4-mini-instruct" : _config.Data.AiModel.Trim();
            var msgs  = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = AiSystemPrompt } };
            var ctx   = BuildAiContext(args);
            if (!string.IsNullOrEmpty(ctx)) msgs.Add(new JsonObject { ["role"] = "system", ["content"] = ctx });
            if (args["messages"] is JsonArray hist)
                foreach (var m in hist)
                {
                    var role = m?["role"]?.GetValue<string>() ?? "user";
                    var content = m?["content"]?.GetValue<string>() ?? "";
                    if (content.Length == 0) continue;
                    msgs.Add(new JsonObject { ["role"] = role, ["content"] = content });
                }

            var body = new JsonObject
            {
                ["model"] = model, ["messages"] = msgs, ["max_tokens"] = 1024,
                ["temperature"] = 0.2, ["top_p"] = 0.7, ["stream"] = true,
            };
            string chatUrl =
                baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? baseUrl :
                baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)               ? baseUrl + "/chat/completions" :
                                                                                            baseUrl + "/v1/chat/completions";

            using var req = new HttpRequestMessage(HttpMethod.Post, chatUrl);
            if (!string.IsNullOrWhiteSpace(key)) req.Headers.Add("Authorization", "Bearer " + key);
            req.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                var txt = await resp.Content.ReadAsStringAsync();
                string detail = txt;
                try { var n = JsonNode.Parse(txt); detail = n?["detail"]?.ToString() ?? n?["error"]?["message"]?.ToString() ?? n?["message"]?.ToString() ?? txt; } catch { }
                Push("aiError", new { streamId, error = $"AI error {(int)resp.StatusCode}: {Trunc(detail, 400)}" });
                return;
            }

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.Length == 0 || !line.StartsWith("data:")) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                try
                {
                    var delta = JsonNode.Parse(data)?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(delta)) Push("aiChunk", new { streamId, delta });
                }
                catch { /* keepalive / non-JSON line */ }
            }
            Push("aiDone", new { streamId });
        }
        catch (Exception e)
        {
            Logger.Warn($"AI stream failed: {e.Message}");
            Push("aiError", new { streamId, error = e.Message });
        }
    }

    /// <summary>Builds a compact context block (mods / logs tail / crash / launcher log) for the model.</summary>
    // ── Profiles (reusable launch presets) ───────────────────────────────────────

    private object GetProfiles()
    {
        // Seed built-in presets on first access.
        if (_config.Data.Profiles.Count == 0)
        {
            _config.Data.Profiles.AddRange(ProfileData.Defaults());
            _config.Save();
        }
        return new
        {
            profiles = _config.Data.Profiles.Select(p => new
            {
                id = p.Id, name = p.Name, icon = p.Icon, ramMax = p.RamMax,
                jvmArgs = p.JvmArgs, vspeedEnabled = p.VspeedEnabled, builtIn = p.BuiltIn,
            }).ToArray(),
        };
    }

    private object SaveProfile(JsonNode args)
    {
        var id   = args.Str("profileId");
        var name = args.Str("name");
        if (string.IsNullOrWhiteSpace(name)) return new { ok = false, error = "Name required" };

        // New profile gets a generated id; existing one is updated in place.
        if (string.IsNullOrWhiteSpace(id))
            id = "p" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var existing = _config.Data.Profiles.FirstOrDefault(p => p.Id == id);
        if (existing == null)
        {
            existing = new ProfileData { Id = id };
            _config.Data.Profiles.Add(existing);
        }
        if (existing.BuiltIn) { /* allow editing values but keep BuiltIn flag */ }

        existing.Name          = name;
        existing.Icon          = string.IsNullOrWhiteSpace(args.Str("icon")) ? existing.Icon : args.Str("icon");
        existing.RamMax        = args.Int("ramMax", existing.RamMax);
        existing.JvmArgs       = args["jvmArgs"] is JsonNode ja ? (ja.GetValue<string>() ?? "") : existing.JvmArgs;
        existing.VspeedEnabled = args.Bool("vspeedEnabled", existing.VspeedEnabled);
        _config.Save();
        Logger.Info($"SaveProfile: {name} ({id})");
        return new { ok = true, id };
    }

    private object DeleteProfile(string profileId)
    {
        var p = _config.Data.Profiles.FirstOrDefault(x => x.Id == profileId);
        if (p == null) return new { ok = false, error = "Profile not found" };
        if (p.BuiltIn) return new { ok = false, error = "Built-in profiles can't be deleted" };
        _config.Data.Profiles.Remove(p);
        _config.Save();
        return new { ok = true };
    }

    /// <summary>Writes a profile's RAM + JVM args into the instance's instance.cfg,
    /// and sets the VSpeed cache flag.</summary>
    private object ApplyProfile(string instanceId, string profileId)
    {
        var p = _config.Data.Profiles.FirstOrDefault(x => x.Id == profileId);
        if (p == null) return new { ok = false, error = "Profile not found" };

        var argsNode = new JsonObject
        {
            ["jvmArgs"] = p.JvmArgs,
            ["ramMax"]  = p.RamMax,
        };
        var res = SaveInstanceCfg(instanceId, argsNode);
        Logger.Info($"ApplyProfile({instanceId}): {p.Name}");
        return new { ok = true, applied = p.Name, ramMax = p.RamMax, vspeedEnabled = p.VspeedEnabled };
    }

    // ── Modrinth mod browser ────────────────────────────────────────────────────

    private static readonly ModrinthClient _modrinth = new();

    /// <summary>Searches Modrinth. kind="mod" scopes to the instance's MC+loader;
    /// kind="modpack" searches modpacks (no loader filter).</summary>
    private async Task<object?> SearchModrinthAsync(string query, string instanceId, int offset, string kind)
    {
        var projectType = kind == "modpack" ? "modpack" : "mod";
        try
        {
            var meta = string.IsNullOrEmpty(instanceId) ? null : InstanceMetaReader.Read(instanceId, _prismDataDir);
            var mcFilter = projectType == "modpack" ? "" : (meta?.Mc ?? "");
            var node = await _modrinth.SearchAsync(query, mcFilter, meta?.Loader ?? "", offset, 20, projectType);
            var hits = node?["hits"]?.AsArray();
            var list = new List<object>();
            if (hits != null)
                foreach (var h in hits)
                {
                    list.Add(new
                    {
                        projectId   = h?["project_id"]?.GetValue<string>() ?? "",
                        slug        = h?["slug"]?.GetValue<string>() ?? "",
                        title       = h?["title"]?.GetValue<string>() ?? "",
                        description = h?["description"]?.GetValue<string>() ?? "",
                        downloads   = h?["downloads"]?.GetValue<long>() ?? 0,
                        follows     = h?["follows"]?.GetValue<long>() ?? 0,
                        iconUrl     = h?["icon_url"]?.GetValue<string>() ?? "",
                        author      = h?["author"]?.GetValue<string>() ?? "",
                    });
                }
            return new { ok = true, hits = list, total = node?["total_hits"]?.GetValue<long>() ?? 0 };
        }
        catch (Exception e)
        {
            Logger.Warn($"SearchModrinth('{query}'): {e.Message}");
            return new { ok = false, error = e.Message, hits = Array.Empty<object>() };
        }
    }

    /// <summary>Lists installable versions of a Modrinth project for this instance.</summary>
    private async Task<object?> GetModrinthVersionsAsync(string projectId, string instanceId)
    {
        try
        {
            var meta = string.IsNullOrEmpty(instanceId) ? null : InstanceMetaReader.Read(instanceId, _prismDataDir);
            var arr  = await _modrinth.GetVersionsAsync(projectId, meta?.Mc ?? "", meta?.Loader ?? "");
            var list = new List<object>();
            if (arr is JsonArray versions)
                foreach (var v in versions)
                {
                    var files   = v?["files"]?.AsArray();
                    // Prefer the file flagged "primary", else the first.
                    JsonNode? file = null;
                    if (files != null && files.Count > 0)
                        file = files.FirstOrDefault(f => f?["primary"]?.GetValue<bool>() == true) ?? files[0];
                    if (file == null) continue;
                    list.Add(new
                    {
                        versionId     = v?["id"]?.GetValue<string>() ?? "",
                        name          = v?["name"]?.GetValue<string>() ?? "",
                        versionNumber = v?["version_number"]?.GetValue<string>() ?? "",
                        datePublished = v?["date_published"]?.GetValue<string>() ?? "",
                        versionType   = v?["version_type"]?.GetValue<string>() ?? "release",
                        downloads     = v?["downloads"]?.GetValue<long>() ?? 0,
                        filename      = file?["filename"]?.GetValue<string>() ?? "",
                        url           = file?["url"]?.GetValue<string>() ?? "",
                        sha512        = file?["hashes"]?["sha512"]?.GetValue<string>() ?? "",
                    });
                }
            return new { ok = true, versions = list };
        }
        catch (Exception e)
        {
            Logger.Warn($"GetModrinthVersions('{projectId}'): {e.Message}");
            return new { ok = false, error = e.Message, versions = Array.Empty<object>() };
        }
    }

    // ── CurseForge (optional second source for the mod browser) ──────────────────

    private static readonly CurseForgeClient _curse = new();

    /// <summary>The CurseForge key to use: the user's own (Settings) if set,
    /// otherwise the app-wide key embedded at build time. Empty only when neither
    /// exists — then CurseForge is unavailable until one is provided.</summary>
    private string EffectiveCurseKey()
    {
        var user = (_config.Data.CurseForgeApiKey ?? "").Trim();
        return user.Length > 0 ? user : CurseForgeClient.DefaultApiKey;
    }

    private const string CurseSetupHint =
        "CurseForge isn't set up. Add a free API key from console.curseforge.com in Settings → Assistant (or embed one app-wide — see CLAUDE.md).";

    private async Task<object?> SearchCurseForgeAsync(string query, string instanceId, int offset, string kind)
    {
        var key = EffectiveCurseKey();
        if (string.IsNullOrEmpty(key)) return new { ok = false, error = CurseSetupHint, hits = Array.Empty<object>() };
        var classId = kind == "modpack" ? CurseForgeClient.ClassModpacks : 6;
        try
        {
            var meta = string.IsNullOrEmpty(instanceId) ? null : InstanceMetaReader.Read(instanceId, _prismDataDir);
            var mcFilter = kind == "modpack" ? "" : (meta?.Mc ?? "");
            var node = await _curse.SearchAsync(key, query, mcFilter, meta?.Loader ?? "", offset, 20, classId);
            var data = node?["data"]?.AsArray();
            var list = new List<object>();
            if (data != null)
                foreach (var h in data)
                {
                    var authors = h?["authors"]?.AsArray();
                    list.Add(new
                    {
                        projectId   = (h?["id"]?.GetValue<int>() ?? 0).ToString(),
                        slug        = h?["slug"]?.GetValue<string>() ?? "",
                        title       = h?["name"]?.GetValue<string>() ?? "",
                        description = h?["summary"]?.GetValue<string>() ?? "",
                        downloads   = (long)(h?["downloadCount"]?.GetValue<double>() ?? 0),
                        follows     = (long)(h?["thumbsUpCount"]?.GetValue<double>() ?? 0),
                        iconUrl     = h?["logo"]?["thumbnailUrl"]?.GetValue<string>() ?? h?["logo"]?["url"]?.GetValue<string>() ?? "",
                        author      = (authors != null && authors.Count > 0) ? authors[0]?["name"]?.GetValue<string>() ?? "" : "",
                    });
                }
            return new { ok = true, hits = list, total = node?["pagination"]?["totalCount"]?.GetValue<int>() ?? list.Count };
        }
        catch (Exception e)
        {
            Logger.Warn($"SearchCurseForge('{query}'): {e.Message}");
            return new { ok = false, error = e.Message, hits = Array.Empty<object>() };
        }
    }

    private async Task<object?> GetCurseForgeFilesAsync(string projectId, string instanceId)
    {
        var key = EffectiveCurseKey();
        if (string.IsNullOrEmpty(key)) return new { ok = false, error = CurseSetupHint, versions = Array.Empty<object>() };
        try
        {
            var meta = string.IsNullOrEmpty(instanceId) ? null : InstanceMetaReader.Read(instanceId, _prismDataDir);
            var node = await _curse.GetFilesAsync(key, projectId, meta?.Mc ?? "", meta?.Loader ?? "");
            var data = node?["data"]?.AsArray();
            var list = new List<object>();
            if (data != null)
                foreach (var f in data)
                {
                    var rel = f?["releaseType"]?.GetValue<int>() ?? 1;  // 1 release, 2 beta, 3 alpha
                    var url = f?["downloadUrl"]?.GetValue<string>();
                    list.Add(new
                    {
                        versionId     = (f?["id"]?.GetValue<int>() ?? 0).ToString(),
                        name          = f?["displayName"]?.GetValue<string>() ?? "",
                        versionNumber = f?["fileName"]?.GetValue<string>() ?? "",
                        datePublished = f?["fileDate"]?.GetValue<string>() ?? "",
                        versionType   = rel == 1 ? "release" : rel == 2 ? "beta" : "alpha",
                        downloads     = (long)(f?["downloadCount"]?.GetValue<double>() ?? 0),
                        filename      = f?["fileName"]?.GetValue<string>() ?? "",
                        url           = url ?? "",
                        sha512        = "",   // CF uses fingerprint/sha1; skip hash verification
                        disabled      = string.IsNullOrEmpty(url),   // author opted out of API distribution
                    });
                }
            return new { ok = true, versions = list };
        }
        catch (Exception e)
        {
            Logger.Warn($"GetCurseForgeFiles('{projectId}'): {e.Message}");
            return new { ok = false, error = e.Message, versions = Array.Empty<object>() };
        }
    }

    /// <summary>Downloads a mod file into the instance's mods folder (SHA-512 verified).
    /// Push events: modDownloadDone / modDownloadError.</summary>
    private object DownloadMod(string instanceId, string url, string filename, string sha512, string projectTitle)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) throw new Exception("No download URL");
                var modsDir = Path.Combine(_prismDataDir, "instances", instanceId, "minecraft", "mods");
                var dest    = await _modrinth.DownloadFileAsync(url, filename, sha512, modsDir);
                Logger.Info($"DownloadMod({instanceId}): {Path.GetFileName(dest)} ← {projectTitle}");
                Push("modDownloadDone", new { ok = true, filename = Path.GetFileName(dest), projectTitle });
            }
            catch (Exception e)
            {
                Logger.Warn($"DownloadMod({instanceId}): {e.Message}");
                Push("modDownloadError", new { error = e.Message, projectTitle });
            }
        });
        return new { ok = true };
    }

    /// <summary>Installs a Modrinth mod version AND its required dependencies
    /// (resolved to versions compatible with the instance's MC + loader, recursively,
    /// de-duplicated). Push events: modDownloadProgress / modDownloadDone / modDownloadError.</summary>
    private object DownloadModrinthMod(string instanceId, string projectId, string versionId, string projectTitle)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(versionId)) throw new Exception("No version selected.");
                var meta    = InstanceMetaReader.Read(instanceId, _prismDataDir);
                var modsDir = Path.Combine(_prismDataDir, "instances", instanceId, "minecraft", "mods");
                Directory.CreateDirectory(modsDir);

                var visited  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // project ids handled
                var depNames = new List<string>();
                string mainFile = "";

                // Downloads a version node's primary file (skips if already present).
                async Task<bool> Grab(JsonNode? ver, bool isDep)
                {
                    if (ver == null) return false;
                    var pid = ver["project_id"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(pid) && !visited.Add(pid)) return false;  // already handled
                    var files = ver["files"]?.AsArray();
                    var pf = files?.FirstOrDefault(f => f?["primary"]?.GetValue<bool>() == true)
                             ?? (files != null && files.Count > 0 ? files[0] : null);
                    if (pf == null) return false;
                    var url = pf["url"]?.GetValue<string>() ?? "";
                    var fn  = pf["filename"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(fn)) return false;
                    var leaf = Path.GetFileName(fn);
                    if (!File.Exists(Path.Combine(modsDir, leaf)))   // skip files already there
                    {
                        var sha = pf["hashes"]?["sha512"]?.GetValue<string>();
                        await _modrinth.DownloadFileAsync(url, fn, sha, modsDir);
                    }
                    if (isDep) depNames.Add(Path.GetFileNameWithoutExtension(leaf)); else mainFile = leaf;
                    return true;
                }

                // Walks required dependencies breadth-first (capped depth + total).
                async Task Resolve(JsonNode? ver, int depth)
                {
                    if (ver == null || depth > 5 || depNames.Count >= 60) return;
                    var deps = ver["dependencies"]?.AsArray();
                    if (deps == null) return;
                    foreach (var dep in deps)
                    {
                        if ((dep?["dependency_type"]?.GetValue<string>() ?? "") != "required") continue;
                        var dvid = dep?["version_id"]?.GetValue<string>();
                        var dpid = dep?["project_id"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(dpid) && visited.Contains(dpid)) continue;
                        JsonNode? dver = null;
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(dvid))
                                dver = await _modrinth.GetVersionAsync(dvid!);
                            else if (!string.IsNullOrWhiteSpace(dpid))
                                dver = (await _modrinth.GetVersionsAsync(dpid!, meta.Mc, meta.Loader) as JsonArray)?.FirstOrDefault();
                        }
                        catch (Exception de) { Logger.Warn($"dep {dpid}/{dvid}: {de.Message}"); continue; }
                        if (dver == null) continue;
                        Push("modDownloadProgress", new { message = $"Adding dependency: {dver["name"]?.GetValue<string>() ?? dpid}" });
                        if (await Grab(dver, true)) await Resolve(dver, depth + 1);
                    }
                }

                Push("modDownloadProgress", new { message = $"Installing {projectTitle}…" });
                var main = await _modrinth.GetVersionAsync(versionId) ?? throw new Exception("Could not fetch version.");
                await Grab(main, false);
                await Resolve(main, 0);

                Logger.Info($"DownloadModrinthMod({instanceId}): {projectTitle} + {depNames.Count} dep(s)");
                Push("modDownloadDone", new { ok = true, projectTitle, filename = mainFile, deps = depNames, depCount = depNames.Count });
            }
            catch (Exception e)
            {
                Logger.Warn($"DownloadModrinthMod({instanceId}): {e.Message}");
                Push("modDownloadError", new { error = e.Message, projectTitle });
            }
        });
        return new { ok = true };
    }

    /// <summary>Hashes every installed jar and asks Modrinth which have newer versions.
    /// Push events: modUpdatesProgress / modUpdatesDone / modUpdatesError.</summary>
    private object CheckModUpdates(string instanceId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var meta    = InstanceMetaReader.Read(instanceId, _prismDataDir);
                var modsDir = Path.Combine(_prismDataDir, "instances", instanceId, "minecraft", "mods");
                if (!Directory.Exists(modsDir)) { Push("modUpdatesDone", new { ok = true, updates = Array.Empty<object>(), scanned = 0 }); return; }

                var files     = Directory.GetFiles(modsDir, "*.jar");
                var hashToFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var hashes    = new List<string>();

                Push("modUpdatesProgress", new { message = $"Hashing {files.Length} mods…" });
                foreach (var f in files)
                {
                    var bytes = await File.ReadAllBytesAsync(f);
                    var h = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(bytes)).ToLowerInvariant();
                    hashToFile[h] = f;
                    hashes.Add(h);
                }

                Push("modUpdatesProgress", new { message = "Checking Modrinth for updates…" });
                var result = await _modrinth.CheckUpdatesAsync(hashes, meta.Mc, meta.Loader);

                var updates = new List<object>();
                if (result is JsonObject map)
                    foreach (var kv in map)
                    {
                        var localHash = kv.Key;
                        var version   = kv.Value;
                        if (!hashToFile.TryGetValue(localHash, out var localPath)) continue;

                        var vFiles = version?["files"]?.AsArray();
                        if (vFiles == null || vFiles.Count == 0) continue;

                        // Already on the latest version if our hash matches any file in it.
                        bool latest = vFiles.Any(x =>
                            string.Equals(x?["hashes"]?["sha512"]?.GetValue<string>(), localHash, StringComparison.OrdinalIgnoreCase));
                        if (latest) continue;

                        var newFile = vFiles.FirstOrDefault(x => x?["primary"]?.GetValue<bool>() == true) ?? vFiles[0];
                        updates.Add(new
                        {
                            currentFile = Path.GetFileName(localPath),
                            newFilename = newFile?["filename"]?.GetValue<string>() ?? "",
                            newVersion  = version?["version_number"]?.GetValue<string>() ?? "",
                            url         = newFile?["url"]?.GetValue<string>() ?? "",
                            sha512      = newFile?["hashes"]?["sha512"]?.GetValue<string>() ?? "",
                            projectId   = version?["project_id"]?.GetValue<string>() ?? "",
                        });
                    }

                Logger.Info($"CheckModUpdates({instanceId}): {updates.Count} update(s) for {files.Length} mods");
                Push("modUpdatesDone", new { ok = true, updates, scanned = files.Length });
            }
            catch (Exception e)
            {
                Logger.Warn($"CheckModUpdates({instanceId}): {e.Message}");
                Push("modUpdatesError", new { error = e.Message });
            }
        });
        return new { ok = true };
    }

    /// <summary>Downloads a newer mod jar and removes the old one.
    /// Push events: modUpdateDone / modUpdateError.</summary>
    private object UpdateMod(string instanceId, string oldFile, string url, string newFilename, string sha512)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var modsDir = Path.Combine(_prismDataDir, "instances", instanceId, "minecraft", "mods");
                var dest    = await _modrinth.DownloadFileAsync(url, newFilename, sha512, modsDir);

                // Remove the previous jar only if it's a different file (some mods reuse the name).
                var oldPath = Path.Combine(modsDir, Path.GetFileName(oldFile));
                if (File.Exists(oldPath) &&
                    !string.Equals(Path.GetFileName(oldPath), Path.GetFileName(dest), StringComparison.OrdinalIgnoreCase))
                    File.Delete(oldPath);

                Logger.Info($"UpdateMod({instanceId}): {oldFile} → {Path.GetFileName(dest)}");
                Push("modUpdateDone", new { ok = true, oldFile, newFile = Path.GetFileName(dest) });
            }
            catch (Exception e)
            {
                Logger.Warn($"UpdateMod({instanceId}/{oldFile}): {e.Message}");
                Push("modUpdateError", new { error = e.Message, oldFile });
            }
        });
        return new { ok = true };
    }

    // ── Modpack Export ────────────────────────────────────────────────────────

    private object ExportModpack(string instanceId)
    {
        _ = Task.Run(() =>
        {
            string? savePath = null;
            WpfApp.Current?.Dispatcher.Invoke(() =>
            {
                var meta = InstanceMetaReader.Read(instanceId, _prismDataDir);
                var dlg  = new Microsoft.Win32.SaveFileDialog
                {
                    Title    = "Export Modpack — " + meta.Name,
                    Filter   = "Modpack ZIP|*.zip",
                    FileName = meta.Name.Length > 0 ? meta.Name : instanceId,
                };
                if (dlg.ShowDialog() == true) savePath = dlg.FileName;
            });

            if (savePath == null) { Push("exportDone", new { ok = false, cancelled = true }); return; }

            try
            {
                var meta       = InstanceMetaReader.Read(instanceId, _prismDataDir);
                var mcDir      = Path.Combine(_prismDataDir, "instances", instanceId, "minecraft");
                Push("exportProgress", new { phase = "start", message = "Building modpack ZIP…" });

                using var zip = System.IO.Compression.ZipFile.Open(savePath, System.IO.Compression.ZipArchiveMode.Create);

                // mods/
                var modsDir = Path.Combine(mcDir, "mods");
                if (Directory.Exists(modsDir))
                    foreach (var f in Directory.GetFiles(modsDir, "*.jar"))
                        zip.CreateEntryFromFile(f, "mods/" + Path.GetFileName(f));

                // config/ (recursive)
                var cfgDir = Path.Combine(mcDir, "config");
                if (Directory.Exists(cfgDir))
                    foreach (var f in Directory.EnumerateFiles(cfgDir, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(mcDir, f).Replace('\\', '/');
                        zip.CreateEntryFromFile(f, rel);
                    }

                // options.txt + servers.dat (safe to share)
                foreach (var fname in new[] { "options.txt", "servers.dat" })
                {
                    var fp = Path.Combine(mcDir, fname);
                    if (File.Exists(fp)) zip.CreateEntryFromFile(fp, fname);
                }

                // cryo-modpack.json — manifest
                var manifest = new JsonObject
                {
                    ["name"]       = meta.Name,
                    ["mc"]         = meta.Mc,
                    ["loader"]     = meta.Loader,
                    ["loaderVer"]  = meta.LoaderVer,
                    ["exportedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["exportedBy"] = "Cryo Launcher",
                };
                var entry = zip.CreateEntry("cryo-modpack.json");
                using var sw = new StreamWriter(entry.Open());
                sw.Write(manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

                Logger.Info($"ExportModpack({instanceId}): {savePath}");
                Push("exportDone", new { ok = true, path = savePath });
            }
            catch (Exception e)
            {
                Logger.Warn($"ExportModpack({instanceId}): {e.Message}");
                Push("exportDone", new { ok = false, error = e.Message });
            }
        });
        return new { ok = true };
    }

    // ── Modpack Import ─────────────────────────────────────────────────────────

    // ── Shared instance-creation helpers (used by create + modpack installs) ─────

    /// <summary>Generates a unique, filesystem-safe instance id from a display name.</summary>
    private string MakeInstanceId(string name)
    {
        var slug = new string((name ?? "").ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (slug.Length > 40) slug = slug[..40];
        if (slug.Length < 2) slug = "cryo-instance";
        var id = slug;
        int n = 2;
        while (Directory.Exists(Path.Combine(_prismDataDir, "instances", id))
            || _config.Data.Instances.Any(e => e.Id == id))
            id = slug + "-" + n++;
        return id;
    }

    /// <summary>Writes instance.cfg + mmc-pack.json and creates the minecraft folder tree.</summary>
    private string CreateInstanceFolder(string id, string name, string mc, string loader, string loaderVer, int ramMax)
    {
        var instanceDir = Path.Combine(_prismDataDir, "instances", id);
        var mcDir       = Path.Combine(instanceDir, "minecraft");
        Directory.CreateDirectory(Path.Combine(mcDir, "mods"));
        Directory.CreateDirectory(Path.Combine(mcDir, "config"));
        Directory.CreateDirectory(Path.Combine(mcDir, "saves"));

        File.WriteAllText(Path.Combine(instanceDir, "instance.cfg"),
            $"[General]\nConfigVersion=1.2\nname={name}\nMaxMemAlloc={ramMax}\nMinMemAlloc=2048\n");

        var components = new JsonArray { new JsonObject { ["uid"] = "net.minecraft", ["version"] = mc } };
        switch ((loader ?? "").ToLowerInvariant())
        {
            case "neoforge": components.Add(new JsonObject { ["uid"] = "net.neoforged",            ["version"] = loaderVer }); break;
            case "forge":    components.Add(new JsonObject { ["uid"] = "net.minecraftforge",        ["version"] = loaderVer }); break;
            case "fabric":   components.Add(new JsonObject { ["uid"] = "net.fabricmc.fabric-loader",["version"] = loaderVer }); break;
            case "quilt":    components.Add(new JsonObject { ["uid"] = "org.quiltmc.quilt-loader",  ["version"] = loaderVer }); break;
        }
        File.WriteAllText(Path.Combine(instanceDir, "mmc-pack.json"),
            new JsonObject { ["components"] = components, ["formatVersion"] = 1 }.ToJsonString());
        return instanceDir;
    }

    // ── Modpack source tracking + update ─────────────────────────────────────────

    private string PackJsonPath(string id) => Path.Combine(_prismDataDir, "instances", id, "cryo-pack.json");

    /// <summary>Remembers which Modrinth/CurseForge pack (and version) an instance came
    /// from, so it can be updated later.</summary>
    private void StorePackSource(string id, string source, string projectId, string versionId, string name)
    {
        try
        {
            File.WriteAllText(PackJsonPath(id), new JsonObject
            {
                ["source"] = source, ["projectId"] = projectId, ["versionId"] = versionId, ["name"] = name,
            }.ToJsonString());
        }
        catch (Exception e) { Logger.Warn($"StorePackSource({id}): {e.Message}"); }
    }

    private JsonNode? ReadPackSource(string id)
    {
        try { var p = PackJsonPath(id); return File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p)) : null; }
        catch { return null; }
    }

    private void WriteMmcPack(string instanceDir, string mc, string loader, string loaderVer)
    {
        var components = new JsonArray { new JsonObject { ["uid"] = "net.minecraft", ["version"] = mc } };
        switch ((loader ?? "").ToLowerInvariant())
        {
            case "neoforge": components.Add(new JsonObject { ["uid"] = "net.neoforged",             ["version"] = loaderVer }); break;
            case "forge":    components.Add(new JsonObject { ["uid"] = "net.minecraftforge",         ["version"] = loaderVer }); break;
            case "fabric":   components.Add(new JsonObject { ["uid"] = "net.fabricmc.fabric-loader", ["version"] = loaderVer }); break;
            case "quilt":    components.Add(new JsonObject { ["uid"] = "org.quiltmc.quilt-loader",   ["version"] = loaderVer }); break;
        }
        File.WriteAllText(Path.Combine(instanceDir, "mmc-pack.json"),
            new JsonObject { ["components"] = components, ["formatVersion"] = 1 }.ToJsonString());
    }

    /// <summary>Reports the instance's modpack source and whether a newer version exists.</summary>
    private async Task<object?> GetModpackInfoAsync(string id)
    {
        var src = ReadPackSource(id);
        if (src == null) return new { hasSource = false };
        var source    = src["source"]?.GetValue<string>() ?? "";
        var projectId = src["projectId"]?.GetValue<string>() ?? "";
        var curVer    = src["versionId"]?.GetValue<string>() ?? "";
        var name      = src["name"]?.GetValue<string>() ?? "";
        var meta      = InstanceMetaReader.Read(id, _prismDataDir);
        try
        {
            string latestId = "", latestName = "";
            if (source == "modrinth")
            {
                var v = (await _modrinth.GetVersionsAsync(projectId, meta.Mc, meta.Loader) as JsonArray)?.FirstOrDefault();
                latestId   = v?["id"]?.GetValue<string>() ?? "";
                latestName = v?["version_number"]?.GetValue<string>() ?? v?["name"]?.GetValue<string>() ?? "";
            }
            else if (source == "curseforge")
            {
                var key = EffectiveCurseKey();
                if (!string.IsNullOrEmpty(key))
                {
                    var d = (await _curse.GetFilesAsync(key, projectId, meta.Mc, meta.Loader))?["data"]?.AsArray();
                    var v = d?.OrderByDescending(x => x?["fileDate"]?.GetValue<string>() ?? "").FirstOrDefault();
                    latestId   = (v?["id"]?.GetValue<long>() ?? 0).ToString();
                    latestName = v?["displayName"]?.GetValue<string>() ?? "";
                }
            }
            return new
            {
                hasSource = true, source, name, projectId,
                currentVersionId = curVer, latestVersionId = latestId, latestName,
                updateAvailable = !string.IsNullOrEmpty(latestId) && latestId != curVer && !string.IsNullOrEmpty(curVer),
            };
        }
        catch (Exception e) { Logger.Warn($"GetModpackInfo({id}): {e.Message}"); return new { hasSource = true, source, name, error = e.Message }; }
    }

    /// <summary>Updates an installed modpack to its latest version in place. Old mods are
    /// MOVED to a timestamped backup (never deleted) and worlds (saves/) are left untouched.
    /// Push events: modpackProgress / modpackDone.</summary>
    private object UpdateModpack(string id)
    {
        _ = Task.Run(async () =>
        {
            string? temp = null;
            try
            {
                var src = ReadPackSource(id) ?? throw new Exception("This instance has no known modpack source to update.");
                var source    = src["source"]?.GetValue<string>() ?? "";
                var projectId  = src["projectId"]?.GetValue<string>() ?? "";
                var name      = src["name"]?.GetValue<string>() ?? "Modpack";
                var meta      = InstanceMetaReader.Read(id, _prismDataDir);
                var instanceDir = Path.Combine(_prismDataDir, "instances", id);
                var mcDir   = Path.Combine(instanceDir, "minecraft");
                var modsDir = Path.Combine(mcDir, "mods");

                Push("modpackProgress", new { phase = "start", message = "Finding the latest version…" });

                string mc = meta.Mc, loader = meta.Loader, loaderVer = "", newVer = "";
                int total = 0, failed = 0, done = 0;

                // Back up the current mods (MOVE, fully reversible) — worlds stay put.
                if (Directory.Exists(modsDir) && Directory.EnumerateFileSystemEntries(modsDir).Any())
                {
                    var bak = Path.Combine(mcDir, "mods.bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    Directory.Move(modsDir, bak);
                    Logger.Info($"UpdateModpack({id}): backed up mods → {Path.GetFileName(bak)}");
                }
                Directory.CreateDirectory(modsDir);

                if (source == "modrinth")
                {
                    var v = (await _modrinth.GetVersionsAsync(projectId, meta.Mc, meta.Loader) as JsonArray)?.FirstOrDefault()
                            ?? throw new Exception("No matching versions found on Modrinth.");
                    newVer = v["id"]?.GetValue<string>() ?? "";
                    var files = v["files"]?.AsArray();
                    var primary = files?.FirstOrDefault(f => f?["primary"]?.GetValue<bool>() == true) ?? (files != null && files.Count > 0 ? files[0] : null);
                    var mrUrl = primary?["url"]?.GetValue<string>() ?? throw new Exception("No .mrpack file in the latest version.");
                    temp = Path.Combine(Path.GetTempPath(), "cryo-upd-" + Guid.NewGuid().ToString("N") + ".mrpack");
                    Push("modpackProgress", new { phase = "download", message = "Downloading update…" });
                    await DownloadToFileAsync(mrUrl, temp);

                    JsonArray? indexFiles = null;
                    using (var z = System.IO.Compression.ZipFile.OpenRead(temp))
                    {
                        var idx = z.GetEntry("modrinth.index.json") ?? throw new Exception("Invalid .mrpack.");
                        using var sr = new StreamReader(idx.Open());
                        var node = JsonNode.Parse(sr.ReadToEnd());
                        var deps = node?["dependencies"]?.AsObject();
                        if (deps != null)
                        {
                            mc = deps["minecraft"]?.GetValue<string>() ?? mc;
                            if (deps["neoforge"] != null)           { loader = "NeoForge"; loaderVer = deps["neoforge"]!.GetValue<string>(); }
                            else if (deps["forge"] != null)         { loader = "Forge";    loaderVer = deps["forge"]!.GetValue<string>(); }
                            else if (deps["fabric-loader"] != null) { loader = "Fabric";   loaderVer = deps["fabric-loader"]!.GetValue<string>(); }
                            else if (deps["quilt-loader"] != null)  { loader = "Quilt";    loaderVer = deps["quilt-loader"]!.GetValue<string>(); }
                        }
                        indexFiles = node?["files"]?.AsArray();
                    }
                    total = indexFiles?.Count ?? 0;
                    if (indexFiles != null)
                        foreach (var f in indexFiles)
                        {
                            var path = f?["path"]?.GetValue<string>() ?? "";
                            var dls  = f?["downloads"]?.AsArray();
                            if (string.IsNullOrEmpty(path) || dls == null || dls.Count == 0) { done++; continue; }
                            var dest = Path.GetFullPath(Path.Combine(mcDir, path));
                            if (!dest.StartsWith(mcDir, StringComparison.OrdinalIgnoreCase)) { done++; continue; }
                            try { await DownloadToFileAsync(dls[0]!.GetValue<string>(), dest); }
                            catch (Exception fe) { failed++; Logger.Warn($"upd file '{path}': {fe.Message}"); }
                            done++;
                            Push("modpackProgress", new { phase = "files", message = $"Downloading mods… {done}/{total}", done, total });
                        }
                    using (var z = System.IO.Compression.ZipFile.OpenRead(temp)) ExtractOverrides(z, mcDir);
                }
                else if (source == "curseforge")
                {
                    var key = EffectiveCurseKey();
                    if (string.IsNullOrEmpty(key)) throw new Exception(CurseSetupHint);
                    var d = (await _curse.GetFilesAsync(key, projectId, meta.Mc, meta.Loader))?["data"]?.AsArray();
                    var latest = d?.OrderByDescending(x => x?["fileDate"]?.GetValue<string>() ?? "").FirstOrDefault()
                                 ?? throw new Exception("No files found on CurseForge.");
                    var packFileId = latest["id"]?.GetValue<long>() ?? 0;
                    newVer = packFileId.ToString();
                    var packMap = await _curse.GetFilesByIdsAsync(key, new[] { packFileId });
                    if (!packMap.TryGetValue(packFileId, out var pf) || string.IsNullOrEmpty(pf.url))
                        throw new Exception("Modpack download not available from CurseForge.");
                    temp = Path.Combine(Path.GetTempPath(), "cryo-upd-" + Guid.NewGuid().ToString("N") + ".zip");
                    Push("modpackProgress", new { phase = "download", message = "Downloading update…" });
                    await DownloadToFileAsync(pf.url!, temp);

                    var projectFileIds = new List<(long projectId, long fileId)>();
                    using (var z = System.IO.Compression.ZipFile.OpenRead(temp))
                    {
                        var man = z.GetEntry("manifest.json") ?? throw new Exception("Invalid CurseForge pack (no manifest.json).");
                        using var sr = new StreamReader(man.Open());
                        var node = JsonNode.Parse(sr.ReadToEnd());
                        mc = node?["minecraft"]?["version"]?.GetValue<string>() ?? mc;
                        var ml = node?["minecraft"]?["modLoaders"]?.AsArray();
                        var prim = ml?.FirstOrDefault(x => x?["primary"]?.GetValue<bool>() == true) ?? (ml != null && ml.Count > 0 ? ml[0] : null);
                        var lid = prim?["id"]?.GetValue<string>() ?? ""; var dash = lid.IndexOf('-');
                        if (dash > 0) { var pfx = lid[..dash].ToLowerInvariant(); loaderVer = lid[(dash + 1)..]; loader = pfx switch { "neoforge" => "NeoForge", "forge" => "Forge", "fabric" => "Fabric", "quilt" => "Quilt", _ => loader }; }
                        var fl = node?["files"]?.AsArray();
                        if (fl != null) foreach (var f in fl) { var pid = f?["projectID"]?.GetValue<long>() ?? 0; var fid = f?["fileID"]?.GetValue<long>() ?? 0; if (pid > 0 && fid > 0) projectFileIds.Add((pid, fid)); }
                    }
                    total = projectFileIds.Count;
                    var urlMap = await _curse.GetFilesByIdsAsync(key, projectFileIds.Select(p => p.fileId));
                    foreach (var (_, fid) in projectFileIds)
                    {
                        urlMap.TryGetValue(fid, out var info);
                        var fname = info.fileName; var url = info.url;
                        if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(fname)) url = CurseForgeClient.FallbackUrl(fid, fname);
                        if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(fname))
                        {
                            try { await DownloadToFileAsync(url, Path.Combine(modsDir, Path.GetFileName(fname))); }
                            catch (Exception fe) { failed++; Logger.Warn($"upd cf {fid}: {fe.Message}"); }
                        }
                        else failed++;
                        done++;
                        Push("modpackProgress", new { phase = "files", message = $"Downloading mods… {done}/{total}", done, total });
                    }
                    using (var z = System.IO.Compression.ZipFile.OpenRead(temp)) ExtractOverrides(z, mcDir);
                }
                else throw new Exception("Unknown modpack source.");

                // Persist the new MC/loader + remembered version, then reinstall the loader.
                WriteMmcPack(instanceDir, mc, loader, loaderVer);
                StorePackSource(id, source, projectId, newVer, name);
                Push("modpackProgress", new { phase = "loader", message = $"Installing {loader} {loaderVer}…" });
                if (loader.Equals("Vanilla", StringComparison.OrdinalIgnoreCase)) StoreEngineVersion(id, mc);
                else InstallLoaderForInstance(id, mc, loader, loaderVer);

                Logger.Info($"UpdateModpack({id}): → {newVer} ({total} files, {failed} failed)");
                Push("modpackDone", new { ok = true, id, name, updated = true, files = total, failed });
            }
            catch (Exception e)
            {
                Logger.Warn($"UpdateModpack({id}): {e.Message}");
                Push("modpackDone", new { ok = false, error = e.Message });
            }
            finally { try { if (temp != null) File.Delete(temp); } catch { } }
        });
        return new { ok = true };
    }

    /// <summary>Registers a Cryo-native instance in config + the live manager.</summary>
    private void RegisterInstance(string id, string name)
    {
        if (_config.Data.Instances.All(e => e.Id != id))
        {
            var entry = new InstanceEntry { Id = id, DisplayName = name, Source = "cryo" };
            _config.Data.Instances.Add(entry);
            _config.Save();
            _manager.AddEntry(entry);
        }
    }

    /// <summary>
    /// Creates a brand-new instance without PrismLauncher.
    /// args: { name, mcVersion, loader, loaderVersion, ramMax }.
    /// For NeoForge/Vanilla the engine version is prepared so it launches standalone.
    /// </summary>
    private object CreateInstance(JsonNode args)
    {
        var name      = args.Str("name").Trim();
        var mc        = args.Str("mcVersion").Trim();
        var loader    = args.Str("loader").Trim();
        var loaderVer = args.Str("loaderVersion").Trim();
        var ramMax    = args.Int("ramMax", 6144);
        if (string.IsNullOrWhiteSpace(name)) return new { ok = false, error = "Name is required." };
        if (string.IsNullOrWhiteSpace(mc))   mc = "1.21.1";
        if (string.IsNullOrWhiteSpace(loader)) loader = "NeoForge";

        try
        {
            var id = MakeInstanceId(name);
            CreateInstanceFolder(id, name, mc, loader, loaderVer, ramMax);
            RegisterInstance(id, name);

            // Install the loader via the engine so the instance launches without Prism
            // (NeoForge/Forge/Fabric/Quilt download; Vanilla just records the version).
            if (loader.Equals("Vanilla", StringComparison.OrdinalIgnoreCase))
                StoreEngineVersion(id, mc);
            else
                InstallLoaderForInstance(id, mc, loader, loaderVer);

            Logger.Info($"CreateInstance: '{id}' ({name}) {loader} {mc}");
            Push("instanceCreated", new { ok = true, id, name, loader });
            return new { ok = true, id, loader };
        }
        catch (Exception e)
        {
            Logger.Warn($"CreateInstance: {e.Message}");
            return new { ok = false, error = e.Message };
        }
    }

    // ── Modpack install: download files from Modrinth / CurseForge ───────────────

    private static readonly HttpClient _dl = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static async Task DownloadToFileAsync(string url, string dest)
    {
        using var resp = await _dl.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await using var fs = File.Create(dest);
        await resp.Content.CopyToAsync(fs);
    }

    /// <summary>Copies a .mrpack/.zip's overrides + client-overrides into minecraft/.</summary>
    private static void ExtractOverrides(System.IO.Compression.ZipArchive z, string mcDir)
    {
        foreach (var entry in z.Entries)
        {
            var fn = entry.FullName;
            string? rel =
                fn.StartsWith("overrides/", StringComparison.Ordinal)        ? fn["overrides/".Length..] :
                fn.StartsWith("client-overrides/", StringComparison.Ordinal) ? fn["client-overrides/".Length..] : null;
            if (string.IsNullOrEmpty(rel) || fn.EndsWith('/')) continue;
            var dest = Path.GetFullPath(Path.Combine(mcDir, rel));
            if (!dest.StartsWith(mcDir, StringComparison.OrdinalIgnoreCase)) continue;  // traversal guard
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    private void FinishModpack(string id, string name, string mc, string loader, string loaderVer, int fileCount, int failed)
    {
        RegisterInstance(id, name);
        // Install whatever loader the pack declares so it launches without Prism.
        if (loader.Equals("Vanilla", StringComparison.OrdinalIgnoreCase))
            StoreEngineVersion(id, mc);
        else
        {
            Push("modpackProgress", new { phase = "loader", message = $"Installing {loader}…" });
            InstallLoaderForInstance(id, mc, loader, loaderVer);
        }
        Logger.Info($"Modpack installed: '{id}' ({name}) {loader} {mc}, {fileCount} files, {failed} failed");
        Push("modpackDone", new { ok = true, id, name, loader, files = fileCount, failed });
    }

    /// <summary>Downloads + installs a Modrinth modpack (.mrpack) as a new instance.
    /// Push events: modpackProgress / modpackDone.</summary>
    private object InstallModrinthModpack(string projectId, string versionId, string name)
    {
        _ = Task.Run(async () =>
        {
            string? temp = null;
            try
            {
                Push("modpackProgress", new { phase = "start", message = "Fetching modpack info…" });
                var ver = await _modrinth.GetVersionAsync(versionId);
                var files = ver?["files"]?.AsArray();
                var primary = files?.FirstOrDefault(f => f?["primary"]?.GetValue<bool>() == true) ?? (files != null && files.Count > 0 ? files[0] : null);
                var mrUrl = primary?["url"]?.GetValue<string>() ?? throw new Exception("No .mrpack file in this version.");

                temp = Path.Combine(Path.GetTempPath(), "cryo-" + Guid.NewGuid().ToString("N") + ".mrpack");
                Push("modpackProgress", new { phase = "download", message = "Downloading modpack…" });
                await DownloadToFileAsync(mrUrl, temp);

                // Parse modrinth.index.json
                string mc = "1.21.1", loader = "NeoForge", loaderVer = "";
                JsonArray? indexFiles = null;
                using (var z = System.IO.Compression.ZipFile.OpenRead(temp))
                {
                    var idx = z.GetEntry("modrinth.index.json") ?? throw new Exception("Invalid .mrpack (no modrinth.index.json).");
                    using var sr = new StreamReader(idx.Open());
                    var node = JsonNode.Parse(sr.ReadToEnd());
                    if (string.IsNullOrWhiteSpace(name)) name = node?["name"]?.GetValue<string>() ?? "Modpack";
                    var deps = node?["dependencies"]?.AsObject();
                    if (deps != null)
                    {
                        mc = deps["minecraft"]?.GetValue<string>() ?? mc;
                        if (deps["neoforge"] != null)          { loader = "NeoForge"; loaderVer = deps["neoforge"]!.GetValue<string>(); }
                        else if (deps["forge"] != null)        { loader = "Forge";    loaderVer = deps["forge"]!.GetValue<string>(); }
                        else if (deps["fabric-loader"] != null){ loader = "Fabric";   loaderVer = deps["fabric-loader"]!.GetValue<string>(); }
                        else if (deps["quilt-loader"] != null) { loader = "Quilt";    loaderVer = deps["quilt-loader"]!.GetValue<string>(); }
                    }
                    indexFiles = node?["files"]?.AsArray();
                }

                var id = MakeInstanceId(name);
                var instanceDir = CreateInstanceFolder(id, name, mc, loader, loaderVer, 6144);
                var mcDir = Path.Combine(instanceDir, "minecraft");
                StorePackSource(id, "modrinth", projectId, versionId, name);

                int total = indexFiles?.Count ?? 0, done = 0, failed = 0;
                if (indexFiles != null)
                    foreach (var f in indexFiles)
                    {
                        var path = f?["path"]?.GetValue<string>() ?? "";
                        var dls  = f?["downloads"]?.AsArray();
                        if (string.IsNullOrEmpty(path) || dls == null || dls.Count == 0) { done++; continue; }
                        var dest = Path.GetFullPath(Path.Combine(mcDir, path));
                        if (!dest.StartsWith(mcDir, StringComparison.OrdinalIgnoreCase)) { done++; continue; }
                        try { await DownloadToFileAsync(dls[0]!.GetValue<string>(), dest); }
                        catch (Exception fe) { failed++; Logger.Warn($"mrpack file '{path}': {fe.Message}"); }
                        done++;
                        Push("modpackProgress", new { phase = "files", message = $"Downloading mods… {done}/{total}", done, total });
                    }

                Push("modpackProgress", new { phase = "overrides", message = "Applying overrides…" });
                using (var z = System.IO.Compression.ZipFile.OpenRead(temp)) ExtractOverrides(z, mcDir);

                FinishModpack(id, name, mc, loader, loaderVer, total, failed);
            }
            catch (Exception e)
            {
                Logger.Warn($"InstallModrinthModpack: {e.Message}");
                Push("modpackDone", new { ok = false, error = e.Message });
            }
            finally { try { if (temp != null) File.Delete(temp); } catch { } }
        });
        return new { ok = true };
    }

    /// <summary>Downloads + installs a CurseForge modpack as a new instance.
    /// Push events: modpackProgress / modpackDone.</summary>
    private object InstallCurseForgeModpack(string projectId, string fileId, string name)
    {
        _ = Task.Run(async () =>
        {
            string? temp = null;
            try
            {
                var key = EffectiveCurseKey();
                if (string.IsNullOrEmpty(key)) throw new Exception(CurseSetupHint);
                if (!long.TryParse(fileId, out var packFileId)) throw new Exception("Invalid file id.");

                Push("modpackProgress", new { phase = "start", message = "Fetching modpack info…" });
                var packMap = await _curse.GetFilesByIdsAsync(key, new[] { packFileId });
                if (!packMap.TryGetValue(packFileId, out var packFile) || string.IsNullOrEmpty(packFile.url))
                    throw new Exception("Modpack download not available from CurseForge.");

                temp = Path.Combine(Path.GetTempPath(), "cryo-" + Guid.NewGuid().ToString("N") + ".zip");
                Push("modpackProgress", new { phase = "download", message = "Downloading modpack…" });
                await DownloadToFileAsync(packFile.url!, temp);

                // Parse manifest.json
                string mc = "1.21.1", loader = "NeoForge", loaderVer = "";
                var projectFileIds = new List<(long projectId, long fileId)>();
                using (var z = System.IO.Compression.ZipFile.OpenRead(temp))
                {
                    var man = z.GetEntry("manifest.json") ?? throw new Exception("Invalid CurseForge pack (no manifest.json).");
                    using var sr = new StreamReader(man.Open());
                    var node = JsonNode.Parse(sr.ReadToEnd());
                    if (string.IsNullOrWhiteSpace(name)) name = node?["name"]?.GetValue<string>() ?? "Modpack";
                    mc = node?["minecraft"]?["version"]?.GetValue<string>() ?? mc;
                    var ml = node?["minecraft"]?["modLoaders"]?.AsArray();
                    var primary = ml?.FirstOrDefault(x => x?["primary"]?.GetValue<bool>() == true) ?? (ml != null && ml.Count > 0 ? ml[0] : null);
                    var lid = primary?["id"]?.GetValue<string>() ?? "";   // e.g. "neoforge-21.1.172"
                    var dash = lid.IndexOf('-');
                    if (dash > 0)
                    {
                        var pfx = lid[..dash].ToLowerInvariant();
                        loaderVer = lid[(dash + 1)..];
                        loader = pfx switch { "neoforge" => "NeoForge", "forge" => "Forge", "fabric" => "Fabric", "quilt" => "Quilt", _ => "NeoForge" };
                    }
                    var fl = node?["files"]?.AsArray();
                    if (fl != null)
                        foreach (var f in fl)
                        {
                            var pid = f?["projectID"]?.GetValue<long>() ?? 0;
                            var fid = f?["fileID"]?.GetValue<long>() ?? 0;
                            if (pid > 0 && fid > 0) projectFileIds.Add((pid, fid));
                        }
                }

                var id = MakeInstanceId(name);
                var instanceDir = CreateInstanceFolder(id, name, mc, loader, loaderVer, 6144);
                var mcDir = Path.Combine(instanceDir, "minecraft");
                var modsDir = Path.Combine(mcDir, "mods");
                StorePackSource(id, "curseforge", projectId, fileId, name);

                // Resolve all file download URLs in one bulk call.
                Push("modpackProgress", new { phase = "resolve", message = $"Resolving {projectFileIds.Count} mods…" });
                var urlMap = await _curse.GetFilesByIdsAsync(key, projectFileIds.Select(p => p.fileId));

                int total = projectFileIds.Count, done = 0, failed = 0;
                foreach (var (_, fid) in projectFileIds)
                {
                    urlMap.TryGetValue(fid, out var info);
                    var fname = info.fileName;
                    var url   = info.url;
                    if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(fname))
                        url = CurseForgeClient.FallbackUrl(fid, fname);
                    if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(fname))
                    {
                        try { await DownloadToFileAsync(url, Path.Combine(modsDir, Path.GetFileName(fname))); }
                        catch (Exception fe) { failed++; Logger.Warn($"CF file {fid}: {fe.Message}"); }
                    }
                    else failed++;
                    done++;
                    Push("modpackProgress", new { phase = "files", message = $"Downloading mods… {done}/{total}", done, total });
                }

                Push("modpackProgress", new { phase = "overrides", message = "Applying overrides…" });
                using (var z = System.IO.Compression.ZipFile.OpenRead(temp)) ExtractOverrides(z, mcDir);

                FinishModpack(id, name, mc, loader, loaderVer, total, failed);
            }
            catch (Exception e)
            {
                Logger.Warn($"InstallCurseForgeModpack: {e.Message}");
                Push("modpackDone", new { ok = false, error = e.Message });
            }
            finally { try { if (temp != null) File.Delete(temp); } catch { } }
        });
        return new { ok = true };
    }

    // Folders that are regenerable / noisy — skipped when duplicating an instance.
    private static readonly HashSet<string> _dupSkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { ".vspeed-cache", "logs", "crash-reports" };

    private static void CopyDirectory(string src, string dst, HashSet<string>? skipTopDirs = null)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (skipTopDirs != null && skipTopDirs.Contains(name)) continue;
            CopyDirectory(dir, Path.Combine(dst, name));   // nested dirs copied fully
        }
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
    }

    /// <summary>Duplicates an instance natively (no Prism): copies the folder, renames,
    /// and registers the copy. Push events: duplicateProgress / duplicateDone.</summary>
    private object DuplicateInstance(string id)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var src = Path.Combine(_prismDataDir, "instances", id);
                if (!Directory.Exists(src)) { Push("duplicateDone", new { ok = false, error = "Instance not found." }); return; }

                var meta    = InstanceMetaReader.Read(id, _prismDataDir);
                var newName = (string.IsNullOrEmpty(meta.Name) ? id : meta.Name) + " (copy)";
                var newId   = MakeInstanceId(newName);
                var dst     = Path.Combine(_prismDataDir, "instances", newId);

                Push("duplicateProgress", new { message = "Copying files…" });
                // Skip regenerable noise at the instance root AND under minecraft/.
                CopyDirectory(src, dst, _dupSkipDirs);
                var dstMc = Path.Combine(dst, "minecraft");
                foreach (var skip in _dupSkipDirs)
                {
                    var p = Path.Combine(dstMc, skip);
                    try { if (Directory.Exists(p)) Directory.Delete(p, recursive: true); } catch { }
                }

                // Rename inside instance.cfg
                var cfg = Path.Combine(dst, "instance.cfg");
                if (File.Exists(cfg))
                    File.WriteAllLines(cfg, File.ReadAllLines(cfg)
                        .Select(l => l.StartsWith("name=", StringComparison.Ordinal) ? "name=" + newName : l));

                RegisterInstance(newId, newName);
                Logger.Info($"DuplicateInstance: '{id}' → '{newId}'");
                Push("duplicateDone", new { ok = true, id = newId, name = newName });
            }
            catch (Exception e)
            {
                Logger.Warn($"DuplicateInstance({id}): {e.Message}");
                Push("duplicateDone", new { ok = false, error = e.Message });
            }
        });
        return new { ok = true };
    }

    private object ImportModpack()
    {
        _ = Task.Run(() =>
        {
            string? zipPath = null;
            WpfApp.Current?.Dispatcher.Invoke(() =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title  = "Import Modpack",
                    Filter = "Modpack ZIP|*.zip;*.mrpack|All files|*.*",
                };
                if (dlg.ShowDialog() == true) zipPath = dlg.FileName;
            });

            if (zipPath == null) { Push("importDone", new { ok = false, cancelled = true }); return; }

            try
            {
                Push("importProgress", new { phase = "start", message = "Reading modpack…" });

                // Read manifest from ZIP
                string name = Path.GetFileNameWithoutExtension(zipPath);
                string mc = "1.21.1", loader = "NeoForge", loaderVer = "";
                using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
                {
                    var manifestEntry = archive.GetEntry("cryo-modpack.json")
                                     ?? archive.GetEntry("modrinth.index.json");  // mrpack support
                    if (manifestEntry != null)
                    {
                        using var sr = new StreamReader(manifestEntry.Open());
                        var node = JsonNode.Parse(sr.ReadToEnd());
                        name      = node?["name"]?.GetValue<string>()      ?? name;
                        mc        = node?["mc"]?.GetValue<string>()         ?? mc;
                        loader    = node?["loader"]?.GetValue<string>()     ?? loader;
                        loaderVer = node?["loaderVer"]?.GetValue<string>()  ?? loaderVer;
                    }
                }

                // Generate a unique instance id
                var id  = "cryo-" + Path.GetFileNameWithoutExtension(zipPath)
                              .ToLowerInvariant()
                              .Replace(" ", "-")
                              .Where(c => char.IsLetterOrDigit(c) || c == '-')
                              .Aggregate("", (a, c) => a + c);
                id = id.Length > 32 ? id[..32] : id;
                if (id.Length < 4) id = "cryo-import-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var instanceDir = Path.Combine(_prismDataDir, "instances", id);
                var mcDir       = Path.Combine(instanceDir, "minecraft");
                if (Directory.Exists(instanceDir))
                    id += "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd");
                Directory.CreateDirectory(mcDir);

                // Minimal instance.cfg
                File.WriteAllText(Path.Combine(instanceDir, "instance.cfg"),
                    $"[General]\nConfigVersion=1.2\nname={name}\nMaxMemAlloc=8192\nMinMemAlloc=2048\n");

                // Minimal mmc-pack.json
                var components = new JsonArray();
                components.Add(new JsonObject { ["uid"] = "net.minecraft", ["version"] = mc });
                if (loader == "NeoForge")
                    components.Add(new JsonObject { ["uid"] = "net.neoforged",    ["version"] = loaderVer });
                else if (loader == "Forge")
                    components.Add(new JsonObject { ["uid"] = "net.minecraftforge",["version"] = loaderVer });
                else if (loader == "Fabric")
                    components.Add(new JsonObject { ["uid"] = "net.fabricmc.fabric-loader", ["version"] = loaderVer });
                File.WriteAllText(Path.Combine(instanceDir, "mmc-pack.json"),
                    new JsonObject { ["components"] = components, ["formatVersion"] = 1 }.ToJsonString());

                // Extract ZIP contents into minecraft/
                Push("importProgress", new { phase = "extract", message = "Extracting files…" });
                using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName == "cryo-modpack.json" || entry.FullName == "modrinth.index.json") continue;
                        if (entry.FullName.EndsWith('/')) continue;  // directory entry
                        var dest = Path.GetFullPath(Path.Combine(mcDir, entry.FullName));
                        if (!dest.StartsWith(mcDir)) continue;  // path traversal guard
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        entry.ExtractToFile(dest, overwrite: true);
                    }
                }

                // Register in Cryo's instance list + the live manager (so it shows without restart)
                if (_config.Data.Instances.All(e => e.Id != id))
                {
                    var entry = new InstanceEntry { Id = id, DisplayName = name };
                    _config.Data.Instances.Add(entry);
                    _config.Save();
                    _manager.AddEntry(entry);
                }

                Logger.Info($"ImportModpack: created '{id}' ({name}) from {zipPath}");
                Push("importDone", new { ok = true, id, name });
            }
            catch (Exception e)
            {
                Logger.Warn($"ImportModpack: {e.Message}");
                Push("importDone", new { ok = false, error = e.Message });
            }
        });
        return new { ok = true };
    }

    // ── Server list ──────────────────────────────────────────────────────────

    private string ServersDatPath(string instanceId)
        => Path.Combine(_prismDataDir, "instances", instanceId, "minecraft", "servers.dat");

    private object GetServers(string instanceId)
    {
        var servers = ServerListClient.ReadServersDat(ServersDatPath(instanceId));
        return new { servers = servers.Select(s => new { name = s.Name, ip = s.Ip }).ToArray() };
    }

    private async Task<object?> PingServerAsync(string ip)
    {
        var st = await ServerListClient.PingAsync(ip);
        return new
        {
            online     = st.Online,
            motd       = st.Motd,
            players    = st.Players,
            maxPlayers = st.MaxPlayers,
            version    = st.Version,
            latencyMs  = st.LatencyMs,
            error      = st.Error,
        };
    }

    private object AddServer(string instanceId, string name, string ip)
    {
        try
        {
            var path    = ServersDatPath(instanceId);
            var servers = ServerListClient.ReadServersDat(path);
            if (servers.Any(s => string.Equals(s.Ip, ip, StringComparison.OrdinalIgnoreCase)))
                return new { ok = false, error = "Server already in the list" };
            servers.Add(new ServerEntry { Name = string.IsNullOrWhiteSpace(name) ? ip : name, Ip = ip });
            ServerListClient.WriteServersDat(path, servers);
            Logger.Info($"AddServer({instanceId}): {name} = {ip}");
            return new { ok = true };
        }
        catch (Exception e) { return new { ok = false, error = e.Message }; }
    }

    private object RemoveServer(string instanceId, string ip)
    {
        try
        {
            var path    = ServersDatPath(instanceId);
            var servers = ServerListClient.ReadServersDat(path);
            servers.RemoveAll(s => string.Equals(s.Ip, ip, StringComparison.OrdinalIgnoreCase));
            ServerListClient.WriteServersDat(path, servers);
            return new { ok = true };
        }
        catch (Exception e) { return new { ok = false, error = e.Message }; }
    }

    // ── World Backups ──────────────────────────────────────────────────────────

    private static string BackupDir(string instanceId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "VSpeedLauncher", "backups", instanceId);

    private object GetWorlds(string instanceId)
    {
        var savesDir = Path.Combine(_prismDataDir, "instances", instanceId, "minecraft", "saves");
        if (!Directory.Exists(savesDir)) return new { worlds = Array.Empty<object>() };
        var worlds = Directory.EnumerateDirectories(savesDir)
            .Select(d => new DirectoryInfo(d))
            .OrderByDescending(di => di.LastWriteTime)
            .Select(di =>
            {
                long sizeBytes = 0;
                try { sizeBytes = di.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
                return (object)new
                {
                    name     = di.Name,
                    sizeBytes,
                    modified = new DateTimeOffset(di.LastWriteTime).ToUnixTimeMilliseconds(),
                };
            })
            .ToArray();
        return new { worlds };
    }

    private object BackupWorld(string instanceId, string worldName)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var worldDir = Path.Combine(_prismDataDir, "instances", instanceId, "minecraft", "saves", worldName);
                if (!Directory.Exists(worldDir)) throw new DirectoryNotFoundException($"World '{worldName}' not found");

                var backupDir = BackupDir(instanceId);
                Directory.CreateDirectory(backupDir);
                var ts      = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
                var zipPath = Path.Combine(backupDir, worldName + "_" + ts + ".zip");

                Push("backupProgress", new { phase = "start", worldName, message = $"Backing up '{worldName}'…" });
                System.IO.Compression.ZipFile.CreateFromDirectory(worldDir, zipPath);
                var size = new FileInfo(zipPath).Length;
                Logger.Info($"BackupWorld({instanceId}/{worldName}): {zipPath} ({size} bytes)");
                Push("backupDone", new { ok = true, worldName, file = Path.GetFileName(zipPath), sizeBytes = size });
            }
            catch (Exception e)
            {
                Logger.Warn($"BackupWorld({instanceId}/{worldName}): {e.Message}");
                Push("backupError", new { error = e.Message });
            }
        });
        return new { ok = true };
    }

    private object GetBackups(string instanceId)
    {
        var dir = BackupDir(instanceId);
        if (!Directory.Exists(dir)) return new { backups = Array.Empty<object>() };
        var backups = Directory.GetFiles(dir, "*.zip")
            .Select(f => new FileInfo(f))
            .OrderByDescending(fi => fi.LastWriteTime)
            .Select(fi => (object)new
            {
                file      = fi.Name,
                sizeBytes = fi.Length,
                modified  = new DateTimeOffset(fi.LastWriteTime).ToUnixTimeMilliseconds(),
            })
            .ToArray();
        return new { backups };
    }

    private object RestoreBackup(string instanceId, string file)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var zipPath = Path.Combine(BackupDir(instanceId), Path.GetFileName(file));
                if (!File.Exists(zipPath)) throw new FileNotFoundException("Backup not found: " + file);

                // Derive world name from filename: "MyWorld_2024-01-01_12-00-00.zip" → "MyWorld"
                var worldName  = Path.GetFileNameWithoutExtension(file);
                var dateIdx    = System.Text.RegularExpressions.Regex.Match(worldName, @"_\d{4}-\d{2}-\d{2}_");
                if (dateIdx.Success) worldName = worldName[..dateIdx.Index];

                var destDir = Path.Combine(_prismDataDir, "instances", instanceId, "minecraft", "saves", worldName + "_restore");
                if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);

                Push("backupProgress", new { phase = "start", worldName, message = $"Restoring '{worldName}'…" });
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, destDir);
                Logger.Info($"RestoreBackup({instanceId}): {file} → {destDir}");
                Push("backupDone", new { ok = true, worldName, restoredAs = worldName + "_restore" });
            }
            catch (Exception e)
            {
                Logger.Warn($"RestoreBackup({instanceId}/{file}): {e.Message}");
                Push("backupError", new { error = e.Message });
            }
        });
        return new { ok = true };
    }

    private object DeleteBackup(string instanceId, string file)
    {
        var path = Path.Combine(BackupDir(instanceId), Path.GetFileName(file));
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) { return new { ok = false, error = e.Message }; }
        return new { ok = true };
    }

    private object OpenWorldsFolder(string instanceId)
    {
        var dir = Path.Combine(_prismDataDir, "instances", instanceId, "minecraft", "saves");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
        return new { ok = true };
    }

    // ── AI Memory ─────────────────────────────────────────────────────────────

    private static string AiMemoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "VSpeedLauncher", "ai-memory.json");

    private static List<JsonObject> LoadMemory()
    {
        try
        {
            if (File.Exists(AiMemoryPath))
            {
                var arr = JsonNode.Parse(File.ReadAllText(AiMemoryPath)) as JsonArray;
                return arr?.Select(n => n as JsonObject).Where(n => n != null).Cast<JsonObject>().ToList()
                       ?? new List<JsonObject>();
            }
        }
        catch { /* ignore */ }
        return new List<JsonObject>();
    }

    private static void WriteMemory(List<JsonObject> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AiMemoryPath)!);
            var arr = new JsonArray();
            foreach (var e in list) arr.Add(JsonNode.Parse(e.ToJsonString()));
            File.WriteAllText(AiMemoryPath, arr.ToJsonString());
        }
        catch (Exception ex) { Logger.Warn($"AI memory write: {ex.Message}"); }
    }

    private object SaveAiMemory(string instanceId, string problem, string solution, JsonNode? actionsNode)
    {
        if (string.IsNullOrWhiteSpace(problem)) return new { ok = false };
        var list = LoadMemory();
        // Remove duplicates for the same problem on this instance (keep freshest)
        list.RemoveAll(e => e["instanceId"]?.GetValue<string>() == instanceId
                         && e["problem"]?.GetValue<string>() == problem);
        var entry = new JsonObject
        {
            ["instanceId"] = instanceId,
            ["problem"]    = Trunc(problem, 400),
            ["solution"]   = Trunc(solution, 800),
            ["timestamp"]  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        if (actionsNode != null) entry["actions"] = JsonNode.Parse(actionsNode.ToJsonString());
        list.Insert(0, entry);
        if (list.Count > 60) list.RemoveRange(60, list.Count - 60);
        WriteMemory(list);
        return new { ok = true };
    }

    private object GetAiMemory(string instanceId)
    {
        var list = LoadMemory();
        var filtered = string.IsNullOrEmpty(instanceId)
            ? list
            : list.Where(e => e["instanceId"]?.GetValue<string>() == instanceId).ToList();
        // Return last 20 for UI
        return new { entries = filtered.Take(20).Select(e => new {
            instanceId = e["instanceId"]?.GetValue<string>() ?? "",
            problem    = e["problem"]?.GetValue<string>() ?? "",
            solution   = e["solution"]?.GetValue<string>() ?? "",
            timestamp  = e["timestamp"]?.GetValue<long>() ?? 0L,
        }).ToArray() };
    }

    private object ClearAiMemory(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
        {
            try { if (File.Exists(AiMemoryPath)) File.Delete(AiMemoryPath); } catch { }
        }
        else
        {
            var list = LoadMemory();
            list.RemoveAll(e => e["instanceId"]?.GetValue<string>() == instanceId);
            WriteMemory(list);
        }
        return new { ok = true };
    }

    private string BuildAiContext(JsonNode args)
    {
        var attach = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (args["attach"] is JsonArray a)
            foreach (var x in a) { var s = x?.GetValue<string>(); if (s != null) attach.Add(s); }
        if (attach.Count == 0) return "";

        var sb = new System.Text.StringBuilder();

        // Launcher's own log — not instance-specific, useful for diagnosing the launcher itself.
        if (attach.Contains("launcher"))
            try
            {
                var lp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSpeedLauncher", "launcher.log");
                if (File.Exists(lp))
                {
                    var ll = File.ReadLines(lp).ToList();
                    sb.AppendLine("## Cryo launcher log (last lines) — for diagnosing the LAUNCHER itself");
                    sb.AppendLine(string.Join("\n", ll.Skip(Math.Max(0, ll.Count - 110))));
                }
            }
            catch { /* ignore */ }

        var id = args["instanceId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
        {
            var only = sb.ToString();
            return only.Length > 12000 ? only[..12000] + "\n…(truncated)" : only;
        }

        var dir = Path.Combine(_prismDataDir, "instances", id, "minecraft");
        sb.AppendLine($"\n# Context for instance \"{id}\" (only use facts from here; do not invent file names)");

        // AI Memory — inject past solutions so the AI doesn't repeat itself
        try
        {
            var mem = LoadMemory()
                .Where(e => e["instanceId"]?.GetValue<string>() == id)
                .Take(6)
                .ToList();
            if (mem.Count > 0)
            {
                sb.AppendLine("\n## Past fixes for this instance (AI memory — avoid repeating)");
                foreach (var e in mem)
                {
                    var ts  = DateTimeOffset.FromUnixTimeMilliseconds(e["timestamp"]?.GetValue<long>() ?? 0).ToString("yyyy-MM-dd");
                    var prb = e["problem"]?.GetValue<string>() ?? "";
                    var sol = e["solution"]?.GetValue<string>() ?? "";
                    sb.AppendLine($"- [{ts}] Problem: \"{prb}\" → Fix: \"{sol}\"");
                }
            }
        }
        catch { /* ignore */ }

        if (attach.Contains("mods"))
            try
            {
                var modsDir = Path.Combine(dir, "mods");
                if (Directory.Exists(modsDir))
                {
                    var enabled  = Directory.EnumerateFiles(modsDir, "*.jar").Select(Path.GetFileName).Where(n => n != null).ToList();
                    var disabled = Directory.EnumerateFiles(modsDir, "*.jar.disabled").Select(Path.GetFileName).Where(n => n != null).ToList();
                    sb.AppendLine($"\n## Mods — {enabled.Count} enabled, {disabled.Count} disabled");
                    sb.AppendLine("Enabled: " + string.Join(", ", enabled.Take(140)) + (enabled.Count > 140 ? $" …(+{enabled.Count - 140} more)" : ""));
                    if (disabled.Count > 0) sb.AppendLine("Disabled: " + string.Join(", ", disabled.Take(60)));
                }
            }
            catch { /* ignore */ }

        if (attach.Contains("logs"))
            try
            {
                var log = Path.Combine(dir, "logs", "latest.log");
                if (File.Exists(log))
                {
                    var lines = File.ReadLines(log).ToList();
                    var tail  = lines.Skip(Math.Max(0, lines.Count - 140));
                    sb.AppendLine("\n## latest.log (last lines)");
                    sb.AppendLine(string.Join("\n", tail));
                }
            }
            catch { /* ignore */ }

        if (attach.Contains("crash"))
            try
            {
                var crashDir = Path.Combine(dir, "crash-reports");
                if (Directory.Exists(crashDir))
                {
                    var latest = new DirectoryInfo(crashDir).GetFiles("*.txt")
                                    .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                    if (latest != null)
                    {
                        sb.AppendLine($"\n## Latest crash report — {latest.Name}");
                        sb.AppendLine(string.Join("\n", File.ReadLines(latest.FullName).Take(90)));
                    }
                }
            }
            catch { /* ignore */ }

        var full = sb.ToString();
        return full.Length > 12000 ? full[..12000] + "\n…(truncated)" : full;
    }

    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    // ── Launcher self-diagnostics ──────────────────────────────────────────────
    [DllImport("kernel32.dll")]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);

    private int ReadRamMax(string instDir)
    {
        try
        {
            var cfg = Path.Combine(instDir, "instance.cfg");
            if (!File.Exists(cfg)) return 0;
            foreach (var l in File.ReadLines(cfg))
                if (l.StartsWith("MaxMemAlloc=", StringComparison.OrdinalIgnoreCase) && int.TryParse(l[12..].Trim(), out var v)) return v;
        }
        catch { /* ignore */ }
        return 0;
    }

    /// <summary>Inspects the launcher's own health and returns checks with optional 1-click fixes.</summary>
    private object SelfCheck()
    {
        var checks = new List<object>();
        int nok = 0, nwarn = 0, nfail = 0;
        void Add(string id, string title, string status, string detail, object? fix = null)
        {
            checks.Add(new { id, title, status, detail, fix });
            if (status == "ok") nok++; else if (status == "warn") nwarn++; else nfail++;
        }

        var data = _prismDataDir;

        // PrismLauncher executable + data dir
        var prism = _config.Data.PrismExe;
        Add("prism", "PrismLauncher executable", !string.IsNullOrWhiteSpace(prism) && File.Exists(prism) ? "ok" : "fail",
            !string.IsNullOrWhiteSpace(prism) && File.Exists(prism) ? prism : (string.IsNullOrWhiteSpace(prism) ? "Not configured." : "Not found: " + prism));
        Add("datadir", "Instances folder", Directory.Exists(data) ? "ok" : "fail", Directory.Exists(data) ? data : "Missing: " + data);

        // WebView2 runtime
        try { Add("webview2", "WebView2 runtime", "ok", "v" + CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch { Add("webview2", "WebView2 runtime", "warn", "Could not determine version."); }

        // AI assistant
        bool hasKey = !string.IsNullOrWhiteSpace(_config.Data.AiApiKey);
        bool local  = (_config.Data.AiBaseUrl ?? "").Contains("localhost");
        Add("ai", "AI assistant", hasKey || local ? "ok" : "warn",
            (hasKey || local ? "Configured · " : "No API key · ") + _config.Data.AiModel,
            hasKey || local ? null : new { type = "openSettings", label = "Add key" });

        // System RAM + disk
        long ramKb = 0; try { GetPhysicallyInstalledSystemMemory(out ramKb); } catch { }
        int sysMb = (int)(ramKb / 1024);
        try
        {
            var root = Path.GetPathRoot(data);
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            {
                double freeGb = new DriveInfo(root).AvailableFreeSpace / 1073741824.0;
                Add("disk", "Free disk space", freeGb < 5 ? "warn" : "ok", $"{freeGb:0.0} GB free on {root}");
            }
        }
        catch { /* ignore */ }

        // Per-instance: folder, VSpeed mod, RAM sanity
        foreach (var inst in _config.Data.Instances)
        {
            var dir = Path.Combine(data, "instances", inst.Id);
            if (!Directory.Exists(dir)) { Add("inst:" + inst.Id, inst.DisplayName, "warn", "Folder missing"); continue; }

            var modsDir = Path.Combine(dir, "minecraft", "mods");
            bool hasMod = Directory.Exists(modsDir) && Directory.EnumerateFiles(modsDir, "vspeed*").Any();
            int ramMax  = ReadRamMax(dir);

            var notes = new List<string>();
            string status = "ok";
            object? fix = null;
            if (!hasMod) { notes.Add("VSpeed mod not installed (optimizer inactive)"); status = "warn"; fix = new { type = "openModsFolder", label = "Open mods folder", args = new { id = inst.Id } }; }
            if (sysMb > 0 && ramMax > 0 && ramMax > sysMb * 0.8)
            {
                int safe = Math.Max(2, (int)(sysMb * 0.6 / 1024)) * 1024;
                notes.Add($"RAM {ramMax / 1024.0:0.0}G exceeds 80% of {sysMb / 1024.0:0.0}G system");
                status = "warn";
                fix = new { type = "setRam", label = $"Set to {safe / 1024}G", args = new { id = inst.Id, ramMax = safe } };
            }
            Add("inst:" + inst.Id, inst.DisplayName, status,
                notes.Count > 0 ? string.Join(" · ", notes) : ("Healthy" + (ramMax > 0 ? $" · RAM {ramMax / 1024.0:0.0}G" : "")), fix);
        }

        // Launcher's own log errors
        try
        {
            var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSpeedLauncher", "launcher.log");
            if (File.Exists(logPath))
            {
                var errs = File.ReadLines(logPath).Where(l => l.Contains(" ERROR ")).ToList();
                if (errs.Count == 0) Add("log", "Launcher log", "ok", "No errors logged.");
                else Add("log", "Launcher log", "warn", $"{errs.Count} error(s). Last: " + Trunc(errs[^1], 120), new { type = "openLauncherLog", label = "Open log" });
            }
        }
        catch { /* ignore */ }

        return new { checks, summary = new { ok = nok, warn = nwarn, fail = nfail } };
    }

    private object OpenLauncherLog()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSpeedLauncher", "launcher.log");
            if (File.Exists(p)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
            return new { ok = true };
        }
        catch (Exception e) { return new { ok = false, error = e.Message }; }
    }

    // ── Mod conflict scanner ───────────────────────────────────────────────────
    private static readonly Regex _modIdRe    = new("^\\s*modId\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex _fabricIdRe = new("\"id\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>
    /// Reads a jar's OWN mod id(s) — the modId under [[mods]] only. Dependency
    /// sections ([[dependencies.x]] → modId="minecraft"/"neoforge"/parent mod) are
    /// ignored, otherwise every mod looks like a duplicate of its platform deps.
    /// </summary>
    private static List<string> ReadModIds(string jarPath)
    {
        var ids = new List<string>();
        try
        {
            using var z = ZipFile.OpenRead(jarPath);
            var toml = z.GetEntry("META-INF/neoforge.mods.toml") ?? z.GetEntry("META-INF/mods.toml");
            if (toml != null)
            {
                using var r = new StreamReader(toml.Open());
                bool inMods = false;   // true only while inside a [[mods]] table
                string? line;
                while ((line = r.ReadLine()) != null)
                {
                    var t = line.TrimStart();
                    if (t.StartsWith("[["))      { inMods = t.StartsWith("[[mods]]", StringComparison.OrdinalIgnoreCase); continue; }
                    if (t.StartsWith("["))       { inMods = false; continue; }   // any other table ends the [[mods]] block
                    if (!inMods) continue;
                    var m = _modIdRe.Match(line);
                    if (m.Success) ids.Add(m.Groups[1].Value);
                }
            }
            else
            {
                var fab = z.GetEntry("fabric.mod.json");
                if (fab != null)
                {
                    using var r = new StreamReader(fab.Open());
                    var m = _fabricIdRe.Match(r.ReadToEnd());   // first "id" = the mod's own id (deps are key-value, not "id")
                    if (m.Success) ids.Add(m.Groups[1].Value);
                }
            }
        }
        catch { /* ignore unreadable jars */ }
        return ids;
    }

    // Platform / loader ids that are never user-managed jars (safety net for odd metadata).
    private static readonly HashSet<string> _ignoreModIds = new(StringComparer.OrdinalIgnoreCase)
        { "minecraft", "forge", "neoforge", "fabricloader", "fabric", "java", "mixinextras", "mixin" };

    private static readonly Regex _depModRe   = new("^\\s*modId\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex _depTypeRe  = new("^\\s*type\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex _depMandRe  = new("^\\s*mandatory\\s*=\\s*(true|false)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _depRangeRe = new("^\\s*versionRange\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    private sealed class ModDep { public string Target = ""; public bool Required = true; public string Range = ""; }
    private sealed class ModGraphInfo { public List<string> OwnIds = new(); public List<ModDep> Deps = new(); }

    /// <summary>Reads a jar's own modId(s) plus its declared dependencies
    /// (from [[dependencies.*]] in neoforge/forge mods.toml).</summary>
    private static ModGraphInfo ReadModGraphInfo(string jarPath)
    {
        var info = new ModGraphInfo();
        try
        {
            using var z = ZipFile.OpenRead(jarPath);
            var toml = z.GetEntry("META-INF/neoforge.mods.toml") ?? z.GetEntry("META-INF/mods.toml");
            if (toml == null)
            {
                // Fabric: read own id only (dependency graph for Fabric isn't parsed here).
                var fab = z.GetEntry("fabric.mod.json");
                if (fab != null)
                {
                    using var fr = new StreamReader(fab.Open());
                    var m = _fabricIdRe.Match(fr.ReadToEnd());
                    if (m.Success) info.OwnIds.Add(m.Groups[1].Value);
                }
                return info;
            }

            using var r = new StreamReader(toml.Open());
            string section = "";        // "mods" | "dep" | other
            ModDep? cur = null;
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                var t = line.TrimStart();
                if (t.StartsWith("[["))
                {
                    if (cur != null && !string.IsNullOrEmpty(cur.Target)) { info.Deps.Add(cur); cur = null; }
                    if (t.StartsWith("[[mods]]", StringComparison.OrdinalIgnoreCase)) section = "mods";
                    else if (t.StartsWith("[[dependencies.", StringComparison.OrdinalIgnoreCase)) { section = "dep"; cur = new ModDep(); }
                    else section = "other";
                    continue;
                }
                if (t.StartsWith("["))
                {
                    if (cur != null && !string.IsNullOrEmpty(cur.Target)) { info.Deps.Add(cur); cur = null; }
                    section = "other";
                    continue;
                }

                if (section == "mods")
                {
                    var m = _modIdRe.Match(line);
                    if (m.Success) info.OwnIds.Add(m.Groups[1].Value);
                }
                else if (section == "dep" && cur != null)
                {
                    var mm = _depModRe.Match(line);   if (mm.Success) { cur.Target = mm.Groups[1].Value; continue; }
                    var tt = _depTypeRe.Match(line);  if (tt.Success) { cur.Required = tt.Groups[1].Value.Equals("required", StringComparison.OrdinalIgnoreCase); continue; }
                    var md = _depMandRe.Match(line);  if (md.Success) { cur.Required = md.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase); continue; }
                    var rg = _depRangeRe.Match(line); if (rg.Success) { cur.Range = rg.Groups[1].Value; continue; }
                }
            }
            if (cur != null && !string.IsNullOrEmpty(cur.Target)) info.Deps.Add(cur);
        }
        catch { /* ignore unreadable jars */ }
        return info;
    }

    /// <summary>Groups enabled jars by declared modId and flags duplicates (same mod, multiple jars).</summary>
    private object ScanMods(string id)
    {
        var modsDir = Path.Combine(_prismDataDir, "instances", id, "minecraft", "mods");
        if (!Directory.Exists(modsDir)) return new { ok = false, error = "No mods folder for this instance." };

        var byId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        int total = 0, unknown = 0;
        foreach (var jar in Directory.EnumerateFiles(modsDir, "*.jar"))
        {
            total++;
            var ids = ReadModIds(jar);
            if (ids.Count == 0) { unknown++; continue; }
            foreach (var mid in ids)
            {
                if (!byId.TryGetValue(mid, out var l)) byId[mid] = l = new List<string>();
                l.Add(Path.GetFileName(jar)!);
            }
        }

        var duplicates = byId.Where(kv => kv.Value.Count > 1 && !_ignoreModIds.Contains(kv.Key))
            .Select(kv => new { modId = kv.Key, files = kv.Value })
            .OrderBy(d => d.modId).ToList();

        return new { ok = true, total, unknown, duplicates };
    }

    /// <summary>
    /// Builds a dependency graph from every jar's mods.toml and flags problems
    /// BEFORE launch: required dependencies that are missing or only present as a
    /// disabled jar. Returns nodes + edges for a focused visualization of the issues.
    /// </summary>
    private object AnalyzeModGraph(string id)
    {
        var modsDir = Path.Combine(_prismDataDir, "instances", id, "minecraft", "mods");
        if (!Directory.Exists(modsDir)) return new { ok = false, error = "No mods folder for this instance." };

        // Map enabled + disabled modIds → primary name; collect deps per owner.
        var enabledIds  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // modId → file
        var disabledIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var owners      = new List<(string ownerId, string file, ModGraphInfo info)>();

        foreach (var jar in Directory.EnumerateFiles(modsDir, "*.jar"))
        {
            var info = ReadModGraphInfo(jar);
            if (info.OwnIds.Count == 0) continue;
            var primary = info.OwnIds[0];
            enabledIds[primary] = Path.GetFileName(jar)!;
            owners.Add((primary, Path.GetFileName(jar)!, info));
        }
        foreach (var jar in Directory.EnumerateFiles(modsDir, "*.jar.disabled"))
        {
            var info = ReadModGraphInfo(jar);
            foreach (var oid in info.OwnIds) disabledIds[oid] = Path.GetFileName(jar)!;
        }

        var issues = new List<object>();
        var edges  = new List<object>();
        var involved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (ownerId, file, info) in owners)
            foreach (var dep in info.Deps)
            {
                if (_ignoreModIds.Contains(dep.Target)) continue;     // platform deps are always present
                bool present  = enabledIds.ContainsKey(dep.Target);
                bool disabled = !present && disabledIds.ContainsKey(dep.Target);

                if (present)
                {
                    edges.Add(new { from = ownerId, to = dep.Target, required = dep.Required, ok = true });
                }
                else if (dep.Required)
                {
                    involved.Add(ownerId); involved.Add(dep.Target);
                    edges.Add(new { from = ownerId, to = dep.Target, required = true, ok = false });
                    issues.Add(new
                    {
                        type     = disabled ? "disabled" : "missing",
                        severity = "error",
                        mod      = ownerId,
                        modFile  = file,
                        dep      = dep.Target,
                        versionRange = dep.Range,
                        message  = disabled
                            ? $"{ownerId} requires {dep.Target}, but it's installed as a DISABLED jar."
                            : $"{ownerId} requires {dep.Target}{(string.IsNullOrEmpty(dep.Range) ? "" : " " + dep.Range)}, which is not installed.",
                    });
                }
            }

        var nodes = enabledIds.Keys
            .Select(k => new { id = k, file = enabledIds[k], installed = true, involved = involved.Contains(k) })
            .Concat(involved.Where(k => !enabledIds.ContainsKey(k))
                .Select(k => new { id = k, file = "", installed = false, involved = true }))
            .ToList<object>();

        return new
        {
            ok = true,
            nodeCount = enabledIds.Count,
            edgeCount = edges.Count,
            issueCount = issues.Count,
            issues,
            nodes,
            edges,
        };
    }

    // ── Cryo Engine — NeoForge install + launch without Prism ────────────────

    private static string EngineRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "VSpeedLauncher", "game");

    /// <summary>Maps a Minecraft version to the Java major version it requires
    /// (matches Mojang's bundled runtimes). Used to gate Java-version-specific flags.</summary>
    private static int JavaMajorForMc(string mc)
    {
        // Parse "1.MINOR(.PATCH)"
        var parts = (mc ?? "").Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[1], out var minor)) return 21;  // assume modern
        int patch = parts.Length >= 3 && int.TryParse(parts[2], out var p) ? p : 0;
        if (minor >= 21) return 21;
        if (minor == 20) return patch >= 5 ? 21 : 17;   // 1.20.5+ → Java 21
        if (minor >= 18) return 17;                       // 1.18–1.20.4 → Java 17
        if (minor == 17) return 16;                       // 1.17 → Java 16
        return 8;                                         // ≤ 1.16 → Java 8
    }

    /// <summary>Game args that make Minecraft connect to a server on launch.
    /// 1.20+ uses --quickPlayMultiplayer; older versions use --server/--port.</summary>
    private static List<CmlLib.Core.ProcessBuilder.MArgument> BuildJoinGameArgs(string mc, string ip)
    {
        var list = new List<CmlLib.Core.ProcessBuilder.MArgument>();
        ip = (ip ?? "").Trim();
        if (ip.Length == 0) return list;
        int minor = 0;
        var parts = (mc ?? "").Split('.');
        if (parts.Length >= 2) int.TryParse(parts[1], out minor);
        if (minor >= 20)   // 1.20+ — Quick Play (single address argument)
        {
            list.Add(CmlLib.Core.ProcessBuilder.MArgument.FromCommandLine("--quickPlayMultiplayer"));
            list.Add(CmlLib.Core.ProcessBuilder.MArgument.FromCommandLine(ip));
        }
        else               // legacy --server <host> --port <port>
        {
            string host = ip, port = "25565";
            var c = ip.LastIndexOf(':');
            if (c > 0 && c < ip.Length - 1) { host = ip[..c]; port = ip[(c + 1)..]; }
            list.Add(CmlLib.Core.ProcessBuilder.MArgument.FromCommandLine("--server"));
            list.Add(CmlLib.Core.ProcessBuilder.MArgument.FromCommandLine(host));
            list.Add(CmlLib.Core.ProcessBuilder.MArgument.FromCommandLine("--port"));
            list.Add(CmlLib.Core.ProcessBuilder.MArgument.FromCommandLine(port));
        }
        return list;
    }

    /// <summary>
    /// Finds an already-installed Mojang JRE (<c>javaw.exe</c>) matching a Java
    /// major version under the engine's runtime dir, or <c>null</c> if none is
    /// present yet (so the caller can let CmlLib download the right one).
    /// </summary>
    private static string? ResolveBundledJava(int major)
    {
        // Mojang runtime components → Java major. For 17 several components exist;
        // prefer the newest. (jre-legacy=8, alpha=16, beta/gamma=17, delta=21.)
        string[] components = major switch
        {
            >= 21 => new[] { "java-runtime-delta" },
            17    => new[] { "java-runtime-gamma", "java-runtime-gamma-snapshot", "java-runtime-beta" },
            16    => new[] { "java-runtime-alpha" },
            <= 8  => new[] { "jre-legacy" },
            _     => new[] { "java-runtime-delta", "java-runtime-gamma", "java-runtime-beta", "jre-legacy" },
        };
        var baseDir = Path.Combine(EngineRoot, "runtime", "windows-x64");
        foreach (var c in components)
            foreach (var exe in new[] { "javaw.exe", "java.exe" })  // prefer no-console javaw
            {
                var p = Path.Combine(baseDir, c, "bin", exe);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    /// <summary>Reads the user-configured Java path (javaw.exe) from an instance's
    /// instance.cfg <c>[General]</c> section, or <c>""</c> if unset.</summary>
    private string ReadInstanceJavaPath(string instanceId)
    {
        var cfgPath = Path.Combine(_prismDataDir, "instances", instanceId, "instance.cfg");
        if (!File.Exists(cfgPath)) return "";
        try
        {
            bool inGeneral = true;
            foreach (var raw in File.ReadAllLines(cfgPath))
            {
                var l = raw.Trim();
                if (l.StartsWith("[")) { inGeneral = l.Equals("[General]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inGeneral) continue;
                if (l.StartsWith("JavaPath=", StringComparison.OrdinalIgnoreCase))
                    return l["JavaPath=".Length..].Trim().Trim('"');
            }
        }
        catch { /* unreadable cfg → treat as unset */ }
        return "";
    }

    private sealed class JavaInfo
    {
        public string Path = ""; public string Version = ""; public int Major; public string Vendor = ""; public string Source = "";
    }

    /// <summary>Parses a Java version string ("1.8.0_412", "17.0.15", "21.0.1") into its major.</summary>
    private static int MajorFromVersion(string ver)
    {
        if (string.IsNullOrWhiteSpace(ver)) return 0;
        var parts = ver.Split('.', '_', '-', '+');
        if (parts.Length == 0) return 0;
        if (!int.TryParse(new string(parts[0].TakeWhile(char.IsDigit).ToArray()), out var first)) return 0;
        if (first == 1 && parts.Length >= 2 && int.TryParse(new string(parts[1].TakeWhile(char.IsDigit).ToArray()), out var second))
            return second;   // 1.8.0 → 8
        return first;        // 17.0.15 → 17
    }

    /// <summary>Reads version/vendor from a JRE's <c>release</c> file (sibling of bin/).</summary>
    private static (string version, int major, string vendor) ReadJavaRelease(string javawPath)
    {
        try
        {
            var home = Path.GetDirectoryName(Path.GetDirectoryName(javawPath)); // <home>/bin/javaw.exe → <home>
            var rel  = home == null ? null : Path.Combine(home, "release");
            if (rel != null && File.Exists(rel))
            {
                string ver = "", vendor = "";
                foreach (var line in File.ReadAllLines(rel))
                {
                    if (line.StartsWith("JAVA_VERSION="))    ver    = line["JAVA_VERSION=".Length..].Trim().Trim('"');
                    else if (line.StartsWith("IMPLEMENTOR=")) vendor = line["IMPLEMENTOR=".Length..].Trim().Trim('"');
                }
                return (ver, MajorFromVersion(ver), vendor);
            }
        }
        catch { }
        return ("", 0, "");
    }

    /// <summary>
    /// Discovers Java installations on this machine — Cryo's bundled Mojang
    /// runtimes, Prism's runtimes, common vendor install dirs, JAVA_HOME and PATH —
    /// and computes the Java major the given instance's Minecraft version needs.
    /// Powers the Settings → Java "Auto-detect" button and the picker list.
    /// </summary>
    private object DetectJavas(string instanceId)
    {
        var found = new Dictionary<string, JavaInfo>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? javawPath, string source)
        {
            if (string.IsNullOrWhiteSpace(javawPath)) return;
            try
            {
                var full = Path.GetFullPath(javawPath);
                if (!File.Exists(full) || found.ContainsKey(full)) return;
                var (ver, major, vendor) = ReadJavaRelease(full);
                found[full] = new JavaInfo { Path = full, Version = ver, Major = major, Vendor = vendor, Source = source };
            }
            catch { }
        }
        void ScanContainer(string? dir, string source)   // scans <dir>/*/bin/javaw.exe
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
                foreach (var sub in Directory.GetDirectories(dir))
                    Consider(Path.Combine(sub, "bin", "javaw.exe"), source);
            }
            catch { }
        }

        // 1) Cryo's own bundled Mojang runtimes (self-contained, preferred).
        ScanContainer(Path.Combine(EngineRoot, "runtime", "windows-x64"), "Cryo bundled");
        // 2) PrismLauncher's runtimes (the user very likely already has these).
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        ScanContainer(Path.Combine(appdata, "PrismLauncher", "java"), "Prism");
        // 3) Common vendor install directories.
        var pf  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var root in new[] { pf, pfx })
        {
            if (string.IsNullOrEmpty(root)) continue;
            ScanContainer(Path.Combine(root, "Java"),             "Oracle/OpenJDK");
            ScanContainer(Path.Combine(root, "Eclipse Adoptium"), "Adoptium");
            ScanContainer(Path.Combine(root, "Microsoft"),        "Microsoft");
            ScanContainer(Path.Combine(root, "Zulu"),             "Azul Zulu");
            ScanContainer(Path.Combine(root, "Amazon Corretto"),  "Corretto");
            ScanContainer(Path.Combine(root, "BellSoft"),         "Liberica");
            ScanContainer(Path.Combine(root, "Semeru"),           "Semeru");
            Consider(Path.Combine(root, "Common Files", "Oracle", "Java", "javapath", "javaw.exe"), "Oracle");
        }
        // 4) JAVA_HOME, then 5) anything on PATH.
        var jh = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(jh)) Consider(Path.Combine(jh, "bin", "javaw.exe"), "JAVA_HOME");
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            Consider(Path.Combine(p.Trim(), "javaw.exe"), "PATH");

        // Required major for this instance's MC version.
        int requiredMajor = 0; string mc = "";
        try { var meta = InstanceMetaReader.Read(instanceId, _prismDataDir); mc = meta.Mc ?? ""; requiredMajor = JavaMajorForMc(mc); }
        catch { }

        // Recommended: Cryo's bundled JRE for the required major if present, else any
        // detected Java whose major matches (leave "" → engine downloads on launch).
        string recommended = ResolveBundledJava(requiredMajor) ?? "";
        if (string.IsNullOrEmpty(recommended) && requiredMajor > 0)
            recommended = found.Values.Where(j => j.Major == requiredMajor)
                                      .OrderByDescending(j => j.Version, StringComparer.Ordinal)
                                      .Select(j => j.Path).FirstOrDefault() ?? "";

        var list = found.Values
            .OrderByDescending(j => j.Major)
            .ThenBy(j => j.Source, StringComparer.Ordinal)
            .Select(j => new
            {
                path        = j.Path,
                version     = j.Version,
                major       = j.Major,
                vendor      = j.Vendor,
                source      = j.Source,
                recommended = requiredMajor > 0 && j.Major == requiredMajor,
            })
            .ToList();

        return new { mc, requiredMajor, recommendedPath = recommended, javas = list };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>Total usable physical RAM in MB — bounds the memory-allocation
    /// sliders to what the machine actually has instead of a fixed 64 GB cap.</summary>
    private object GetSystemRam()
    {
        long totalMb = 0;
        try
        {
            var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref st)) totalMb = (long)(st.ullTotalPhys / (1024UL * 1024UL));
        }
        catch { }
        // Fallback if the P/Invoke fails for any reason.
        if (totalMb <= 0)
            try { totalMb = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)); } catch { }
        return new { totalMb };
    }

    /// <summary>Reads the CmlLib version name previously stored for an instance.</summary>
    private string? GetStoredEngineVersion(string instanceId)
    {
        var path = Path.Combine(_prismDataDir, "instances", instanceId, "cryo-engine.json");
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path))?["versionName"]?.GetValue<string>(); }
        catch { return null; }
    }

    private void StoreEngineVersion(string instanceId, string versionName)
    {
        var path = Path.Combine(_prismDataDir, "instances", instanceId, "cryo-engine.json");
        File.WriteAllText(path, new JsonObject { ["versionName"] = versionName }.ToJsonString());
    }

    /// <summary>Installs ANY loader (NeoForge/Forge/Fabric/Quilt/Vanilla) via the engine
    /// and stores the version name so the instance launches without Prism.
    /// Push events: loaderProgress / loaderDone / loaderError.</summary>
    private void InstallLoaderForInstance(string instanceId, string mc, string loader, string loaderVer)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                Push("loaderProgress", new { id = instanceId, message = $"Installing {loader} for Minecraft {mc}…" });
                var core = new LauncherCore(EngineRoot);
                core.FileProgress += (name, done, total) =>
                    Push("loaderProgress", new { id = instanceId, name, done, total });
                var statusProg = new Progress<string>(msg => Push("loaderProgress", new { id = instanceId, message = msg }));
                var versionName = await core.InstallLoaderAsync(mc, loader, string.IsNullOrWhiteSpace(loaderVer) ? null : loaderVer, statusProg);
                StoreEngineVersion(instanceId, versionName);
                Logger.Info($"Loader installed for '{instanceId}': {versionName}");
                Push("loaderDone", new { ok = true, id = instanceId, versionName, loader });
            }
            catch (Exception e)
            {
                Logger.Warn($"InstallLoader({instanceId}/{loader}): {e.Message}");
                Push("loaderError", new { id = instanceId, error = e.Message });
            }
        });
    }

    private object GetEngineStatus(string instanceId)
    {
        var versionName = GetStoredEngineVersion(instanceId);
        var meta  = InstanceMetaReader.Read(instanceId, _prismDataDir);
        var entry = _manager.FindById(instanceId)?.Entry;
        return new
        {
            installed    = !string.IsNullOrEmpty(versionName),
            versionName  = versionName ?? "",
            mcVersion    = meta.Mc,
            loader       = meta.Loader,
            loaderVer    = meta.LoaderVer,
            loggedIn     = MicrosoftAccount.Instance.LoggedIn,
            source       = entry?.Source ?? "prism",
        };
    }

    /// <summary>Marks an instance as "cryo" (engine) or "prism" source.
    /// After marking as "cryo", the standard Launch button uses CmlLib directly.</summary>
    private object SetEngineSource(string instanceId, string source)
    {
        var entry = _manager.FindById(instanceId)?.Entry;
        if (entry == null) return new { ok = false, error = "Instance not found" };
        source = source == "cryo" ? "cryo" : "prism";
        entry.Source = source;
        _config.Save();
        Logger.Info($"SetEngineSource({instanceId}): source={source}");
        return new { ok = true, source };
    }

    private async Task<object?> GetNeoForgeVersionsAsync(string mcVersion)
    {
        if (string.IsNullOrWhiteSpace(mcVersion)) mcVersion = "1.21.1";
        try
        {
            var core = new LauncherCore(EngineRoot);
            var versions = await core.GetNeoForgeVersionsAsync(mcVersion);
            return new { ok = true, versions };
        }
        catch (Exception e)
        {
            Logger.Warn($"GetNeoForgeVersions({mcVersion}): {e.Message}");
            return new { ok = false, error = e.Message, versions = Array.Empty<string>() };
        }
    }

    /// <summary>
    /// Downloads NeoForge into the shared CmlLib game root and stores the resulting
    /// version name so <see cref="LaunchWithEngine"/> can use it later.
    /// Push events: neoforgeProgress / neoforgeDone / neoforgeError.
    /// </summary>
    private object InstallNeoForge(string instanceId, string neoForgeVersion)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var meta = InstanceMetaReader.Read(instanceId, _prismDataDir);
                var mc   = meta.Mc;
                if (string.IsNullOrEmpty(mc)) mc = "1.21.1";
                var nfv  = string.IsNullOrWhiteSpace(neoForgeVersion) ? null : neoForgeVersion;

                Push("neoforgeProgress", new { phase = "start", message = $"Preparing NeoForge for Minecraft {mc}…" });

                var core = new LauncherCore(EngineRoot);
                core.FileProgress += (name, done, total) =>
                    Push("neoforgeProgress", new { phase = "file", name, done, total });
                core.ByteProgress += (b, t) =>
                    Push("neoforgeProgress", new { phase = "bytes", bytesDone = b, bytesTotal = t });

                var statusProg = new Progress<string>(msg =>
                    Push("neoforgeProgress", new { phase = "status", message = msg }));

                var versionName = await core.InstallNeoForgeAsync(mc, nfv, statusProg);
                StoreEngineVersion(instanceId, versionName);

                Logger.Info($"NeoForge installed: {versionName} for instance {instanceId}");
                Push("neoforgeDone", new { ok = true, versionName, instanceId });
            }
            catch (Exception e)
            {
                Logger.Warn($"NeoForge install failed ({instanceId}): {e.Message}");
                Push("neoforgeError", new { error = e.Message });
            }
        });
        return new { ok = true };
    }

    /// <summary>
    /// Launches the instance using the Cryo engine (CmlLib.Core) instead of Prism.
    /// Requires: (1) NeoForge installed via InstallNeoForge, (2) Microsoft session.
    /// The instance's own .minecraft folder (mods, config, saves) is passed as gameDir.
    /// Push events: engineProgress / engineError.
    /// </summary>
    private object LaunchWithEngine(string instanceId, string joinServer = "")
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var inst = _manager.FindById(instanceId)
                    ?? throw new Exception($"Instance not found: {instanceId}");

                if (!MicrosoftAccount.Instance.LoggedIn)
                    throw new Exception("Not signed in. Please sign in from the titlebar first.");

                var versionName = GetStoredEngineVersion(instanceId)
                    ?? throw new Exception("NeoForge not installed via engine yet. Click 'Install Engine' first.");

                var session = MicrosoftAccount.Instance.Session!;
                var meta    = InstanceMetaReader.Read(instanceId, _prismDataDir);
                var gameDir = Path.Combine(_prismDataDir, "instances", instanceId, "minecraft");

                if (inst.State is InstanceState.Loading or InstanceState.Ready)
                {
                    Push("engineError", new { error = "Instance already running." });
                    return;
                }

                inst.State     = InstanceState.Loading;
                inst.LastError = null;
                inst.Notify();

                if (_config.Data.AutoHideOnLaunch)
                    WpfApp.Current?.Dispatcher.Invoke(() => WpfApp.Current?.MainWindow?.Hide());

                Push("engineProgress", new { phase = "start", message = $"Launching {meta.Name} via Cryo engine…" });

                var core = new LauncherCore(EngineRoot);
                core.FileProgress += (name, done, total) =>
                    Push("engineProgress", new { phase = "file", name, done, total });
                core.ByteProgress += (b, t) =>
                    Push("engineProgress", new { phase = "bytes", bytesDone = b, bytesTotal = t });

                // Build extra JVM args: VSpeed pipe signal + AppCDS.
                var extraJvm = new List<CmlLib.Core.ProcessBuilder.MArgument>
                {
                    CmlLib.Core.ProcessBuilder.MArgument.FromCommandLine($"-Dvspeed.daemon=true"),
                    CmlLib.Core.ProcessBuilder.MArgument.FromCommandLine($"-Dvspeed.instance={instanceId}"),
                };
                // AppCDS auto-archive (-XX:+AutoCreateSharedArchive) only exists in Java 19+.
                // Older Minecraft uses Java 17/8 — adding it there makes the JVM abort on start
                // ("Unrecognized VM option"). Gate it on the Java the MC version actually uses.
                int javaMajor = JavaMajorForMc(meta.Mc);
                if (javaMajor >= 19)
                {
                    try
                    {
                        var cdsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSpeedLauncher", "cds");
                        Directory.CreateDirectory(cdsDir);
                        var safe = new string(instanceId.Where(char.IsLetterOrDigit).ToArray());
                        var jsa  = Path.Combine(cdsDir, (safe.Length > 0 ? safe : "inst") + ".jsa");
                        if (!jsa.Contains(' '))
                        {
                            extraJvm.Add(CmlLib.Core.ProcessBuilder.MArgument.FromCommandLine($"-XX:+AutoCreateSharedArchive"));
                            extraJvm.Add(CmlLib.Core.ProcessBuilder.MArgument.FromCommandLine($"-XX:SharedArchiveFile={jsa}"));
                        }
                    }
                    catch (Exception ex) { Logger.Warn($"AppCDS setup skipped for engine launch: {ex.Message}"); }
                }
                else Logger.Info($"AppCDS skipped for {instanceId}: MC {meta.Mc} uses Java {javaMajor} (<19)");

                // Capture the game's stdout/stderr so an early JVM crash is diagnosable
                // even when the game never wrote its own latest.log.
                var engineLog = Path.Combine(gameDir, "logs", "cryo-engine.log");

                // ── Java selection ───────────────────────────────────────────
                // 1) An explicit user override (instance.cfg → JavaPath) wins, but
                //    only if the file actually exists (a stale path must not break
                //    launch — fall through to auto-detect instead).
                // 2) Otherwise pick the bundled Mojang JRE whose major matches the
                //    MC version. This hardens modded launches: a Fabric/Forge JSON
                //    may not carry its own javaVersion, so CmlLib could otherwise
                //    fall back to the wrong runtime (e.g. Java 8 → ClassFormatError).
                // 3) If no suitable bundled JRE is present yet, leave null so CmlLib
                //    downloads and resolves the correct runtime itself (vanilla path).
                string? javaExe   = null;
                var     userJava  = ReadInstanceJavaPath(instanceId);
                if (!string.IsNullOrWhiteSpace(userJava))
                {
                    if (File.Exists(userJava)) { javaExe = userJava; Logger.Info($"Java: user override → {userJava}"); }
                    else Logger.Warn($"Java: configured path not found, ignoring → {userJava}");
                }
                if (javaExe == null)
                {
                    javaExe = ResolveBundledJava(javaMajor);
                    if (javaExe != null) Logger.Info($"Java: auto-selected bundled JRE for Java {javaMajor} → {javaExe}");
                    else Logger.Info($"Java: no bundled Java {javaMajor} on disk yet — letting CmlLib resolve/download");
                }

                // "Join server": pass quickPlay/server args so the game connects on launch.
                var extraGame = BuildJoinGameArgs(meta.Mc, joinServer);
                if (extraGame.Count > 0) Push("engineProgress", new { phase = "join", message = $"Will join {joinServer} on launch…" });

                var proc = await core.InstallAndLaunchAsync(
                    versionName, session, meta.RamMax > 0 ? meta.RamMax : 4096,
                    gameDir: gameDir, javaPath: javaExe, extraJvmArgs: extraJvm, stdoutLog: engineLog,
                    extraGameArgs: extraGame);

                inst.PrismProcess = proc;
                inst.Notify();
                Logger.Info($"Engine launch: '{instanceId}' pid={proc.Id} version={versionName}");
                Push("engineProgress", new { phase = "launched", pid = proc.Id });

                // Watch exit
                _ = Task.Run(async () =>
                {
                    await proc.WaitForExitAsync();
                    if (inst.State == InstanceState.Loading)
                    {
                        inst.LastError = $"Game exited (code {proc.ExitCode}) before reaching the main menu.";
                        inst.State     = InstanceState.Crashed;
                        WpfApp.Current?.Dispatcher.Invoke(inst.Notify);
                    }
                });
            }
            catch (Exception e)
            {
                Logger.Warn($"Engine launch failed ({instanceId}): {e.Message}");
                var inst = _manager.FindById(instanceId);
                if (inst != null)
                {
                    inst.LastError = e.Message;
                    inst.State     = InstanceState.Crashed;
                    WpfApp.Current?.Dispatcher.Invoke(inst.Notify);
                }
                Push("engineError", new { error = e.Message });
            }
        });
        return new { ok = true };
    }

    // ── Standalone engine (CmlLib.Core) — beta self-test: install + launch offline ──
    private object CoreTest(string version, int ram)
    {
        if (string.IsNullOrWhiteSpace(version)) version = "1.21.1";
        _ = Task.Run(async () =>
        {
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSpeedLauncher", "game");
                var core = new LauncherCore(root);
                core.FileProgress += (name, done, total) => Push("coreProgress", new { name, done, total });
                core.ByteProgress += (b, t) => Push("coreProgress", new { bytesDone = b, bytesTotal = t });
                Push("coreProgress", new { name = "Starting install of Minecraft " + version + "…", done = 0, total = 0 });
                var session = MSession.CreateOfflineSession("CryoTest");
                var proc = await core.InstallAndLaunchAsync(version, session, ram > 0 ? ram : 4096);
                Push("coreDone", new { ok = true, pid = proc.Id, version });
            }
            catch (Exception e)
            {
                Logger.Warn($"Core engine test failed: {e.Message}");
                Push("coreError", new { error = e.Message });
            }
        });
        return new { ok = true };
    }

    // ── Microsoft account ──────────────────────────────────────────────────────
    private async Task<object?> AccountStatusAsync()
    {
        if (!MicrosoftAccount.Instance.LoggedIn)
            await MicrosoftAccount.Instance.RestoreSilentAsync();
        return new
        {
            loggedIn = MicrosoftAccount.Instance.LoggedIn,
            username = MicrosoftAccount.Instance.Username,
            uuid     = MicrosoftAccount.Instance.Uuid,
        };
    }

    private object AccountLogin()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var s = await MicrosoftAccount.Instance.LoginInteractiveAsync();
                Push("accountChanged", new { loggedIn = true, username = s.Username, uuid = s.UUID });
            }
            catch (Exception e)
            {
                Logger.Warn($"Microsoft login failed: {e.Message}");
                Push("accountError", new { error = e.Message });
            }
        });
        return new { ok = true };
    }

    private object AccountLogout()
    {
        _ = Task.Run(async () =>
        {
            await MicrosoftAccount.Instance.LogoutAsync();
            Push("accountChanged", new { loggedIn = false });
        });
        return new { ok = true };
    }

    /// <summary>Reads vspeed-stats.json written by the mod (real data-load timings).</summary>
    private object GetStats(string id)
    {
        var path = Path.Combine(_prismDataDir, "instances", id, "minecraft", "vspeed-stats.json");
        if (!File.Exists(path))
            return new { available = false };
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            var types = node["types"]?.AsObject();
            var outTypes = new List<object>();
            long totalMs = 0; int totalEntries = 0; string mode = "unknown";
            if (types != null)
                foreach (var kv in types)
                {
                    var v = kv.Value!;
                    long ms = v["ms"]?.GetValue<long>() ?? 0;
                    int en  = v["entries"]?.GetValue<int>() ?? 0;
                    string m = v["mode"]?.GetValue<string>() ?? "";
                    totalMs += ms; totalEntries += en; mode = m;
                    outTypes.Add(new { type = kv.Key, ms, entries = en, mode = m });
                }
            return new
            {
                available    = true,
                updatedAt    = node["updatedAt"]?.GetValue<long>() ?? 0,
                cacheEnabled = node["cacheEnabled"]?.GetValue<bool>() ?? true,
                mode,                        // "hit" (cached) or "cold" (scanned)
                totalMs, totalEntries,
                types        = outTypes,
            };
        }
        catch (Exception e)
        {
            Logger.Warn($"GetStats({id}): {e.Message}");
            return new { available = false, error = e.Message };
        }
    }

    private object StopInstance(string id)
    {
        var inst = _manager.FindById(id);
        if (inst != null) _manager.Kill(inst);
        return new { ok = true };
    }

    private object HibernateInstance(string id)
    {
        var inst = _manager.FindById(id);
        if (inst != null) _manager.Hibernate(inst);
        return new { ok = true };
    }

    private object WakeInstance(string id)
    {
        var inst = _manager.FindById(id);
        if (inst != null) _ = _manager.WakeAsync(inst);
        return new { ok = true };
    }

    // ── Window controls ───────────────────────────────────────────────────────

    private object WindowMinimize()
    {
        WpfApp.Current?.Dispatcher.Invoke(() =>
            WpfApp.Current.MainWindow!.WindowState = System.Windows.WindowState.Minimized);
        return new { ok = true };
    }

    private object WindowMaximize()
    {
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            var w = WpfApp.Current.MainWindow!;
            w.WindowState = w.WindowState == System.Windows.WindowState.Maximized
                            ? System.Windows.WindowState.Normal
                            : System.Windows.WindowState.Maximized;
        });
        return new { ok = true };
    }

    private object WindowClose()
    {
        WpfApp.Current?.Dispatcher.Invoke(
            () => WpfApp.Current.MainWindow?.Hide());
        return new { ok = true };
    }

    // Frameless window drag — WebView2 swallows WPF non-client hit-testing, so we
    // initiate the OS move loop manually on titlebar mousedown.
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    private object WindowDragStart()
    {
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            var w = WpfApp.Current.MainWindow;
            if (w == null) return;
            var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (h == 0) return;
            ReleaseCapture();
            SendMessage(h, WM_NCLBUTTONDOWN, HTCAPTION, 0);
        });
        return new { ok = true };
    }

    // ── Config ────────────────────────────────────────────────────────────────

    private object GetConfig() => new
    {
        prismExe         = _config.Data.PrismExe,
        prismDataDir     = _config.Data.PrismDataDir,
        showOnLaunch     = _config.Data.ShowOnLaunch,
        autoHibernate    = _config.Data.AutoHibernate,
        autoHideOnLaunch = _config.Data.AutoHideOnLaunch,
        startWithWindows = _config.Data.StartWithWindows,
        autoCleanCache   = _config.Data.AutoCleanCache,
        notifyLaunchDone = _config.Data.NotifyLaunchDone,
        notifyCacheBuilt = _config.Data.NotifyCacheBuilt,
        notifyCrash      = _config.Data.NotifyCrash,
        defaultRamMax    = _config.Data.DefaultRamMax,
        defaultJvmPreset = _config.Data.DefaultJvmPreset,
        // AI assistant — never echo the raw key back to the UI.
        aiHasKey         = !string.IsNullOrWhiteSpace(_config.Data.AiApiKey),
        aiBaseUrl        = _config.Data.AiBaseUrl,
        aiModel          = _config.Data.AiModel,
        aiAutoApply      = _config.Data.AiAutoApply,
        curseHasKey      = !string.IsNullOrWhiteSpace(_config.Data.CurseForgeApiKey),
        curseEnabled     = !string.IsNullOrWhiteSpace(EffectiveCurseKey()),
        discordEnabled   = _config.Data.DiscordEnabled,
        discordHasId     = !string.IsNullOrWhiteSpace(_config.Data.DiscordClientId),
        instances        = _config.Data.Instances,
    };

    private object SaveConfig(JsonNode args)
    {
        if (args["prismExe"]         is JsonNode pe)  _config.Data.PrismExe         = pe.GetValue<string>();
        if (args["autoHibernate"]    is JsonNode ah)  _config.Data.AutoHibernate    = ah.GetValue<bool>();
        if (args["showOnLaunch"]     is JsonNode sl)  _config.Data.ShowOnLaunch     = sl.GetValue<bool>();
        if (args["autoHideOnLaunch"] is JsonNode hl)  _config.Data.AutoHideOnLaunch = hl.GetValue<bool>();
        if (args["autoCleanCache"]   is JsonNode ac)  _config.Data.AutoCleanCache   = ac.GetValue<bool>();
        if (args["notifyLaunchDone"] is JsonNode n1)  _config.Data.NotifyLaunchDone = n1.GetValue<bool>();
        if (args["notifyCacheBuilt"] is JsonNode n2)  _config.Data.NotifyCacheBuilt = n2.GetValue<bool>();
        if (args["notifyCrash"]      is JsonNode n3)  _config.Data.NotifyCrash      = n3.GetValue<bool>();
        if (args["defaultRamMax"]    is JsonNode dr)  _config.Data.DefaultRamMax    = dr.GetValue<int>();
        if (args["defaultJvmPreset"] is JsonNode dp)  _config.Data.DefaultJvmPreset = dp.GetValue<string>();
        if (args["aiApiKey"]    is JsonNode ak) _config.Data.AiApiKey    = ak.GetValue<string>() ?? "";
        if (args["aiBaseUrl"]   is JsonNode ab) _config.Data.AiBaseUrl   = (ab.GetValue<string>() ?? "").Trim().TrimEnd('/');
        if (args["aiModel"]     is JsonNode am) _config.Data.AiModel     = am.GetValue<string>() ?? "";
        if (args["aiAutoApply"] is JsonNode aa) _config.Data.AiAutoApply = aa.GetValue<bool>();
        if (args["curseForgeApiKey"] is JsonNode ck) _config.Data.CurseForgeApiKey = ck.GetValue<string>() ?? "";
        bool discordTouched = false;
        if (args["discordClientId"] is JsonNode dci) { _config.Data.DiscordClientId = (dci.GetValue<string>() ?? "").Trim(); discordTouched = true; }
        if (args["discordEnabled"]  is JsonNode den) { _config.Data.DiscordEnabled  = den.GetValue<bool>(); discordTouched = true; }
        _config.Save();
        if (discordTouched) UpdateDiscordPresence();
        return new { ok = true };
    }

    // ── Instance config (instance.cfg) ───────────────────────────────────────

    private object GetInstanceCfg(string id)
    {
        var cfgPath = Path.Combine(_prismDataDir, "instances", id, "instance.cfg");
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(cfgPath))
        {
            bool inGeneral = true;
            foreach (var raw in File.ReadAllLines(cfgPath))
            {
                var l = raw.Trim();
                if (l.StartsWith("[")) { inGeneral = l.Equals("[General]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inGeneral) continue;
                var eq = l.IndexOf('=');
                if (eq > 0) kv[l[..eq]] = l[(eq + 1)..];
            }
        }
        // Prism wraps space-containing JvmArgs in quotes — strip them for clean chips.
        var jvmRaw = kv.GetValueOrDefault("JvmArgs", "").Trim();
        if (jvmRaw.Length >= 2 && jvmRaw.StartsWith("\"") && jvmRaw.EndsWith("\""))
            jvmRaw = jvmRaw[1..^1];
        return new
        {
            jvmArgs  = jvmRaw,
            javaPath = kv.GetValueOrDefault("JavaPath", "").Trim('"'),
            ramMin   = int.TryParse(kv.GetValueOrDefault("MinMemAlloc", "2048"), out var mn) ? mn : 2048,
            ramMax   = int.TryParse(kv.GetValueOrDefault("MaxMemAlloc", "8192"), out var mx) ? mx : 8192,
        };
    }

    private object SaveInstanceCfg(string id, JsonNode args)
    {
        var cfgPath = Path.Combine(_prismDataDir, "instances", id, "instance.cfg");
        if (!File.Exists(cfgPath))
            return new { ok = false, error = "instance.cfg not found" };

        var lines = File.ReadAllLines(cfgPath).ToList();
        var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (args["jvmArgs"]  is JsonNode ja) { updates["JvmArgs"] = ja.GetValue<string>() ?? ""; updates["OverrideJavaArgs"] = "true"; }
        if (args["javaPath"] is JsonNode jp)   updates["JavaPath"]     = jp.GetValue<string>() ?? "";
        if (args["ramMin"]   is JsonNode mn)   updates["MinMemAlloc"]  = mn.GetValue<int>().ToString();
        if (args["ramMax"]   is JsonNode mx)   updates["MaxMemAlloc"]  = mx.GetValue<int>().ToString();

        bool inGeneral = true;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i].Trim();
            if (l.StartsWith("[")) { inGeneral = l.Equals("[General]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inGeneral) continue;
            var eq = l.IndexOf('=');
            if (eq <= 0) continue;
            var k = l[..eq];
            if (updates.TryGetValue(k, out var v)) { lines[i] = k + "=" + v; updates.Remove(k); }
        }
        // Append remaining keys before [UI] section (or at end)
        foreach (var kv in updates)
        {
            var ui = lines.FindIndex(l => l.Trim().Equals("[UI]", StringComparison.OrdinalIgnoreCase));
            if (ui >= 0) lines.Insert(ui, kv.Key + "=" + kv.Value);
            else lines.Add(kv.Key + "=" + kv.Value);
        }

        File.WriteAllLines(cfgPath, lines);
        Logger.Info($"SaveInstanceCfg({id}): updated JvmArgs/Java/RAM");
        return new { ok = true };
    }

    // ── Shell / file actions ─────────────────────────────────────────────────

    private object OpenFolder(string id)
    {
        var mc = Path.Combine(_prismDataDir, "instances", id, "minecraft");
        var dir = Directory.Exists(mc) ? mc : Path.Combine(_prismDataDir, "instances", id);
        if (Directory.Exists(dir))
            System.Diagnostics.Process.Start("explorer.exe", dir);
        else
            Logger.Warn($"OpenFolder: directory not found: {dir}");
        return new { ok = true };
    }

    private object OpenCrashReport(string id)
    {
        var crashDir = Path.Combine(_prismDataDir, "instances", id, "minecraft", "crash-reports");
        if (!Directory.Exists(crashDir))
            return new { ok = false, error = "No crash-reports folder found" };
        var latest = Directory.GetFiles(crashDir, "*.txt")
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
        if (latest == null)
            return new { ok = false, error = "No crash reports found" };
        System.Diagnostics.Process.Start("notepad.exe", latest);
        Logger.Info($"OpenCrashReport({id}): {latest}");
        return new { ok = true, path = latest };
    }

    private object ExportLogs(string id, string content)
    {
        string savePath = "";
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title      = $"Export logs — {id}",
                FileName   = $"cryo-log-{id}-{DateTime.Now:yyyy-MM-dd_HH-mm}",
                DefaultExt = ".log",
                Filter     = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() == true) savePath = dlg.FileName;
        });

        if (string.IsNullOrEmpty(savePath))
            return new { ok = false };

        File.WriteAllText(savePath, content);
        Logger.Info($"ExportLogs({id}): saved {content.Length} chars → {savePath}");
        // Highlight the file in Explorer
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{savePath}\"");
        return new { ok = true, path = savePath };
    }

    private object RemoveFromLauncher(string id)
    {
        // Kill the running instance if any
        var inst = _manager.FindById(id);
        if (inst != null) _manager.Kill(inst);

        // Remove from config
        _config.Data.Instances.RemoveAll(i => i.Id == id);
        _config.Save();

        // Remove from InstanceManager's observable collection
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            var running = _manager.Instances.FirstOrDefault(i => i.Entry.Id == id);
            if (running != null) _manager.Instances.Remove(running);
        });

        Logger.Info($"RemoveFromLauncher({id}): removed from config");
        return new { ok = true };
    }
}

// ── JsonNode extension helpers ────────────────────────────────────────────────

internal static class JsonNodeExt
{
    public static string Str(this JsonNode n, string key, string def = "")
        => n[key] is JsonNode v ? (v.GetValue<string?>() ?? def) : def;

    public static int Int(this JsonNode n, string key, int def = 0)
        => n[key] is JsonNode v && v.GetValue<int?>() is int i ? i : def;

    public static bool Bool(this JsonNode n, string key, bool def = false)
    {
        if (n[key] is not JsonNode v) return def;
        try { return v.GetValue<bool>(); } catch { return def; }
    }
}
