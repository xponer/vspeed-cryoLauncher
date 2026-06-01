using System.Diagnostics;
using System.Net.Http;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;
using CmlLib.Core.Installer.NeoForge.Installers;
using CmlLib.Core.Installer.NeoForge.Versions;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using CmlLib.Core.ProcessBuilder;

namespace VSpeedLauncher.Core;

/// <summary>
/// Standalone Minecraft launch engine built on CmlLib.Core — installs the
/// version/libraries/assets, builds the launch command, and starts the game
/// WITHOUT PrismLauncher. This is the foundation for making Cryo self-contained.
///
/// <para><b>NeoForge:</b> use <see cref="InstallNeoForgeAsync"/> first to install
/// NeoForge into the shared CmlLib game root; the returned version name is then
/// passed to <see cref="InstallAndLaunchAsync"/>.</para>
/// </summary>
public sealed class LauncherCore
{
    private readonly string _root;

    /// <summary>Shared CmlLib game root: libraries, versions, assets.</summary>
    public string Root => _root;

    public LauncherCore(string gameRoot) => _root = gameRoot;

    /// <summary>(name, progressedTasks, totalTasks)</summary>
    public event Action<string, int, int>? FileProgress;
    /// <summary>(progressedBytes, totalBytes)</summary>
    public event Action<long, long>? ByteProgress;

    // ── NeoForge ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns available NeoForge version strings for a given Minecraft version,
    /// newest first (e.g. ["21.1.172", "21.1.168", …]).
    /// </summary>
    public async Task<string[]> GetNeoForgeVersionsAsync(string mcVersion)
    {
        var path      = new MinecraftPath(_root);
        var launcher  = new MinecraftLauncher(path);
        var installer = new NeoForgeInstaller(launcher);
        var versions  = await installer.GetForgeVersions(mcVersion);
        return versions
            .Select(v => v.VersionName ?? "")
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();
    }

    /// <summary>
    /// Downloads and installs NeoForge into the shared CmlLib game root.
    /// Returns the CmlLib internal version name (e.g. <c>"neoforge-21.1.172"</c>)
    /// that should be stored and later passed to <see cref="InstallAndLaunchAsync"/>.
    /// </summary>
    /// <param name="mcVersion">Minecraft version string, e.g. <c>"1.21.1"</c>.</param>
    /// <param name="neoForgeVersion">
    /// NeoForge version string, e.g. <c>"21.1.172"</c>.  Pass <c>null</c> to install
    /// the latest stable release for <paramref name="mcVersion"/>.
    /// </param>
    public async Task<string> InstallNeoForgeAsync(
        string mcVersion,
        string? neoForgeVersion    = null,
        IProgress<string>? output  = null,
        CancellationToken ct       = default)
    {
        var path      = new MinecraftPath(_root);
        var launcher  = new MinecraftLauncher(path);
        launcher.FileProgressChanged += (_, a) => FileProgress?.Invoke(a.Name ?? "", a.ProgressedTasks, a.TotalTasks);
        launcher.ByteProgressChanged += (_, a) => ByteProgress?.Invoke(a.ProgressedBytes, a.TotalBytes);

        var installer = new NeoForgeInstaller(launcher);
        var opts = new NeoForgeInstallOptions
        {
            InstallerOutput      = output,
            CancellationToken    = ct,
            SkipIfAlreadyInstalled = true,
        };

        return string.IsNullOrEmpty(neoForgeVersion)
            ? await installer.Install(mcVersion, opts)
            : await installer.Install(mcVersion, neoForgeVersion, opts);
    }

    // ── Forge / Fabric / Quilt ───────────────────────────────────────────────

    private MinecraftLauncher NewLauncher()
    {
        var launcher = new MinecraftLauncher(new MinecraftPath(_root));
        launcher.FileProgressChanged += (_, a) => FileProgress?.Invoke(a.Name ?? "", a.ProgressedTasks, a.TotalTasks);
        launcher.ByteProgressChanged += (_, a) => ByteProgress?.Invoke(a.ProgressedBytes, a.TotalBytes);
        return launcher;
    }

