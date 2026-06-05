using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpfApp = System.Windows.Application;

namespace VSpeedLauncher.Core;

/// <summary>
/// Server hosting — run a dedicated Minecraft server for a modpack, with a live
/// console you can type commands into, plus RAM/port settings and EULA handling.
///
/// One hosted server per instance (the pack), living under
/// <c>%LocalAppData%\VSpeedLauncher\servers\&lt;instanceId&gt;\</c>. It's set up
/// from the instance's OWN mods/config and the matching loader's official server
/// install, so "create a server for this pack" is one click.
///
/// Loaders this build can set up: NeoForge, Fabric, Vanilla. (Forge/Quilt next.)
/// </summary>
public sealed partial class CryoBridge
{
    private static readonly HttpClient _srvHttp = new() { Timeout = TimeSpan.FromMinutes(10) };
    private const int ServerBufferCap = 2500;   // console ring-buffer line cap

    /// <summary>In-memory state for a running (or installing) hosted server.</summary>
    private sealed class HostedServer
    {
        public Process?       Proc;
        public string         State = "stopped";  // stopped|installing|starting|running|stopping|crashed
        public readonly object Gate = new();
        public readonly List<string> Buffer = new();
        public StreamWriter?  Stdin;
        public DateTime       StartedAt;
    }

    // instanceId -> server runtime state (survives only while the launcher runs)
    private static readonly Dictionary<string, HostedServer> _servers =
        new(StringComparer.OrdinalIgnoreCase);

