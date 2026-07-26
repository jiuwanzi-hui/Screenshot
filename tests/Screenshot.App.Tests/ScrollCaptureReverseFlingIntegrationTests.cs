using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

/// <summary>
/// End-to-end regression for the reported reverse-fling failure: capture a
/// real window showing an editor-like document with near-identical repeated
/// blocks, scroll down with smooth-scroll style motion, then fling back up
/// past the starting viewport. The capture must keep following the viewport
/// and prepend the content above the start.
/// </summary>
public sealed class ScrollCaptureReverseFlingIntegrationTests
{
    [Fact]
    public async Task DownThenUpFlingPrependsContentAboveTheStart()
    {
        Window? window = null;
        ScrollViewer? scrollViewer = null;
        ScrollCaptureTarget? target = null;
        var wheelEvents = Channel.CreateUnbounded<int>();
        const double startOffset = 2200;
        const double downTravel = (6 * 260) + 140;
        double scrollableHeight = 0;

        WpfTestHost.Invoke(() =>
        {
            var content = BuildEditorLikeContent();

            scrollViewer = new ScrollViewer
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x20, 0x24)),
                CanContentScroll = false,
                Content = content,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };

            scrollViewer.PreviewMouseWheel += (_, eventArgs) =>
            {
                var nextOffset = Math.Clamp(
                    scrollViewer.VerticalOffset + (-eventArgs.Delta),
                    0,
                    scrollViewer.ScrollableHeight);
                scrollViewer.ScrollToVerticalOffset(nextOffset);
                eventArgs.Handled = true;
            };

            window = new Window
            {
                Width = 1050,
                Height = 360,
                Content = scrollViewer,
                Left = 40,
                Top = 40,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                WindowStyle = WindowStyle.None,
            };
            window.Show();
            window.Activate();
            window.UpdateLayout();
            scrollViewer.ScrollToVerticalOffset(startOffset);
            window.UpdateLayout();
            scrollableHeight = scrollViewer.ScrollableHeight;

