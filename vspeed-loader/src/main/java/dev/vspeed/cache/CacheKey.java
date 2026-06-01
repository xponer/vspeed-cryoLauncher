package dev.vspeed.cache;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HexFormat;
import java.util.List;
import java.util.stream.Stream;

/**
 * Computes a fast, deterministic cache-invalidation key.
 *
 * <h2>Strategy</h2>
 * Instead of hashing file contents (too slow for hundreds of mod JARs),
 * we hash directory metadata: filename + file-size for every file in
 * {@code mods/} and {@code scripts/} (CraftTweaker).  This is O(n) in
 * directory entries (pure stat calls, no file reads) and invalidates
 * correctly whenever:
 * <ul>
 *   <li>A mod JAR is added, removed, or updated (size changes)</li>
 *   <li>A CraftTweaker / KubeJS script is added, removed, or changed
 *       (size changes)</li>
 * </ul>
 *
 * <p>False-positive invalidation (same size, different content) is
 * theoretically possible but extremely rare in practice.</p>
 */
public final class CacheKey {

    private static final String HASH_ALG  = "SHA-256";
    private static final int    HEX_CHARS = 16;   // 64-bit prefix, collision-safe for any mod-pack

    private CacheKey() {}

    /**
     * Compute and return the cache key.  Never throws; returns {@code "error"}
     * on any failure so the cache safely misses.
     */
    public static String compute() {
        try {
            return computeInternal();
        } catch (Exception e) {
            System.err.println("[VSpeed-Cache] CacheKey.compute failed: " + e);
            return "error";
        }
    }

    private static String computeInternal() throws IOException, NoSuchAlgorithmException {
        MessageDigest sha = MessageDigest.getInstance(HASH_ALG);
        Path root = Paths.get("").toAbsolutePath();

        List<Path> entries = new ArrayList<>();
        addDir(entries, root.resolve("mods"),    "*.jar");
        addDir(entries, root.resolve("scripts"), "*");         // CraftTweaker scripts
        addDir(entries, root.resolve("kubejs"),  "*.js");      // KubeJS scripts

        // Sort for determinism
        entries.sort(Comparator.comparing(Path::toString));

        for (Path p : entries) {
            long size = Files.size(p);
            // Include relative path (detects renames) + file size (detects updates)
            String meta = p.getFileName().toString() + "|" + size + "\n";
            sha.update(meta.getBytes(StandardCharsets.UTF_8));
        }

        String fullHex = HexFormat.of().formatHex(sha.digest());
        System.out.printf("[VSpeed-Cache] Key: %s  (%d mod/script entries)%n",
                          fullHex.substring(0, HEX_CHARS), entries.size());
        return fullHex.substring(0, HEX_CHARS);
    }

    /**
     * Collect all files in {@code dir} matching the given glob, non-recursively.
     * Silently skips missing or unreadable directories.
     */
    private static void addDir(List<Path> out, Path dir, String glob) {
        if (!Files.isDirectory(dir)) return;
        try (Stream<Path> s = Files.list(dir)) {
            s.filter(Files::isRegularFile)
             .filter(p -> {
                 String name = p.getFileName().toString();
                 return glob.equals("*") || matchesGlob(name, glob);
             })
             .forEach(out::add);
        } catch (IOException e) {
            System.err.println("[VSpeed-Cache] addDir failed for " + dir + ": " + e.getMessage());
        }
    }

    /** Minimal glob: only supports leading/trailing {@code *} wildcards. */
    private static boolean matchesGlob(String name, String glob) {
        if (glob.equals("*")) return true;
        if (glob.startsWith("*") && glob.endsWith("*")) {
            return name.contains(glob.substring(1, glob.length() - 1));
        }
        if (glob.startsWith("*")) return name.endsWith(glob.substring(1));
        if (glob.endsWith("*"))   return name.startsWith(glob.substring(0, glob.length() - 1));
        return name.equals(glob);
    }
}
