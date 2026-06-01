using System.Runtime.InteropServices;
using System.Text;

namespace VSpeedLauncher.Core;

/// <summary>
/// Find and hide/show the game window owned by a given JVM.
///
/// <para>
/// The mod side could in principle pass its GLFW window handle to the daemon
/// (we even reserve a {@code hwnd=} field in the pipe protocol), but plumbing
/// that through LWJGL's reflective GLFW API is fragile.  Instead we use the
/// classic Win32 approach: enumerate top-level windows, filter to those
/// owned by the target PID, pick the largest visible one.  That heuristic
/// reliably finds the LWJGL window (which is the only large client-area
/// window Minecraft creates) and skips any stray invisible message-pump
/// windows the JVM might own.
/// </para>
/// </summary>
public static class WindowManager
{
    // ── P/Invoke ─────────────────────────────────────────────────────────────

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextW(nint hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // ShowWindow constants
    private const int SW_HIDE     = 0;
    private const int SW_SHOW     = 5;
    private const int SW_RESTORE  = 9;
    private const int SW_SHOWNA   = 8;     // show without activating (don't steal focus)

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Locate the game window for the given PID.  Returns 0 if no suitable
    /// window exists yet (e.g. the JVM hasn't created the GLFW window).
    ///
    /// Selection heuristic: among all visible top-level windows owned by
    /// the PID, pick the one with the biggest client area.  Minecraft's
    /// LWJGL window is always the largest by a wide margin.
    /// </summary>
    public static nint FindGameWindow(int pid)
    {
        nint best = 0;
        long bestArea = 0;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var winPid);
            if ((int)winPid != pid) return true;       // not ours, keep enumerating
            if (!IsWindowVisible(hWnd))  return true;  // skip hidden helpers

            if (!GetWindowRect(hWnd, out var rc)) return true;
            long area = (long)(rc.Right - rc.Left) * (rc.Bottom - rc.Top);
            if (area < 100 * 100) return true;          // skip tiny tooltip windows

            if (area > bestArea)
            {
                bestArea = area;
                best = hWnd;
            }
            return true;
        }, 0);

        return best;
    }

    /// <summary>Read a window's caption.  Used for diagnostics only.</summary>
    public static string GetTitle(nint hWnd)
    {
        if (hWnd == 0) return "";
        int len = GetWindowTextLengthW(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowTextW(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Hide the window.  Uses <c>ShowWindowAsync</c> rather than
    /// <c>ShowWindow</c> because we typically call this immediately before
    /// suspending the process — once threads stop running, a synchronous
    /// <c>ShowWindow</c> would never get its WM_SHOWWINDOW dispatched.
    /// <c>ShowWindowAsync</c> queues the message and returns immediately,
    /// letting the window manager process the hide before the threads die.
    /// </summary>
    public static void Hide(nint hWnd)
    {
        if (hWnd == 0) return;
        ShowWindowAsync(hWnd, SW_HIDE);
        Logger.Info($"WindowManager: hid HWND 0x{hWnd:X}");
    }

    /// <summary>
    /// Restore + bring to foreground.  Order matters: SW_SHOW first to make
    /// the HWND eligible for focus, then SetForegroundWindow to actually
    /// move it to the top of the Z-order.
    /// </summary>
    public static void ShowAndFocus(nint hWnd)
    {
        if (hWnd == 0) return;
        ShowWindowAsync(hWnd, SW_SHOW);
        BringWindowToTop(hWnd);
        SetForegroundWindow(hWnd);
        Logger.Info($"WindowManager: showed HWND 0x{hWnd:X} ({GetTitle(hWnd)})");
    }
}
