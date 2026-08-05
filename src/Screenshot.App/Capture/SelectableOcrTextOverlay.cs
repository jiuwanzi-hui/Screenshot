using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Screenshot.App.Text;
using WpfColor = System.Windows.Media.Color;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace Screenshot.App.Capture;

internal sealed class SelectableOcrTextOverlay : FrameworkElement
{
    private readonly List<SelectableSegment> _segments = [];
    private readonly SolidColorBrush _selectionBrush;
    private TextPosition? _anchor;
    private TextPosition? _active;

    public SelectableOcrTextOverlay(
        IReadOnlyList<OcrWordRegion> words,
        double scaleX,
        double scaleY,
        WpfColor accentColor)
    {
        ArgumentNullException.ThrowIfNull(words);
        Focusable = true;
        Cursor = System.Windows.Input.Cursors.IBeam;
        _selectionBrush = new SolidColorBrush(WpfColor.FromArgb(
            112,
            accentColor.R,
            accentColor.G,
            accentColor.B));
        _selectionBrush.Freeze();

        _segments.AddRange(OrderByTextLine(words)
            .Select(entry => new SelectableSegment(
                entry.Word.Text.Trim(),
                new Rect(
                    entry.Word.X * scaleX,
                    entry.Word.Y * scaleY,
                    Math.Max(2, entry.Word.Width * scaleX),
                    Math.Max(2, entry.Word.Height * scaleY)),
                entry.LineIndex)));
    }

    public string SelectedText
    {
        get
        {
            if (!TryGetOrderedSelection(out var start, out var end))
            {
                return string.Empty;
            }

            var parts = new List<string>();
            var previousSegmentIndex = -1;
            for (var index = start.SegmentIndex; index <= end.SegmentIndex; index++)
            {
                var segment = _segments[index];
                var firstCharacter = index == start.SegmentIndex
                    ? start.CharacterIndex
                    : 0;
                var lastCharacter = index == end.SegmentIndex
                    ? end.CharacterIndex
                    : segment.Text.Length;
                firstCharacter = Math.Clamp(firstCharacter, 0, segment.Text.Length);
                lastCharacter = Math.Clamp(lastCharacter, firstCharacter, segment.Text.Length);
                if (lastCharacter <= firstCharacter)
                {
                    continue;
                }

                if (previousSegmentIndex >= 0)
                {
                    var previous = _segments[previousSegmentIndex];
                    parts.Add(previous.LineIndex == segment.LineIndex
                        ? GetInlineSeparator(previous.Text, segment.Text)
                        : Environment.NewLine);
                }

                parts.Add(segment.Text[firstCharacter..lastCharacter]);
                previousSegmentIndex = index;
            }

            return string.Concat(parts);
        }
    }

    internal IReadOnlyList<Rect> SegmentBounds =>
        _segments.Select(segment => segment.Bounds).ToArray();

    internal void SelectAllText()
    {
        if (_segments.Count == 0)
        {
            return;
        }

        _anchor = new TextPosition(0, 0);
        _active = new TextPosition(
            _segments.Count - 1,
            _segments[^1].Text.Length);
        InvalidateVisual();
    }

    internal void SelectTextRange(
        int startSegmentIndex,
        int startCharacterIndex,
        int endSegmentIndex,
        int endCharacterIndex)
    {
        _anchor = new TextPosition(startSegmentIndex, startCharacterIndex);
        _active = new TextPosition(endSegmentIndex, endCharacterIndex);
        InvalidateVisual();
    }

