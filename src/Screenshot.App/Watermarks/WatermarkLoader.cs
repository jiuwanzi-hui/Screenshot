using System.Windows.Media.Imaging;
using Screenshot.App.Core;

namespace Screenshot.App.Watermarks;

internal sealed record WatermarkLoadResult(WatermarkComposition Composition, string? Warning);

internal sealed class WatermarkLoader
{
    private readonly Func<string, CancellationToken, Task<BitmapSource?>> _loadImage;
    private readonly Func<WatermarkLocationDisplay, CancellationToken, Task<WatermarkLocationResult>> _readLocation;

    internal WatermarkLoader(
        Func<string, CancellationToken, Task<BitmapSource?>>? loadImage = null,
        Func<WatermarkLocationDisplay, CancellationToken, Task<WatermarkLocationResult>>? readLocation = null)
    {
        _loadImage = loadImage ?? ((id, token) => new WatermarkImageStore().LoadAsync(id, false, token));
        _readLocation = readLocation ?? WatermarkLocationService.ReadAsync;
    }

    internal async Task<WatermarkLoadResult> LoadAsync(WatermarkSettings settings, DateTimeOffset timestamp,
        CancellationToken cancellationToken, Action<WatermarkComposition>? showPartial = null)
    {
        settings = settings.Normalize();
        BitmapSource? image = null;
        string? warning = null;
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(settings.SelectedImageId))
        {
            try { image = await _loadImage(settings.SelectedImageId, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { warning = "水印图片无法读取，请在水印设置中重新导入。"; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        showPartial?.Invoke(new WatermarkComposition(settings, timestamp, null, image));
        string? location = null;
        if (settings.IncludeLocation && settings.UseSystemLocation)
        {
            var result = await _readLocation(settings.LocationDisplay, cancellationToken);
            location = result.Location;
            if (location is null) warning = string.Join(" ", new[] { warning, result.Message }.Where(value => !string.IsNullOrEmpty(value)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(new WatermarkComposition(settings, timestamp, location, image), warning);
    }
}
