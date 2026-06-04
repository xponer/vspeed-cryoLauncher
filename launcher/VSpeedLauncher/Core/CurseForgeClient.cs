using System.Net.Http;
using System.Text.Json.Nodes;

namespace VSpeedLauncher.Core;

/// <summary>
/// Thin client for the CurseForge v1 API (https://docs.curseforge.com).
/// Requires a personal API key from console.curseforge.com (set in Settings).
/// </summary>
public sealed class CurseForgeClient
{
    private const string Base   = "https://api.curseforge.com/v1";
    private const int    GameId = 432;   // Minecraft
    private const int    ClassMods = 6;  // "Mc Mods" class

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// App-wide CurseForge key embedded at build time from <c>curseforge.key</c>
    /// (see the .csproj). Lets CurseForge work for every user with no per-user
    /// setup. Empty when the build had no key file — callers then require the
    /// user to supply their own key in Settings.
    /// </summary>
    public static readonly string DefaultApiKey =
        typeof(CurseForgeClient).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "CurseForgeApiKey")?.Value ?? "";

    /// <summary>Cryo loader name → CurseForge modLoaderType enum.</summary>
    public static int LoaderType(string loader) => (loader ?? "").ToLowerInvariant() switch
    {
        "forge"    => 1,
        "fabric"   => 4,
        "quilt"    => 5,
        "neoforge" => 6,
        _          => 0,   // Any
    };

    public const int ClassModpacks = 4471;

    public async Task<JsonNode?> SearchAsync(string apiKey, string query, string mcVersion, string loader, int offset, int limit, int classId = ClassMods, int sortField = 2)
    {
        // sortField: 2 Popularity · 3 LastUpdated · 6 TotalDownloads (CurseForge enum).
        var qp = new List<string>
        {
            $"gameId={GameId}", $"classId={classId}",
            $"searchFilter={Uri.EscapeDataString(query ?? "")}",
            $"sortField={sortField}", "sortOrder=desc",
            $"index={offset}", $"pageSize={limit}",
        };
        if (!string.IsNullOrEmpty(mcVersion)) qp.Add($"gameVersion={Uri.EscapeDataString(mcVersion)}");
        // Modpacks bring their own loader — only constrain loader when searching mods.
        var lt = classId == ClassMods ? LoaderType(loader) : 0;
        if (lt > 0) qp.Add($"modLoaderType={lt}");

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/mods/search?" + string.Join("&", qp));
        req.Headers.Add("x-api-key", apiKey);
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync());
    }

    /// <summary>Resolves many file IDs to their download URLs in one request
    /// (POST /mods/files). Returns a map of fileId → (downloadUrl, fileName).</summary>
    public async Task<Dictionary<long, (string? url, string fileName)>> GetFilesByIdsAsync(string apiKey, IEnumerable<long> fileIds)
    {
        var result = new Dictionary<long, (string?, string)>();
        var body = new JsonObject { ["fileIds"] = new JsonArray(fileIds.Select(i => JsonValue.Create(i)).ToArray()) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/mods/files");
        req.Headers.Add("x-api-key", apiKey);
        req.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
        var data = node?["data"]?.AsArray();
        if (data != null)
            foreach (var f in data)
            {
                var fid = f?["id"]?.GetValue<long>() ?? 0;
                if (fid == 0) continue;
                result[fid] = (f?["downloadUrl"]?.GetValue<string>(), f?["fileName"]?.GetValue<string>() ?? "");
            }
        return result;
    }

    /// <summary>Best-effort fallback download URL for files whose API downloadUrl is null
    /// (CurseForge edge CDN path scheme).</summary>
    public static string FallbackUrl(long fileId, string fileName)
    {
        var p1 = fileId / 1000;
        var p2 = fileId % 1000;
        return $"https://mediafilez.forgecdn.net/files/{p1}/{p2}/{Uri.EscapeDataString(fileName)}";
    }

    public async Task<JsonNode?> GetFilesAsync(string apiKey, string modId, string mcVersion, string loader)
    {
        var qp = new List<string> { "pageSize=30" };
        if (!string.IsNullOrEmpty(mcVersion)) qp.Add($"gameVersion={Uri.EscapeDataString(mcVersion)}");
        var lt = LoaderType(loader);
        if (lt > 0) qp.Add($"modLoaderType={lt}");

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/mods/{Uri.EscapeDataString(modId)}/files?" + string.Join("&", qp));
        req.Headers.Add("x-api-key", apiKey);
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync());
    }
}
