package dev.vspeed.mixin;

import org.objectweb.asm.tree.ClassNode;
import org.spongepowered.asm.mixin.extensibility.IMixinConfigPlugin;
import org.spongepowered.asm.mixin.extensibility.IMixinInfo;

import java.util.List;
import java.util.Set;

public class VSpeedMixinPlugin implements IMixinConfigPlugin {

    private static final String OPT_PROP = "vspeed.optimized";
    private static final String PROF_PROP = "vspeed.cds.profiling";

    @Override
    public void onLoad(String mixinPackage) {
        String opt = System.getProperty(OPT_PROP);
        String prof = System.getProperty(PROF_PROP);
        if ("true".equals(opt) || "true".equals(prof)) {
            System.out.println("[VSpeed-Mixin] Mixin plugin initialized.");
        }
    }

    @Override
    public boolean shouldApplyMixin(String targetClassName, String mixinClassName) {
        String opt = System.getProperty(OPT_PROP, "false");
        String prof = System.getProperty(PROF_PROP, "false");

        // CDS Profiling mode: only TitleScreen mixin
        if ("true".equals(prof)) {
            return mixinClassName.endsWith("CdsTitleScreenMixin");
        }

        // Optimized mode: all mixins including parallel ones
        if ("true".equals(opt)) {
            return true;
        }

        return false;
    }

    @Override
    public String getRefMapperConfig() { return null; }
    @Override
    public void acceptTargets(Set<String> myTargets, Set<String> otherTargets) {}
    @Override
    public List<String> getMixins() { return null; }
    @Override
    public void preApply(String targetClassName, ClassNode targetClass, String mixinClassName, IMixinInfo mixinInfo) {}
    @Override
    public void postApply(String targetClassName, ClassNode targetClass, String mixinClassName, IMixinInfo mixinInfo) {}
}
