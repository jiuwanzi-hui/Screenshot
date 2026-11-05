using System.Globalization;
using System.Text;

namespace Screenshot.App.Capture;

public static class ClipboardTextService
{
    internal static bool HasPendingWrites => NativeClipboardWriter.HasPendingWrites;

    internal static Task WriteAsync(Action write, CancellationToken cancellationToken) =>
        NativeClipboardWriter.WriteAsync(write, cancellationToken);

    public static Task SetTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("剪贴板文字不能为空。", nameof(text));
        }

        return WriteAsync(
            () => SetNativeClipboardText(text, cancellationToken),
            cancellationToken);
    }

    public static Task SetHtmlAsync(
        string html,
        string plainText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            throw new ArgumentException("剪贴板 HTML 不能为空。", nameof(html));
        }

        return WriteAsync(
            () => SetNativeClipboardHtml(html, plainText ?? string.Empty, cancellationToken),
            cancellationToken);
    }

    private static void SetNativeClipboardHtml(
        string html,
        string plainText,
        CancellationToken cancellationToken)
    {
        var fragment = $"<html><body><!--StartFragment-->{html}<!--EndFragment--></body></html>";
        const string headerTemplate =
            "Version:0.9\r\nStartHTML:{0:D8}\r\nEndHTML:{1:D8}\r\n" +
            "StartFragment:{2:D8}\r\nEndFragment:{3:D8}\r\n";
        var headerLength = Encoding.UTF8.GetByteCount(
            string.Format(CultureInfo.InvariantCulture, headerTemplate, 0, 0, 0, 0));
        var startHtml = headerLength;
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(fragment);
        var startFragment = startHtml + Encoding.UTF8.GetByteCount("<html><body><!--StartFragment-->");
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(html);
        var cfHtml = string.Format(
            CultureInfo.InvariantCulture,
            headerTemplate, startHtml, endHtml, startFragment, endFragment) + fragment;
        var textBytes = Encoding.Unicode.GetBytes(plainText + '\0');
        var htmlBytes = Encoding.UTF8.GetBytes(cfHtml + '\0');
        using var textPayload = new ClipboardPayload(13, textBytes);
        using var htmlPayload = new ClipboardPayload(NativeClipboardWriter.RegisterFormat("HTML Format"), htmlBytes);
        NativeClipboardWriter.Publish(cancellationToken, textPayload, htmlPayload);
    }

    private static void SetNativeClipboardText(string text, CancellationToken cancellationToken)
    {
        using var payload = new ClipboardPayload(13, Encoding.Unicode.GetBytes(text + '\0'));
        NativeClipboardWriter.Publish(cancellationToken, payload);
    }
}
