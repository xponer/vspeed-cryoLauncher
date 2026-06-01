using System.IO;
using System.Text;

namespace VSpeedLauncher.Core;

/// <summary>
/// Cheap append-only logger that writes to a single rotating file.  We don't
/// pull in Serilog because the launcher is a 30 MB single-file exe and we
/// care about size.  Format is line-based so users can <c>tail -F</c> it for
/// live debug while they're testing.
///
/// <para>
/// <b>FileShare matters.</b>  The original implementation used
/// <c>new StreamWriter(path, append: true)</c> which under the hood opens the
/// file with <c>FileShare.Read</c> — fine for one writer, but it explodes
/// when a stale instance of the launcher still holds the handle (we crashed
/// once in production exactly because of that).  We now open the underlying
/// <see cref="FileStream"/> explicitly with <c>FileShare.ReadWrite | Delete</c>
/// so:
///   • notepad / <c>tail -f</c> can read the log live without locking us out, and
///   • multiple launcher instances racing at startup don't murder each other —
///     the single-instance mutex (see <c>App.OnStartup</c>) is the proper guard,
///     but defence in depth doesn't hurt.
/// </para>
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static StreamWriter? _writer;

    public static void Init(string path)
    {
        // Rotate if larger than 5 MB — keep one .old file as backup.
        var fi = new FileInfo(path);
        if (fi.Exists && fi.Length > 5 * 1024 * 1024)
        {
            try
            {
                var old = path + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
            catch { /* in-use — keep growing rather than crash */ }
        }

        // Tolerate other readers/writers (tail -f, notepad, stale launcher).
        var fs = new FileStream(path,
            FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
        Info($"--- Log opened (pid {Environment.ProcessId}) ---");
    }

    public  static void Info (string msg) => Write("INFO ", msg);
    public  static void Warn (string msg) => Write("WARN ", msg);
    public  static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        lock (_lock)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {msg}";
            _writer?.WriteLine(line);
            System.Diagnostics.Debug.WriteLine(line);
        }
    }
}
