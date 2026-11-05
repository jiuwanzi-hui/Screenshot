using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using ContentControl = System.Windows.Controls.ContentControl;
using Control = System.Windows.Controls.Control;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using FlowDirection = System.Windows.FlowDirection;

namespace Screenshot.App.Presentation;

internal static class UiLocalization
{
    internal sealed record LanguageOption(string Code, string Name);
    internal static IReadOnlyList<LanguageOption> Languages { get; } =
    [new("zh-Hans", "简体中文"), new("en", "English"), new("fr", "Français"),
     new("ru", "Русский"), new("es", "Español"), new("ar", "العربية")];
    private static readonly ConditionalWeakTable<FrameworkElement, ElementState> States = new();
    private static readonly ConditionalWeakTable<Window, object> TrackedWindows = new();
    private static readonly List<WeakReference<ElementState>> Active = [];
    private static Dictionary<string, string> _catalog = new(StringComparer.Ordinal);
    private static Template[] _templates = [];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> DynamicCache = new(StringComparer.Ordinal);
    private static bool _initialized;
    internal static string Language { get; private set; } = "zh-Hans";
    internal static event EventHandler? Changed;
    internal static string Normalize(string? language) => Languages.Any(l => l.Code == language) ? language! : "zh-Hans";
    internal static string Get(string text)
    {
        if (Language == "zh-Hans") return text;
        if (_catalog.TryGetValue(text, out var translated)) return translated;
        if (!text.Any(c => c is >= '\u3400' and <= '\u9fff')) return text;
        if (DynamicCache.TryGetValue(text, out translated)) return translated;
        translated = GetDynamic(text);
        if (text.Length <= 4096)
        {
            if (DynamicCache.Count >= 512) DynamicCache.Clear();
            DynamicCache[text] = translated;
        }
        return translated;
    }

    private static string GetDynamic(string text)
    {
        foreach (var template in _templates)
            if (template.TryTranslate(text) is { } translated) return translated;
        // Toolbar descriptions append a shortcut; the shortcut is data and must
        // remain byte-for-byte unchanged while its description follows the UI.
        var suffix = Regex.Match(text, @"^(.*?)(\s*[（(][A-Za-z0-9+ /,⌘⇧↑↓←→-]+[）)])$", RegexOptions.CultureInvariant);
        if (suffix.Success && _catalog.TryGetValue(suffix.Groups[1].Value, out var description))
            return description + suffix.Groups[2].Value;
        if (text.Contains('\n')) return string.Join('\n', text.Split('\n').Select(Get));
        return text;
    }

