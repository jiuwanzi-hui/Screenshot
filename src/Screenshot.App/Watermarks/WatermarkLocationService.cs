using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Screenshot.App.Core;
using Windows.Devices.Geolocation;

namespace Screenshot.App.Watermarks;

internal sealed record WatermarkLocationResult(string? Location, string Message);

internal static class WatermarkLocationService
{
    // One foreground, user-requested lookup. No subscriptions, timers or cached coordinates.
    internal static async Task<WatermarkLocationResult> ReadAsync(WatermarkLocationDisplay display, CancellationToken cancellationToken)
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync().AsTask(cancellationToken);
            if (access != GeolocationAccessStatus.Allowed)
                return new(null, "系统未允许定位，将使用手填地点。可在 Windows 隐私设置中允许定位。");
            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            // The city service accepts only the calling device's fresh platform location.
            var position = await locator.GetGeopositionAsync(TimeSpan.Zero, TimeSpan.FromSeconds(6)).AsTask(cancellationToken);
            var point = position.Coordinate.Point.Position;
            if (display == WatermarkLocationDisplay.Coordinates)
                return new(string.Format(CultureInfo.InvariantCulture, "{0:F4}°, {1:F4}°", point.Latitude, point.Longitude), "已获取系统经纬度。");

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4), MaxResponseContentBufferSize = 64 * 1024 };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SnapCut/4.0");
                var url = FormattableString.Invariant($"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={point.Latitude:R}&longitude={point.Longitude:R}&localityLanguage=zh");
                var json = await client.GetStringAsync(url, cancellationToken);
                var place = ParsePlaceName(json);
                return string.IsNullOrWhiteSpace(place)
                    ? new(null, "已获取经纬度，但未查询到城市／地区，将使用手填地点；也可选择经纬度模式。")
                    : new(place, "已获取城市／地区：" + place + "。可手动输入更具体的地点。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                return new(null, "已获取经纬度，但城市查询暂时不可用，将使用手填地点；可重试或切换经纬度模式。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(null, "暂时无法获取系统定位，将使用手填地点。"); }
    }

    internal static string? ParsePlaceName(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty : string.Empty;
        // Never silently substitute the network/VPN's IP location for system coordinates.
        if (Read("lookupSource").Contains("ip", StringComparison.OrdinalIgnoreCase)) return null;
        var names = new[] { Read("principalSubdivision"), Read("city"), Read("locality") }
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var result = string.Join(" · ", names);
        return result.Length == 0 ? null : result[..Math.Min(result.Length, 120)];
    }
}
