using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using VSpeedLauncher.Core;

namespace VSpeedLauncher.UI;

public partial class MainWindow : Window
{
    private CryoBridge? _bridge;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VSpeedLauncher", "webview2");

        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: dataDir,
            options: new CoreWebView2EnvironmentOptions
            {
                // GPU-accelerated (no --disable-gpu) so the glass/blur UI stays smooth.
                // msWebView2BrowserHitTransparent enables -webkit-app-region:drag.
                AdditionalBrowserArguments = "--enable-features=msWebView2BrowserHitTransparent",
            });

        await WebView.EnsureCoreWebView2Async(env);

        var uiFolder = Path.Combine(AppContext.BaseDirectory, "WebUI");
        // Dev convenience: if the source WebUI exists (4 levels up from bin/.../win-x64),
        // serve from it so JS/CSS edits apply on relaunch without a full rebuild.
        try
        {
            var dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "WebUI"));
            if (File.Exists(Path.Combine(dev, "Cryo Launcher.html")) && Directory.Exists(Path.Combine(dev, "src")))
            {
                uiFolder = dev;
                Logger.Info($"Serving UI from source: {dev}");
            }
        }
        catch { /* fall back to bundled output */ }

        if (Directory.Exists(uiFolder))
        {
            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "cryo.local", uiFolder,
                CoreWebView2HostResourceAccessKind.Allow);
        }
        else
        {
            Logger.Warn($"WebUI folder not found: {uiFolder}");
        }

        WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        WebView.CoreWebView2.Settings.AreDevToolsEnabled            = true;  // F12 to debug
        WebView.CoreWebView2.Settings.IsStatusBarEnabled            = false;
        WebView.CoreWebView2.Settings.IsZoomControlEnabled          = false;
        WebView.CoreWebView2.Settings.IsWebMessageEnabled           = true;

        WebView.CoreWebView2.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith("https://cryo.local/", StringComparison.OrdinalIgnoreCase)
             && !args.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
            }
        };

        _bridge = new CryoBridge(
            App.Current.Manager,
            App.Current.Config,
            App.Current.History,
            WebView);

        WebView.CoreWebView2.WebMessageReceived += _bridge.OnWebMessageReceived;
        WebView.CoreWebView2.Navigate("https://cryo.local/Cryo Launcher.html");
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
