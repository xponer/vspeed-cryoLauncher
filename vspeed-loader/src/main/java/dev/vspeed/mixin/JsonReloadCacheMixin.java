package dev.vspeed.mixin;

import com.google.gson.JsonElement;
import dev.vspeed.cache.JsonReloadCache;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.server.ServerAdvancementManager;
import net.minecraft.server.packs.resources.ResourceManager;
import net.minecraft.server.packs.resources.SimpleJsonResourceReloadListener;
import net.minecraft.util.profiling.ProfilerFiller;
import net.minecraft.world.item.crafting.RecipeManager;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Unique;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

import java.util.Map;
import java.util.Optional;

/**
 * Persistent JSON-reload cache — skips JAR I/O on warm boots.
 *
 * <h2>Why this targets the superclass</h2>
 * {@link RecipeManager} (and the other data managers) <em>inherit</em>
 * {@code prepare()} from {@link SimpleJsonResourceReloadListener} rather than
 * declaring it.  Mixin only sees methods declared in the {@code @Mixin} target's
 * own bytecode, so {@code @Inject(method="prepare")} on
 * {@code @Mixin(RecipeManager.class)} finds <strong>zero</strong> targets and
 * silently no-ops (with {@code require = 0}).  Targeting the superclass — where
 * {@code prepare()} is actually declared — is the only way to intercept it.
 *
 * <h2>Flow</h2>
 * <pre>
 * Cold boot (cache miss):
 *   prepare() HEAD  → tryLoad() empty, do nothing
 *                   → [JAR I/O, Gson parse] → Map&lt;RL, JsonElement&gt;
 *   prepare() RETURN ← WE SAVE the returned map here
 *
 * Warm boot (cache hit):
 *   prepare() HEAD  → tryLoad() present → setReturnValue(cached) + cancel
 *                     (method returns at HEAD; the RETURN inject never runs,
 *                      so no re-save — no flag needed)
 * </pre>
 *
 * <p>{@code apply()} still runs every boot (condition evaluation + recipe codec
 * parsing) on the cached raw JSON — pure CPU work, far cheaper than JAR I/O
 * across hundreds of mod archives.</p>
 *
 * <h2>Scope</h2>
 * Every {@code SimpleJsonResourceReloadListener} shares this one instrumented
 * {@code prepare()}, but {@link #vspeed$cacheType()} returns {@code null} for
 * everything except an allow-list of known-safe managers, so non-cached listeners
 * pay only a single {@code instanceof} check.
 *
 * <h2>Safety</h2>
 * <ul>
 *   <li>Both injections use {@code require = 0}: if Mixin can't inject (API
 *       change), the game falls back to normal loading silently.</li>
 *   <li>All cache errors are caught and logged; they never crash the game.</li>
 *   <li>Cache is keyed by a hash of mod JAR / script filenames+sizes; any mod
 *       update triggers a full rebuild.</li>
 * </ul>
 */
@Mixin(SimpleJsonResourceReloadListener.class)
public abstract class JsonReloadCacheMixin {

    /** Wall-clock start of this prepare() call — used to time the cold JAR scan. */
    @org.spongepowered.asm.mixin.Unique
    private long vspeed$scanStart = 0L;

    // ── LOAD: intercept prepare() before JAR scanning begins ─────────────────────

    @Inject(
        method = "prepare(Lnet/minecraft/server/packs/resources/ResourceManager;" +
                         "Lnet/minecraft/util/profiling/ProfilerFiller;)" +
                         "Ljava/util/Map;",
        at = @At("HEAD"),
        cancellable = true,
        require = 0
    )
    private void vspeed$loadFromCache(
            ResourceManager rm,
            ProfilerFiller profiler,
            CallbackInfoReturnable<Map<ResourceLocation, JsonElement>> cir) {
        String type = vspeed$cacheType();
        if (type == null) return;   // not an allow-listed manager
        vspeed$scanStart = System.nanoTime();
        try {
            Optional<Map<ResourceLocation, JsonElement>> cached = JsonReloadCache.tryLoad(type);
            if (cached.isPresent()) {
                cir.setReturnValue(cached.get());   // skips scanDirectory + RETURN inject
                System.out.println("[VSpeed-Cache] prepare(" + type + ") cancelled — serving "
                                   + cached.get().size() + " entries from cache.");
            }
        } catch (Exception e) {
            System.err.println("[VSpeed-Cache] load inject error (" + type + "): " + e.getMessage());
        }
    }

    // ── SAVE: persist the scanned map (cache miss only) ──────────────────────────

    @Inject(
        method = "prepare(Lnet/minecraft/server/packs/resources/ResourceManager;" +
                         "Lnet/minecraft/util/profiling/ProfilerFiller;)" +
                         "Ljava/util/Map;",
        at = @At("RETURN"),
        require = 0
    )
    private void vspeed$saveToCache(
            ResourceManager rm,
            ProfilerFiller profiler,
            CallbackInfoReturnable<Map<ResourceLocation, JsonElement>> cir) {
        String type = vspeed$cacheType();
        if (type == null) return;
        try {
            Map<ResourceLocation, JsonElement> map = cir.getReturnValue();
            if (map != null) {
                // We only reach RETURN on a cold scan (HIT cancels at HEAD).
                long ms = (System.nanoTime() - vspeed$scanStart) / 1_000_000L;
                dev.vspeed.cache.Stats.record(type, "cold", ms, map.size());
                System.out.printf("[VSpeed-Cache] cold scan %s — %d entries in %d ms%n",
                                  type, map.size(), ms);
                JsonReloadCache.trySave(type, map);
            }
        } catch (Exception e) {
            System.err.println("[VSpeed-Cache] save inject error (" + type + "): " + e.getMessage());
        }
    }

    // ── Allow-list ───────────────────────────────────────────────────────────────

    /**
     * Returns the cache type name for this listener, or {@code null} if it should
     * not be cached.  Derived from {@code instanceof} (rather than shadowing the
     * private {@code directory} field) so a mappings change can never hard-fail
     * mixin application.
     *
     * <p>Cached managers must <em>not</em> override {@code prepare()} (otherwise the
     * superclass inject wouldn't capture their real output).  Verified for 1.21.1:
     * {@link RecipeManager} and {@link ServerAdvancementManager} only override
     * {@code apply()}.  NeoForge's {@code LootModifierManager} <em>does</em> override
     * {@code prepare()} (custom layering), so it is deliberately excluded — the inject
     * that fires on its internal {@code super.prepare()} call safely no-ops here.</p>
     *
     * <p>Vanilla loot tables are <em>not</em> in this list: in 1.21 they load via the
     * registry data loader, not {@code SimpleJsonResourceReloadListener}.</p>
     */
    @Unique
    private String vspeed$cacheType() {
        Object self = this;
        if (self instanceof RecipeManager)            return "recipe";
        if (self instanceof ServerAdvancementManager) return "advancement";
        return null;
    }
}
