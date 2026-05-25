package dev.vspeed;

import net.neoforged.fml.common.Mod;
import net.neoforged.fml.event.lifecycle.FMLLoadCompleteEvent;
import net.neoforged.bus.api.IEventBus;
import java.lang.management.ManagementFactory;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardOpenOption;
import java.time.LocalDateTime;
import java.time.format.DateTimeFormatter;

@Mod("vspeed")
public class VSpeedMod {
    private static final long START_TIME = ManagementFactory.getRuntimeMXBean().getStartTime();

    public VSpeedMod(IEventBus modEventBus) {
        System.out.println("[vspeed-Core] VSpeed Loader initialized.");
        modEventBus.addListener(this::onLoadComplete);
    }

    private void onLoadComplete(FMLLoadCompleteEvent event) {
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

        if ("true".equals(isProfiling)) {
            // Do NOT call System.exit() here.
            //
            // -XX:ArchiveClassesAtExit writes the Dynamic CDS archive during JVM shutdown
            // (inside halt0 native code).  On this configuration (HotSpot 21.0.10 + 481 mods
            // + Mixin-transformed classes) the JVM crashes with EXCEPTION_ACCESS_VIOLATION
            // inside jvm.dll before the archive is written.
            //
            // Instead, the Python script calls `jcmd <pid> VM.dynamicdump <archive>` while
            // the JVM is still alive — this writes the archive without going through the
            // crash-prone shutdown path — then force-kills the process via halt().
            System.out.println("[VSpeed] Profiling: game fully loaded. Waiting for jcmd dump from Python...");
        } else if ("true".equals(isBenchmark)) {
            // Fast halt: skip NeoForge shutdown hooks (they take minutes for 480+ mods).
            new Thread(() -> {
                try { Thread.sleep(500); } catch (Exception ignored) {}
                System.out.println("[VSpeed] Halting JVM...");
                Runtime.getRuntime().halt(0);
            }).start();
        }
    }
}
