using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VSpeedLauncher.Core;

/// <summary>
/// Windows-native "soft hibernation" for an arbitrary process tree.
///
/// <para>
/// <b>The trick:</b> on Windows, every committed memory page of a process is
/// fundamentally a candidate for paging to <c>pagefile.sys</c>.  What keeps a
/// page resident is (a) recent access and (b) the working-set policy of the
/// process.  If we trim the working set to zero AND keep the process from
/// running (so no new accesses dirty pages), Windows will quickly evict every
/// page to the page file.  The process now "lives in pagefile.sys": its
/// resident memory is just kernel structures (PEB, TEB, handle tables) — on
/// the order of 50–100 MB instead of 12 GB.
/// </para>
///
/// <para>
/// To resume, we call <c>NtResumeProcess</c>.  The threads start running and
/// the first instruction or heap access faults a page back from disk on
/// demand.  This gives a 1–5 second "warm-up" before the game feels snappy
/// again, with no further work from us.
/// </para>
///
/// <para>
/// All P/Invoke targets are <c>kernel32</c> and <c>ntdll</c> exports that have
/// been stable since Windows XP.  No Administrator rights required because
/// we operate on processes we ourselves spawned (and therefore own a handle
/// to with PROCESS_ALL_ACCESS).
/// </para>
/// </summary>
public static class ProcessHibernator
{
    // ── P/Invoke ─────────────────────────────────────────────────────────────

    /// <summary>Open a handle for suspending and trimming working set.</summary>
    private const uint PROCESS_SUSPEND_RESUME      = 0x0800;
    private const uint PROCESS_SET_QUOTA           = 0x0100;
    private const uint PROCESS_QUERY_INFORMATION   = 0x0400;
    private const uint PROCESS_VM_OPERATION        = 0x0008;
    private const uint PROCESS_TERMINATE           = 0x0001;

    private const uint OPEN_HANDLE_RIGHTS = PROCESS_SUSPEND_RESUME
                                          | PROCESS_SET_QUOTA
                                          | PROCESS_QUERY_INFORMATION
                                          | PROCESS_VM_OPERATION
                                          | PROCESS_TERMINATE;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess,
                                           [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
                                           int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    /// <summary>
    /// Atomic process-wide suspend.  Walks all threads in the target kernel-side,
    /// safer than enumerating with CreateToolhelp32Snapshot which can race a
    /// thread being created mid-iteration.  Undocumented but stable since XP.
    /// </summary>
    [DllImport("ntdll.dll", SetLastError = false)]
    private static extern uint NtSuspendProcess(nint hProcess);

    [DllImport("ntdll.dll", SetLastError = false)]
    private static extern uint NtResumeProcess(nint hProcess);

    /// <summary>
    /// The magic call.  Passing (SIZE_T)-1 for both min and max tells Windows
    /// to evict every page that isn't actively pinned.  Returns immediately
    /// — the eviction happens lazily as the memory manager has time.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(nint hProcess,
                                                        nint dwMinimumWorkingSetSize,
                                                        nint dwMaximumWorkingSetSize);

    /// <summary>Cheaper synonym for SetProcessWorkingSetSize(-1,-1).</summary>
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(nint hProcess);

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Put a process to sleep: suspend all its threads, then evict its pages.
    /// Idempotent — calling on an already-suspended process is harmless.
    /// </summary>
    /// <param name="pid">Target process id.  Must still be alive.</param>
    /// <returns>Resident bytes after hibernation, or -1 on failure.</returns>
    public static long Hibernate(int pid)
    {
        var hProc = OpenProcess(OPEN_HANDLE_RIGHTS, false, pid);
        if (hProc == 0)
        {
            Logger.Warn($"Hibernate: OpenProcess({pid}) failed ({Marshal.GetLastWin32Error()})");
            return -1;
        }
        try
        {
            uint rc = NtSuspendProcess(hProc);
            if (rc != 0)
            {
                Logger.Warn($"Hibernate: NtSuspendProcess returned 0x{rc:X}");
                return -1;
            }

            // First call evicts pages but the WS counter is still cached.
            // Loop a few times to drain — Windows occasionally needs nudging.
            for (int i = 0; i < 3; i++)
            {
                if (!EmptyWorkingSet(hProc))
                    Logger.Warn($"Hibernate: EmptyWorkingSet attempt {i} failed ({Marshal.GetLastWin32Error()})");
                Thread.Sleep(50);
            }
            // Belt-and-braces: explicit min=max=-1 in case EmptyWorkingSet was a no-op.
            SetProcessWorkingSetSize(hProc, -1, -1);

            long resident = ReadResidentBytes(pid);
            Logger.Info($"Hibernate({pid}): resident={resident / 1024 / 1024} MB");
            return resident;
        }
        finally
        {
            CloseHandle(hProc);
        }
    }

    /// <summary>Resume a hibernated process.  Threads start running again.</summary>
    public static bool Wake(int pid)
    {
        var hProc = OpenProcess(OPEN_HANDLE_RIGHTS, false, pid);
        if (hProc == 0)
        {
            Logger.Warn($"Wake: OpenProcess({pid}) failed ({Marshal.GetLastWin32Error()})");
            return false;
        }
        try
        {
            uint rc = NtResumeProcess(hProc);
            if (rc != 0)
            {
                Logger.Warn($"Wake: NtResumeProcess returned 0x{rc:X}");
                return false;
            }
            Logger.Info($"Wake({pid})");
            return true;
        }
        finally
        {
            CloseHandle(hProc);
        }
    }

    /// <summary>
    /// Force-kill an unresponsive JVM.  Used by Quit when the user wants the
    /// daemon gone immediately, not to wait for graceful shutdown hooks.
    /// </summary>
    public static void Terminate(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { /* already exited */ }
        catch (Win32Exception ex) { Logger.Warn($"Terminate({pid}): {ex.Message}"); }
    }

    private static long ReadResidentBytes(int pid)
    {
        try
        {
            // Process.WorkingSet64 is sampled at Refresh() time — re-read live.
            using var p = Process.GetProcessById(pid);
            p.Refresh();
            return p.WorkingSet64;
        }
        catch { return -1; }
    }
}
