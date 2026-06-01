using System.IO;
using System.Security.Cryptography;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;

namespace VSpeedLauncher.Core;

/// <summary>
/// Microsoft (Java edition) account + game session.
///
/// <para><b>Token security.</b> CmlLib persists the refresh/access tokens in a JSON
/// account file. We never leave that plaintext at rest: it lives only transiently
/// while an auth call runs, and is immediately re-encrypted with Windows <b>DPAPI</b>
/// (<see cref="DataProtectionScope.CurrentUser"/>) into <c>accounts.bin</c>. That blob
/// is decryptable only by this Windows user on this machine — it can't be copied off
/// and replayed elsewhere (the failure mode that bit ATLauncher's plaintext tokens).</para>
/// </summary>
public sealed class MicrosoftAccount
{
    public static MicrosoftAccount Instance { get; } = new();

    private readonly string _plain;   // transient plaintext used by CmlLib during an auth call
    private readonly string _enc;     // DPAPI-encrypted blob, at rest
    private static readonly byte[] _entropy = System.Text.Encoding.UTF8.GetBytes("Cryo.VSpeed.Account.v1");
    private readonly System.Threading.SemaphoreSlim _lock = new(1, 1);
    private bool _triedRestore;   // silent restore is attempted at most once per launch (avoid self-throttling)

    public MSession? Session { get; private set; }
    public string? Username { get; private set; }
    public string? Uuid     { get; private set; }
    public bool LoggedIn => Session != null && !string.IsNullOrEmpty(Session.Username);

    private MicrosoftAccount()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSpeedLauncher", "auth");
        Directory.CreateDirectory(dir);
        _plain = Path.Combine(dir, "accounts.json");
        _enc   = Path.Combine(dir, "accounts.bin");
    }

    private JELoginHandler BuildHandler()
    {
        DecryptToPlain();
        return new JELoginHandlerBuilder().WithAccountManager(_plain).Build();
    }

    private void DecryptToPlain()
    {
        try
        {
            if (File.Exists(_enc))
            {
                var dec = ProtectedData.Unprotect(File.ReadAllBytes(_enc), _entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_plain, dec);
            }
        }
        catch (Exception e) { Logger.Warn($"Account decrypt failed: {e.Message}"); }
    }

    private void EncryptAndShred()
    {
        try
        {
            if (File.Exists(_plain))
            {
                var enc = ProtectedData.Protect(File.ReadAllBytes(_plain), _entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_enc, enc);
                try { File.Delete(_plain); } catch { /* best effort */ }
            }
        }
        catch (Exception e) { Logger.Warn($"Account encrypt failed: {e.Message}"); }
    }

    private void Capture(MSession s) { Session = s; Username = s.Username; Uuid = s.UUID; }

    /// <summary>Restores the session from the encrypted cache without user interaction.
    /// Attempted at most once per launch — repeated calls return the cached state and
    /// make NO network request, so the titlebar chip + Settings page can't trigger an
    /// auth storm that Microsoft would throttle (503).</summary>
    public async Task<bool> RestoreSilentAsync()
    {
        if (LoggedIn) return true;
        if (_triedRestore) return false;
        await _lock.WaitAsync();
        try
        {
            if (LoggedIn) return true;
            if (_triedRestore) return false;
            _triedRestore = true;
            if (!File.Exists(_enc)) return false;
            try
            {
                var handler = BuildHandler();
                var s = await handler.AuthenticateSilently();
                if (s != null && !string.IsNullOrEmpty(s.Username)) { Capture(s); return true; }
            }
            catch (Exception e) { Logger.Info($"Silent login unavailable: {e.Message}"); }
            finally { EncryptAndShred(); }
            return false;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Interactive Microsoft login (opens the Microsoft sign-in page; the user
    /// authenticates there — this app never sees the password).</summary>
    public async Task<MSession> LoginInteractiveAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _triedRestore = true;   // an explicit login supersedes the silent attempt
            var handler = BuildHandler();
            MSession s;
            try
            {
                s = await handler.AuthenticateInteractively();
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // The browser/MSAL step likely succeeded; a transient upstream error (e.g. 503
                // from Xbox/Minecraft services) hit the token exchange. Retry the silent chain
                // a couple times WITHOUT re-opening the browser (the MSAL token is cached).
                Logger.Warn($"Login transient error ({ex.Message}); retrying token exchange…");
                s = await RetrySilentAsync(handler, 2);
            }
            Capture(s);
            return s;
        }
        finally { EncryptAndShred(); _lock.Release(); }
    }

    private static bool IsTransient(Exception e)
    {
        var m = e.Message ?? "";
        return m.Contains("503") || m.Contains("502") || m.Contains("500")
            || m.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase)
            || m.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || m.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<MSession> RetrySilentAsync(JELoginHandler handler, int tries)
    {
        Exception? last = null;
        for (int i = 0; i < tries; i++)
        {
            await Task.Delay(3000 * (i + 1));
            try
            {
                var s = await handler.AuthenticateSilently();
                if (s != null && !string.IsNullOrEmpty(s.Username)) return s;
            }
            catch (Exception e) { last = e; Logger.Info($"Retry {i + 1}/{tries}: {e.Message}"); }
        }
        throw last ?? new Exception("Login failed after retries");
    }

    public async Task LogoutAsync()
    {
        try { await BuildHandler().Signout(); }
        catch (Exception e) { Logger.Warn($"Signout: {e.Message}"); }
        Session = null; Username = null; Uuid = null;
        try { if (File.Exists(_plain)) File.Delete(_plain); } catch { }
        try { if (File.Exists(_enc))   File.Delete(_enc);   } catch { }
    }
}
