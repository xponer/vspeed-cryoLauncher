using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VSpeedLauncher.Core;

/// <summary>
/// Reads the PrismLauncher game log (<c>minecraft/logs/latest.log</c>) and
/// parses lines into structured log entries for the Cryo UI.
/// </summary>
public static class LogReader
{
    // [28May2026 22:02:01.246] [main/INFO] [source/category]: message
    private static readonly Regex _logLine = new(
        @"^\[(?<ts>[^\]]+)\]\s*\[(?<thread>[^/]+)/(?<level>[A-Z]+)\]\s*\[(?<src>[^\]]+)\]:\s*(?<msg>.*)",
        RegexOptions.Compiled);

    // Fallback: lines that don't match the structured format
    private static readonly Regex _simpleLine = new(
        @"^\[(?<ts>\d+:\d+:\d+)\]\s*\[(?<thread>[^\]]+)\]:\s*(?<msg>.*)",
        RegexOptions.Compiled);

    public static List<LogEntry> Read(string instanceId, string prismDataDir, int maxLines = 3000)
    {
        var logPath = Path.Combine(prismDataDir, "instances", instanceId,
                                   "minecraft", "logs", "latest.log");
        if (!File.Exists(logPath))
        {
            Logger.Warn($"Log not found: {logPath}");
            return new();
        }

        var entries = new List<LogEntry>();
        int id = 0;
        long epochOffset = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 90_000; // ~90s ago

        try
        {
            // FileShare.ReadWrite so we can read while the game is writing.
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            // UTF-8 with BOM detection; invalid bytes replaced with U+FFFD (never throws).
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
            using var sr = new StreamReader(fs, utf8, detectEncodingFromByteOrderMarks: true);

            // Read all lines — we'll take the last maxLines.
            var lines = new List<string>(capacity: 8000);
            string? line;
            while ((line = sr.ReadLine()) != null) lines.Add(line);

            int start = Math.Max(0, lines.Count - maxLines);
            for (int i = start; i < lines.Count; )
            {
                var entry = Parse(lines[i], ref epochOffset, id++);
                i++;

                // Collect stack trace / continuation lines that follow the entry.
                var stackLines = new List<string>();
                while (i < lines.Count && IsStackContinuation(lines[i]))
                {
                    stackLines.Add(lines[i]);
                    i++;
                }
                if (stackLines.Count > 0) entry.Stack = stackLines.ToArray();

                entries.Add(entry);
            }
        }
        catch (Exception e)
        {
            Logger.Warn($"LogReader({instanceId}): {e.Message}");
        }

        return entries;
    }

    private static LogEntry Parse(string raw, ref long lastEpoch, int id)
    {
        var m = _logLine.Match(raw);
        if (m.Success)
        {
            // Approximate epoch from time-of-day + today's date
            var epoch = ApproxEpoch(m.Groups["ts"].Value, ref lastEpoch);
            var src   = m.Groups["src"].Value;
            // Shorten noisy source path like "net.neoforged.fml.loading.ModDiscoverer/SCAN"
            var shortSrc = src.Contains('/') ? src.Split('/')[^1]
                         : src.Contains('.') ? src.Split('.')[^1]
                         : src;

            return new LogEntry
            {
                Id     = id,
                T      = epoch,
                Level  = m.Groups["level"].Value,
                Src    = shortSrc,
                Thread = m.Groups["thread"].Value,
                Msg    = m.Groups["msg"].Value,
            };
        }

        var m2 = _simpleLine.Match(raw);
        return new LogEntry
        {
            Id     = id,
            T      = lastEpoch += 50,
            Level  = "INFO",
            Src    = "Minecraft",
            Thread = m2.Success ? m2.Groups["thread"].Value : "main",
            Msg    = m2.Success ? m2.Groups["msg"].Value : raw,
        };
    }

    /// <summary>Returns true if the line is a stack-trace or log-continuation line
    /// that belongs to the preceding structured log entry.</summary>
    private static bool IsStackContinuation(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        // Java stack frames: "\tat com.example.Foo.bar(Foo.java:42)"
        if (line.StartsWith('\t')) return true;
        // "at " prefix (some loggers omit the leading tab)
        if (line.StartsWith("at ", StringComparison.Ordinal)) return true;
        // "Caused by:" / "Suppressed:" continuation blocks
        if (line.StartsWith("Caused by:", StringComparison.Ordinal)) return true;
        if (line.StartsWith("Suppressed:", StringComparison.Ordinal)) return true;
        // Abbreviated frame count: "\t... 14 more" or just "... 14 more"
        if (line.StartsWith("...")) return true;
        return false;
    }

    private static long ApproxEpoch(string ts, ref long lastEpoch)
    {
        // ts might be "28May2026 22:02:01.246" or "22:02:01.246"
        var today = DateTime.Today;
        var parts = ts.Split(' ');
        var timePart = parts.Length > 1 ? parts[1] : parts[0];

        if (TimeSpan.TryParse(timePart.Replace('.', ':'), out var t))
        {
            var epoch = new DateTimeOffset(today + t, TimeSpan.Zero).ToUnixTimeMilliseconds();
            lastEpoch = epoch;
            return epoch;
        }
        return lastEpoch += 50;
    }
}

public sealed class LogEntry
{
    public int      Id     { get; set; }
    public long     T      { get; set; }
    public string   Level  { get; set; } = "INFO";
    public string   Src    { get; set; } = "";
    public string   Thread { get; set; } = "";
    public string   Msg    { get; set; } = "";
    /// <summary>Stack-trace / continuation lines that follow this entry, or null.</summary>
    public string[]? Stack { get; set; }
}
