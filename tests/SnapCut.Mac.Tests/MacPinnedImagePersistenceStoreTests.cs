using SnapCut.Mac.Pin;

namespace SnapCut.Mac.Tests;

public sealed class MacPinnedImagePersistenceStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SnapCut-Pins-{Guid.NewGuid():N}");

    [Fact]
    public void RoundTripsExistingPinnedImagesAndDropsMissingFiles()
    {
        Directory.CreateDirectory(_directory);
        var imagePath = Path.Combine(_directory, "capture.png");
        File.WriteAllBytes(imagePath, [1, 2, 3]);
        var store = new MacPinnedImagePersistenceStore(
            Path.Combine(_directory, "pins.json"));
        var expected = new MacPinnedImageState(
            Guid.NewGuid(), imagePath, 120, 80, 1.5, 0.7, true);

        store.Save(
        [
            expected,
            expected with
            {
                Id = Guid.NewGuid(),
                ImagePath = Path.Combine(_directory, "missing.png"),
            },
        ]);
        var actual = store.Load();

        Assert.Equal([expected], actual);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
