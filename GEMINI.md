# Cryo-Loader: Minecraft Startup Optimization

Cryo-Loader is a performance-focused project designed to significantly reduce the "cold start" time of heavy Minecraft modpacks (specifically tested with ATM10 on Minecraft 1.21.1 with NeoForge). It achieves this through a multi-layered optimization strategy.

## Project Overview

- **Target Environment:** Minecraft 1.21.1, NeoForge, Java 21 (Generational ZGC).
- **Core Goal:** Reduce startup time from ~180s to ~80-100s for 400+ modpacks.
- **Main Technologies:**
    - **Java Agent (ByteBuddy):** Parallel I/O prefetching for configuration files.
    - **Mixins:** Parallelization of DataPack loading and Model Baking.
    - **AppCDS:** Application Class Data Sharing to accelerate JVM and mod class loading.
    - **Python Orchestrator:** Automated lifecycle management for AppCDS and optimized launch.

## Architecture & Components

### 1. Java Agent (`cryo-agent`)
- **Purpose:** Parallelly reads all configuration files (`config/*.toml`, `config/*.json`) into RAM before NeoForge requests them sequentially.
- **Mechanism:** Uses ByteBuddy to intercept `java.nio.file.Files.readAllBytes`.
- **Key Files:** `dev.cryo.agent.PrefetchAgent`, `dev.cryo.agent.FilesReadInterceptor`.

### 2. Mixins (`cryo-loader`)
- **`ParallelReloadMixin`:** Parallelizes DataPack loading by redirecting the background executor to a dedicated `FixedThreadPool`.
- **`ParallelModelBakingMixin`:** Parallelizes block/item model baking using Java Parallel Streams.
- **`CdsTitleScreenMixin`:** Automatically shuts down the game once the Title Screen is reached during a CDS profiling run.
- **`CryoMixinPlugin`:** Handles dynamic Mixin application logic.

### 3. Python Orchestrator (`scripts/cryo_launcher.py`)
- **Purpose:** Manages the AppCDS archive (`.jsa`) generation.
- **Workflow:**
    1. Detects mod changes via file hashing.
    2. If changed, runs a "profiling" launch to generate a `classlist`.
    3. Generates a `.jsa` archive from the `classlist`.
    4. Launches the actual game with optimized JVM flags (AppCDS, Agent, ZGC).

## Building and Running

### Building the Project
Use the Gradle wrapper to build both the mod and the agent:
```bash
./gradlew clean build
```
This generates:
- `build/libs/cryo-v2-1.0-SNAPSHOT.jar` (The Mixin mod)
- `cryo-agent/build/libs/cryo-agent.jar` (The Java Agent)

### Running the Optimized Launch
The main entry point for running the game with all optimizations:
```bash
python scripts/cryo_launcher.py
```
*Note: Ensure the paths in `scripts/cryo_launcher.py` (e.g., `INSTANCE_ROOT`, `PRISM_EXE`) are correctly configured for your local environment.*

### Benchmarking
To compare baseline startup time vs. optimized startup time:
```bash
python scripts/cryo_test.py
```

## Development Conventions

- **Thread Priority:** Always use `Thread.NORM_PRIORITY` for background workers to avoid starving the main render thread.
- **Daemon Threads:** All background worker threads must be marked as `daemon = true`.
- **Surgical Updates:** When modifying Mixins, verify target signatures against the specific NeoForge/Minecraft version (currently 1.21.1).
- **Security:** Never hardcode absolute paths that are specific to a single machine in shared code; use the configuration section in `cryo_launcher.py`.

## Key Files & Directories

- `cryo-loader/`: Source code for the Mixin-based mod.
- `cryo-agent/`: Source code for the ByteBuddy-based Java Agent.
- `scripts/`: Automation and benchmarking scripts.
- `IMPLEMENTATION_SUMMARY.md`: Summary of recent fixes and current verification status.
- `CRYO_LOADER_CODEGEN_SPEC.md`: Detailed technical specification and architectural roadmap.
- `cryo.mixins.json`: Configuration for Spongepowered Mixin.