    protected override void OnPreviewKeyDown(WpfKeyEventArgs e)
    {
        if (e.Key == Key.A &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SelectAllText();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override HitTestResult? HitTestCore(
        PointHitTestParameters hitTestParameters)
    {
        return FindContainingSegment(hitTestParameters.HitPoint) >= 0
            ? new PointHitTestResult(this, hitTestParameters.HitPoint)
            : null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = e.GetPosition(this);
        var segmentIndex = FindContainingSegment(point);
        if (segmentIndex < 0)
        {
            return;
        }

        Focus();
        _anchor = GetTextPosition(segmentIndex, point.X, trailingEdge: false);
        _active = _anchor;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(WpfMouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || _anchor is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        var segmentIndex = FindNearestSegment(point);
        if (segmentIndex < 0)
        {
            return;
        }

        _active = GetTextPosition(segmentIndex, point.X, trailingEdge: true);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured)
        {
            return;
        }

        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!TryGetOrderedSelection(out var start, out var end))
        {
            return;
        }

        for (var index = start.SegmentIndex; index <= end.SegmentIndex; index++)
        {
            var segment = _segments[index];
            var firstCharacter = index == start.SegmentIndex
                ? start.CharacterIndex
                : 0;
            var lastCharacter = index == end.SegmentIndex
                ? end.CharacterIndex
                : segment.Text.Length;
            firstCharacter = Math.Clamp(firstCharacter, 0, segment.Text.Length);
            lastCharacter = Math.Clamp(lastCharacter, firstCharacter, segment.Text.Length);
            if (lastCharacter <= firstCharacter)
            {
                continue;
            }

            var characterWidth = segment.Bounds.Width / segment.Text.Length;
            var highlight = new Rect(
                segment.Bounds.X + (firstCharacter * characterWidth),
                segment.Bounds.Y,
                Math.Max(1, (lastCharacter - firstCharacter) * characterWidth),
                segment.Bounds.Height);
            drawingContext.DrawRoundedRectangle(
                _selectionBrush,
                null,
                highlight,
                2,
                2);
        }
    }

    private bool TryGetOrderedSelection(
        out TextPosition start,
        out TextPosition end)
    {
        start = default;
        end = default;
        if (_anchor is not { } anchor ||
            _active is not { } active ||
            anchor == active)
        {
            return false;
        }

        if (Compare(anchor, active) <= 0)
        {
            start = anchor;
            end = active;
        }
        else
        {
            start = active;
            end = anchor;
        }

        return true;
    }

    private int FindContainingSegment(WpfPoint point)
    {
        for (var index = 0; index < _segments.Count; index++)
        {
            var bounds = _segments[index].Bounds;
            bounds.Inflate(2, 3);
            if (bounds.Contains(point))
            {
                return index;
            }
        }

        return -1;
    }

    private int FindNearestSegment(WpfPoint point)
    {
        var containing = FindContainingSegment(point);
        if (containing >= 0)
        {
            return containing;
        }

        var bestIndex = -1;
        var bestDistance = double.MaxValue;
        for (var index = 0; index < _segments.Count; index++)
        {
            var bounds = _segments[index].Bounds;
            var distanceX = point.X < bounds.Left
                ? bounds.Left - point.X
                : point.X > bounds.Right
                    ? point.X - bounds.Right
                    : 0;
            var distanceY = point.Y < bounds.Top
                ? bounds.Top - point.Y
                : point.Y > bounds.Bottom
                    ? point.Y - bounds.Bottom
                    : 0;
            var distance = (distanceX * distanceX) + (distanceY * distanceY);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private TextPosition GetTextPosition(
        int segmentIndex,
        double x,
        bool trailingEdge)
    {
        var segment = _segments[segmentIndex];
        var relative = Math.Clamp(
            (x - segment.Bounds.Left) / Math.Max(1, segment.Bounds.Width),
            0,
            1);
        var rawIndex = relative * segment.Text.Length;
        var characterIndex = trailingEdge
            ? (int)Math.Ceiling(rawIndex)
            : (int)Math.Floor(rawIndex);
        return new TextPosition(
            segmentIndex,
            Math.Clamp(characterIndex, 0, segment.Text.Length));
    }

    private static int Compare(TextPosition first, TextPosition second)
    {
        var segmentComparison = first.SegmentIndex.CompareTo(second.SegmentIndex);
        return segmentComparison != 0
            ? segmentComparison
            : first.CharacterIndex.CompareTo(second.CharacterIndex);
    }

    private static IEnumerable<(OcrWordRegion Word, int LineIndex)>
        OrderByTextLine(IReadOnlyList<OcrWordRegion> words)
    {
        var lines = new List<List<OcrWordRegion>>();
        foreach (var word in words
                     .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                     .OrderBy(word => word.Y)
                     .ThenBy(word => word.X))
        {
            var matchingLine = lines
                .Select((line, index) => new
                {
                    Index = index,
                    Overlap = GetVerticalOverlapRatio(line, word),
                })
                .Where(candidate => candidate.Overlap >= 0.45)
                .OrderByDescending(candidate => candidate.Overlap)
                .Select(candidate => candidate.Index)
                .DefaultIfEmpty(-1)
                .First();
            if (matchingLine < 0)
            {
                lines.Add([word]);
            }
            else
            {
                lines[matchingLine].Add(word);
            }
        }

        return lines
            .OrderBy(line => line.Min(word => word.Y))
            .SelectMany((line, lineIndex) => line
                .OrderBy(word => word.X)
                .Select(word => (word, lineIndex)));
    }

    private static double GetVerticalOverlapRatio(
        IReadOnlyList<OcrWordRegion> line,
        OcrWordRegion word)
    {
        var lineTop = line.Min(item => item.Y);
        var lineBottom = line.Max(item => item.Y + item.Height);
        var overlap = Math.Max(
            0,
            Math.Min(lineBottom, word.Y + word.Height) -
            Math.Max(lineTop, word.Y));
        return overlap / Math.Max(1, Math.Min(lineBottom - lineTop, word.Height));
    }

    private static string GetInlineSeparator(string previous, string current)
    {
        return EndsWithCjk(previous) || StartsWithCjk(current) ||
               EndsWithOpeningPunctuation(previous) ||
               StartsWithClosingPunctuation(current)
            ? string.Empty
            : " ";
    }

    private static bool EndsWithCjk(string value) =>
        value.Length > 0 && IsCjk(value[^1]);

    private static bool StartsWithCjk(string value) =>
        value.Length > 0 && IsCjk(value[0]);

    private static bool IsCjk(char value) =>
        value is >= '\u3400' and <= '\u9fff' or
            >= '\uf900' and <= '\ufaff' or
            >= '\u3040' and <= '\u30ff' or
            >= '\uac00' and <= '\ud7af';

    private static bool EndsWithOpeningPunctuation(string value) =>
        value.Length > 0 && "([{（【《“‘".Contains(value[^1]);

    private static bool StartsWithClosingPunctuation(string value) =>
        value.Length > 0 && ")]},.!?;:，。！？；：）】》”’".Contains(value[0]);

    private sealed record SelectableSegment(
        string Text,
        Rect Bounds,
        int LineIndex);

    private readonly record struct TextPosition(int SegmentIndex, int CharacterIndex);
}
