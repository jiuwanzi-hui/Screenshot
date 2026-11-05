using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using Screenshot.App.Core;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed class TranslationProfileReorderTests
{
    private static readonly string[] ReorderedIds = ["b", "a"];
    private static SettingsViewModel CreateModel() => new(new AppSettings
    {
        TranslationProfiles = [
            new AiTranslationProfile { Id = "a", Name = "A", Provider = "Zhipu" },
            new AiTranslationProfile { Id = "public", Name = "公共", Provider = "Baidu" },
            new AiTranslationProfile { Id = "b", Name = "B", Provider = "OpenAICompatible", Endpoint = "https://example.test/v1", Model = "chosen-model" }],
    });

    [Fact]
    public void FilteredViewsPublishMovesAndKeepPublicOrderAndConnectionFields()
    {
        var model = CreateModel();
        var online = model.OnlineTranslationProfiles;
        var publicView = model.PublicTranslationProfiles;
        var actions = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)online).CollectionChanged += (_, args) => actions.Add(args.Action);
        Assert.True(model.MoveTranslationProfileToInsertionIndex(model.TranslationProfiles[0], 3));
        Assert.Same(online, model.OnlineTranslationProfiles);
        Assert.Equal(ReorderedIds, online.Select(item => item.Id));
        Assert.Contains(NotifyCollectionChangedAction.Move, actions);
        Assert.Same(publicView, model.PublicTranslationProfiles);
        Assert.Equal("public", Assert.Single(publicView).Id);
        var saved = model.CreateSettings();
        var reloaded = new SettingsViewModel(saved);
        Assert.Equal(ReorderedIds, reloaded.OnlineTranslationProfiles.Select(item => item.Id));
        Assert.Equal("https://example.test/v1", reloaded.OnlineTranslationProfiles[0].Endpoint);
        Assert.Equal("chosen-model", reloaded.OnlineTranslationProfiles[0].Model);
    }

    [Fact]
    public void InsertRemoveAndProviderEditRefreshExistingFilteredViewInstances()
    {
        var model = CreateModel();
        var online = model.OnlineTranslationProfiles;
        var item = online[0];
        item.Provider = "Youdao";
        model.RefreshTranslationProfileViews();
        Assert.Single(online);
        Assert.Contains(item, model.PublicTranslationProfiles);
        model.TranslationProfiles.Remove(item);
        Assert.DoesNotContain(item, model.PublicTranslationProfiles);
        model.TranslationProfiles.Insert(0, item);
        Assert.Equal(item, model.PublicTranslationProfiles[0]);
    }

    [Fact]
    public void ReorderUpdatesVisibleRowsAndReleasesDragPreview()
    {
        WpfTestHost.Invoke(() =>
        {
            var model = CreateModel();
            var list = new ItemsControl { ItemsSource = model.OnlineTranslationProfiles,
                ItemTemplate = (DataTemplate)XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border Height='56' Margin='0,0,0,6' Background='Gray'><TextBlock Text='{Binding Name}'/></Border></DataTemplate>") };
            var host = new AdornerDecorator { Child = list };
            host.Measure(new Size(600, 300)); host.Arrange(new Rect(0, 0, 600, 300)); host.UpdateLayout();
            var container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0);
            var row = FindBorder(container)!;
            using var feedback = new ProfileReorderFeedback(row, list, new Point(20, 20));
            Assert.NotEmpty(host.AdornerLayer.GetAdorners(list)!);
            feedback.Update(new Point(20, 100));
            Assert.True(model.MoveTranslationProfileToInsertionIndex(model.TranslationProfiles[0], 3));
            feedback.AnimatePlacement();
            feedback.Dispose();
            Assert.True(host.AdornerLayer.GetAdorners(list) is not { Length: > 0 });
            Assert.Equal(1, row.Opacity);
            Assert.Equal("b", ((AiTranslationProfileItem)list.Items[0]).Id);
            for (var index = 0; index < list.Items.Count; index++)
            {
                var current = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(index);
                current.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Assert.True(current.RenderTransform.Value.IsIdentity);
            }
        });
    }

    [Fact]
    public void PriorityDragPreservesRowsAvailabilityAndPersistsActualProviderOrder()
    {
        var model = CreateModel();
        var original = model.TranslationPriorityItems.ToArray();
        original[0].SetAvailability(false, "余额不足");
        var actions = new List<NotifyCollectionChangedAction>();
        model.TranslationPriorityItems.CollectionChanged += (_, args) => actions.Add(args.Action);
        Assert.True(model.MoveTranslationProviderToInsertionIndex(original[0], 3));
        Assert.Same(original[0], model.TranslationPriorityItems[2]);
        Assert.Equal("余额不足", model.TranslationPriorityItems[2].AvailabilityReason);
        Assert.Equal(Enumerable.Range(1, 3), model.TranslationPriorityItems.Select(item => item.Position));
        Assert.Equal(new[] { original[1].Provider, original[2].Provider, original[0].Provider },
            new SettingsViewModel(model.CreateSettings()).TranslationPriorityItems.Select(item => item.Provider));
        Assert.Equal(new[] { NotifyCollectionChangedAction.Move }, actions);
        Assert.False(model.MoveTranslationProviderToInsertionIndex(original[0], 2));
        Assert.False(model.MoveTranslationProviderToInsertionIndex(original[0], -1));
        Assert.False(model.MoveTranslationProviderToInsertionIndex(original[0], 4));
        Assert.True(model.MoveTranslationProviderToInsertionIndex(original[0], 0));
        Assert.Equal(original, model.TranslationPriorityItems);
    }

    [Fact]
    public void PriorityRowsReuseDragFeedbackAndReleaseItAfterPlacementOrCancellation()
    {
        WpfTestHost.Invoke(() =>
        {
            var model = CreateModel();
            var list = new ItemsControl { ItemsSource = model.TranslationPriorityItems,
                ItemTemplate = (DataTemplate)XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border Height='48' Background='Transparent'><TextBlock Text='{Binding Label}'/></Border></DataTemplate>") };
            var host = new AdornerDecorator { Child = list };
            host.Measure(new Size(600, 300)); host.Arrange(new Rect(0, 0, 600, 300)); host.UpdateLayout();
            var row = FindBorder((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0))!;
            using var feedback = new ProfileReorderFeedback(row, list, new Point(20, 20));
            var moved = model.TranslationPriorityItems[0];
            Assert.True(model.MoveTranslationProviderToInsertionIndex(moved, 3));
            feedback.AnimatePlacement();
            Assert.Same(moved, list.Items[2]);
            Assert.True(host.AdornerLayer.GetAdorners(list) is not { Length: > 0 });
            Assert.Equal(1, row.Opacity);
            for (var index = 0; index < list.Items.Count; index++)
                ((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(index)).RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            using var cancelled = new ProfileReorderFeedback(row, list, new Point(20, 20));
            cancelled.Dispose();
            Assert.Same(moved, list.Items[2]);
            Assert.True(host.AdornerLayer.GetAdorners(list) is not { Length: > 0 });
        });
    }

    private static Border? FindBorder(DependencyObject root)
    {
        if (root is Border border) return border;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindBorder(VisualTreeHelper.GetChild(root, index)) is { } result) return result;
        return null;
    }
}
