using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;
using Screenshot.App.Presentation;

namespace Screenshot.App.Infrastructure;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripItem[] _menuItems;
    private readonly ToolStripMenuItem _hidePinnedImagesItem;
    private Bitmap? _glassBackground;
    private AppTheme? _appliedTheme;
    private GlassTrayMenuWindow? _glassMenu;
    private bool _disposed;
    private readonly TrayClickSequence _clicks = new();
    private readonly Dictionary<ToolStripItem, string> _menuLabels = [];
    private readonly System.Windows.Threading.DispatcherTimer _singleClickTimer = new();

    public TrayIconService(AppTheme theme = AppTheme.AuroraMist)
    {
        var regionCaptureItem = new ToolStripMenuItem("区域截图");
        regionCaptureItem.Click += OnRegionCaptureClicked;

        var scrollCaptureItem = new ToolStripMenuItem("长截图");
        scrollCaptureItem.Click += OnScrollCaptureClicked;

        var videoRecordingItem = new ToolStripMenuItem("录制视频");
        videoRecordingItem.Click += OnVideoRecordingClicked;

        var openSettingsItem = new ToolStripMenuItem("打开设置");
        openSettingsItem.Click += OnOpenSettingsClicked;

        var realtimeItem = new ToolStripMenuItem("实时翻译 / 停止");
        realtimeItem.Click += (_, _) => RealtimeTranslationRequested?.Invoke(this, EventArgs.Empty);

        var historyItem = new ToolStripMenuItem("历史查看");
        historyItem.Click += OnHistoryClicked;

        _hidePinnedImagesItem = new ToolStripMenuItem("最小化全部钉图");
        _hidePinnedImagesItem.Click += OnHidePinnedImagesClicked;

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += OnExitClicked;

        _contextMenu = new RoundedContextMenuStrip
        {
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular),
            ShowCheckMargin = false,
            ShowImageMargin = false,
            Padding = new Padding(2),
            DropShadowEnabled = true,
        };
        _menuItems =
        [
            regionCaptureItem,
            scrollCaptureItem,
            videoRecordingItem,
            realtimeItem,
            historyItem,
            _hidePinnedImagesItem,
            openSettingsItem,
            new ToolStripSeparator(),
            exitItem,
        ];
        _contextMenu.Items.AddRange(_menuItems);
        foreach (var item in _menuItems) _menuLabels[item] = item.Text ?? string.Empty;
        UiLocalization.Changed += OnInterfaceLanguageChanged;
        OnInterfaceLanguageChanged(null, EventArgs.Empty);
        _contextMenu.Closed += OnContextMenuClosed;
        UpdatePinnedImageCommands(
            hasPinnedImages: false,
            hasHiddenPinnedImages: false);
        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _contextMenu,
            Icon = LoadApplicationIcon(),
            Text = AppMetadata.DisplayName,
            Visible = false,
        };
        _singleClickTimer.Interval = TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime);
        _singleClickTimer.Tick += OnSingleClickTimer;
        _notifyIcon.MouseDoubleClick += OnTrayIconDoubleClick;
        _notifyIcon.MouseUp += OnTrayMouseUp;
        ApplyTheme(theme);
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

    public event EventHandler? RealtimeTranslationRequested;

    public event EventHandler? HistoryRequested;

    public event EventHandler? HidePinnedImagesRequested;


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
        if (_appliedTheme == theme) return;
        _glassMenu?.Close();
        _notifyIcon.ContextMenuStrip = theme == AppTheme.GlassMist ? null : _contextMenu;
        var colors = AppThemeManager.GetChromeColors(theme);
        var background = ColorTranslator.FromHtml(colors.Background);
        var foreground = ColorTranslator.FromHtml(colors.Foreground);
        var accentMuted = ColorTranslator.FromHtml(colors.Selection);
        var border = ColorTranslator.FromHtml(colors.Border);
        var previousBackground = _glassBackground;
        _glassBackground = null;

        _contextMenu.BackColor = background;
        _contextMenu.ForeColor = foreground;
        _contextMenu.Renderer = new TrayMenuRenderer(
            background,
            foreground,
            accentMuted,
            border,
            GetGlassBackground);
        foreach (var item in _menuItems)
        {
            item.BackColor = background;
            item.ForeColor = foreground;
            item.Padding = item is ToolStripSeparator
                ? Padding.Empty
                : new Padding(8, 6, 10, 6);
            item.Margin = item is ToolStripSeparator
                ? new Padding(4, 2, 4, 2)
                : new Padding(0, 1, 0, 1);
        }
        _appliedTheme = theme;
        previousBackground?.Dispose();
        _contextMenu.Invalidate();
    }

    private void OnContextMenuClosed(object? sender, ToolStripDropDownClosedEventArgs e)
    {
        _glassBackground?.Dispose();
        _glassBackground = null;
    }

    private Bitmap? GetGlassBackground(Size size)
    {
        if (_disposed || _appliedTheme != AppTheme.GlassInk || size.Width <= 0 || size.Height <= 0) return null;
        if (_glassBackground?.Size == size) return _glassBackground;
        _glassBackground?.Dispose();
        _glassBackground = null;
        // The menu owns only its actual pixel-sized texture, while visible.
        // Avoid three 1000px copies (WPF, managed transfer, GDI) at startup.
        var visual = new System.Windows.Media.DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(LiquidGlassMaterial.CreateVectorBackground(true), null,
                new System.Windows.Rect(0, 0, size.Width, size.Height));
        var source = new RenderTargetBitmap(size.Width, size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        source.Render(visual);
        source.Freeze();
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        var bitmap = new Bitmap(source.PixelWidth, source.PixelHeight, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }
        return _glassBackground = bitmap;
    }

    public void SetVisible(bool isVisible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _notifyIcon.Visible = isVisible;
    }

    public void UpdatePinnedImageCommands(
        bool hasPinnedImages,
        bool hasHiddenPinnedImages)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _hidePinnedImagesItem.Available =
            hasPinnedImages;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _glassMenu?.Close();
        _notifyIcon.MouseUp -= OnTrayMouseUp;
        _notifyIcon.MouseDoubleClick -= OnTrayIconDoubleClick;
        _singleClickTimer.Stop();
        _singleClickTimer.Tick -= OnSingleClickTimer;
        _clicks.Cancel();
        UiLocalization.Changed -= OnInterfaceLanguageChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Closed -= OnContextMenuClosed;
        _contextMenu.Dispose();
        _glassBackground?.Dispose();
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

    private void OnHidePinnedImagesClicked(object? sender, EventArgs e)
    {
        HidePinnedImagesRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnTrayIconDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _disposed) return;
        _singleClickTimer.Stop();
        _clicks.DoubleClicked();
        OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSingleClickTimer(object? sender, EventArgs e)
    {
        _singleClickTimer.Stop();
        if (!_disposed && _clicks.CompleteSingleClick()) RegionCaptureRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnInterfaceLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var (item, source) in _menuLabels) item.Text = UiLocalization.Get(source);
        _glassMenu?.Close();
    }

    private void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (_disposed) return;
        if (e.Button == MouseButtons.Left)
        {
            if (_clicks.LeftReleased())
            {
                _singleClickTimer.Stop();
                _singleClickTimer.Interval = TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime);
                _singleClickTimer.Start();
            }
            return;
        }
        _singleClickTimer.Stop(); _clicks.Cancel();
        if (e.Button != MouseButtons.Right || _appliedTheme != AppTheme.GlassMist || _disposed) return;
        _glassMenu?.Close();
        var menu = CreateGlassMenu();
        _glassMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_glassMenu, menu)) _glassMenu = null; };
        menu.ShowAt(Cursor.Position);
    }

    internal GlassTrayMenuWindow CreateGlassMenu() => new(_menuItems.Where(item => item.Available)
        .Select(item => item is ToolStripSeparator
            ? new TrayMenuEntry(null, false, null)
            : new TrayMenuEntry(item.Text, item.Enabled, item.PerformClick)));

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
        private readonly Func<Size, Bitmap?> _glassBackground;

        public TrayMenuRenderer(
            Color background,
            Color foreground,
            Color selection,
            Color border,
            Func<Size, Bitmap?> glassBackground)
            : base(new TrayMenuColorTable(background, selection, border))
        {
            _background = background;
            _foreground = foreground;
            _selection = selection;
            _border = border;
            _glassBackground = glassBackground;
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
            if (_glassBackground(e.ToolStrip.ClientSize) is { } background)
            {
                var state = e.Graphics.Save();
                try
                {
                    e.Graphics.SetClip(path, CombineMode.Intersect);
                    e.Graphics.DrawImage(background, e.ToolStrip.ClientRectangle);
                }
                finally { e.Graphics.Restore(state); }
            }
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
        // NotifyIcon positions the menu using its measured size. Changing that
        // size in SetBoundsCore shifts its anchored edge away from the cursor,
        // especially with larger fonts or mixed monitor DPI.

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
