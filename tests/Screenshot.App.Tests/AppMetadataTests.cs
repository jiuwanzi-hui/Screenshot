using Screenshot.App.Core;

namespace Screenshot.App.Tests;

public sealed class AppMetadataTests
{
    [Fact]
    public void UsesTheExpectedApplicationName()
    {
        Assert.Equal("Screenshot", AppMetadata.ApplicationName);
    }
}
