using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TextBox = System.Windows.Controls.TextBox;

namespace Screenshot.App.Text;

public partial class ImageQuestionWindow
{
    // One latest snapshot, one short-lived UI timer. Fast token streams cannot
    // flood the dispatcher or steal focus from the next message being typed.
    internal sealed class LiveAnswer : IDisposable
    {
        private readonly TextBox _answer;
        private readonly ScrollViewer _scroll;
        private readonly TextBox _reasoning;
        private readonly Expander _expander;
        private readonly DispatcherTimer _timer;
        private ImageQuestionProgress? _latest;
        private bool _finished;
        internal bool HasAnswer { get; private set; }
        internal LiveAnswer(TextBox answer, ScrollViewer scroll)
        {
            _answer = answer;
            _scroll = scroll;
            _reasoning = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(0, 6, 0, 8),
                MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _reasoning.SetResourceReference(ForegroundProperty, "AppTextSecondaryBrush");
            _expander = new Expander { Header = "处理思路 · 模型返回", Content = _reasoning, IsExpanded = true,
                Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8),
                ToolTip = "仅显示服务实际返回的思路或摘要；未返回时不显示。" };
            _expander.SetResourceReference(ForegroundProperty, "AppTextSecondaryBrush");
            ((StackPanel)answer.Parent).Children.Insert(1, _expander);
            _timer = new DispatcherTimer(DispatcherPriority.Background, answer.Dispatcher) { Interval = TimeSpan.FromMilliseconds(80) };
            _timer.Tick += OnTick;
            _timer.Start();
        }
        internal void Report(ImageQuestionProgress progress) => Interlocked.Exchange(ref _latest, progress);
        private void OnTick(object? sender, EventArgs e) => Flush();
        private void Flush()
        {
            if (Interlocked.Exchange(ref _latest, null) is not { } value) return;
            var follow = _scroll.VerticalOffset >= _scroll.ScrollableHeight - 24;
            if (value.Answer.Length > 0) { HasAnswer = true; _answer.Text = value.Answer; }
            else if (value.Reasoning.Length > 0) _answer.Text = "正在生成回答…";
            if (value.Reasoning.Length > 0)
            { _reasoning.Text = value.Reasoning; _expander.Visibility = Visibility.Visible; }
            if (follow) _scroll.ScrollToEnd();
        }
        internal void Complete()
        {
            if (_finished) return;
            _finished = true;
            _timer.Stop(); _timer.Tick -= OnTick;
            Flush(); _expander.IsExpanded = false;
        }
        public void Dispose() => Complete();
    }
}
