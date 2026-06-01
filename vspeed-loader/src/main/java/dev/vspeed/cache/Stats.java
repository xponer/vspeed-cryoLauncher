package dev.vspeed.cache;

import java.io.BufferedWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardCopyOption;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

/**
 * Writes a small {@code vspeed-stats.json} in the game working directory with the
 * real data-load timings the launcher displays.  This is how the Cryo launcher
 * measures the actual speed-up: a "cold" (cache off / miss) run records the JAR
 * scan time, a "warm" run records the cache load time — the launcher compares them.
 *
 * <pre>
 * {
 *   "updatedAt": 1717000000000,
 *   "cacheEnabled": true,
 *   "types": {
 *     "recipe":      {"mode":"hit","ms":612,"entries":100645},
 *     "advancement": {"mode":"cold","ms":4180,"entries":48739}
 *   }
 * }
 * </pre>
 */
public final class Stats {

    private static final Path FILE = Paths.get("vspeed-stats.json");
    private static final Map<String, Entry> TYPES = new ConcurrentHashMap<>();
    private static volatile boolean cacheEnabled = true;

    private Stats() {}

    public static void setCacheEnabled(boolean v) { cacheEnabled = v; }

    /** mode = "hit" (served from cache) or "cold" (scanned JARs). */
    public static void record(String type, String mode, long ms, int entries) {
        try {
            TYPES.put(type, new Entry(mode, ms, entries));
            write();
        } catch (Exception e) {
            System.err.println("[VSpeed-Stats] write failed: " + e.getMessage());
        }
    }

    private static synchronized void write() throws Exception {
        StringBuilder sb = new StringBuilder(256);
        sb.append("{\n");
        sb.append("  \"updatedAt\": ").append(System.currentTimeMillis()).append(",\n");
        sb.append("  \"cacheEnabled\": ").append(cacheEnabled).append(",\n");
        sb.append("  \"types\": {\n");
        int i = 0, n = TYPES.size();
        for (Map.Entry<String, Entry> e : TYPES.entrySet()) {
            Entry v = e.getValue();
            sb.append("    \"").append(e.getKey()).append("\": {")
              .append("\"mode\":\"").append(v.mode).append("\",")
              .append("\"ms\":").append(v.ms).append(",")
              .append("\"entries\":").append(v.entries).append("}");
            sb.append(++i < n ? ",\n" : "\n");
        }
        sb.append("  }\n}\n");

        Path tmp = Paths.get("vspeed-stats.json.tmp");
        try (BufferedWriter w = Files.newBufferedWriter(tmp, StandardCharsets.UTF_8)) {
            w.write(sb.toString());
        }
        Files.move(tmp, FILE, StandardCopyOption.REPLACE_EXISTING);
    }

    private record Entry(String mode, long ms, int entries) {}
}
