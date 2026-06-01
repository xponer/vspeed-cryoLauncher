using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace VSpeedLauncher.Core;

/// <summary>One entry in a Minecraft <c>servers.dat</c> file.</summary>
public sealed class ServerEntry
{
    public string Name { get; set; } = "";
    public string Ip   { get; set; } = "";
}

/// <summary>Live status from a Server List Ping.</summary>
public sealed class ServerStatus
{
    public bool    Online    { get; set; }
    public string  Motd      { get; set; } = "";
    public int     Players   { get; set; }
    public int     MaxPlayers{ get; set; }
    public string  Version   { get; set; } = "";
    public long    LatencyMs { get; set; }
    public string? Error     { get; set; }
}

/// <summary>
/// Reads/writes Minecraft's <c>servers.dat</c> (uncompressed NBT) and pings servers
/// using the modern Server List Ping protocol — no third-party libraries.
/// </summary>
public static class ServerListClient
{
    // ── servers.dat (NBT) ───────────────────────────────────────────────────────

    public static List<ServerEntry> ReadServersDat(string path)
    {
        var list = new List<ServerEntry>();
        if (!File.Exists(path)) return list;
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BigEndianReader(fs);
            // Root compound: type(1) + name(string) then payload
            if (br.ReadByte() != 10) return list;       // TAG_Compound
            br.ReadString();                            // root name (usually "")
            ReadCompoundInto(br, list);
        }
        catch (Exception e) { Logger.Warn($"ReadServersDat: {e.Message}"); }
        return list;
    }

    private static void ReadCompoundInto(BigEndianReader br, List<ServerEntry> servers)
    {
        while (true)
        {
            var type = br.ReadByte();
            if (type == 0) break;                        // TAG_End
            var name = br.ReadString();
            if (type == 9 && name == "servers")          // TAG_List "servers"
            {
                var itemType = br.ReadByte();            // should be 10 (compound)
                var count    = br.ReadInt();
                for (int i = 0; i < count; i++)
                {
                    if (itemType == 10) servers.Add(ReadServerCompound(br));
                    else SkipPayload(br, itemType);
                }
            }
            else SkipPayload(br, type);
        }
    }

    private static ServerEntry ReadServerCompound(BigEndianReader br)
    {
        var e = new ServerEntry();
        while (true)
        {
            var type = br.ReadByte();
            if (type == 0) break;
            var name = br.ReadString();
            if (type == 8 && name == "name")      e.Name = br.ReadString();
            else if (type == 8 && name == "ip")   e.Ip   = br.ReadString();
            else SkipPayload(br, type);
        }
        return e;
    }

    private static void SkipPayload(BigEndianReader br, byte type)
    {
        switch (type)
        {
            case 1: br.ReadByte(); break;
            case 2: br.ReadBytes(2); break;
            case 3: br.ReadBytes(4); break;
            case 4: br.ReadBytes(8); break;
            case 5: br.ReadBytes(4); break;
            case 6: br.ReadBytes(8); break;
            case 7: { var n = br.ReadInt(); br.ReadBytes(n); break; }
            case 8: br.ReadString(); break;
            case 9: { var it = br.ReadByte(); var n = br.ReadInt(); for (int i = 0; i < n; i++) SkipPayload(br, it); break; }
            case 10: { while (true) { var t = br.ReadByte(); if (t == 0) break; br.ReadString(); SkipPayload(br, t); } break; }
            case 11: { var n = br.ReadInt(); br.ReadBytes(n * 4); break; }
            case 12: { var n = br.ReadInt(); br.ReadBytes(n * 8); break; }
            default: throw new Exception($"Unknown NBT tag {type}");
        }
    }

    /// <summary>Writes a minimal <c>servers.dat</c> (backs up the existing file first).</summary>
    public static void WriteServersDat(string path, List<ServerEntry> servers)
    {
        try { if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true); } catch { }

        using var fs = File.Create(path);
        using var bw = new BigEndianWriter(fs);
        bw.WriteByte(10);                  // root TAG_Compound
        bw.WriteString("");                // root name
        bw.WriteByte(9);                   // TAG_List
        bw.WriteString("servers");
        bw.WriteByte(10);                  // list items are compounds
        bw.WriteInt(servers.Count);
        foreach (var s in servers)
        {
            bw.WriteByte(8); bw.WriteString("ip");   bw.WriteString(s.Ip ?? "");
            bw.WriteByte(8); bw.WriteString("name"); bw.WriteString(s.Name ?? "");
            bw.WriteByte(0);               // TAG_End for this server compound
        }
        bw.WriteByte(0);                   // TAG_End for root
    }

    // ── Server List Ping ──────────────────────────────────────────────────────

    public static async Task<ServerStatus> PingAsync(string address, int timeoutMs = 4000)
    {
        var (host, port) = SplitHostPort(address);
        var status = new ServerStatus();
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) != connect)
            { status.Error = "timeout"; return status; }
            await connect;   // surface connect exceptions

            using var stream = client.GetStream();
            stream.ReadTimeout = timeoutMs; stream.WriteTimeout = timeoutMs;

            // Handshake: protocol=-1, host, port, nextState=1 (status)
            using (var hs = new MemoryStream())
            {
                WriteVarInt(hs, 0x00);
                WriteVarInt(hs, -1);
                WriteStr(hs, host);
                hs.WriteByte((byte)(port >> 8)); hs.WriteByte((byte)(port & 0xFF));
                WriteVarInt(hs, 1);
                WritePacket(stream, hs.ToArray());
            }
            // Status request (empty)
            using (var rq = new MemoryStream()) { WriteVarInt(rq, 0x00); WritePacket(stream, rq.ToArray()); }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Read status response
            ReadVarInt(stream);                  // packet length (ignored)
            ReadVarInt(stream);                  // packet id (0x00)
            var jsonLen = ReadVarInt(stream);
            var buf = ReadExact(stream, jsonLen);
            sw.Stop();
            status.LatencyMs = sw.ElapsedMilliseconds;

            var json = JsonNode.Parse(Encoding.UTF8.GetString(buf));
            status.Online     = true;
            status.Players    = json?["players"]?["online"]?.GetValue<int>() ?? 0;
            status.MaxPlayers = json?["players"]?["max"]?.GetValue<int>() ?? 0;
            status.Version    = json?["version"]?["name"]?.GetValue<string>() ?? "";
            status.Motd       = ExtractMotd(json?["description"]);
        }
        catch (Exception e) { status.Error = e.Message; status.Online = false; }
        return status;
    }

    private static string ExtractMotd(JsonNode? desc)
    {
        if (desc == null) return "";
        if (desc is JsonValue v) return v.ToString();
        var sb = new StringBuilder();
        sb.Append(desc["text"]?.GetValue<string>() ?? "");
        if (desc["extra"] is JsonArray extra)
            foreach (var part in extra)
                sb.Append(part?["text"]?.GetValue<string>() ?? (part is JsonValue pv ? pv.ToString() : ""));
        return sb.ToString().Trim();
    }

    private static (string host, int port) SplitHostPort(string address)
    {
        address = (address ?? "").Trim();
        var idx = address.LastIndexOf(':');
        if (idx > 0 && int.TryParse(address[(idx + 1)..], out var p))
            return (address[..idx], p);
        return (address, 25565);
    }

    // ── packet helpers ────────────────────────────────────────────────────────

    private static void WritePacket(Stream s, byte[] data)
    {
        using var ms = new MemoryStream();
        WriteVarInt(ms, data.Length);
        ms.Write(data, 0, data.Length);
        var b = ms.ToArray();
        s.Write(b, 0, b.Length);
    }

    private static void WriteStr(Stream s, string str)
    {
        var bytes = Encoding.UTF8.GetBytes(str);
        WriteVarInt(s, bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void WriteVarInt(Stream s, int value)
    {
        uint v = (uint)value;
        do { var b = (byte)(v & 0x7F); v >>= 7; if (v != 0) b |= 0x80; s.WriteByte(b); } while (v != 0);
    }

    private static int ReadVarInt(Stream s)
    {
        int value = 0, shift = 0; byte b;
        do
        {
            int read = s.ReadByte();
            if (read < 0) throw new EndOfStreamException();
            b = (byte)read;
            value |= (b & 0x7F) << shift;
            shift += 7;
            if (shift > 35) throw new Exception("VarInt too big");
        } while ((b & 0x80) != 0);
        return value;
    }

    private static byte[] ReadExact(Stream s, int n)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = s.Read(buf, off, n - off);
            if (r <= 0) throw new EndOfStreamException();
            off += r;
        }
        return buf;
    }

    // ── big-endian NBT primitives ───────────────────────────────────────────────

    private sealed class BigEndianReader : IDisposable
    {
        private readonly Stream _s;
        public BigEndianReader(Stream s) => _s = s;
        public byte ReadByte() { int b = _s.ReadByte(); if (b < 0) throw new EndOfStreamException(); return (byte)b; }
        public byte[] ReadBytes(int n) { var b = new byte[n]; int o = 0; while (o < n) { int r = _s.Read(b, o, n - o); if (r <= 0) throw new EndOfStreamException(); o += r; } return b; }
        public int ReadInt() { var b = ReadBytes(4); return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]; }
        public ushort ReadUShort() { var b = ReadBytes(2); return (ushort)((b[0] << 8) | b[1]); }
        public string ReadString() { var len = ReadUShort(); return Encoding.UTF8.GetString(ReadBytes(len)); }
        public void Dispose() { }
    }

    private sealed class BigEndianWriter : IDisposable
    {
        private readonly Stream _s;
        public BigEndianWriter(Stream s) => _s = s;
        public void WriteByte(byte b) => _s.WriteByte(b);
        public void WriteInt(int v) { _s.WriteByte((byte)(v >> 24)); _s.WriteByte((byte)(v >> 16)); _s.WriteByte((byte)(v >> 8)); _s.WriteByte((byte)v); }
        public void WriteUShort(ushort v) { _s.WriteByte((byte)(v >> 8)); _s.WriteByte((byte)v); }
        public void WriteString(string str) { var b = Encoding.UTF8.GetBytes(str ?? ""); WriteUShort((ushort)b.Length); _s.Write(b, 0, b.Length); }
        public void Dispose() { }
    }
}
