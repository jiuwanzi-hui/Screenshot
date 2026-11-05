using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Panel = System.Windows.Controls.Panel;
using Control = System.Windows.Controls.Control;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Screenshot.App.Presentation;

/// <summary>A travelling selection plate, painted beneath the group's controls.</summary>
public static class SelectionMotion
{
    public static readonly DependencyProperty IsScopeProperty = DependencyProperty.RegisterAttached(
        "IsScope", typeof(bool), typeof(SelectionMotion), new PropertyMetadata(false));
    public static readonly DependencyProperty SurfaceNameProperty = DependencyProperty.RegisterAttached(
        "SurfaceName", typeof(string), typeof(SelectionMotion), new PropertyMetadata(null, OnConfigurationChanged));
    public static readonly DependencyProperty ThemeVisibilityProperty = DependencyProperty.RegisterAttached(
        "ThemeVisibility", typeof(Visibility), typeof(SelectionMotion), new PropertyMetadata(Visibility.Collapsed, OnConfigurationChanged));
    public static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached(
        "Fill", typeof(Brush), typeof(SelectionMotion), new PropertyMetadata(null, OnPaintChanged));
    public static readonly DependencyProperty EdgeProperty = DependencyProperty.RegisterAttached(
        "Edge", typeof(Brush), typeof(SelectionMotion), new PropertyMetadata(null, OnPaintChanged));
    private static readonly DependencyPropertyKey IsHostedPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsHosted", typeof(bool), typeof(SelectionMotion), new PropertyMetadata(false));
    public static readonly DependencyProperty IsHostedProperty = IsHostedPropertyKey.DependencyProperty;
    private static readonly ConditionalWeakTable<Control, Member> Members = new();
    private static readonly ConditionalWeakTable<Panel, Host> Hosts = new();

    public static string? GetSurfaceName(DependencyObject value) => (string?)value.GetValue(SurfaceNameProperty);
    public static bool GetIsScope(DependencyObject value) => (bool)value.GetValue(IsScopeProperty);
    public static void SetIsScope(DependencyObject value, bool enabled) => value.SetValue(IsScopeProperty, enabled);
    public static void SetSurfaceName(DependencyObject value, string name) => value.SetValue(SurfaceNameProperty, name);
    public static Visibility GetThemeVisibility(DependencyObject value) => (Visibility)value.GetValue(ThemeVisibilityProperty);
    public static void SetThemeVisibility(DependencyObject value, Visibility visibility) => value.SetValue(ThemeVisibilityProperty, visibility);
    public static Brush? GetFill(DependencyObject value) => (Brush?)value.GetValue(FillProperty);
    public static void SetFill(DependencyObject value, Brush brush) => value.SetValue(FillProperty, brush);
    public static Brush? GetEdge(DependencyObject value) => (Brush?)value.GetValue(EdgeProperty);
    public static void SetEdge(DependencyObject value, Brush brush) => value.SetValue(EdgeProperty, brush);
    public static bool GetIsHosted(DependencyObject value) => (bool)value.GetValue(IsHostedProperty);

