using System.Drawing;
using System.IO;
using System.Windows.Forms;
using VSpeedLauncher.Core;
using ContextMenuStrip  = System.Windows.Forms.ContextMenuStrip;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace VSpeedLauncher.UI;

public sealed class TrayIcon : IDisposable
{
    private readonly InstanceManager  _manager;
    private readonly Action           _openMain;
    private readonly Action           _exit;
    private readonly NotifyIcon       _icon;
    private readonly ContextMenuStrip _menu;

    public TrayIcon(InstanceManager manager, Action openMain, Action exit)
    {
        _manager  = manager;
        _openMain = openMain;
        _exit     = exit;

        _menu = new ContextMenuStrip();
        _icon = new NotifyIcon
        {
            Icon             = LoadIcon(),
            Visible          = true,
            Text             = "Cryo Launcher",
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => _openMain();
        _menu.Opening     += (_, _) => RebuildMenu();
        RebuildMenu();

        foreach (var inst in _manager.Instances)
            inst.Changed += OnInstanceChanged;

        _manager.Instances.CollectionChanged += (_, _) =>
        {
            foreach (var inst in _manager.Instances)
            {
                inst.Changed -= OnInstanceChanged;
                inst.Changed += OnInstanceChanged;
            }
        };
    }

    private void OnInstanceChanged(RunningInstance inst)
    {
        var counts = _manager.Instances
            .GroupBy(i => i.State)
            .Select(g => $"{g.Key}: {g.Count()}");
        _icon.Text = ("Cryo: " + string.Join(", ", counts)).Truncate(127);
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem("Open launcher…", null, (_, _) => _openMain()));
        _menu.Items.Add(new ToolStripSeparator());

        if (_manager.Instances.Count == 0)
            _menu.Items.Add(new ToolStripMenuItem("(no instances configured)") { Enabled = false });
        else
            foreach (var inst in _manager.Instances)
            {
                var sub = new ToolStripMenuItem($"{inst.Entry.DisplayName}  [{inst.State}]");
                if (inst.State is InstanceState.Stopped or InstanceState.Crashed)
                    sub.DropDownItems.Add(new ToolStripMenuItem("Play",
                        null, async (_, _) => await _manager.LaunchAsync(inst)));
                if (inst.State == InstanceState.Hibernated)
                    sub.DropDownItems.Add(new ToolStripMenuItem("Wake up",
                        null, async (_, _) => await _manager.WakeAsync(inst)));
                if (inst.State == InstanceState.Ready)
                    sub.DropDownItems.Add(new ToolStripMenuItem("Hibernate now",
                        null, (_, _) => _manager.Hibernate(inst)));
                if (inst.State != InstanceState.Stopped)
                    sub.DropDownItems.Add(new ToolStripMenuItem("Force stop",
                        null, (_, _) => _manager.Kill(inst)));
                _menu.Items.Add(sub);
            }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Quit Cryo", null, (_, _) => _exit()));
    }

    /// <summary>Show a Windows tray balloon notification.</summary>
    public void Notify(string title, string text)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText  = text;
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception e) { Logger.Warn($"Notify failed: {e.Message}"); }
    }

    private static Icon LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
        if (File.Exists(path)) try { return new Icon(path); } catch { }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}

internal static class StringExt
{
    public static string Truncate(this string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";
}
