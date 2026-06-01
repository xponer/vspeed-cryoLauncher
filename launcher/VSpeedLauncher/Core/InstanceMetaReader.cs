using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VSpeedLauncher.Core;

/// <summary>
/// Reads metadata from a PrismLauncher instance folder: loader, MC version,
/// mod count, RAM config, Java version, cache state, etc.
/// </summary>
public sealed class InstanceMeta
{
    public string  Id          { get; set; } = "";
    public string  Name        { get; set; } = "";
    public string  Loader      { get; set; } = "";
    public string  LoaderVer   { get; set; } = "";
    public string  Mc          { get; set; } = "";
    public string  Java        { get; set; } = "";
    public int     RamMin      { get; set; } = 2048;
    public int     RamMax      { get; set; } = 8192;
    public int     ModCount    { get; set; } = 0;
    public long    LastPlayed  { get; set; } = 0;
    public string  Accent      { get; set; } = "#38BDF8";
    public string  CacheState  { get; set; } = "off";
    // Runtime fields filled in by InstanceManager
    public string  State       { get; set; } = "stopped";
    public int     JvmPid      { get; set; } = 0;
    public long    LoadSeconds { get; set; } = 0;
    public long    ResidentMB  { get; set; } = 0;
    public string  LastError   { get; set; } = "";
}

public static class InstanceMetaReader
{
    private static readonly Dictionary<string, string> _loaderColors = new()
    {
        ["NeoForge"] = "#E87B2F",
        ["Forge"]    = "#6A8DCA",
        ["Fabric"]   = "#C8AA6E",
        ["Quilt"]    = "#9466CC",
    };

    public static InstanceMeta Read(string instanceId, string prismDataDir)
    {
        var meta = new InstanceMeta { Id = instanceId };
        var dir  = Path.Combine(prismDataDir, "instances", instanceId);
        if (!Directory.Exists(dir)) return meta;

        // ── instance.cfg ───────────────────────────────────────────────────────
        var cfgPath = Path.Combine(dir, "instance.cfg");
        if (File.Exists(cfgPath))
        {
            foreach (var raw in File.ReadAllLines(cfgPath))
            {
                var line = raw.Trim();
                if (line.StartsWith("name="))        meta.Name      = line[5..];
                else if (line.StartsWith("JavaVersion="))  meta.Java = "Java " + line[12..].Split('.')[0];
                else if (line.StartsWith("JavaVendor="))   meta.Java += " (" + line[11..] + ")";
                else if (line.StartsWith("MaxMemAlloc=") &&
                         int.TryParse(line[12..], out var mx))  meta.RamMax = mx;
                else if (line.StartsWith("MinMemAlloc=") &&
                         int.TryParse(line[12..], out var mn))  meta.RamMin = mn;
                else if (line.StartsWith("lastLaunchTime=") &&
                         long.TryParse(line[15..], out var t))  meta.LastPlayed = t;
            }
        }
        if (string.IsNullOrEmpty(meta.Name)) meta.Name = instanceId;

        // ── mmc-pack.json — loader + MC version ────────────────────────────────
        var packPath = Path.Combine(dir, "mmc-pack.json");
        if (File.Exists(packPath))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(packPath));
                var comps = root?["components"]?.AsArray();
                if (comps != null)
                {
                    foreach (var c in comps)
                    {
                        var uid = c?["uid"]?.GetValue<string>() ?? "";
                        var ver = c?["version"]?.GetValue<string>() ?? "";
                        if (uid == "net.minecraft")          meta.Mc = ver;
                        else if (uid == "net.neoforged")     { meta.Loader = "NeoForge"; meta.LoaderVer = ver; }
                        else if (uid == "net.minecraftforge") { meta.Loader = "Forge";   meta.LoaderVer = ver; }
                        else if (uid == "net.fabricmc.fabric-loader") { meta.Loader = "Fabric"; meta.LoaderVer = ver; }
                        else if (uid == "org.quiltmc.quilt-loader")   { meta.Loader = "Quilt";  meta.LoaderVer = ver; }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Warn($"InstanceMeta: parse mmc-pack.json for '{instanceId}': {e.Message}");
            }
        }

        // ── Mods count ──────────────────────────────────────────────────────────
        var modsDir = Path.Combine(dir, "minecraft", "mods");
        if (Directory.Exists(modsDir))
            meta.ModCount = Directory.GetFiles(modsDir, "*.jar").Length;

        // ── Cache state ─────────────────────────────────────────────────────────
        meta.CacheState = ReadCacheState(dir);

        // ── Accent colour based on loader ───────────────────────────────────────
        meta.Accent = _loaderColors.GetValueOrDefault(meta.Loader, "#38BDF8");

        return meta;
    }

    public static string ReadCacheState(string instanceDir)
    {
        var cacheRoot = Path.Combine(instanceDir, "minecraft", ".vspeed-cache", "json");
        if (!Directory.Exists(cacheRoot)) return "off";

        var hasBin = Directory.EnumerateFiles(cacheRoot, "*.bin", SearchOption.AllDirectories).Any();
        return hasBin ? "ready" : "off";
    }

    public static CacheInfo ReadCacheInfo(string instanceDir)
    {
        var info = new CacheInfo();
        var cacheRoot = Path.Combine(instanceDir, "minecraft", ".vspeed-cache", "json");
        if (!Directory.Exists(cacheRoot)) return info;

        foreach (var typeDir in Directory.EnumerateDirectories(cacheRoot))
        {
            var typeName = Path.GetFileName(typeDir);
            var bins = Directory.GetFiles(typeDir, "*.bin");
            if (bins.Length == 0) continue;
            var newest = bins.OrderByDescending(File.GetLastWriteTime).First();
            var size   = new FileInfo(newest).Length;
            var hash   = Path.GetFileNameWithoutExtension(newest);

            switch (typeName)
            {
                case "recipe":      info.RecipeBinPath = newest; info.RecipeSizeBytes = size; info.Hash = hash; break;
                case "advancement": info.AdvancementBinPath = newest; info.AdvancementSizeBytes = size; break;
            }
            info.TotalSizeBytes += size;
            if (info.BuiltAt == null || File.GetLastWriteTime(newest) > info.BuiltAt)
                info.BuiltAt = File.GetLastWriteTime(newest);
        }

        return info;
    }
}

public sealed class CacheInfo
{
    public string?   Hash                 { get; set; }
    public string?   RecipeBinPath        { get; set; }
    public string?   AdvancementBinPath   { get; set; }
    public long      RecipeSizeBytes      { get; set; }
    public long      AdvancementSizeBytes { get; set; }
    public long      TotalSizeBytes       { get; set; }
    public DateTime? BuiltAt              { get; set; }
    public int       Recipes              { get; set; }   // requires parsing — set to 0
    public int       Advancements         { get; set; }   // requires parsing — set to 0
}
