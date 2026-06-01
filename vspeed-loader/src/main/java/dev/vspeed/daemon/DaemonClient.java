package dev.vspeed.daemon;

import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.concurrent.TimeUnit;

/**
 * IPC client that notifies the VSpeed tray daemon when the game is loaded.
 *
 * <h2>Design constraint: never block a Minecraft worker thread</h2>
 * <p>The previous incarnation of this class did a request-response handshake:
 * write {@code READY ...}, then read back {@code ACK}.  That deadlocked on a
 * production launch:</p>
 * <ul>
 *   <li>{@code FMLLoadCompleteEvent} fires on a {@code Worker-ResourceReload-N}
 *       thread — these are pool workers Minecraft NEEDS for the rest of the
 *       resource reload pipeline.</li>
 *   <li>The {@code RandomAccessFile.read()} call blocked indefinitely (the
 *       daemon's pipe-write hadn't reached us yet, or the underlying
 *       NamedPipeServerStream took its time).</li>
 *   <li>That worker never returned to the pool → resource reload stalled →
 *       the title screen never appeared → user saw a permanent Mojang splash.</li>
 * </ul>
 *
 * <h2>Current design: fire-and-forget on a daemon thread</h2>
 * <ol>
 *   <li>{@link #notifyReady(long)} returns IMMEDIATELY (synchronous-looking
 *       but no blocking).</li>
 *   <li>The actual pipe write happens on a daemon thread named
 *       {@code vspeed-daemon-notifier}.  Because it's a daemon, it never
 *       prevents JVM shutdown.</li>
 *   <li>The thread opens the pipe with {@link FileOutputStream}, writes the
 *       READY line, flushes, and closes.  No read, no protocol round-trip.</li>
 *   <li>If the pipe doesn't exist (no daemon listening) the open throws
 *       {@code FileNotFoundException} which we swallow.</li>
 *   <li>A 5-second join timeout on the notifier thread ensures the worker
 *       eventually moves on even in the pathological case where the OS
 *       wedges on the open call.</li>
 * </ol>
 */
public final class DaemonClient {

    /** Pipe the C# tray app listens on (cf. launcher/.../PipeServer.cs). */
    public static final String PIPE_PATH = "\\\\.\\pipe\\vspeed-daemon";

    private DaemonClient() {}

    /**
     * Fire-and-forget READY notification.  Returns within ~5 s in the worst
     * case (pipe wedged), instantly in the common case.
     */
    public static void notifyReady(long loadSeconds) {
        String pid      = String.valueOf(ProcessHandle.current().pid());
        String instance = resolveInstanceLabel();
        String hwnd     = System.getProperty("vspeed.hwnd", "0");

        // Use \n (not %n) — \r\n confuses some line readers and we want
        // exactly one terminator on the wire.
        String line = String.format(
            "READY pid=%s loadSeconds=%d instance=%s hwnd=%s\n",
            pid, loadSeconds, instance, hwnd);

        System.out.println("[vspeed-Daemon] Will notify " + PIPE_PATH);
        System.out.println("[vspeed-Daemon]   " + line.trim());
        System.out.flush();

        Thread notifier = new Thread(() -> writeToPipe(line),
                                     "vspeed-daemon-notifier");
        notifier.setDaemon(true);
        notifier.start();

        // Wait up to 5 s for the write to finish.  If it does, we get a tidy
        // log line ("Daemon notified" or "No daemon listening").  If not, we
        // log a timeout and return — the worker thread that called us is
        // freed regardless, so resource reload continues normally.
        try {
            notifier.join(TimeUnit.SECONDS.toMillis(5));
            if (notifier.isAlive()) {
                System.out.println("[vspeed-Daemon] Notifier still running after 5 s — "
                    + "returning anyway so resource reload can proceed.");
            }
        } catch (InterruptedException ie) {
            Thread.currentThread().interrupt();
        }
        System.out.flush();
    }

    /** Body of the notifier thread.  Never throws to its caller. */
    private static void writeToPipe(String line) {
        try (FileOutputStream out = new FileOutputStream(PIPE_PATH)) {
            // FileOutputStream on \\.\pipe\X opens via CreateFileW.  Writes
            // go through WriteFile and are buffered by the OS, not by us.
            out.write(line.getBytes(StandardCharsets.UTF_8));
            out.flush();
            // try-with-resources will FlushFileBuffers + CloseHandle here,
            // which is the daemon's signal that the message is complete.
            System.out.println("[vspeed-Daemon] Daemon notified — hibernation will follow.");
        } catch (java.io.FileNotFoundException fnf) {
            System.out.println("[vspeed-Daemon] No daemon listening on " + PIPE_PATH
                + " — running normally without hibernation.");
        } catch (Exception e) {
            System.out.println("[vspeed-Daemon] Pipe error ("
                + e.getClass().getSimpleName() + "): " + e.getMessage());
        }
        System.out.flush();
    }

    /**
     * Best-effort instance-label resolution so multi-instance daemons can
     * tell which JVM is reporting in.  Priority:
     * <ol>
     *   <li>{@code -Dvspeed.instance=…} system property.</li>
     *   <li>Walk up from cwd looking for {@code instance.cfg} — Prism's
     *       instance folder marker.</li>
     *   <li>Working directory name.</li>
     *   <li>Literal "Minecraft".</li>
     * </ol>
     */
    private static String resolveInstanceLabel() {
        String prop = System.getProperty("vspeed.instance");
        if (prop != null && !prop.isEmpty()) return prop;

        try {
            Path p = Paths.get("").toAbsolutePath();
            for (int i = 0; i < 5 && p != null; i++) {
                if (Files.exists(p.resolve("instance.cfg"))) return p.getFileName().toString();
                p = p.getParent();
            }
            Path cwd = Paths.get("").toAbsolutePath();
            if (cwd.getFileName() != null) return cwd.getFileName().toString();
        } catch (Exception ignored) {}
        return "Minecraft";
    }
}
