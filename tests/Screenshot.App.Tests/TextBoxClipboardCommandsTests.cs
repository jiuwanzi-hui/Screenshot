using System.Windows;
using System.Windows.Controls;
using Screenshot.App.Presentation;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class TextBoxClipboardCommandsTests
{
    [Fact]
    public async Task CopiesRemainOrderedAndCancelledWaiterDoesNotBlockNextCopy()
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<int>();
        var first = ClipboardTextService.WriteAsync(() =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            writes.Add(1);
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var second = ClipboardTextService.WriteAsync(() => writes.Add(2), cancellation.Token);
            var last = ClipboardTextService.WriteAsync(() => writes.Add(3), CancellationToken.None);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.True(ClipboardTextService.HasPendingWrites);
            Assert.False(last.IsCompleted);
            release.Set();
            await Task.WhenAll(first, last).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { 1, 3 }, writes);
            Assert.False(ClipboardTextService.HasPendingWrites);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task BusyClipboardDoesNotBlockEditingAndCopiesOriginalSelection()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? operation = null;
        string? copied = null;
        WpfTestHost.Invoke(() =>
        {
            var box = new TextBox { Text = "prefix sample-app-id suffix" };
            box.Select(7, 13);
            operation = TextBoxClipboardCommands.CopyAsync(box, text =>
            {
                copied = text;
                return pending.Task;
            }, message => Assert.Fail(message));
            Assert.False(operation.IsCompleted);
            box.Text = "User can continue typing";
        });
        WpfTestHost.Invoke(() => Assert.Equal("sample-app-id", copied));
        pending.SetResult();
        await operation!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyFailurePreservesTextAndDoesNotUpdateClosedEditor(bool closed)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? operation = null;
        TextBox? box = null;
        string? notice = null;
        WpfTestHost.Invoke(() =>
        {
            box = new TextBox { Text = "sample-app-id" };
            box.SelectAll();
            operation = TextBoxClipboardCommands.CopyAsync(box, _ => pending.Task, message => notice = message);
            if (closed) box.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        });
        pending.SetException(new InvalidOperationException("Clipboard busy"));
        await operation!.WaitAsync(TimeSpan.FromSeconds(5));
        WpfTestHost.Invoke(() =>
        {
            Assert.Equal("sample-app-id", box!.Text);
            Assert.Equal(closed, notice is null);
        });
    }
}
