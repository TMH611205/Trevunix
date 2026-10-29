using System.Windows;
using Trevunix.Core.Monitoring;
using Trevunix.Localization;
using Forms = System.Windows.Forms;

namespace Trevunix.Services;

/// <summary>
///     The Trevunix icon in the notification area: live CPU and RAM in its tooltip, a menu for
///     the quick actions, and balloon notifications. WPF has no tray icon, so this uses the
///     WinForms one.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly SystemMonitor _monitor = new();
    private DateTime _sampledAt;

    /// <param name="withMenu">False for the short-lived icon of a scheduled cleanup, which only shows a notification.</param>
    public TrayService(bool withMenu = true)
    {
        _icon = new Forms.NotifyIcon { Icon = LoadIcon(), Text = "Trevunix" };

        if (withMenu)
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add(Tr.Get("Tray.Open"), null, (_, _) => OpenRequested?.Invoke());
            menu.Items.Add(Tr.Get("Tray.FreeRam"), null, (_, _) => FreeRamRequested?.Invoke());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Tr.Get("Tray.Exit"), null, (_, _) => ExitRequested?.Invoke());
            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();

            // The tooltip only shows while hovered, so refresh it then instead of on a timer.
            _icon.MouseMove += (_, _) => UpdateTooltip();
            _monitor.Sample();
            _sampledAt = DateTime.UtcNow;
        }

        _icon.Visible = true;
    }

    public event Action? OpenRequested;
    public event Action? FreeRamRequested;
    public event Action? ExitRequested;

    public void Notify(string title, string message, bool warning = false) =>
        _icon.ShowBalloonTip(
            5000,
            title,
            message,
            warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info
        );

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }

    private void UpdateTooltip()
    {
        // CPU use is measured between two samples; too short a gap gives a meaningless number.
        if (DateTime.UtcNow - _sampledAt < TimeSpan.FromSeconds(1))
            return;
        _sampledAt = DateTime.UtcNow;
        var sample = _monitor.Sample();
        // The tooltip holds at most 127 characters.
        var text = Tr.Format("Tray.Tooltip", (int)sample.CpuPercent, (int)sample.MemoryPercent);
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/Logo.ico")
        );
        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}