    private sealed class Template
    {
        private readonly string _prefix, _suffix, _pattern, _translation;
        private readonly int[] _arguments;
        private Regex? _regex;
        internal int FixedLength { get; }
        internal Template(string source, string translation)
        {
            var holes = Regex.Matches(source, @"\{(\d+)\}");
            _prefix = source[..holes[0].Index]; _suffix = source[(holes[^1].Index + holes[^1].Length)..];
            _arguments = holes.Select(h => int.Parse(h.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            _pattern = "^" + Regex.Replace(Regex.Escape(source), @"\\\{(\d+)}", "(.*?)") + "$";
            _translation = translation; FixedLength = source.Length - holes.Sum(h => h.Length);
        }
        internal string? TryTranslate(string text)
        {
            if (!text.StartsWith(_prefix, StringComparison.Ordinal) || !text.EndsWith(_suffix, StringComparison.Ordinal)) return null;
            // Most status templates are never used in the resident process.
            // Build a matcher only after its fixed prefix and suffix match.
            Match match;
            try
            {
                match = (_regex ??= new Regex(_pattern, RegexOptions.CultureInvariant | RegexOptions.Singleline,
                    TimeSpan.FromMilliseconds(20))).Match(text);
            }
            catch (RegexMatchTimeoutException) { return null; }
            if (!match.Success) return null;
            return Regex.Replace(_translation, @"\{(\d+)\}", placeholder =>
            {
                var index = Array.IndexOf(_arguments, int.Parse(placeholder.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                return index >= 0 ? match.Groups[index + 1].Value : placeholder.Value;
            });
        }
    }

    internal static void Apply(string language)
    {
        if (!_initialized)
        {
            _initialized = true;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnLoaded), true);
            // WPF skips Loaded broadcasts for branches with no instance/style
            // Loaded handlers. Plain labels often fall in that category; class
            // registration alone does not opt them into the broadcast.
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.SizeChangedEvent,
                new SizeChangedEventHandler(OnSizeChanged), true);
            EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent,
                new ToolTipEventHandler(OnToolTipOpening), true);
        }
        language = Normalize(language);
        if (language == Language) return;
        _catalog = LoadCatalog(language);
        _templates = _catalog.Where(pair => Regex.IsMatch(pair.Key, @"\{\d+\}"))
            .Select(pair => new Template(pair.Key, pair.Value)).OrderByDescending(t => t.FixedLength).ToArray();
        DynamicCache.Clear();
        Language = language;
        for (var i = Active.Count - 1; i >= 0; i--)
        {
            if (!Active[i].TryGetTarget(out var state) || !state.IsAttached) { Active.RemoveAt(i); continue; }
            if (language == "zh-Hans") state.Detach();
            else state.Refresh();
        }
        if (language == "zh-Hans")
        {
            Active.Clear();
            foreach (var entry in TrackedWindows) entry.Key.Closed -= OnWindowClosed;
            TrackedWindows.Clear();
        }
        else AttachExistingRoots();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void AttachExistingRoots()
    {
        // The authored Chinese UI needs no per-element tracking. Discover
        // already-created windows only when the user switches languages;
        // popup HWNDs have separate roots and are not Application.Windows.
        var pending = new Stack<DependencyObject>();
        if (System.Windows.Application.Current is { } application)
            foreach (Window window in application.Windows) pending.Push(window);
        foreach (PresentationSource source in PresentationSource.CurrentSources)
            if (source.RootVisual is { } root) pending.Push(root);
        var visited = new HashSet<DependencyObject>();
        while (pending.TryPop(out var item))
        {
            if (!visited.Add(item)) continue;
            if (item is FrameworkElement element) Attach(element);
            foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())
                pending.Push(child);
            if (item is Visual)
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++)
                    pending.Push(VisualTreeHelper.GetChild(item, index));
        }
    }

    internal static Dictionary<string, string> LoadCatalog(string language)
    {
        if (language == "zh-Hans") return new(StringComparer.Ordinal);
        using var stream = typeof(UiLocalization).Assembly.GetManifestResourceStream(
            $"Screenshot.App.Presentation.Localization.{Normalize(language)}.json")
            ?? throw new InvalidDataException("Missing interface language: " + language);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new(StringComparer.Ordinal);
    }

    // Result text, editable fields, paths, model names and user-created content
    // remain data. Only presentation labels are eligible for catalog lookup.
    private static bool IsDataText(FrameworkElement element)
    {
        if (element is not Window && Window.GetWindow(element)?.GetType().Name is "RealtimeOverlayWindow") return true;
        for (DependencyObject? parent = element; parent is not null; parent = Parent(parent))
        {
            if (parent is TextBoxBase or PasswordBox) return true;
            if (parent is FrameworkElement framework && framework.Tag is string tag && tag == "UserContent") return true;
        }
        return false;
    }
    private static DependencyObject? Parent(DependencyObject item) =>
        item is Visual ? VisualTreeHelper.GetParent(item) ?? LogicalTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (ReferenceEquals(sender, args.OriginalSource) && sender is FrameworkElement element) Attach(element);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (ReferenceEquals(sender, args.OriginalSource) && sender is FrameworkElement element) Attach(element);
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs args)
    {
        // Panels can acquire a tooltip after their first layout without resizing.
        if (sender is FrameworkElement element && element.ToolTip is not null) Attach(element);
    }

    private static void Attach(FrameworkElement element)
    {
        if (Language == "zh-Hans") return;
        if (States.TryGetValue(element, out var existing) && existing.IsAttached)
        {
            existing.TrackOwner();
            return;
        }
        if (element is not TextBlock and not AccessText and not Control and not Window && element.ToolTip is null) return;
        if (IsDataText(element) && element is not TextBoxBase and not PasswordBox) return;
        var state = States.GetValue(element, e => new ElementState(e));
        if (!state.Attach()) return;
        Active.Add(new(state));
        if (Active.Count > 4096) Active.RemoveAll(reference => !reference.TryGetTarget(out var item) || !item.IsAttached);
    }

    private static void TrackWindow(Window window)
    {
        if (TrackedWindows.TryGetValue(window, out _)) return;
        TrackedWindows.Add(window, new object());
        window.Closed += OnWindowClosed;
    }

    private static void OnWindowClosed(object? sender, EventArgs args)
    {
        if (sender is not Window window) return;
        window.Closed -= OnWindowClosed;
        TrackedWindows.Remove(window);
        // Explicit language changes also visit never-shown prepared windows.
        // Such trees do not necessarily receive WPF's Unloaded broadcast.
        for (var index = Active.Count - 1; index >= 0; index--)
        {
            if (!Active[index].TryGetTarget(out var state) || !state.IsAttached)
                Active.RemoveAt(index);
            else if (state.IsOwnedBy(window))
            {
                state.Detach();
                Active.RemoveAt(index);
            }
        }
    }

    private sealed class ElementState(FrameworkElement element)
    {
        private sealed class Label(DependencyObject target, DependencyProperty property, string source)
        {
            internal DependencyObject Target = target;
            internal DependencyProperty Property = property;
            internal string Source = source;
            internal string Last = source;
            internal DependencyPropertyDescriptor? Descriptor;
        }
        private readonly List<Label> _labels = [];
        private bool _changing;
        private object _originalFlowDirection = DependencyProperty.UnsetValue;
        private bool _ownsFlowDirection;
        private WeakReference<Window>? _owner;
        internal bool IsAttached { get; private set; }
        internal bool IsOwnedBy(Window window) => _owner is { } owner && owner.TryGetTarget(out var target) && ReferenceEquals(target, window);
        internal void TrackOwner()
        {
            if ((element as Window ?? Window.GetWindow(element)) is not { } window) return;
            if (!IsOwnedBy(window)) _owner = new(window);
            TrackWindow(window);
        }
        internal bool Attach()
        {
            if (IsAttached) return false;
            _labels.Clear();
            void Add(DependencyProperty property, DependencyObject? target = null)
            {
                target ??= element;
                var text = target.GetValue(property) as string ?? string.Empty;
                var label = new Label(target, property, text) { Descriptor = DependencyPropertyDescriptor.FromProperty(property, target.GetType()) };
                label.Descriptor?.AddValueChanged(target, OnValueChanged);
                _labels.Add(label);
            }
            if (element is TextBlock textBlock)
            {
                if (textBlock.Inlines.Count > 1 || textBlock.Inlines.FirstInline is Span)
                {
                    void AddInlines(InlineCollection inlines)
                    {
                        foreach (var inline in inlines)
                            if (inline is Run run && !System.Windows.Data.BindingOperations.IsDataBound(run, Run.TextProperty)) Add(Run.TextProperty, run);
                            else if (inline is Span span) AddInlines(span.Inlines);
                    }
                    AddInlines(textBlock.Inlines);
                }
                else Add(TextBlock.TextProperty);
            }
            if (element is Window) Add(Window.TitleProperty);
            if (element is AccessText) Add(AccessText.TextProperty);
            // Localize rendered text rather than changing a ComboBox/ListBox's item values.
            if (element is ContentControl and not ComboBoxItem and not ListBoxItem) Add(ContentControl.ContentProperty);
            if (element is HeaderedItemsControl) Add(HeaderedItemsControl.HeaderProperty);
            if (element is HeaderedContentControl) Add(HeaderedContentControl.HeaderProperty);
            Add(FrameworkElement.ToolTipProperty);
            if (!string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(element)))
                Add(System.Windows.Automation.AutomationProperties.NameProperty);
            if (!string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetHelpText(element)))
                Add(System.Windows.Automation.AutomationProperties.HelpTextProperty);
            if (_labels.Count == 0) return false;
            if (element is TextBlock or AccessText)
                _originalFlowDirection = element.ReadLocalValue(FrameworkElement.FlowDirectionProperty);
            TrackOwner();
            IsAttached = true; element.Unloaded += OnUnloaded; Refresh(); return true;
        }
        private void OnValueChanged(object? sender, EventArgs args)
        {
            if (!_changing) Refresh();
        }
        internal void Refresh()
        {
            _changing = true;
            try
            {
                foreach (var label in _labels)
                {
                    if (label.Target.GetValue(label.Property) is not string current) continue;
                    if (current != label.Last) label.Source = current;
                    label.Last = Get(label.Source);
                    if (current != label.Last) label.Target.SetCurrentValue(label.Property, label.Last);
                }
                // Text direction changes, while screenshot geometry and shortcuts do not.
                if (element is TextBlock or AccessText)
                {
                    var direction = Language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                    if (element.FlowDirection != direction)
                    {
                        element.SetCurrentValue(FrameworkElement.FlowDirectionProperty, direction);
                        _ownsFlowDirection = true;
                    }
                }
            }
            finally { _changing = false; }
        }
        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            if (!ReferenceEquals(sender, args.OriginalSource)) return;
            Detach();
        }
        internal void Detach()
        {
            if (!IsAttached) return;
            element.Unloaded -= OnUnloaded;
            foreach (var label in _labels)
            {
                label.Descriptor?.RemoveValueChanged(label.Target, OnValueChanged);
                // Restore source captions before a reusable popup is loaded again.
                if (label.Target.GetValue(label.Property) is string text && text == label.Last && text != label.Source)
                    label.Target.SetCurrentValue(label.Property, label.Source);
            }
            if (_ownsFlowDirection)
            {
                if (_originalFlowDirection == DependencyProperty.UnsetValue)
                    element.ClearValue(FrameworkElement.FlowDirectionProperty);
                else if (_originalFlowDirection is FlowDirection direction)
                    element.SetCurrentValue(FrameworkElement.FlowDirectionProperty, direction);
                else element.InvalidateProperty(FrameworkElement.FlowDirectionProperty);
            }
            _ownsFlowDirection = false;
            _owner = null;
            _labels.Clear();
            IsAttached = false;
            States.Remove(element);
        }
    }
}
