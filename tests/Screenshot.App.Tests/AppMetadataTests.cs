using Screenshot.App.Core;

namespace Screenshot.App.Tests;

public sealed class AppMetadataTests
{
    [Fact]
    public void UsesTheExpectedApplicationName()
    {
        Assert.Equal("Screenshot", AppMetadata.ApplicationName);
    }

    [Fact]
    public void UsesSnapCutAsTheUserFacingDisplayName()
    {
        Assert.Equal("SnapCut", AppMetadata.DisplayName);
    }

    [Fact]
    public void FormatsTheCompletedUpdateStatusWithTheDisplayName()
    {
        Assert.Equal(
            "已更新到 SnapCut 2.3.2。",
            AppMetadata.FormatUpdatedVersionStatus(" 2.3.2 "));
    }
}