            var windowHandle = new WindowInteropHelper(window).Handle;
            Assert.True(ForegroundWindowCaptureService.TryGetClientScreenRegion(
                windowHandle,
                out var clientRegion));
            var selectedRegion = new ScreenRegion(
                clientRegion.X + 12,
                clientRegion.Y + 12,
                clientRegion.Width - 40,
                clientRegion.Height - 24);
            Assert.True(ForegroundWindowCaptureService.TryCreateScrollCaptureTarget(
                windowHandle,
                selectedRegion,
                out target));
        });

        Assert.True(scrollableHeight >= 4200, $"文档太矮：{scrollableHeight}");

        async Task SmoothScrollByAsync(double delta, int durationMs)
        {
            var steps = Math.Max(1, durationMs / 9);
            var stepDelta = delta / steps;
            for (var index = 0; index < steps; index++)
            {
                WpfTestHost.Invoke(() =>
                {
                    var viewer = scrollViewer!;
                    viewer.ScrollToVerticalOffset(Math.Clamp(
                        viewer.VerticalOffset + stepDelta,
                        0,
                        viewer.ScrollableHeight));
                });
                await Task.Delay(9);
            }
        }

        try
        {
            Assert.NotNull(target);
            var completionRequested = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var previewStates = new ConcurrentQueue<ScrollCapturePreviewState>();
            await Task.Delay(250);

            // Windows from earlier desktop tests can linger topmost at the
            // same coordinates for a moment; the screen capture would then see
            // their static pixels instead of this document. Re-assert z-order
            // right before sampling starts.
            WpfTestHost.Invoke(() =>
            {
                window!.Topmost = false;
                window.Topmost = true;
                window.Activate();
            });
            await Task.Delay(150);

            var captureTask = ScrollCaptureService.CaptureOnWheelAsync(
                target,
                completionRequested.Task,
                wheelEvents.Reader,
                ScrollCaptureOptions.Default,
                setPreviewVisibilityAsync: (_, _) => Task.CompletedTask,
                previewChanged: previewStates.Enqueue);

            // Down fling: wheel ticks arriving while smooth scrolling animates.
            for (var tick = 0; tick < 6; tick++)
            {
                wheelEvents.Writer.TryWrite(-240);
                await SmoothScrollByAsync(260, 130);
            }

            // Deceleration tail without wheel input.
            await SmoothScrollByAsync(140, 320);
            await Task.Delay(650);

            // Up fling past the starting viewport. The pace is a brisk fling
            // in real terms while leaving margin for this contended test
            // process: under a full-suite load the sampler itself gets starved
            // of CPU, and scroll speed times the worst-case sampling interval
            // must stay under one viewport or no stitcher could connect
            // consecutive samples at all. Speed extremes are exercised by the
            // isolated stress runs, not by this regression gate.
            for (var tick = 0; tick < 11; tick++)
            {
                wheelEvents.Writer.TryWrite(240);
                await SmoothScrollByAsync(-180, 150);
            }

            await SmoothScrollByAsync(-160, 320);
            await Task.Delay(500);

            // Rapid wiggling: the wheel direction flips faster than smooth
            // scrolling settles, so the wheel hint disagrees with the actual
            // screen motion on many frames. The stitcher must follow the image
            // evidence and keep the anchor through every flip.
            for (var flip = 0; flip < 4; flip++)
            {
                var sign = flip % 2 == 0 ? 1 : -1;
                wheelEvents.Writer.TryWrite(-240 * sign);
                await SmoothScrollByAsync(190 * sign, 140);
            }

            await Task.Delay(400);

            // The user's failing gesture: reverse down again briefly, then
            // immediately fling up past the previous top. The transitional
            // frames are expensive to match, and the second up-run must not
            // let the backlog decimate itself into unmatchable gaps.
            for (var tick = 0; tick < 2; tick++)
            {
                wheelEvents.Writer.TryWrite(-240);
                await SmoothScrollByAsync(230, 120);
            }

            for (var tick = 0; tick < 7; tick++)
            {
                wheelEvents.Writer.TryWrite(240);
                await SmoothScrollByAsync(-190, 140);
            }

            await SmoothScrollByAsync(-140, 300);
            await Task.Delay(900);

            completionRequested.TrySetResult();
            wheelEvents.Writer.TryComplete();
            var result = await captureTask.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.NotNull(result.Image);

            double finalOffset = 0;
            WpfTestHost.Invoke(() => finalOffset = scrollViewer!.VerticalOffset);
            var travelled = startOffset - finalOffset;
            Assert.True(
                travelled > 600,
                $"上滑没有越过起点足够远：final={finalOffset}");

            var addedAbove = previewStates.Max(state => state.AddedAboveFrameCount);
            var addedBelow = previewStates.Max(state => state.AddedBelowFrameCount);

            using (result.Image)
            {
                var report =
                    $"final image {result.Image.Bitmap.Width}x{result.Image.Bitmap.Height}, " +
                    $"above={addedAbove}, below={addedBelow}, " +
                    $"startOffset={startOffset}, finalOffset={finalOffset}, " +
                    $"scrollable={scrollableHeight}";

                Assert.True(addedBelow > 0, $"下行没有扩展任何内容。{report}");
                Assert.True(
                    addedAbove >= 5,
                    $"上滑越过起点后几乎没有向上扩展。{report}");

                // The stitch has to cover the span the viewport visited: the
                // down travel plus the up travel above the start. Scroll units
                // map at least 1:1 to device pixels, so their sum bounds the
                // image height from below. A stalled capture stops near the
                // down-only height; the guard is calibrated to catch that
                // while tolerating the small tail this contended test process
                // occasionally leaves before completion drains it.
                var expectedMinimumHeight = (int)(
                    Math.Min(downTravel, scrollableHeight - startOffset) +
                    (travelled * 0.75));
                Assert.True(
                    result.Image.Bitmap.Height >= expectedMinimumHeight,
                    $"拼接高度不足。{report}");
            }
        }
        finally
        {
            wheelEvents.Writer.TryComplete();
            WpfTestHost.Invoke(() => window?.Close());
        }
    }

    private static StackPanel BuildEditorLikeContent()
    {
        var stack = new StackPanel
        {
            Width = 1050,
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x20, 0x24)),
        };

        var protocols = new[] { "udp", "tcp", "icmp", "raw", "fwd" };
        var line = 7100;

        for (var block = 0; block < 22; block++)
        {
            var protocol = protocols[block % protocols.Length];
            AddCodeLine(stack, ref line, 2, "push_limited_event(", "#d4d4d4");
            AddCodeLine(stack, ref line, 3, "&mut wintun_runtime_received_packets,", "#9cdcfe");
            AddCodeLine(stack, ref line, 3, "item: serde_json::json!({", "#d4d4d4");
            AddCodeLine(stack, ref line, 4, "\"packetIndex\": packet_index,", "#ce9178");
            AddCodeLine(stack, ref line, 4, $"\"protocol\": \"{protocol}\".to_owned(),", "#ce9178");
            AddCodeLine(stack, ref line, 4, "\"sourceIp\": packet.source_ip,", "#ce9178");
            AddCodeLine(stack, ref line, 4, "\"destinationIp\": packet.destination_ip,", "#ce9178");
            AddCodeLine(stack, ref line, 4, "\"payloadBytes\": packet.payload.len(),", "#ce9178");
            AddCodeLine(stack, ref line, 4, "\"forwarded\": should_forward,", "#ce9178");
            AddCodeLine(stack, ref line, 4, "}),", "#d4d4d4");
            AddCodeLine(stack, ref line, 3, "RUNTIME_DIAGNOSTIC_EVENT_LOG_LIMIT,", "#4fc1ff");
            AddCodeLine(stack, ref line, 2, ");", "#d4d4d4");
            AddCodeLine(stack, ref line, 2, "", "#d4d4d4");
            AddCodeLine(stack, ref line, 2, $"for target in {protocol}_forward_targets {{", "#c586c0");
            AddCodeLine(stack, ref line, 3, "if !should_forward {", "#c586c0");
            AddCodeLine(stack, ref line, 4, "break;", "#c586c0");
            AddCodeLine(stack, ref line, 3, "}", "#d4d4d4");
            AddCodeLine(stack, ref line, 3, $"let payload_{block} = seal_tunnel_payload(", "#dcdcaa");
            AddCodeLine(stack, ref line, 4, "key,", "#9cdcfe");
            AddCodeLine(stack, ref line, 4, $"&mut relay_clients_{block},", "#9cdcfe");
            AddCodeLine(stack, ref line, 3, ")?;", "#d4d4d4");
            AddCodeLine(stack, ref line, 2, "}", "#d4d4d4");
        }

        return stack;
    }

    private static void AddCodeLine(
        StackPanel stack,
        ref int lineNumber,
        int indent,
        string text,
        string colorHex)
    {
        var row = new Grid { Height = 23 };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(58),
        });
        row.ColumnDefinitions.Add(new ColumnDefinition());

        var number = new TextBlock
        {
            Text = lineNumber.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6e, 0x76, 0x81)),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 3, 12, 0),
        };
        Grid.SetColumn(number, 0);
        row.Children.Add(number);

        var color = (Color)ColorConverter.ConvertFromString(colorHex);
        var code = new TextBlock
        {
            Text = new string(' ', indent * 4) + text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            Foreground = new SolidColorBrush(color),
            Margin = new Thickness(0, 2, 0, 0),
        };
        Grid.SetColumn(code, 1);
        row.Children.Add(code);

        stack.Children.Add(row);
        lineNumber++;
    }
}
