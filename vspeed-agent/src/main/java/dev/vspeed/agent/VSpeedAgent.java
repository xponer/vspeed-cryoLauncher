package dev.vspeed.agent;

import java.io.*;
import java.lang.instrument.Instrumentation;
import java.net.URL;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.jar.JarFile;

import net.bytebuddy.agent.builder.AgentBuilder;
import net.bytebuddy.asm.Advice;
import net.bytebuddy.matcher.ElementMatchers;

public class VSpeedAgent {

    public static final ConcurrentHashMap<String, byte[]> configCache = new ConcurrentHashMap<>();
    public static final AtomicInteger cacheHits = new AtomicInteger(0);

    public static void log(String msg) {
        // МАКСИМАЛЬНО ПРОСТО: Только консоль, чтобы ничего не могло упасть
        System.out.println("[VSpeed-Agent] " + msg);
    }

    public static String normalize(Path path) {
        return path.toAbsolutePath().normalize().toString().toLowerCase().replace("\\", "/");
    }

    public static void premain(String args, Instrumentation inst) {
        log("PREMAIN STARTED");
        log("Working Dir: " + System.getProperty("user.dir"));

        try {
            URL agentUrl = VSpeedAgent.class.getProtectionDomain().getCodeSource().getLocation();
            log("Agent JAR: " + agentUrl.getPath());
            inst.appendToBootstrapClassLoaderSearch(new JarFile(new File(agentUrl.toURI())));
        } catch (Exception e) {
            log("ERROR during bootstrap injection: " + e.getMessage());
        }

        // Поиск конфигов
        File[] searchPaths = { new File("config"), new File("minecraft/config"), new File("../config") };
        for (File dir : searchPaths) {
            log("Checking path: " + dir.getAbsolutePath() + " (exists: " + dir.exists() + ")");
            if (dir.exists() && dir.isDirectory()) {
                prefetchDirectory(dir);
            }
        }
        log("PREFETCH DONE. Total cached: " + configCache.size());

        try {
            installTransformer(inst);
            log("TRANSFORMER INSTALLED");
        } catch (Exception e) {
            log("CRITICAL: Failed to install transformer: " + e.getMessage());
        }
    }

    private static void prefetchDirectory(File dir) {
        File[] files = dir.listFiles();
        if (files == null) return;
        for (File file : files) {
            if (file.isDirectory()) {
                prefetchDirectory(file);
            } else {
                String name = file.getName().toLowerCase();
                if (name.endsWith(".toml") || name.endsWith(".json") || name.endsWith(".cfg")) {
                    try {
                        byte[] data = Files.readAllBytes(file.toPath());
                        configCache.put(normalize(file.toPath()), data);
                    } catch (IOException ignored) {}
                }
            }
        }
    }

    public static void updateHits() {
        System.setProperty("vspeed.internal.hits", String.valueOf(cacheHits.get()));
    }

    private static void installTransformer(Instrumentation inst) {
        new AgentBuilder.Default()
            .with(AgentBuilder.RedefinitionStrategy.RETRANSFORMATION)
            .with(AgentBuilder.TypeStrategy.Default.REDEFINE)
            .ignore(ElementMatchers.none())
            .type(ElementMatchers.named("java.nio.file.Files"))
            .transform((builder, typeDescription, classLoader, module, protectionDomain) ->
                builder
                    .visit(Advice.to(FilesReadInterceptor.BytesAdvice.class)
                        .on(ElementMatchers.named("readAllBytes").and(ElementMatchers.takesArguments(Path.class))))
                    .visit(Advice.to(FilesReadInterceptor.StreamAdvice.class)
                        .on(ElementMatchers.named("newInputStream").and(ElementMatchers.takesArgument(0, Path.class))))
                    .visit(Advice.to(FilesReadInterceptor.StringAdvice.class)
                        .on(ElementMatchers.named("readString").and(ElementMatchers.takesArgument(0, Path.class))))
            )
            .installOn(inst);
    }
}