    public async Task<string> InstallForgeAsync(string mcVersion, string? forgeVersion = null)
    {
        var installer = new ForgeInstaller(NewLauncher());
        return string.IsNullOrEmpty(forgeVersion)
            ? await installer.Install(mcVersion)
            : await installer.Install(mcVersion, forgeVersion);
    }

    public async Task<string> InstallFabricAsync(string mcVersion, string? loaderVersion = null)
    {
        var path = new MinecraftPath(_root);
        var installer = new FabricInstaller(new HttpClient());
        return string.IsNullOrEmpty(loaderVersion)
            ? await installer.Install(mcVersion, path)
            : await installer.Install(mcVersion, loaderVersion, path);
    }

    public async Task<string> InstallQuiltAsync(string mcVersion, string? loaderVersion = null)
    {
        var path = new MinecraftPath(_root);
        var installer = new QuiltInstaller(new HttpClient());
        return string.IsNullOrEmpty(loaderVersion)
            ? await installer.Install(mcVersion, path)
            : await installer.Install(mcVersion, loaderVersion, path);
    }

    /// <summary>
    /// Installs the given loader and returns the CmlLib version name to launch.
    /// Vanilla returns the MC version unchanged (installed lazily at first launch).
    /// </summary>
    public async Task<string> InstallLoaderAsync(string mcVersion, string loader, string? loaderVersion = null,
                                                 IProgress<string>? output = null)
    {
        switch ((loader ?? "").ToLowerInvariant())
        {
            case "neoforge": return await InstallNeoForgeAsync(mcVersion, loaderVersion, output);
            case "forge":    return await InstallForgeAsync(mcVersion, loaderVersion);
            case "fabric":   return await InstallFabricAsync(mcVersion, loaderVersion);
            case "quilt":    return await InstallQuiltAsync(mcVersion, loaderVersion);
            default:         return mcVersion;   // vanilla
        }
    }

    // ── Launch ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Installs (if missing) and launches a Minecraft version, returning the started process.
    /// </summary>
    /// <param name="versionId">CmlLib version name — vanilla e.g. <c>"1.21.1"</c>,
    /// NeoForge e.g. <c>"neoforge-21.1.172"</c> (returned by
    /// <see cref="InstallNeoForgeAsync"/>).</param>
    /// <param name="gameDir">
    /// Per-instance <c>.minecraft</c> directory (mods, config, saves).
    /// When <c>null</c>, uses the shared CmlLib root (only suitable for plain vanilla).
    /// </param>
    public async Task<Process> InstallAndLaunchAsync(
        string versionId,
        MSession session,
        int ramMb,
        string? gameDir  = null,
        string? javaPath = null,
        IEnumerable<MArgument>? extraJvmArgs = null,
        CancellationToken ct = default)
    {
        // Libraries/versions live in _root; game dir (mods/config/saves) is separate.
        var mcPath   = new MinecraftPath(_root);
        var launcher = new MinecraftLauncher(mcPath);
        launcher.FileProgressChanged += (_, a) => FileProgress?.Invoke(a.Name ?? "", a.ProgressedTasks, a.TotalTasks);
        launcher.ByteProgressChanged += (_, a) => ByteProgress?.Invoke(a.ProgressedBytes, a.TotalBytes);

        var opt = new MLaunchOption
        {
            Session      = session,
            MaximumRamMb = ramMb > 0 ? ramMb : 4096,
        };
        if (!string.IsNullOrWhiteSpace(javaPath)) opt.JavaPath = javaPath;
        // --gameDir tells Minecraft/NeoForge where to find mods, config, saves.
        if (!string.IsNullOrWhiteSpace(gameDir))
            opt.ExtraGameArguments = new[]
            {
                MArgument.FromCommandLine("--gameDir"),
                MArgument.FromCommandLine(gameDir),
            };
        if (extraJvmArgs != null) opt.ExtraJvmArguments = extraJvmArgs;

        var proc = await launcher.InstallAndBuildProcessAsync(versionId, opt, ct);
        proc.Start();
        return proc;
    }
}
