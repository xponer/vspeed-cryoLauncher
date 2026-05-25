package dev.vspeed.mixin;

import net.minecraft.server.packs.resources.ReloadableResourceManager;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.ModifyArg;

import java.util.concurrent.Executor;

@Mixin(ReloadableResourceManager.class)
public class ParallelReloadMixin {

    /**
     * NOTE: Replacing the background executor here is NOT beneficial.
     *
     * Minecraft already passes a parallel executor to SimpleReloadInstance.create()
     * (typically backed by Util.backgroundExecutor(), a work-stealing ForkJoinPool).
     * Replacing it with a newFixedThreadPool produced a +2s regression in benchmarks
     * (72s CDS-only vs 74s CDS + this mixin) — the default pool is already optimal
     * for Minecraft's async prepare pipeline.
     *
     * This @ModifyArg must remain registered (defaultRequire: 1 in vspeed.mixins.json
     * requires the injection point to exist), but is deliberately a no-op pass-through.
     * The real startup speedup comes from AppCDS alone (~15% gain).
     */
    @ModifyArg(
        method = "createReload",
        at = @At(
            value = "INVOKE",
            target = "Lnet/minecraft/server/packs/resources/SimpleReloadInstance;create(Lnet/minecraft/server/packs/resources/ResourceManager;Ljava/util/List;Ljava/util/concurrent/Executor;Ljava/util/concurrent/Executor;Ljava/util/concurrent/CompletableFuture;Z)Lnet/minecraft/server/packs/resources/ReloadInstance;"
        ),
        index = 2
    )
    private Executor redirectPreparationExecutor(Executor original) {
        // Always return original — replacement pool regresses startup by ~2s
        return original;
    }
}