    private static void OnConfigurationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Control control) return;
        if (e.Property == SurfaceNameProperty && e.OldValue is null)
        {
            control.Loaded += OnLoaded;
            control.Unloaded += OnUnloaded;
            control.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnSelected));
            control.AddHandler(Selector.SelectedEvent, new RoutedEventHandler(OnSelected));
            control.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnDeselected));
            control.AddHandler(Selector.UnselectedEvent, new RoutedEventHandler(OnDeselected));
        }
        if (control.IsLoaded) Register(control);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource)) Register((Control)sender);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource)) Unregister((Control)sender);
    }

    internal static void Register(Control control)
    {
        Unregister(control);
        if (GetThemeVisibility(control) != Visibility.Visible || GetSurfaceName(control) is not { Length: > 0 } name) return;
        control.ApplyTemplate();
        if (control.Template?.FindName(name, control) is not Border surface) return;
        DependencyObject? parent = VisualTreeHelper.GetParent(control);
        Panel? panel = null;
        while (parent is not null)
        {
            if (parent is Panel candidate)
            {
                panel ??= candidate;
                if (GetIsScope(candidate)) { panel = candidate; break; }
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
        if (panel is null) return;
        var host = Hosts.GetValue(panel, static panel => new Host(panel));
        var member = new Member(control, surface, host, control is RadioButton radio ? radio.GroupName : string.Empty);
        Members.Add(control, member);
        host.Add(member);
        control.SetValue(IsHostedPropertyKey, true);
    }

    internal static void Unregister(Control control)
    {
        if (Members.TryGetValue(control, out var member))
        {
            member.Host.Remove(member);
            Members.Remove(control);
        }
        control.SetValue(IsHostedPropertyKey, false);
    }

    private static void OnSelected(object sender, RoutedEventArgs e)
    {
        if (sender is Control control && ReferenceEquals(sender, e.OriginalSource) && Members.TryGetValue(control, out var member))
            member.Host.Select(member, UiTransitions.IsEnabled);
    }

    private static void OnDeselected(object sender, RoutedEventArgs e)
    {
        if (sender is not Control control || !ReferenceEquals(sender, e.OriginalSource) || !Members.TryGetValue(control, out var member)) return;
        // Radio groups uncheck the old button before checking the new one.
        // Defer only cleanup; tool commands and selection run immediately.
        _ = control.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => member.Host.ClearIfUnselected(member.Group));
    }

    private static void OnPaintChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Control control && Members.TryGetValue(control, out var member) && member.Selected)
            member.Host.Select(member, false);
    }

    internal static Rect CurrentBounds(Control control) => Members.TryGetValue(control, out var member)
        ? member.Host.Bounds(member.Group) : Rect.Empty;

    internal static void Move(Control control, bool animate)
    {
        if (Members.TryGetValue(control, out var member)) member.Host.Select(member, animate);
    }

    private sealed record Member(Control Control, Border Surface, Host Host, string Group)
    {
        internal bool Selected => Control switch
        {
            ToggleButton toggle => toggle.IsChecked == true,
            ListBoxItem item => item.IsSelected,
            TabItem tab => tab.IsSelected,
            _ => false,
        };
        internal Rect Bounds => new(Surface.TranslatePoint(new System.Windows.Point(), Host.Panel), Surface.RenderSize);
    }

    private sealed class Plate
    {
        internal readonly RectangleGeometry Geometry = new();
        internal readonly GeometryDrawing Drawing;
        internal Member? Selected;
        internal Rect Target = Rect.Empty;
        internal int Revision;
        internal Plate() => Drawing = new GeometryDrawing(null, null, Geometry);
    }

    private sealed class Host
    {
        internal readonly Panel Panel;
        private readonly Brush? _background;
        private readonly DrawingBrush _paint;
        private readonly DrawingGroup _drawing = new();
        private readonly RectangleGeometry _extent = new();
        private readonly Dictionary<string, Plate> _plates = new(StringComparer.Ordinal);
        private readonly HashSet<Member> _members = new();

        internal Host(Panel panel)
        {
            Panel = panel;
            _background = panel.Background;
            _drawing.Children.Add(new GeometryDrawing(_background ?? Brushes.Transparent, null, _extent));
            _paint = new DrawingBrush(_drawing) { Stretch = Stretch.Fill };
            panel.SetCurrentValue(Panel.BackgroundProperty, _paint);
            panel.SizeChanged += OnSizeChanged;
            panel.IsVisibleChanged += OnVisibilityChanged;
            UpdateExtent();
        }

        internal void Add(Member member)
        {
            _members.Add(member);
            member.Control.SizeChanged += OnSizeChanged;
            if (member.Selected) Select(member, false);
        }

        internal Rect Bounds(string group) => _plates.TryGetValue(group, out var plate) ? plate.Geometry.Rect : Rect.Empty;

        internal void Select(Member member, bool animate)
        {
            if (!_members.Contains(member)) return;
            var target = member.Bounds;
            if (target.Width <= 0 || target.Height <= 0) return;
            // Keep the outline inside the panel so brush bounds never stretch.
            target.Inflate(-.5, -.5);
            if (!_plates.TryGetValue(member.Group, out var plate))
            {
                plate = new Plate();
                _plates.Add(member.Group, plate);
                _drawing.Children.Add(plate.Drawing);
            }
            var from = plate.Geometry.Rect;
            var hadSelection = plate.Selected is not null && !from.IsEmpty;
            plate.Selected = member;
            plate.Target = target;
            plate.Drawing.Brush = GetFill(member.Control);
            plate.Drawing.Pen = new Pen(GetEdge(member.Control), 1);
            plate.Geometry.RadiusX = plate.Geometry.RadiusY = member.Surface.CornerRadius.TopLeft;
            var revision = ++plate.Revision;
            plate.Geometry.BeginAnimation(RectangleGeometry.RectProperty, null);
            plate.Geometry.Rect = target;
            if (animate && hadSelection && from != target)
            {
                // Retarget from the current animated position, including fast
                // repeated clicks. No transforms touch text or input bounds.
                // A newly scheduled clock starts on the next render tick.
                // Retain the current position until that tick, not the end.
                plate.Geometry.Rect = from;
                var animation = new RectAnimation(from, target, TimeSpan.FromMilliseconds(220))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
                animation.Completed += (_, _) =>
                {
                    if (plate.Revision != revision) return;
                    plate.Geometry.Rect = target;
                    plate.Geometry.BeginAnimation(RectangleGeometry.RectProperty, null);
                };
                plate.Geometry.BeginAnimation(RectangleGeometry.RectProperty, animation);
            }
        }

        internal void ClearIfUnselected(string group)
        {
            if (_members.Any(member => member.Group == group && member.Selected)) return;
            if (_plates.Remove(group, out var plate))
            {
                plate.Geometry.BeginAnimation(RectangleGeometry.RectProperty, null);
                _drawing.Children.Remove(plate.Drawing);
            }
        }

        internal void Remove(Member member)
        {
            member.Control.SizeChanged -= OnSizeChanged;
            _members.Remove(member);
            ClearIfUnselected(member.Group);
            if (_members.Count != 0) return;
            foreach (var plate in _plates.Values) plate.Geometry.BeginAnimation(RectangleGeometry.RectProperty, null);
            Panel.SizeChanged -= OnSizeChanged;
            Panel.IsVisibleChanged -= OnVisibilityChanged;
            if (ReferenceEquals(Panel.Background, _paint)) Panel.SetCurrentValue(Panel.BackgroundProperty, _background);
            Hosts.Remove(Panel);
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateExtent();
            foreach (var member in _members.Where(member => member.Selected))
            {
                var target = member.Bounds;
                target.Inflate(-.5, -.5);
                if (_plates.TryGetValue(member.Group, out var plate))
                {
                    // A page scrollbar can resize the group without moving its
                    // tabs. Do not cancel a live selection animation for that.
                    if (plate.Target == target) continue;
                    Select(member, false);
                }
                else Select(member, false);
            }
        }
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Snap();
        private void UpdateExtent() => _extent.Rect = new Rect(Panel.RenderSize);
        private void Snap()
        {
            foreach (var member in _members.Where(member => member.Selected)) Select(member, false);
        }
    }
}
