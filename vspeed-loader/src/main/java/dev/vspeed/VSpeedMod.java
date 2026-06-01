package dev.vspeed;

import dev.vspeed.daemon.DaemonClient;
import net.neoforged.fml.common.Mod;
import net.neoforged.fml.event.lifecycle.FMLCommonSetupEvent;
import net.neoforged.fml.event.lifecycle.FMLClientSetupEvent;
import net.neoforged.fml.event.lifecycle.FMLLoadCompleteEvent;
import net.neoforged.bus.api.IEventBus;
import net.neoforged.bus.api.EventPriority;
import java.lang.management.ManagementFactory;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.nio.file.StandardOpenOption;
import java.util.concurrent.ConcurrentHashMap;

@Mod("vspeed")
public class VSpeedMod {
    private static final long START_TIME = ManagementFactory.getRuntimeMXBean().getStartTime();

    /** Timestamps keyed by phase name, populated by HIGHEST-priority listeners. */
    private static final ConcurrentHashMap<String, Long> PHASE_START = new ConcurrentHashMap<>();

    public VSpeedMod(IEventBus modEventBus) {
        System.out.println("[vspeed-Core] VSpeed Loader initialized.");

        // ── Cross-event phase timing ──────────────────────────────────────────
        // NeoForge has a synchronisation BARRIER between lifecycle phases:
        // FMLClientSetupEvent cannot start for ANY mod until ALL mods have
        // finished FMLCommonSetupEvent.  Therefore:
        //
        //   t(FMLClientSetupEvent.HIGHEST) - t(FMLCommonSetupEvent.HIGHEST)
        //   = total wall-clock duration of the Common-Setup phase
        //
        // The lambda variants of addListener() didn't fire — NeoForge's bus
        // does generic type erasure resolution against the consumer's *method*,
        // not against a captured lambda type.  Method references work because
        // the bus can introspect the target method's parameter type.

        modEventBus.addListener(EventPriority.HIGHEST, true, VSpeedMod::onCommonSetupStart);
        modEventBus.addListener(EventPriority.HIGHEST, true, VSpeedMod::onClientSetupStart);
        modEventBus.addListener(this::onLoadComplete);
    }

    private static void onCommonSetupStart(FMLCommonSetupEvent e) {
        PHASE_START.put("common", System.nanoTime());
        System.out.println("[vspeed-Metrics] PHASE_COMMON_SETUP_START");
        System.out.flush();
    }

    private static void onClientSetupStart(FMLClientSetupEvent e) {
        logPhase("COMMON_SETUP", "common");
        PHASE_START.put("client", System.nanoTime());
        System.out.println("[vspeed-Metrics] PHASE_CLIENT_SETUP_START");
        System.out.flush();
    }

    /**
     * Emits the wall-clock duration of a NeoForge lifecycle phase.
     * The measurement is cross-event: timer starts at phase N's HIGHEST listener
     * and stops at phase N+1's HIGHEST listener (which can only fire after N's barrier).
     */
    private static void logPhase(String label, String key) {
        Long t = PHASE_START.get(key);
        if (t == null) return;
        long ms = (System.nanoTime() - t) / 1_000_000L;
        System.out.printf("[vspeed-Metrics] PHASE_%-20s %5d ms%n", label + ":", ms);
        System.out.flush();
    }

    private void onLoadComplete(FMLLoadCompleteEvent event) {
        // Reaching onLoadComplete means the Client-Setup barrier has been crossed.
        logPhase("CLIENT_SETUP", "client");

        long duration = (System.currentTimeMillis() - START_TIME) / 1000;
        String isProfiling = System.getProperty("vspeed.cds.profiling", "false");
        String isBenchmark = System.getProperty("vspeed.benchmark", "false");

        // Получаем хиты напрямую из System Properties (установленных агентом)
        String agentHits = System.getProperty("vspeed.internal.hits", "0");

        System.out.println("========================================");
        System.out.println("[vspeed-Metrics] STARTUP_TIME_SECONDS: " + duration);
        System.out.println("[vspeed-Metrics] AGENT_CACHE_HITS: " + agentHits);
        System.out.println("[VSpeed] FINAL_READY_SIGNAL");
        System.out.println("========================================");
        System.out.flush();

        try {
            Files.writeString(Paths.get("vspeed_signal.done"), String.valueOf(duration),
                StandardOpenOption.CREATE, StandardOpenOption.TRUNCATE_EXISTING);
        } catch (Exception e) {
            System.err.println("[vspeed-Metrics] Error writing signal: " + e.getMessage());
        }

        // ── Always notify the daemon ─────────────────────────────────────────
        // No -Dvspeed.daemon=true gate.  Rationale:
        //   • PrismLauncher's launch pipeline often drops JAVA_TOOL_OPTIONS
        //     between its own JVM and the spawned game JVM (it builds a fresh
        //     argv and only forwards a curated env-var allowlist).
        //   • notifyReady() is harmless when no daemon is listening — opening
        //     the named pipe throws FileNotFoundException which we swallow,
        //     log, and move on.  Cost: one I/O syscall.
        // So we make the pipe itself the source of truth: daemon present →
        // hibernate path runs; daemon absent → game runs normally.
        if ("true".equals(isProfiling)) {
            System.out.println("[VSpeed] Profiling: game fully loaded. Waiting for jcmd dump from Python...");
        } else if ("true".equals(isBenchmark)) {
            // Benchmark mode: measure-only, fast-halt skipping NeoForge shutdown.
            new Thread(() -> {
                try { Thread.sleep(500); } catch (Exception ignored) {}
                System.out.println("[VSpeed] Halting JVM...");
                Runtime.getRuntime().halt(0);
            }).start();
        } else {
            // Production mode: try the daemon.  Best-effort, never blocks.
            DaemonClient.notifyReady(duration);
        }
    }
}
