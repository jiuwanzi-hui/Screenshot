using System.Windows;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class SettingsInteractionCostTests
{
    [Fact]
    public void SameThemeDoesNotReplaceBrushesOrNotifyEveryWindowAgain()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new AppThemeManager();
            var notifications = 0;
            manager.ThemeChanged += (_, _) => notifications++;
            manager.Apply(AppTheme.GlassInk);
            var brush = Application.Current.Resources["AppTextPrimaryBrush"];
            manager.Apply(AppTheme.GlassInk);
            Assert.Equal(1, notifications);
            Assert.Same(brush, Application.Current.Resources["AppTextPrimaryBrush"]);
            manager.Apply(AppTheme.AuroraMist);
            Assert.Equal(2, notifications);
            Assert.NotSame(brush, Application.Current.Resources["AppTextPrimaryBrush"]);
        });
    }
}
