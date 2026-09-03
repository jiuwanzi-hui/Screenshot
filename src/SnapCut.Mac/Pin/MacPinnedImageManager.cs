using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SnapCut.Core;
using SnapCut.Mac.Presentation;
using SnapCut.Mac.Capture;

namespace SnapCut.Mac.Pin;

internal sealed class MacPinnedImageManager
{
    private readonly MacPinnedImagePersistenceStore _store = new();
    private readonly Dictionary<Guid, PinnedImageWindow> _windows = [];
    private PinnedImageGroupWindow? _groupWindow;
    private Guid[] _groupMembers = [];

    public event Action? Changed;

    public void Restore()
    {
        foreach (var state in _store.Load())
        {
            try
            {
                CreateWindow(LoadImage(state.ImagePath), state.ImagePath, state);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
            }
        }
    }

    public void Pin(PixelImage image, string imagePath)
    {
        CreateWindow(
            image,
            imagePath,
            new MacPinnedImageState(
                Guid.NewGuid(),
                imagePath,
                100,
                100,
                1,
                1,
                false));
        Save();
    }

    public bool HasVisible => _windows.Values.Any(window => window.IsVisible);

    public bool HasHidden => _windows.Values.Any(window => !window.IsVisible);

    public bool CanGroup => _groupWindow is null &&
        _windows.Values.Count(window => window.IsVisible) >= 2;

    public void HideAll()
    {
        foreach (var window in _windows.Values)
        {
            window.HidePinnedImage();
        }

        Save();
    }

    public void ShowAll()
    {
        foreach (var window in _windows.Values)
        {
            window.ShowPinnedImage();
        }

        Save();
    }

    public void GroupVisible()
    {
        if (!CanGroup)
        {
            return;
        }

        var members = _windows.Values.Where(window => window.IsVisible).ToArray();
        var composite = Compose(members.Select(window => window.SourceImage).ToArray());
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "SnapCut");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            $"SnapCut-Group-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        MacScreenCaptureService.SavePng(composite, path);
        _groupMembers = members.Select(window => window.GetState().Id).ToArray();
        foreach (var member in members)
        {
            member.HidePinnedImage();
        }

        _groupWindow = new PinnedImageGroupWindow(composite, path);
        _groupWindow.UngroupRequested += Ungroup;
        _groupWindow.CloseRequested += CloseGroup;
        _groupWindow.Show();
        Changed?.Invoke();
    }

    private void Ungroup()
    {
        _groupWindow?.Close();
        _groupWindow = null;
        foreach (var id in _groupMembers)
        {
            if (_windows.TryGetValue(id, out var window))
            {
                window.ShowPinnedImage();
            }
        }
        _groupMembers = [];
        Save();
    }

    private void CloseGroup()
    {
        _groupWindow?.Close();
        _groupWindow = null;
        foreach (var id in _groupMembers)
        {
            if (_windows.Remove(id, out var window))
            {
                window.Close();
            }
        }
        _groupMembers = [];
        Save();
    }

    private void CreateWindow(
        PixelImage image,
        string imagePath,
        MacPinnedImageState state)
    {
        var window = new PinnedImageWindow(image, imagePath, state);
        _windows[state.Id] = window;
        window.StateChanged += Save;
        window.CropRequested += async () => await CropAsync(state.Id);
        window.EditRequested += async () => await EditAsync(state.Id);
        window.PinnedClosed += () =>
        {
            _windows.Remove(state.Id);
            Save();
        };
        if (!state.Hidden)
        {
            window.Show();
        }
    }

    private void Save()
    {
        _store.Save(_windows.Values.Select(window => window.GetState()));
        Changed?.Invoke();
    }

    private async Task CropAsync(Guid id)
    {
        if (!_windows.TryGetValue(id, out var window))
        {
            return;
        }

        var result = await new PinnedImageCropWindow(window.SourceImage).ShowAsync();
        if (result is null || result.Value.SurfaceSize.Width <= 0 ||
            result.Value.SurfaceSize.Height <= 0)
        {
            return;
        }

        var selection = result.Value.Selection;
        var source = window.SourceImage;
        var left = Math.Clamp(
            (int)Math.Round(selection.Left * source.Width / result.Value.SurfaceSize.Width),
            0,
            source.Width - 1);
        var top = Math.Clamp(
            (int)Math.Round(selection.Top * source.Height / result.Value.SurfaceSize.Height),
            0,
            source.Height - 1);
        var right = Math.Clamp(
            (int)Math.Round(selection.Right * source.Width / result.Value.SurfaceSize.Width),
            left + 1,
            source.Width);
        var bottom = Math.Clamp(
            (int)Math.Round(selection.Bottom * source.Height / result.Value.SurfaceSize.Height),
            top + 1,
            source.Height);
        var cropped = Crop(source, left, top, right - left, bottom - top);
        var directory = Path.GetDirectoryName(window.GetState().ImagePath)
            ?? Path.GetTempPath();
        var path = Path.Combine(
            directory,
            $"SnapCut-Crop-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        MacScreenCaptureService.SavePng(cropped, path);
        var oldState = window.GetState();
        _windows.Remove(id);
        window.Close();
        CreateWindow(
            cropped,
            path,
            oldState with { ImagePath = path, Zoom = 1 });
        Save();
    }

    private async Task EditAsync(Guid id)
    {
        if (!_windows.TryGetValue(id, out var window))
        {
            return;
        }

        var edited = await window.EditAsync();
        if (edited is null)
        {
            return;
        }

        var state = window.GetState();
        var directory = Path.GetDirectoryName(state.ImagePath) ?? Path.GetTempPath();
        var path = Path.Combine(
            directory,
            $"SnapCut-Edit-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        MacScreenCaptureService.SavePng(edited, path);
        _windows.Remove(id);
        window.Close();
        CreateWindow(edited, path, state with { ImagePath = path });
        Save();
    }

    private static PixelImage Crop(
        PixelImage source,
        int left,
        int top,
        int width,
        int height)
    {
        var result = new PixelImage(width, height);
        for (var y = 0; y < height; y++)
        {
            source.Pixels.AsSpan(
                ((top + y) * source.Stride) + (left * 4),
                width * 4).CopyTo(result.Pixels.AsSpan(y * result.Stride));
        }
        return result;
    }

    private static PixelImage LoadImage(string path)
    {
        using var image = Image.Load<Bgra32>(path);
        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);
        return PixelImage.FromBgra(image.Width, image.Height, pixels);
    }

    private static PixelImage Compose(IReadOnlyList<PixelImage> images)
    {
        const int separator = 1;
        var width = images.Sum(image => image.Width) +
            (separator * Math.Max(0, images.Count - 1));
        var height = images.Max(image => image.Height);
        var result = new PixelImage(width, height);
        result.Fill(255, 255, 255);
        var offsetX = 0;
        foreach (var image in images)
        {
            for (var y = 0; y < image.Height; y++)
            {
                image.Pixels.AsSpan(y * image.Stride, image.Stride).CopyTo(
                    result.Pixels.AsSpan(
                        (y * result.Stride) + (offsetX * 4),
                        image.Stride));
            }
            offsetX += image.Width;
            if (offsetX < result.Width)
            {
                result.FillRect(offsetX, 0, separator, height, 132, 132, 132);
                offsetX += separator;
            }
        }
        return result;
    }
}
