using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace Screenshot.App.Presentation;

internal sealed class ProfileReorderFeedback : IDisposable
{
    private readonly Border _row;
    private readonly double _originalOpacity;
    private readonly AdornerLayer? _layer;
    private readonly DragPreview? _preview;
    private readonly Dictionary<object, double> _positions;
    private bool _disposed;
    internal ItemsControl Owner { get; }

    internal ProfileReorderFeedback(Border row, ItemsControl owner, Point pointer)
    {
        _row = row;
        Owner = owner;
        _positions = CapturePositions(owner);
        _originalOpacity = row.Opacity;
        _layer = AdornerLayer.GetAdornerLayer(owner);
        if (_layer is not null)
        {
            _preview = new DragPreview(owner, row, pointer);
            _layer.Add(_preview);
            row.Opacity = .45;
        }
        row.Unloaded += OnRowUnloaded;
        owner.PreviewDragOver += OnDragOver;
    }

    internal void Update(Point pointer) => _preview?.Move(pointer);

    internal void AnimatePlacement()
    {
        Dispose();
        Owner.UpdateLayout();
        if (!UiTransitions.IsEnabled) return;
        for (var index = 0; index < Owner.Items.Count; index++)
        {
            if (Owner.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container ||
                !_positions.TryGetValue(Owner.Items[index], out var oldY)) continue;
            var delta = oldY - container.TranslatePoint(new Point(), Owner).Y;
            if (Math.Abs(delta) < .5) continue;
            AnimateOffset(container, delta);
        }
    }

    internal static void AnimateOffset(FrameworkElement container, double offset)
    {
        if (!UiTransitions.IsEnabled) return;
        var transform = new TranslateTransform(0, 0);
        container.RenderTransform = transform;
        var animation = new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        void Finish(object? sender, EventArgs args)
        {
            animation.Completed -= Finish;
            container.Unloaded -= Finish;
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            if (ReferenceEquals(container.RenderTransform, transform)) container.ClearValue(UIElement.RenderTransformProperty);
        }
        animation.Completed += Finish;
        container.Unloaded += Finish;
        transform.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    private static Dictionary<object, double> CapturePositions(ItemsControl owner)
    {
        var positions = new Dictionary<object, double>();
        for (var index = 0; index < owner.Items.Count; index++)
            if (owner.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
                positions[owner.Items[index]] = container.TranslatePoint(new Point(), owner).Y;
        return positions;
    }

    private void OnRowUnloaded(object sender, RoutedEventArgs args) => Dispose();
    private void OnDragOver(object sender, System.Windows.DragEventArgs args) => Update(args.GetPosition(Owner));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _row.Unloaded -= OnRowUnloaded;
        Owner.PreviewDragOver -= OnDragOver;
        _row.Opacity = _originalOpacity;
        if (_preview is not null) _layer?.Remove(_preview);
    }

    private sealed class DragPreview : Adorner
    {
        private readonly Border _source;
        private readonly VisualBrush _content;
        private readonly Vector _grabOffset;
        private Point _position;

        internal DragPreview(ItemsControl owner, Border source, Point pointer) : base(owner)
        {
            IsHitTestVisible = false;
            _source = source;
            _content = new VisualBrush(source.Child) { Stretch = Stretch.Fill };
            _grabOffset = pointer - source.TranslatePoint(new Point(), owner);
            Move(pointer);
        }

        internal void Move(Point pointer) { _position = pointer - _grabOffset; InvalidateVisual(); }

        protected override void OnRender(DrawingContext context)
        {
            var bounds = new Rect(_position, _source.RenderSize);
            context.PushOpacity(.94);
            context.DrawRoundedRectangle(_source.Background,
                new Pen(_source.TryFindResource("AppAccentBrush") as System.Windows.Media.Brush ?? _source.BorderBrush, 1.5), bounds, 6, 6);
            var inset = _source.Padding;
            context.DrawRectangle(_content, null, new Rect(bounds.X + inset.Left + 1, bounds.Y + inset.Top + 1,
                Math.Max(1, bounds.Width - inset.Left - inset.Right - 2), Math.Max(1, bounds.Height - inset.Top - inset.Bottom - 2)));
            context.Pop();
        }
    }
}
