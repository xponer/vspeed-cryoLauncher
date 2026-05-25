package dev.vspeed.mixin;

import net.minecraft.client.resources.model.ModelBakery;
import net.minecraft.client.resources.model.UnbakedModel;
import net.minecraft.client.resources.model.ModelResourceLocation;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Redirect;

import java.util.Map;
import java.util.function.BiConsumer;

@Mixin(ModelBakery.class)
public class ParallelModelBakingMixin {

    /**
     * NOTE: Parallel model baking is NOT safe at this injection point.
     *
     * The consumer passed to bakeModels() does two things for each entry:
     *   1. Calls ModelBakery.bake() which writes into the internal `bakedModels` HashMap.
     *   2. Calls the output BiConsumer which writes into ModelManager's model map.
     * Neither target is thread-safe — parallelStream() here causes silent HashMap
     * corruption or ConcurrentModificationException (not always thrown, so the bug
     * can be invisible).
     *
     * This @Redirect is kept registered (defaultRequire: 1 in vspeed.mixins.json
     * requires the injection point to exist), but is deliberately a no-op pass-through.
     * The real parallel speedup comes from ParallelReloadMixin, which replaces the
     * background executor in ReloadableResourceManager.createReload() — a path that
     * Minecraft explicitly designed for concurrent CompletableFuture execution.
     */
    @Redirect(
        method = "bakeModels",
        at = @At(
            value = "INVOKE",
            target = "Ljava/util/Map;forEach(Ljava/util/function/BiConsumer;)V"
        )
    )
    private void redirectBakeModels(Map<ModelResourceLocation, UnbakedModel> map, BiConsumer<ModelResourceLocation, UnbakedModel> consumer) {
        // Always sequential — parallelStream() here corrupts ModelBakery's HashMap state
        map.forEach(consumer);
    }
}
