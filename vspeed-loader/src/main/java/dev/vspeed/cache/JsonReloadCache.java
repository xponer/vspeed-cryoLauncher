package dev.vspeed.cache;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import net.minecraft.resources.ResourceLocation;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Optional;
import java.util.zip.GZIPInputStream;
import java.util.zip.GZIPOutputStream;

/**
 * Persistent cache for the raw {@code Map<ResourceLocation, JsonElement>} produced
 * by {@link net.minecraft.server.packs.resources.SimpleJsonResourceReloadListener#prepare
 * SimpleJsonResourceReloadListener.prepare()} — i.e. the output of scanning every
 * mod JAR for a given data {@code type} ({@code recipe}, {@code loot_table}, …) and
 * Gson-parsing each file.  Stored as a gzipped NDJSON file on disk.
 *
 * <h2>Why this is fast</h2>
 * <ul>
 *   <li>Serialization: {@link JsonElement#toString()} — pure in-memory Gson output.
 *       No Minecraft codec, no registry ops, no reflection.</li>
 *   <li>Deserialization: a single {@link GZIPInputStream} read +
 *       {@link JsonParser#parseString} per line.  Completely avoids opening any
 *       mod JAR.</li>
 *   <li>Key comparison: only a stat walk over {@code mods/} and {@code scripts/}
 *       (no file content reads) — see {@link CacheKey}.</li>
 * </ul>
 *
 * <h2>Correctness</h2>
 * Only the <em>raw</em> JSON is cached — the per-boot {@code apply()} pass still
 * re-evaluates NeoForge load conditions and runs the codec every time, so a cache
 * hit produces byte-for-byte the same in-memory state as a cold scan.
 *
 * <h2>Format</h2>
 * <pre>
 * VSPEED-JSON-v1                       ← version header (line 1)
 * {"k":"ns:path","v":{...}}            ← one compact-JSON line per entry
 * ...
 * </pre>
 */
public final class JsonReloadCache {

    private static final String FORMAT = "VSPEED-JSON-v1";
    private static final CacheStore STORE = new CacheStore();

    /** Computed once per JVM session in {@link #getKey()}. */
    private static volatile String sessionKey;

    private JsonReloadCache() {}

    // ── Public API ──────────────────────────────────────────────────────────────

    /**
     * Try to load a cached map for {@code type}.  Returns empty on miss or any
     * error.  Safe to call on any thread.
     */
    /** Vanilla / no-optimization mode: launcher passes -Dvspeed.cache.enabled=false. */
    public static boolean cacheEnabled() {
        return !"false".equalsIgnoreCase(System.getProperty("vspeed.cache.enabled", "true"));
    }

    public static Optional<Map<ResourceLocation, JsonElement>> tryLoad(String type) {
        if (!cacheEnabled()) {
            Stats.setCacheEnabled(false);
            System.out.println("[VSpeed-Cache] DISABLED (vspeed.cache.enabled=false) — cold scan for " + type);
            return Optional.empty();
        }
        Stats.setCacheEnabled(true);
        String key = getKey();
        if ("error".equals(key) || !STORE.has(type, key)) {
            System.out.println("[VSpeed-Cache] MISS " + type + " (key=" + key + ")");
            return Optional.empty();
        }

        long t0 = System.nanoTime();
        try {
            Map<ResourceLocation, JsonElement> map = deserialize(STORE.readPath(type, key));
            long ms = (System.nanoTime() - t0) / 1_000_000;
            System.out.printf("[VSpeed-Cache] HIT %s — %d entries loaded in %d ms%n",
                              type, map.size(), ms);
            Stats.record(type, "hit", ms, map.size());
            return Optional.of(map);
        } catch (Exception e) {
            System.err.println("[VSpeed-Cache] Load failed for " + type + " ("
                               + e.getClass().getSimpleName() + ": " + e.getMessage()
                               + ") — will rebuild");
            try { Files.deleteIfExists(STORE.readPath(type, key)); } catch (IOException ignored) {}
            return Optional.empty();
        }
    }

    /**
     * Persist {@code data} for {@code type} if no cache exists for the current key.
     * No-op if already cached (avoids overwriting on hot-reload).
     */
    public static void trySave(String type, Map<ResourceLocation, JsonElement> data) {
        if (!cacheEnabled()) return;   // vanilla mode: never build the cache
        String key = getKey();
        if ("error".equals(key) || STORE.has(type, key)) return;

        long t0 = System.nanoTime();
        System.out.printf("[VSpeed-Cache] Saving %s: %d entries (key=%s)...%n",
                          type, data.size(), key);
        try {
            Path tmp = STORE.writeTmp(type, key);
            serialize(data, tmp);
            STORE.commit(type, key);
            System.out.printf("[VSpeed-Cache] Saved %s in %d ms%n",
                              type, (System.nanoTime() - t0) / 1_000_000);
        } catch (Exception e) {
            System.err.println("[VSpeed-Cache] Save failed for " + type + ": " + e.getMessage());
            STORE.cleanupTmp(type, key);
        }
    }

    // ── Serialization ─────────────────────────────────────────────────────────

    private static void serialize(Map<ResourceLocation, JsonElement> data,
                                   Path dest) throws IOException {
        try (GZIPOutputStream gz = new GZIPOutputStream(
                 new BufferedOutputStream(Files.newOutputStream(dest)));
             BufferedWriter w = new BufferedWriter(
                 new OutputStreamWriter(gz, StandardCharsets.UTF_8))) {

            w.write(FORMAT);
            w.newLine();

            for (Map.Entry<ResourceLocation, JsonElement> e : data.entrySet()) {
                JsonObject line = new JsonObject();
                line.addProperty("k", e.getKey().toString());
                line.add("v", e.getValue());
                w.write(line.toString());
                w.newLine();
            }
        }
    }

    private static Map<ResourceLocation, JsonElement> deserialize(Path src) throws IOException {
        Map<ResourceLocation, JsonElement> result = new LinkedHashMap<>();

        try (GZIPInputStream gz = new GZIPInputStream(
                 new BufferedInputStream(Files.newInputStream(src)));
             BufferedReader r = new BufferedReader(
                 new InputStreamReader(gz, StandardCharsets.UTF_8))) {

            String ver = r.readLine();
            if (!FORMAT.equals(ver)) throw new IOException("Bad version: " + ver);

            String line;
            while ((line = r.readLine()) != null) {
                if (line.isEmpty()) continue;
                try {
                    JsonObject obj = JsonParser.parseString(line).getAsJsonObject();
                    ResourceLocation key = ResourceLocation.parse(obj.get("k").getAsString());
                    result.put(key, obj.get("v"));
                } catch (Exception e) {
                    // Skip malformed lines; a few misses are fine
                }
            }
        }
        return result;
    }

    // ── Key ───────────────────────────────────────────────────────────────────

    private static String getKey() {
        if (sessionKey == null) {
            synchronized (JsonReloadCache.class) {
                if (sessionKey == null) sessionKey = CacheKey.compute();
            }
        }
        return sessionKey;
    }
}
