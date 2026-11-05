using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Border = System.Windows.Controls.Border;

namespace Screenshot.App.Presentation;

public partial class MainWindow
{
    private void ConfigureResponsiveSettings(Grid columns, Grid sidebar, Grid content)
    {
        var forms = LogicalDescendants(content).OfType<Grid>()
            .Where(grid => Equals(grid.Tag, "SettingsFormRow")).ToArray();
        var themes = ((StackPanel)((Border)_generalPages[0][0]).Child)
            .Children.OfType<UniformGrid>().Single();
        var labels = SettingsNavigation.Items.OfType<ListBoxItem>()
            .SelectMany(item => LogicalDescendants(item).OfType<TextBlock>()).ToArray();
        foreach (var label in labels)
        {
            label.Height = double.NaN;
            label.TextWrapping = TextWrapping.Wrap;
            label.LineHeight = 20;
            if (label.Parent is Grid row) { row.Height = double.NaN; row.MinHeight = 24; }
        }
        var previousMode = -1;
        var updateHeader = (Grid)CheckForUpdatesButton.Parent;
        updateHeader.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        updateHeader.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var modelHeaders = LogicalDescendants(content).OfType<DockPanel>()
            .Where(panel => Equals(panel.Tag, "ModelIdentityHeader")).ToArray();
        ScrollViewer.SetHorizontalScrollBarVisibility(SettingsNavigation, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(SettingsNavigation, ScrollBarVisibility.Auto);
        void Refresh()
        {
            var width = columns.ActualWidth > 0 ? columns.ActualWidth : Width;
            var mode = width < 680 ? 2 : width < 860 ? 1 : 0;
            // WPF lays out the current viewport during resizing. Only rebuild
            // responsive rows when a breakpoint changes, never stretch the UI.
            if (previousMode == mode) return;
            previousMode = mode;
            var compact = mode == 2;
            Grid.SetColumn(CheckForUpdatesButton, compact ? 0 : 1);
            Grid.SetRow(CheckForUpdatesButton, compact ? 1 : 0);
            CheckForUpdatesButton.HorizontalAlignment = compact
                ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
            CheckForUpdatesButton.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(16, 0, 0, 0);
            foreach (var header in modelHeaders)
                if (header.Children.OfType<WrapPanel>().FirstOrDefault(panel => Equals(panel.Tag, "ModelActions")) is { } actions)
                {
                    DockPanel.SetDock(actions, mode == 0 ? Dock.Right : Dock.Bottom);
                    actions.Width = mode == 0 ? 258 : double.NaN;
                    actions.Margin = mode == 0 ? new Thickness(12, 0, 0, 0) : new Thickness(0, 10, 0, 0);
                }
            columns.ColumnDefinitions[0].Width = new GridLength(compact ? 64 : UiLocalization.Language == "zh-Hans" ? 180 : 210);
            sidebar.Margin = compact ? new Thickness(5, 16, 5, 12) : new Thickness(12, 20, 12, 16);
            foreach (var item in SettingsNavigation.Items.OfType<ListBoxItem>())
            {
                item.MinWidth = 0;
                item.Padding = compact ? new Thickness(12, 10, 8, 10) : new Thickness(12, 10, 12, 10);
                item.ToolTip = LogicalDescendants(item).OfType<TextBlock>().FirstOrDefault()?.Text;
            }
            foreach (var label in labels) label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            themes.Columns = mode == 0 ? 4 : mode == 1 ? 3 : 2;
            foreach (var toggle in GeneralAppearanceGroup.Children.OfType<System.Windows.Controls.CheckBox>())
                toggle.Width = compact ? 340 : 420;
            foreach (var row in forms)
            {
                row.RowDefinitions.Clear();
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.ColumnDefinitions[0].Width = mode == 0 ? new GridLength(180) : GridLength.Auto;
                row.ColumnDefinitions[1].Width = mode == 0 ? GridLength.Auto : new GridLength(0);
                for (var index = 0; index < row.Children.Count; index++)
                {
                    var child = (FrameworkElement)row.Children[index];
                    var editor = index > 0;
                    Grid.SetColumn(child, mode == 0 && editor ? 1 : 0);
                    Grid.SetRow(child, mode != 0 && editor ? 1 : 0);
                    if (!editor) child.Margin = mode == 0
                        ? new Thickness(0, 0, 14, 0) : new Thickness(0, 0, 0, 7);
                }
            }
        }
        columns.SizeChanged += (_, _) => Refresh();
        Loaded += (_, _) => Refresh();
        EventHandler languageChanged = (_, _) => { previousMode = -1; Refresh(); };
        UiLocalization.Changed += languageChanged;
        Closed += (_, _) => UiLocalization.Changed -= languageChanged;
        Refresh();
    }
}
