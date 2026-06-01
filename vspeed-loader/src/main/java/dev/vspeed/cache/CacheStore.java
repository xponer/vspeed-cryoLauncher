package dev.vspeed.cache;

import java.io.IOException;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardCopyOption;
import java.util.stream.Stream;

/**
 * Manages the {@code .vspeed-cache/} directory next to the game's working
 * directory.
 *
 * <h2>Layout</h2>
 * <pre>
 * .vspeed-cache/
 *   json/
 *     &lt;type&gt;/                 — one folder per reload-listener directory
 *       &lt;cacheKey&gt;.bin       — serialized JsonElement map (one file per key)
 * </pre>
 *
 * <p>{@code <type>} is the (sanitised) {@code directory} of the
 * {@link net.minecraft.server.packs.resources.SimpleJsonResourceReloadListener}
 * — e.g. {@code recipe}, {@code loot_table}, {@code advancement}.  Keeping each
 * type in its own folder means eviction is scoped per type: updating the mod set
 * invalidates every type, but the types never collide with each other.</p>
 *
 * <h2>Atomic writes</h2>
 * Writes go to {@code <file>.tmp} first, then {@code Files.move(..., ATOMIC_MOVE)}
 * (falling back to a plain replace if the filesystem can't do atomic moves) so a
 * crash mid-write never leaves a corrupt cache file.
 *
 * <h2>Eviction</h2>
 * On every commit we delete all other {@code *.bin} files in the same
 * {@code <type>/} folder, keeping only the current key.  This prevents unbounded
 * growth when the mod-pack is updated frequently.
 */
public final class CacheStore {

    /** Root cache directory relative to the game working directory. */
    public static final String CACHE_DIR_NAME = ".vspeed-cache";

    private final Path cacheRoot;

    public CacheStore() {
        cacheRoot = Paths.get("").toAbsolutePath().resolve(CACHE_DIR_NAME);
    }

    // ── Per-type JSON caches ────────────────────────────────────────────────────

    private Path typeDir(String type) {
        return cacheRoot.resolve("json").resolve(type);
    }

    private Path file(String type, String key) {
        return typeDir(type).resolve(key + ".bin");
    }

    /** @return {@code true} if a cached file for {@code (type, key)} exists. */
    public boolean has(String type, String key) {
        return Files.isRegularFile(file(type, key));
    }

    /**
     * Returns the path to the cache file for reading.
     * Caller must check {@link #has(String, String)} first.
     */
    public Path readPath(String type, String key) {
        return file(type, key);
    }

    /**
     * Returns a tmp path for writing.  After writing, call
     * {@link #commit(String, String)} to atomically rename it into place.
     */
    public Path writeTmp(String type, String key) throws IOException {
        Files.createDirectories(typeDir(type));
        return typeDir(type).resolve(key + ".tmp");
    }

    /**
     * Atomically moves {@code <key>.tmp} → {@code <key>.bin} and deletes all
     * other {@code *.bin} files (old keys) in the same {@code <type>/} folder.
     */
    public void commit(String type, String key) throws IOException {
        Path tmp  = typeDir(type).resolve(key + ".tmp");
        Path dest = file(type, key);
        try {
            Files.move(tmp, dest, StandardCopyOption.ATOMIC_MOVE,
                                  StandardCopyOption.REPLACE_EXISTING);
        } catch (AtomicMoveNotSupportedException e) {
            Files.move(tmp, dest, StandardCopyOption.REPLACE_EXISTING);
        }

        // Evict stale entries for THIS type only.
        try (Stream<Path> entries = Files.list(typeDir(type))) {
            entries.filter(p -> p.getFileName().toString().endsWith(".bin"))
                   .filter(p -> !p.equals(dest))
                   .forEach(p -> {
                       try { Files.deleteIfExists(p); }
                       catch (IOException ignored) {}
                   });
        }
        System.out.printf("[VSpeed-Cache] %s cache written → json/%s/%s%n",
                          type, type, dest.getFileName());
    }

    /** Delete a corrupt or incomplete tmp file, if present. */
    public void cleanupTmp(String type, String key) {
        try { Files.deleteIfExists(typeDir(type).resolve(key + ".tmp")); }
        catch (IOException ignored) {}
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    public Path cacheRoot() { return cacheRoot; }
}