    private static string ServersRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "VSpeedLauncher", "servers");

    private string ServerDir(string id)      => Path.Combine(ServersRoot, id);
    private string ServerMetaPath(string id) => Path.Combine(ServerDir(id), "cryo-server.json");
    private string InstanceMcDir(string id)  => Path.Combine(InstanceDataDir(id), "instances", id, "minecraft");

    private HostedServer GetSrv(string id)
    {
        lock (_servers)
        {
            if (!_servers.TryGetValue(id, out var s)) { s = new HostedServer(); _servers[id] = s; }
            return s;
        }
    }

    private static bool IsLoaderServerSupported(string? loader)
    {
        var l = (loader ?? "").ToLowerInvariant();
        return l is "neoforge" or "fabric" or "" or "vanilla";
    }

    private void SetServerState(string id, HostedServer srv, string state)
    {
        srv.State = state;
        var pid = (srv.Proc is { HasExited: false }) ? srv.Proc.Id : 0;
        Push("serverState", new { id, state, pid });
    }

    private void AppendServerLine(string id, HostedServer srv, string line)
    {
        lock (srv.Gate)
        {
            srv.Buffer.Add(line);
            if (srv.Buffer.Count > ServerBufferCap)
                srv.Buffer.RemoveRange(0, srv.Buffer.Count - ServerBufferCap);
        }
    }

    // ── Java for the server (console java.exe, not windowless javaw) ──────────
    private string ServerJavaExe(string mc)
    {
        var major = JavaMajorForMc(mc);
        var javaw = ResolveBundledJava(major);
        if (javaw != null)
        {
            var dir = Path.GetDirectoryName(javaw)!;
            var je  = Path.Combine(dir, "java.exe");
            return File.Exists(je) ? je : javaw;
        }
        return "java";   // fall back to whatever is on PATH
    }

    // ── Status / list ─────────────────────────────────────────────────────────

    private object GetHostedServer(string instanceId)
    {
        if (!IsSafeSegment(instanceId)) return new { exists = false, error = "Invalid instance." };
        var meta = InstanceMetaReader.Read(instanceId, InstanceDataDir(instanceId));
        var sp   = ServerMetaPath(instanceId);
        var srv  = _servers.GetValueOrDefault(instanceId);
        JsonNode? sm = null;
        var exists = File.Exists(sp);
        if (exists) { try { sm = JsonNode.Parse(File.ReadAllText(sp)); } catch { } }

        return new
        {
            exists,
            instanceId,
            name         = meta.Name,
            mc           = meta.Mc,
            loader       = meta.Loader,
            loaderVer    = meta.LoaderVer,
            supported    = IsLoaderServerSupported(meta.Loader),
            setupDone    = sm?["setupDone"]?.GetValue<bool>() ?? false,
            eulaAccepted = sm?["eulaAccepted"]?.GetValue<bool>() ?? false,
            ramMb        = sm?["ramMb"]?.GetValue<int>() ?? (meta.RamMax > 0 ? meta.RamMax : 4096),
            port         = sm?["port"]?.GetValue<int>() ?? 25565,
            state        = srv?.State ?? "stopped",
            pid          = (srv?.Proc is { HasExited: false }) ? srv!.Proc!.Id : 0,
        };
    }

    private JsonObject LoadServerMeta(string id)
    {
        try { if (File.Exists(ServerMetaPath(id))) return (JsonNode.Parse(File.ReadAllText(ServerMetaPath(id))) as JsonObject) ?? new(); }
        catch (Exception e) { Logger.Warn($"LoadServerMeta({id}): {e.Message}"); }
        return new JsonObject();
    }

    private void SaveServerMeta(string id, JsonObject m)
    {
        try { Directory.CreateDirectory(ServerDir(id)); File.WriteAllText(ServerMetaPath(id), m.ToJsonString()); }
        catch (Exception e) { Logger.Warn($"SaveServerMeta({id}): {e.Message}"); }
    }

    // ── Create / set up the server from the instance ───────────────────────────

    private object CreateServer(string instanceId)
    {
        if (!IsSafeSegment(instanceId)) return new { ok = false, error = "Invalid instance." };
        var srv = GetSrv(instanceId);
        if (srv.State is "installing" or "starting" or "running")
            return new { ok = false, error = "Server is busy (" + srv.State + ")." };
        _ = Task.Run(() => SetupServerAsync(instanceId));
        return new { ok = true };
    }

    private async Task SetupServerAsync(string instanceId)
    {
        var srv = GetSrv(instanceId);
        void Log(string m) { AppendServerLine(instanceId, srv, m); Push("serverSetupProgress", new { id = instanceId, message = m }); }
        try
        {
            var meta = InstanceMetaReader.Read(instanceId, InstanceDataDir(instanceId));
            if (!IsLoaderServerSupported(meta.Loader))
            {
                Push("serverSetupError", new { id = instanceId, error = $"{meta.Loader} server hosting isn't supported yet (NeoForge, Fabric and Vanilla are)." });
                return;
            }

            SetServerState(instanceId, srv, "installing");
            var dir = ServerDir(instanceId);
            Directory.CreateDirectory(dir);
            var mc   = string.IsNullOrWhiteSpace(meta.Mc) ? "1.21.1" : meta.Mc;
            var java = ServerJavaExe(mc);
            var ram  = meta.RamMax > 0 ? meta.RamMax : 4096;

            Log($"Setting up {meta.Loader} {mc} server for \"{meta.Name}\"…");

            // 1) Copy the pack's own mods + config into the server.
            var srcMc = InstanceMcDir(instanceId);
            Log("Copying mods…");
            CopyEnabledMods(Path.Combine(srcMc, "mods"), Path.Combine(dir, "mods"));
            foreach (var sub in new[] { "config", "defaultconfigs", "kubejs", "scripts" })
                CopyTree(Path.Combine(srcMc, sub), Path.Combine(dir, sub), overwrite: false);

            // 2) Install the loader's server files.
            var m = LoadServerMeta(instanceId);
            var loader = (meta.Loader ?? "").ToLowerInvariant();
            if (loader is "" or "vanilla")
            {
                Log("Downloading vanilla server.jar…");
                var url = await GetVanillaServerUrlAsync(mc) ?? throw new Exception($"No vanilla server jar for {mc}.");
                await DownloadToAsync(url, Path.Combine(dir, "server.jar"));
                m["launchKind"] = "jar"; m["launchArg"] = "server.jar";
            }
            else if (loader == "fabric")
            {
                Log("Downloading Fabric server launcher…");
                var lv = meta.LoaderVer;
                if (string.IsNullOrWhiteSpace(lv)) lv = await GetLatestFabricLoaderAsync(mc) ?? throw new Exception("No Fabric loader version.");
                var url = $"https://meta.fabricmc.net/v2/versions/loader/{mc}/{lv}/server/jar";
                await DownloadToAsync(url, Path.Combine(dir, "fabric-server-launch.jar"));
                m["launchKind"] = "jar"; m["launchArg"] = "fabric-server-launch.jar";
            }
            else if (loader == "neoforge")
            {
                var ver = meta.LoaderVer;
                if (string.IsNullOrWhiteSpace(ver)) throw new Exception("NeoForge version unknown for this instance.");
                Log($"Downloading NeoForge {ver} installer…");
                var installer = Path.Combine(dir, "neoforge-installer.jar");
                await DownloadToAsync($"https://maven.neoforged.net/releases/net/neoforged/neoforge/{ver}/neoforge-{ver}-installer.jar", installer);
                Log("Running NeoForge --installServer (this can take a minute)…");
                var code = await RunJavaAsync(java, new[] { "-jar", "neoforge-installer.jar", "--installServer" }, dir,
                                              l => AppendServerLine(instanceId, srv, l));
                if (code != 0) throw new Exception($"NeoForge installer exited with code {code}.");
                try { File.Delete(installer); } catch { }
                var winArgs = $"libraries/net/neoforged/neoforge/{ver}/win_args.txt";
                if (!File.Exists(Path.Combine(dir, winArgs.Replace('/', Path.DirectorySeparatorChar))))
                    throw new Exception("NeoForge server args not found after install.");
                m["launchKind"] = "neoargs"; m["launchArg"] = winArgs;
            }
            else throw new Exception($"{meta.Loader} not supported.");

            // 3) server.properties + JVM RAM + persisted meta.
            int port = m["port"]?.GetValue<int>() ?? 25565;
            SetServerProp(dir, "server-port", port.ToString());
            SetServerProp(dir, "motd", meta.Name);
            if ((string)m["launchKind"]! == "neoargs") WriteNeoJvmArgs(dir, ram);

            m["loader"]    = meta.Loader; m["mc"] = mc; m["loaderVer"] = meta.LoaderVer;
            m["ramMb"]     = m["ramMb"]?.GetValue<int>() ?? ram;
            m["port"]      = port;
            m["eulaAccepted"] = m["eulaAccepted"]?.GetValue<bool>() ?? false;
            m["setupDone"] = true;
            SaveServerMeta(instanceId, m);

            Log("Server ready. Accept the Minecraft EULA, then Start.");
            SetServerState(instanceId, srv, "stopped");
            Push("serverSetupDone", new { id = instanceId, ok = true });
        }
        catch (Exception e)
        {
            Logger.Warn($"SetupServer({instanceId}): {e.Message}");
            AppendServerLine(instanceId, srv, "[setup error] " + e.Message);
            SetServerState(instanceId, srv, "stopped");
            Push("serverSetupError", new { id = instanceId, error = e.Message });
        }
    }

    // ── Start / stop / command ──────────────────────────────────────────────────

    private object StartServer(string instanceId)
    {
        if (!IsSafeSegment(instanceId)) return new { ok = false, error = "Invalid instance." };
        var srv = GetSrv(instanceId);
        if (srv.Proc is { HasExited: false }) return new { ok = false, error = "Server already running." };
        if (srv.State is "installing" or "starting") return new { ok = false, error = "Server is busy." };

        var m = LoadServerMeta(instanceId);
        if (!(m["setupDone"]?.GetValue<bool>() ?? false)) return new { ok = false, error = "Server isn't set up yet." };
        if (!(m["eulaAccepted"]?.GetValue<bool>() ?? false)) return new { ok = false, needEula = true, error = "Accept the Minecraft EULA first." };

        var dir = ServerDir(instanceId);
        File.WriteAllText(Path.Combine(dir, "eula.txt"), "eula=true\n");

        var mc   = m["mc"]?.GetValue<string>() ?? "1.21.1";
        var ram  = m["ramMb"]?.GetValue<int>() ?? 4096;
        var kind = m["launchKind"]?.GetValue<string>() ?? "jar";
        var arg  = m["launchArg"]?.GetValue<string>() ?? "server.jar";
        var java = ServerJavaExe(mc);

        var args = new List<string>();
        if (kind == "neoargs")
        {
            WriteNeoJvmArgs(dir, ram);
            args.Add("@user_jvm_args.txt");
            args.Add("@" + arg);
            args.Add("nogui");
        }
        else // plain jar (vanilla / fabric)
        {
            args.Add($"-Xms{ram}M");
            args.Add($"-Xmx{ram}M");
            args.Add("-jar");
            args.Add(arg);
            args.Add("nogui");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = java,
                WorkingDirectory       = dir,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) AppendServerLine(instanceId, srv, e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data != null) AppendServerLine(instanceId, srv, e.Data); };

            SetServerState(instanceId, srv, "starting");
            AppendServerLine(instanceId, srv, $"[cryo] starting server: {Path.GetFileName(java)} {string.Join(' ', args)}");
            p.Start();
            srv.Proc      = p;
            srv.Stdin     = p.StandardInput;
            srv.StartedAt = DateTime.UtcNow;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            SetServerState(instanceId, srv, "running");

            _ = Task.Run(async () =>
            {
                await p.WaitForExitAsync();
                var graceful = srv.State == "stopping";
                var ranFor   = DateTime.UtcNow - srv.StartedAt;
                AppendServerLine(instanceId, srv, $"[cryo] server stopped (exit {p.ExitCode}).");
                srv.Stdin = null;
                SetServerState(instanceId, srv,
                    (!graceful && p.ExitCode != 0 && ranFor < TimeSpan.FromSeconds(40)) ? "crashed" : "stopped");
            });
            return new { ok = true, pid = p.Id };
        }
        catch (Exception e)
        {
            Logger.Warn($"StartServer({instanceId}): {e.Message}");
            AppendServerLine(instanceId, srv, "[start error] " + e.Message);
            SetServerState(instanceId, srv, "stopped");
            return new { ok = false, error = e.Message };
        }
    }

    private object StopServer(string instanceId)
    {
        var srv = _servers.GetValueOrDefault(instanceId);
        if (srv?.Proc is not { HasExited: false } p) return new { ok = true };
        SetServerState(instanceId, srv, "stopping");
        _ = Task.Run(async () =>
        {
            try { srv.Stdin?.WriteLine("stop"); srv.Stdin?.Flush(); } catch { }
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await p.WaitForExitAsync(cts.Token);
            }
            catch { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } }
        });
        return new { ok = true };
    }

    private object SendServerCommand(string instanceId, string cmd)
    {
        var srv = _servers.GetValueOrDefault(instanceId);
        if (srv?.Proc is not { HasExited: false }) return new { ok = false, error = "Server isn't running." };
        cmd = (cmd ?? "").Replace("\r", "").Replace("\n", "");
        if (cmd.Length == 0) return new { ok = true };
        try
        {
            AppendServerLine(instanceId, srv, "> " + cmd);
            srv.Stdin!.WriteLine(cmd);
            srv.Stdin!.Flush();
            return new { ok = true };
        }
        catch (Exception e) { return new { ok = false, error = e.Message }; }
    }

    private object GetServerConsole(string instanceId, int n)
    {
        var srv = _servers.GetValueOrDefault(instanceId);
        if (srv == null) return new { state = "stopped", pid = 0, lines = Array.Empty<string>() };
        string[] lines;
        lock (srv.Gate)
        {
            var start = n > 0 && srv.Buffer.Count > n ? srv.Buffer.Count - n : 0;
            lines = srv.Buffer.GetRange(start, srv.Buffer.Count - start).ToArray();
        }
        return new
        {
            state = srv.State,
            pid   = (srv.Proc is { HasExited: false }) ? srv.Proc.Id : 0,
            lines,
        };
    }

    // ── Settings / EULA / delete ────────────────────────────────────────────────

    private object SaveServerSettings(string instanceId, int ramMb, int port)
    {
        if (!IsSafeSegment(instanceId)) return new { ok = false, error = "Invalid instance." };
        var srv = _servers.GetValueOrDefault(instanceId);
        if (srv?.Proc is { HasExited: false }) return new { ok = false, error = "Stop the server before changing settings." };
        var m = LoadServerMeta(instanceId);
        if (ramMb >= 512 && ramMb <= 65536) m["ramMb"] = ramMb;
        if (port  >= 1   && port  <= 65535) { m["port"] = port; SetServerProp(ServerDir(instanceId), "server-port", port.ToString()); }
        SaveServerMeta(instanceId, m);
        return new { ok = true, ramMb = m["ramMb"]?.GetValue<int>() ?? 0, port = m["port"]?.GetValue<int>() ?? 0 };
    }

    private object AcceptServerEula(string instanceId)
    {
        if (!IsSafeSegment(instanceId)) return new { ok = false, error = "Invalid instance." };
        var m = LoadServerMeta(instanceId);
        m["eulaAccepted"] = true;
        SaveServerMeta(instanceId, m);
        try { Directory.CreateDirectory(ServerDir(instanceId)); File.WriteAllText(Path.Combine(ServerDir(instanceId), "eula.txt"), "eula=true\n"); } catch { }
        return new { ok = true };
    }

    private object DeleteServer(string instanceId)
    {
        if (!IsSafeSegment(instanceId)) return new { ok = false, error = "Invalid instance." };
        var srv = _servers.GetValueOrDefault(instanceId);
        if (srv?.Proc is { HasExited: false }) return new { ok = false, error = "Stop the server first." };
        try
        {
            var dir = ServerDir(instanceId);
            if (IsContained(ServersRoot, dir) && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            lock (_servers) _servers.Remove(instanceId);
            return new { ok = true };
        }
        catch (Exception e) { return new { ok = false, error = e.Message }; }
    }

    private object OpenServerFolder(string instanceId)
    {
        if (!IsSafeSegment(instanceId)) return new { ok = false, error = "Invalid instance." };
        var dir = ServerDir(instanceId);
        if (!Directory.Exists(dir)) return new { ok = false, error = "No server folder yet." };
        try { Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true }); return new { ok = true }; }
        catch (Exception e) { return new { ok = false, error = e.Message }; }
    }

    // ── Full server.properties read/write (edit everything from the UI) ─────────

    private static bool IsValidPropKey(string k)
    {
        if (string.IsNullOrEmpty(k) || k.Length > 64) return false;
        foreach (var c in k) if (!(char.IsLetterOrDigit(c) || c == '-' || c == '.' || c == '_')) return false;
        return true;
    }

    private object GetServerProperties(string id)
    {
        if (!IsSafeSegment(id)) return new { ok = false, error = "Invalid instance." };
        var f = Path.Combine(ServerDir(id), "server.properties");
        var props = new Dictionary<string, string>();
        if (File.Exists(f))
        {
            foreach (var raw in File.ReadAllLines(f))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                props[line[..eq].Trim()] = line[(eq + 1)..];   // value verbatim (may contain spaces/json)
            }
        }
        return new { ok = true, exists = File.Exists(f), props };
    }

    /// <summary>Write the full server.properties, preserving comments + existing
    /// key order and appending any new keys. Sanitises keys/values; keeps the
    /// cached port in cryo-server.json in sync.</summary>
    private object SaveServerProperties(string id, JsonNode? propsNode)
    {
        if (!IsSafeSegment(id)) return new { ok = false, error = "Invalid instance." };
        if (propsNode is not JsonObject obj) return new { ok = false, error = "No properties supplied." };
        var srv = _servers.GetValueOrDefault(id);
        if (srv?.Proc is { HasExited: false }) return new { ok = false, error = "Stop the server before editing its properties." };

        var newVals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in obj)
        {
            if (!IsValidPropKey(kv.Key)) continue;
            var val = (kv.Value?.ToString() ?? "").Replace("\r", "").Replace("\n", "");
            newVals[kv.Key] = val;
        }

        var dir = ServerDir(id);
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, "server.properties");
        var existing = File.Exists(f) ? File.ReadAllLines(f).ToList() : new List<string>();
        var written  = new HashSet<string>(StringComparer.Ordinal);
        var outLines = new List<string>();
        foreach (var raw in existing)
        {
            var t = raw.Trim();
            if (t.Length == 0 || t[0] == '#') { outLines.Add(raw); continue; }
            var eq = t.IndexOf('=');
            if (eq <= 0) { outLines.Add(raw); continue; }
            var key = t[..eq].Trim();
            if (newVals.TryGetValue(key, out var nv)) { outLines.Add(key + "=" + nv); written.Add(key); }
            else outLines.Add(raw);
        }
        foreach (var kv in newVals) if (!written.Contains(kv.Key)) outLines.Add(kv.Key + "=" + kv.Value);

        try
        {
            File.WriteAllText(f, string.Join("\n", outLines) + "\n");
            if (newVals.TryGetValue("server-port", out var p) && int.TryParse(p, out var pi))
            {
                var m = LoadServerMeta(id); m["port"] = pi; SaveServerMeta(id, m);
            }
            return new { ok = true, count = newVals.Count };
        }
        catch (Exception e) { Logger.Warn($"SaveServerProperties({id}): {e.Message}"); return new { ok = false, error = e.Message }; }
    }

    // ── helpers: downloads, file ops, loader metadata ───────────────────────────

    private async Task<string?> GetVanillaServerUrlAsync(string mc)
    {
        var manifest = await _srvHttp.GetStringAsync("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json");
        var versions = JsonNode.Parse(manifest)?["versions"]?.AsArray();
        string? verUrl = null;
        if (versions != null)
            foreach (var v in versions)
                if ((v?["id"]?.GetValue<string>()) == mc) { verUrl = v?["url"]?.GetValue<string>(); break; }
        if (verUrl == null) return null;
        var vjson = await _srvHttp.GetStringAsync(verUrl);
        return JsonNode.Parse(vjson)?["downloads"]?["server"]?["url"]?.GetValue<string>();
    }

    private async Task<string?> GetLatestFabricLoaderAsync(string mc)
    {
        var json = await _srvHttp.GetStringAsync($"https://meta.fabricmc.net/v2/versions/loader/{mc}");
        return JsonNode.Parse(json)?.AsArray().FirstOrDefault()?["loader"]?["version"]?.GetValue<string>();
    }

    private async Task DownloadToAsync(string url, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var resp = await _srvHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(dest);
        await resp.Content.CopyToAsync(fs);
    }

    private async Task<int> RunJavaAsync(string javaExe, IEnumerable<string> args, string workDir, Action<string> onLine)
    {
        var psi = new ProcessStartInfo
        {
            FileName = javaExe, WorkingDirectory = workDir,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    /// <summary>Copy enabled mod jars (skip *.jar.disabled) into the server's mods folder.</summary>
    private static void CopyEnabledMods(string srcMods, string dstMods)
    {
        if (!Directory.Exists(srcMods)) return;
        Directory.CreateDirectory(dstMods);
        foreach (var jar in Directory.GetFiles(srcMods, "*.jar"))
            File.Copy(jar, Path.Combine(dstMods, Path.GetFileName(jar)), overwrite: true);
    }

    private static void CopyTree(string src, string dst, bool overwrite)
    {
        if (!Directory.Exists(src)) return;
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            if (!overwrite && File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>Set or append a key=value line in the server's server.properties.</summary>
    private static void SetServerProp(string serverDir, string key, string val)
    {
        var f = Path.Combine(serverDir, "server.properties");
        var lines = File.Exists(f) ? File.ReadAllLines(f).ToList() : new List<string>();
        var found = false;
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].StartsWith(key + "=", StringComparison.Ordinal)) { lines[i] = key + "=" + val; found = true; break; }
        if (!found) lines.Add(key + "=" + val);
        File.WriteAllText(f, string.Join("\n", lines) + "\n");
    }

    /// <summary>Rewrite NeoForge's user_jvm_args.txt with the chosen heap size.</summary>
    private static void WriteNeoJvmArgs(string serverDir, int ramMb)
    {
        var f = Path.Combine(serverDir, "user_jvm_args.txt");
        var lines = File.Exists(f) ? File.ReadAllLines(f).ToList() : new List<string>();
        lines = lines.Where(l => { var t = l.Trim(); return !t.StartsWith("-Xmx") && !t.StartsWith("-Xms"); }).ToList();
        lines.Insert(0, $"-Xms{ramMb}M");
        lines.Insert(1, $"-Xmx{ramMb}M");
        File.WriteAllText(f, string.Join("\n", lines) + "\n");
    }
}
