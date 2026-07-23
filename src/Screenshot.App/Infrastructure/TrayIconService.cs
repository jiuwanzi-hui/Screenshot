using System.Drawing;
using System.Windows.Forms;
using Screenshot.App.Core;

namespace Screenshot.App.Infrastructure;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private bool _disposed;

    public TrayIconService()
    {
        var regionCaptureItem = new ToolStripMenuItem("区域截图");
        regionCaptureItem.Click += OnRegionCaptureClicked;

        var scrollCaptureItem = new ToolStripMenuItem("长截图");
        scrollCaptureItem.Click += OnScrollCaptureClicked;

        var openSettingsItem = new ToolStripMenuItem("打开设置");
        openSettingsItem.Click += OnOpenSettingsClicked;

        var historyItem = new ToolStripMenuItem("截图历史");
        historyItem.Click += OnHistoryClicked;

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += OnExitClicked;

        var contextMenu = new ContextMenuStrip
        {
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular),
        };
        contextMenu.Items.Add(regionCaptureItem);
        contextMenu.Items.Add(scrollCaptureItem);
        contextMenu.Items.Add(historyItem);
        contextMenu.Items.Add(openSettingsItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = contextMenu,
            Icon = LoadApplicationIcon(),
            Text = AppMetadata.ApplicationName,
            Visible = false,
        };
        _notifyIcon.DoubleClick += OnTrayIconDoubleClick;
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                var icon = Icon.ExtractAssociatedIcon(executablePath);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
            // Fall back when the process icon cannot be extracted during startup.
        }

        return SystemIcons.Application;
    }

    public event EventHandler? OpenSettingsRequested;

    public event EventHandler? RegionCaptureRequested;

    public event EventHandler? ScrollCaptureRequested;

    public event EventHandler? HistoryRequested;

    public event EventHandler? ExitRequested;

    public void SetVisible(bool isVisible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _notifyIcon.Visible = isVisible;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.DoubleClick -= OnTrayIconDoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private void OnOpenSettingsClicked(object? sender, EventArgs e)
    {
        OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRegionCaptureClicked(object? sender, EventArgs e)
    {
        RegionCaptureRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnScrollCaptureClicked(object? sender, EventArgs e)
    {
        ScrollCaptureRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnHistoryClicked(object? sender, EventArgs e)
    {
        HistoryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnTrayIconDoubleClick(object? sender, EventArgs e)
    {
        OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
    }
}
