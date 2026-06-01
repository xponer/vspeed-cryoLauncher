using System;
using Velopack;

namespace VSpeedLauncher;

/// <summary>
/// Explicit entry point. <see cref="VelopackApp"/> MUST run before any other code
/// so it can handle install/update hooks (<c>--veloapp-install</c>,
/// <c>--veloapp-updated</c>, etc.) and exit immediately when invoked as a hook.
/// During a normal launch it returns instantly and we start WPF as usual.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            VelopackApp.Build().Run();
        }
        catch
        {
            // A Velopack hook failure must never block a normal launch.
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
