using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace Screenshot.App.Text;

public partial class ImageQuestionWindow : Window, IDisposable
{
    private readonly ImageQuestionService _service;
    private readonly HttpClient? _ownedClient;
    private readonly List<ImageQuestionTurn> _history = [];
    private readonly List<PendingQuestion> _pending = [];
    private readonly List<QuestionImage> _images = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request;
    private PendingQuestion? _active;
    private Task _pump = Task.CompletedTask;
    private bool _closed, _paused;
    private readonly Action? _returnToEditor;

    private sealed class PendingQuestion(string text, AiTranslationProfile profile, QuestionImage[] images, string strength, bool updateHealth)
    {
        internal string Text { get; } = text;
        internal AiTranslationProfile Profile { get; } = profile;
        internal string Strength { get; } = strength;
        internal bool UpdateHealth { get; } = updateHealth;
        internal QuestionImage[] Images { get; set; } = images;
        internal TextBlock Status { get; set; } = null!;
        internal StackPanel Actions { get; set; } = null!;
        internal TextBox? Answer { get; set; }
        internal bool Steered { get; set; }
    }

    internal ImageQuestionWindow(BitmapSource? image, AiTranslationProfile[] profiles, Action? returnToEditor = null,
        ImageQuestionService? service = null)
    {
        _returnToEditor = returnToEditor;
        _ownedClient = service is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(3) } : null;
        _service = service ?? new ImageQuestionService(_ownedClient!, new DpapiTranslationCredentialStore());
        InitializeComponent();
        WindowShell.Attach(this, "AI 问图");
        if (image is not null) AddImage(image); else RefreshImages();
        ModelPicker.ItemsSource = profiles;
        ModelPicker.SelectedIndex = 0;
        ReturnButton.Visibility = returnToEditor is null ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) => PromptBox.Focus();
    }

    internal void ShowForUserRequest()
    {
        WindowActivation.Show(this);
        if (IsActive) PromptBox.Focus();
    }

    internal Task SendAsync() => Submit(PromptBox.Text, clearDraft: true);

    private Task Submit(string text, bool clearDraft)
    {
        if (_closed || ModelPicker.SelectedItem is not AiTranslationProfile profile) return Task.CompletedTask;
        var question = text.Trim();
        var model = SpecificModelBox.Text.Trim();
        if (model.Length == 0) { StatusText.Text = "请选择或输入具体模型。"; return Task.CompletedTask; }
        var updateHealth = model == profile.Model && (StrengthBox.SelectedValue as string ?? "auto") == "auto";
        profile = profile with { Model = model };
        if (question.Length == 0) { StatusText.Text = "请输入要 AI 做的事情。"; PromptBox.Focus(); return Task.CompletedTask; }
        // Bound queued snapshots; accepted messages are never dropped.
        if (_pending.Count >= 10) { StatusText.Text = "已有 10 条排队消息，请等待处理；输入已保留。"; return Task.CompletedTask; }
        if (_images.Count == 0 && _history.Count == 0 && _active is null && OnlineModelCapabilities.Resolve(profile) != OnlineModelPurpose.ImageGeneration)
        { StatusText.Text = "请先添加图片。"; return Task.CompletedTask; }
        var item = new PendingQuestion(question, profile, _images.ToArray(), StrengthBox.SelectedValue as string ?? "auto", updateHealth);
        WelcomeText.Visibility = Visibility.Collapsed;
        AddUserMessage(item);
        _pending.Add(item);
        SetImagesExpanded(false);
        if (clearDraft) PromptBox.Clear();
        RefreshQueue(); ConversationScroll.ScrollToEnd();
        if (!_paused) StartPump();
        PromptBox.Focus();
        return _pump;
    }

    private void StartPump()
    {
        if (_closed || _active is not null || _pending.Count == 0 || _paused) return;
        _pump = ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
        using var activity = FeatureActivity.Begin();
        while (!_closed && !_paused && _pending.Count > 0)
        {
            var item = _active = _pending[0];
            _pending.RemoveAt(0);
            item.Actions.Children.Clear(); item.Status.Text = "已发送";
            item.Answer = AddMessage(item.Profile.Name + " · " + item.Profile.Model, "正在处理…", user: false);
            using var live = new LiveAnswer(item.Answer, ConversationScroll);
            var history = _history.ToArray();
            using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _request = request; RefreshQueue();
            try
            {
                var reply = await Task.Run(async () =>
                {
                    var pngs = new List<byte[]>();
                    foreach (var image in item.Images)
                    {
                        request.Token.ThrowIfCancellationRequested();
                        pngs.Add(image.Encode());
                    }
                    return await _service.AskWithImagesAsync(item.Profile, pngs, history, item.Text, request.Token, item.Strength, item.UpdateHealth, live.Report).ConfigureAwait(false);
                }, request.Token);
                live.Complete();
                if (_closed || request.IsCancellationRequested) continue;
                var partial = reply.Text.Length > 0 ? reply.Text : live.HasAnswer ? item.Answer.Text : "";
                item.Answer.Text = reply.IsSuccess ? reply.Text : (partial.Length > 0 ? partial + "\n\n" : "") + (reply.Error ?? "请求失败，请重试。");
                item.Status.Text = reply.IsSuccess ? "已回答" : "未完成";
                foreach (var generated in reply.Images) AddGeneratedImage((StackPanel)item.Answer.Parent, generated);
                if (reply.IsSuccess) _history.Add(new(item.Text, reply.Text));
                else AddRetry(item);
                StatusText.Text = reply.Error ?? "回答完成，可继续追问。";
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (!_closed) { item.Answer.Text = e is InvalidOperationException ? e.Message : "问图失败，请重试。"; item.Status.Text = "未完成"; AddRetry(item); }
            }
            finally
            {
                live.Complete();
                // Providers may ignore cancellation and return a late answer.
                if (!_closed && request.IsCancellationRequested)
                {
                    item.Answer.Text = (live.HasAnswer ? item.Answer.Text + "\n\n" : "") + (item.Steered ? "已按补充要求调整方向。" : "已停止。");
                    item.Status.Text = item.Steered ? "已调整方向" : "已停止";
                    if (!item.Steered) AddRetry(item);
                }
                item.Images = []; _request = null; _active = null;
            }
        }
        if (!_closed) RefreshQueue();
    }

    internal void SteerQueued(int index)
    {
        if (_closed || index < 0 || index >= _pending.Count) return;
        var next = _pending[index];
        _pending.RemoveAt(index); _pending.Insert(0, next);
        if (_active is { Steered: false } current)
        {
            // Keep the unfinished request as user context, without inventing an answer.
            current.Steered = true; _history.Add(new(current.Text, "")); _request?.Cancel();
        }
        _paused = false; RefreshQueue(); StartPump();
    }

    internal void StopProcessing()
    {
        _paused = true; _request?.Cancel();
        RefreshQueue();
    }

    private void RefreshQueue()
    {
        for (var i = 0; i < _pending.Count; i++) _pending[i].Status.Text = $"{(_paused ? "已保存" : "排队中")} · 第 {i + 1} 条";
        StopButton.Visibility = _active is null ? Visibility.Collapsed : Visibility.Visible;
        ResumeButton.Visibility = _paused && _pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SendButton.Content = _active is not null || _paused ? "加入队列" : "发送";
        if (_active is not null && !_paused) StatusText.Text = $"正在处理 · {_pending.Count} 条排队 · 可继续输入或调整方向";
        else if (_paused) StatusText.Text = "处理已停止，排队消息和输入草稿已保留。";
    }

    private void AddUserMessage(PendingQuestion item)
    {
        var content = AddMessage("你" + (item.Images.Length > 0 ? $" · {item.Images.Length} 张图片" : ""), item.Text, user: true);
        var body = (StackPanel)content.Parent;
        item.Status = new TextBlock { FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        item.Status.SetResourceReference(ForegroundProperty, "AppTextSecondaryBrush"); body.Children.Add(item.Status);
        item.Actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var steer = new Button { Content = "调整方向", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 4, 6, 0),
            ToolTip = "停止当前请求，带着原请求和这条补充要求继续处理" };
        steer.Click += (_, _) => SteerQueued(_pending.IndexOf(item));
        var remove = new Button { Content = "移回输入框", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 4, 0, 0) };
        remove.Click += (_, _) =>
        {
            if (!_pending.Remove(item)) return;
            PromptBox.Text = string.IsNullOrWhiteSpace(PromptBox.Text) ? item.Text : PromptBox.Text + Environment.NewLine + item.Text;
            item.Status.Text = "已移回输入框"; item.Actions.Children.Clear(); item.Images = []; RefreshQueue();
        };
        item.Actions.Children.Add(steer); item.Actions.Children.Add(remove); body.Children.Add(item.Actions);
    }

    private void AddRetry(PendingQuestion item)
    {
        var retry = new Button { Content = "重新发送", Padding = new Thickness(8, 3, 8, 3), ToolTip = "使用上方当前图片重新发送这条要求" };
        retry.Click += (_, _) => { _paused = false; _ = Submit(item.Text, clearDraft: false); };
        item.Actions.Children.Clear(); item.Actions.Children.Add(retry);
    }

    private TextBox AddMessage(string author, string text, bool user)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = author, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var content = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        content.SetResourceReference(ForegroundProperty, "AppTextPrimaryBrush"); body.Children.Add(content);
        var bubble = new Border { Child = body, CornerRadius = new CornerRadius(12), Padding = new Thickness(12), BorderThickness = new Thickness(1),
            HorizontalAlignment = user ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left,
            Margin = user ? new Thickness(42, 4, 2, 10) : new Thickness(2, 4, 42, 10) };
        bubble.SetResourceReference(BackgroundProperty, user ? "AppAccentMutedBrush" : "AppInputBackgroundBrush");
        bubble.SetResourceReference(Border.BorderBrushProperty, user ? "AppAccentBrush" : "AppBorderBrush");
        bubble.SetBinding(MaxWidthProperty, new System.Windows.Data.Binding("ActualWidth") { Source = ConversationScroll, Converter = new BubbleWidthConverter() });
        Conversation.Children.Add(bubble);
        return content;
    }

    private sealed class BubbleWidthConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Math.Max(100, (double)value - 58);
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        _modelListRequest?.Cancel();
        if (ModelPicker.SelectedItem is not AiTranslationProfile p) return;
        SpecificModelBox.ItemsSource = _modelCatalogs.TryGetValue(CatalogKey(p), out var models) ? models : new[] { p.Model }; SpecificModelBox.Text = p.Model;
        if (_active is null) StatusText.Text = $"{p.Name} · 可选择具体模型与强度；当前对话不会修改已保存配置。";
    }
    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync();
    private void OnStopClick(object sender, RoutedEventArgs e) => StopProcessing();
    private void OnResumeClick(object sender, RoutedEventArgs e) { _paused = false; RefreshQueue(); StartPump(); }
    private void OnReturnClick(object sender, RoutedEventArgs e) => _returnToEditor?.Invoke();
    private async void OnSuggestionClick(object sender, RoutedEventArgs e) => await Submit((string)((Button)sender).Tag, clearDraft: false);
    private async void OnPromptKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; await SendAsync(); } }
    protected override void OnClosed(EventArgs e) { Dispose(); base.OnClosed(e); }
    public void Dispose()
    {
        if (_closed) return;
        _closed = true; _lifetime.Cancel(); _request?.Cancel(); _ownedClient?.Dispose();
        _modelListRequest?.Cancel();
        _modelCatalogs.Clear(); SpecificModelBox.ItemsSource = null;
        _pending.Clear(); _history.Clear(); _images.Clear();
        foreach (var preview in _previews.ToArray()) preview.Close();
        _previews.Clear(); AttachmentsPanel.Children.Clear(); ModelPicker.ItemsSource = null;
        Conversation.Children.Clear(); PromptBox.Clear(); _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
