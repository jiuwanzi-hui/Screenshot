using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Screenshot.App.Core;

namespace Screenshot.App.Infrastructure;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripItem[] _menuItems;
    private bool _disposed;

    public TrayIconService(AppTheme theme = AppTheme.System)
    {
        var regionCaptureItem = new ToolStripMenuItem("区域截图");
        regionCaptureItem.Click += OnRegionCaptureClicked;

        var scrollCaptureItem = new ToolStripMenuItem("长截图");
        scrollCaptureItem.Click += OnScrollCaptureClicked;

        var videoRecordingItem = new ToolStripMenuItem("录制视频");
        videoRecordingItem.Click += OnVideoRecordingClicked;

        var openSettingsItem = new ToolStripMenuItem("打开设置");
        openSettingsItem.Click += OnOpenSettingsClicked;

        var historyItem = new ToolStripMenuItem("截图历史");
        historyItem.Click += OnHistoryClicked;

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += OnExitClicked;

        _contextMenu = new RoundedContextMenuStrip
        {
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular),
            ShowCheckMargin = false,
            ShowImageMargin = false,
            Padding = new Padding(4),
            DropShadowEnabled = true,
        };
        _menuItems =
        [
            regionCaptureItem,
            scrollCaptureItem,
            videoRecordingItem,
            historyItem,
            openSettingsItem,
            new ToolStripSeparator(),
            exitItem,
        ];
        _contextMenu.Items.AddRange(_menuItems);
        ApplyTheme(theme);

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _contextMenu,
            Icon = LoadApplicationIcon(),
            Text = AppMetadata.DisplayName,
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

    public event EventHandler? VideoRecordingRequested;

    public event EventHandler? HistoryRequested;

    public event EventHandler? ExitRequested;

    internal ContextMenuStrip ContextMenuForTesting => _contextMenu;

    internal Color HoverBackgroundForTesting =>
        ((TrayMenuRenderer)_contextMenu.Renderer).SelectionColor;

    internal Color HoverForegroundForTesting =>
        ((TrayMenuRenderer)_contextMenu.Renderer).ForegroundColor;

    internal static int CornerRadiusForTesting =>
        RoundedContextMenuStrip.CornerRadius;

    public void ApplyTheme(AppTheme theme)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var isDark = theme != AppTheme.Light;
        var background = ColorTranslator.FromHtml(isDark ? "#19292E" : "#F4FBFA");
        var foreground = ColorTranslator.FromHtml(isDark ? "#DDF2EF" : "#26464B");
        var accentMuted = ColorTranslator.FromHtml(isDark ? "#2E6762" : "#BFE8E2");
        var border = ColorTranslator.FromHtml(isDark ? "#6B8784" : "#8EB9B7");

        _contextMenu.BackColor = background;
        _contextMenu.ForeColor = foreground;
        _contextMenu.Renderer = new TrayMenuRenderer(
            background,
            foreground,
            accentMuted,
            border);
        foreach (var item in _menuItems)
        {
            item.BackColor = background;
            item.ForeColor = foreground;
            item.Padding = item is ToolStripSeparator
                ? Padding.Empty
                : new Padding(12, 7, 18, 7);
            item.Margin = item is ToolStripSeparator
                ? new Padding(6, 3, 6, 3)
                : new Padding(0, 1, 0, 1);
        }
    }

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
        _contextMenu.Dispose();
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

    private void OnVideoRecordingClicked(object? sender, EventArgs e)
    {
        VideoRecordingRequested?.Invoke(this, EventArgs.Empty);
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

    private sealed class TrayMenuColorTable : ProfessionalColorTable
    {
        private readonly Color _background;
        private readonly Color _selection;
        private readonly Color _border;

        public TrayMenuColorTable(
            Color background,
            Color selection,
            Color border)
        {
            _background = background;
            _selection = selection;
            _border = border;
            UseSystemColors = false;
        }

        public override Color ToolStripDropDownBackground => _background;
        public override Color MenuBorder => _border;
        public override Color MenuItemBorder => _border;
        public override Color MenuItemSelected => _selection;
        public override Color MenuItemSelectedGradientBegin => _selection;
        public override Color MenuItemSelectedGradientEnd => _selection;
        public override Color MenuItemPressedGradientBegin => _selection;
        public override Color MenuItemPressedGradientEnd => _selection;
        public override Color SeparatorDark => _border;
        public override Color SeparatorLight => _background;
        public override Color ImageMarginGradientBegin => _background;
        public override Color ImageMarginGradientMiddle => _background;
        public override Color ImageMarginGradientEnd => _background;
    }

    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly Color _background;
        private readonly Color _foreground;
        private readonly Color _selection;
        private readonly Color _border;

        public TrayMenuRenderer(
            Color background,
            Color foreground,
            Color selection,
            Color border)
            : base(new TrayMenuColorTable(background, selection, border))
        {
            _background = background;
            _foreground = foreground;
            _selection = selection;
            _border = border;
        }

        public Color SelectionColor => _selection;

        public Color ForegroundColor => _foreground;

        protected override void OnRenderToolStripBackground(
            ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(_background);
            using var path = CreateRoundedPath(
                e.ToolStrip.ClientRectangle,
                RoundedContextMenuStrip.CornerRadius);
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderToolStripBorder(
            ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip.Width < 2 || e.ToolStrip.Height < 2)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(_border);
            using var path = CreateRoundedPath(
                new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1),
                RoundedContextMenuStrip.CornerRadius);
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnRenderMenuItemBackground(
            ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || e.Item.Width < 4 || e.Item.Height < 4)
            {
                return;
            }

            var bounds = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(_selection);
            using var path = CreateRoundedPath(bounds, radius: 5);
            e.Graphics.FillPath(brush, path);
            using var pen = new Pen(_border);
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = _foreground;
            base.OnRenderItemText(e);
        }
    }

    private sealed class RoundedContextMenuStrip : ContextMenuStrip
    {
        public const int CornerRadius = 8;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateRoundedRegion();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateRoundedRegion();
        }

        private void UpdateRoundedRegion()
        {
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            using var path = CreateRoundedPath(ClientRectangle, CornerRadius);
            var previousRegion = Region;
            Region = new Region(path);
            previousRegion?.Dispose();
        }
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }

        var diameter = Math.Min(
            radius * 2,
            Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
