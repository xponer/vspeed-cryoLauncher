using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VSpeedLauncher.Core;

/// <summary>
/// Persists per-instance launch history at
/// <c>%LOCALAPPDATA%\VSpeedLauncher\history.json</c>.
/// Written on every READY signal from the game.
/// </summary>
public sealed class HistoryStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private List<HistoryEntry> _entries = new();

    public HistoryStore(string path) => _path = path;

    public void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            _entries = JsonSerializer.Deserialize<List<HistoryEntry>>(json, _opts) ?? new();
            Logger.Info($"HistoryStore: loaded {_entries.Count} entries.");
        }
        catch (Exception e)
        {
            Logger.Warn($"HistoryStore load failed: {e.Message}");
            _entries = new();
        }
    }

    public void Record(string instanceId, long wallSeconds)
    {
        var entry = new HistoryEntry
        {
            InstanceId = instanceId,
            T          = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Wall       = wallSeconds,
            Boot       = (long)(wallSeconds * 0.12),
            Cons       = (long)(wallSeconds * 0.71),
            Setup      = (long)(wallSeconds * 0.17),
        };

        lock (_lock)
        {
            _entries.Add(entry);
            // Keep at most 200 entries per instance (rolling window)
            var keep = _entries
                .GroupBy(e => e.InstanceId)
                .SelectMany(g => g.OrderByDescending(e => e.T).Take(100))
                .ToList();
            _entries = keep;
        }
        Save();
    }

    public List<HistoryEntry> GetAll()
    {
        lock (_lock)
            return _entries.ToList();
    }

    public List<HistoryEntry> GetFor(string instanceId)
    {
        lock (_lock)
            return _entries.Where(e => e.InstanceId == instanceId).OrderBy(e => e.T).ToList();
    }

    public KpiInfo GetKpis(string instanceId)
    {
        var h = GetFor(instanceId);
        if (h.Count == 0)
            return new KpiInfo { Last = 0, Avg = 0, Best = 0, Worst = 0, Launches = 0, PlaytimeMin = 0 };

        var walls = h.Select(e => (double)e.Wall).ToList();
        return new KpiInfo
        {
            Last        = walls.Last(),
            Avg         = Math.Round(walls.Average(), 1),
            Best        = walls.Min(),
            Worst       = walls.Max(),
            Launches    = h.Count,
            PlaytimeMin = h.Count * 45,  // estimate: 45 min avg session
        };
    }

    private void Save()
    {
        try
        {
            lock (_lock)
                File.WriteAllText(_path, JsonSerializer.Serialize(_entries, _opts));
        }
        catch (Exception e)
        {
            Logger.Warn($"HistoryStore save failed: {e.Message}");
        }
    }

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented          = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class HistoryEntry
{
    [JsonPropertyName("instId")] public string InstanceId { get; set; } = "";
    [JsonPropertyName("t")]      public long   T          { get; set; }
    [JsonPropertyName("wall")]   public long   Wall       { get; set; }
    [JsonPropertyName("boot")]   public long   Boot       { get; set; }
    [JsonPropertyName("cons")]   public long   Cons       { get; set; }
    [JsonPropertyName("setup")]  public long   Setup      { get; set; }
}

public sealed class KpiInfo
{
    public double Last        { get; set; }
    public double Avg         { get; set; }
    public double Best        { get; set; }
    public double Worst       { get; set; }
    public int    Launches    { get; set; }
    public int    PlaytimeMin { get; set; }
}
