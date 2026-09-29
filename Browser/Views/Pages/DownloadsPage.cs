using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Browser.Core;
using Browser.Models;
using Browser.Services;

namespace Browser.Views.Pages;

public sealed class DownloadsPage : UserControl, IPage
{
    private readonly TabManager _tabs;
    private readonly StackPanel _list = new();
    private readonly TextBlock _empty;

    public DownloadsPage(TabManager tabs)
    {
        _tabs = tabs;
        SetResourceReference(BackgroundProperty, "Bg");

        var body = new StackPanel { MaxWidth = 820, Margin = new Thickness(32, 32, 32, 56) };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = "Downloads", Style = (Style)FindResource("PageTitle") });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var folder = new Button { Content = "Downloads folder", Style = (Style)FindResource("OutlineButton") };
        folder.Click += (_, _) => Native.OpenExternal(DownloadsFolder());
        var clear = new Button { Content = "Clear list", Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += (_, _) => _tabs.Downloads.ClearFinished();
        buttons.Children.Add(folder);
        buttons.Children.Add(clear);
        Grid.SetColumn(buttons, 1);
        head.Children.Add(buttons);
        body.Children.Add(head);
        body.Children.Add(new TextBlock { Text = "Files are saved to the Windows Downloads folder. A download keeps its tab from sleeping until it finishes.", Style = (Style)FindResource("PageLead") });

        _empty = Kit.Text("Nothing downloaded yet.", 13, "Faint");
        body.Children.Add(_empty);
        body.Children.Add(_list);

        Content = new ScrollViewer { Style = (Style)FindResource("SlimScroll"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Focusable = false };
        _tabs.Downloads.Items.CollectionChanged += (_, _) => Rebuild();
    }

    public void OnShown(BrowserTab tab) => Rebuild();

    private static string DownloadsFolder()
        => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private void Rebuild()
    {
        _list.Children.Clear();
        _empty.Visibility = _tabs.Downloads.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in _tabs.Downloads.Items) _list.Children.Add(BuildCard(item));
    }

    private FrameworkElement BuildCard(DownloadItem item)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = item.ExtensionBrush, Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock { Text = item.Extension, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = item.Extension.Length > 3 ? 10 : 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        grid.Children.Add(icon);

        var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Kit.Text(item.FileName, 13);
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        middle.Children.Add(name);
        middle.Children.Add(new TextBlock { Text = item.Folder, Style = (Style)FindResource("Mono"), FontSize = 11, Margin = new Thickness(0, 4, 0, 7) });

        var track = new Grid { Height = 3 };
        var back = new Border { CornerRadius = new CornerRadius(1.5) };
        back.SetResourceReference(Border.BackgroundProperty, "Well");
        var bar = new Border { CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left };
        bar.SetResourceReference(Border.BackgroundProperty, "Accent");
        track.Children.Add(back);
        track.Children.Add(bar);
        void UpdateBar() => bar.Width = Math.Max(0, track.ActualWidth * item.Progress);
        track.SizeChanged += (_, _) => UpdateBar();
        middle.Children.Add(track);
        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);

        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 120 };
        var status = new TextBlock { Style = (Style)FindResource("Mono"), FontSize = 11, TextAlignment = TextAlignment.Right };
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(DownloadItem.Status)) { Source = item });
        status.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        right.Children.Add(status);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        Button Icon(string glyph, string tip, Action action)
        {
            var b = new Button { Content = glyph, ToolTip = tip, Style = (Style)FindResource("IconButton"), Width = 26, Height = 26, FontSize = 11 };
            b.Click += (_, _) => action();
            actions.Children.Add(b);
            return b;
        }
        var open = Icon("", "Open file", () => DownloadService.Open(item));
        Icon("", "Show in folder", () => DownloadService.ShowInFolder(item));
        var pause = Icon("", "Pause", () => DownloadService.TogglePause(item));
        var cancel = Icon("", "Cancel", () => DownloadService.Cancel(item));
        right.Children.Add(actions);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        void Refresh()
        {
            UpdateBar();
            pause.Visibility = cancel.Visibility = item.IsInProgress ? Visibility.Visible : Visibility.Collapsed;
            pause.Content = item.IsPaused ? "" : "";
            open.IsEnabled = item.IsDone;
        }
        item.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(Refresh);
        Refresh();

        var card = Kit.Card(grid, new Thickness(14));
        card.CornerRadius = new CornerRadius(12);
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }
}
