package dev.vspeed.mixin;

import net.minecraft.client.gui.screens.TitleScreen;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Previously called halt(0) here to stop the JVM during CDS profiling.
 *
 * This is no longer needed and was actively harmful:
 *   - VSpeedMod hooks FMLLoadCompleteEvent, which fires long before TitleScreen.init().
 *     It calls System.exit(0) to let the JVM write the Dynamic AppCDS archive gracefully
 *     (-XX:ArchiveClassesAtExit) and then shut down.
 *   - halt(0) bypasses all JVM shutdown hooks, so the archive would never be written.
 *
 * This mixin is now unregistered (removed from vspeed.mixins.json client array).
 * The class is kept for historical reference only.
 */
@Mixin(TitleScreen.class)
public class CdsTitleScreenMixin {

    @Inject(method = "init", at = @At("HEAD"))
    private void onInit(CallbackInfo ci) {
        // No-op. Profiling exit is handled entirely by VSpeedMod.onLoadComplete().
    }
}
