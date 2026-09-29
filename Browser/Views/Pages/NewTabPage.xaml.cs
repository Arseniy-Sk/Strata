using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Browser.Core;
using Browser.Models;
using Browser.Services;

namespace Browser.Views.Pages;

public partial class NewTabPage : UserControl, IPage
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("en-US");
    private readonly TabManager _tabs;
    private readonly Action<string> _openPalette;
    private Space? _space;

    public NewTabPage(TabManager tabs, Action<string> openPalette)
    {
        _tabs = tabs;
        _openPalette = openPalette;
        InitializeComponent();
        BookmarksHeader.Content = Kit.SectionHeader("BOOKMARKS", new Thickness(0, 36, 0, 12));
        ResumeHeader.Content = Kit.SectionHeader("PICK UP WHERE YOU LEFT OFF", new Thickness(0, 36, 0, 12));
        SpacesHeader.Content = Kit.SectionHeader("SPACES", new Thickness(0, 34, 0, 12));
        StatsHeader.Content = Kit.SectionHeader("ENGINE", new Thickness(0, 34, 0, 12));
        Kit.AutoColumns(ResumeGrid, 230);
        Kit.AutoColumns(SpacesGrid, 210);
        SearchCard.MouseEnter += (_, _) => SearchCard.SetResourceReference(Border.BorderBrushProperty, "Accent");
        SearchCard.MouseLeave += (_, _) => SearchCard.SetResourceReference(Border.BorderBrushProperty, "Hair");
    }

    public void OnShown(BrowserTab tab)
    {
        _space = tab.Space ?? _tabs.ActiveSpace;
        var now = DateTime.Now;
        Greeting.Text = $"{Ru.DateTimeFormat.GetDayName(now.DayOfWeek)} · {now.ToString("d MMMM", Ru)} · {Plural.Tabs(_space.TabCount)}".ToUpper(Ru);
        Headline.Text = string.IsNullOrWhiteSpace(_space.Headline) ? _space.Name : _space.Headline;

        BuildBookmarks();
        BuildResume();
        BuildSpaces();
        Stats.Text = $"{_tabs.LiveTabs} loaded · {_tabs.AsleepTabs} asleep · {Format.Bytes(_tabs.MemoryBytes)} across {_tabs.ProcessCount} proc. · " +
                     $"trackers blocked this session: {_tabs.TotalBlocked} · search: {_tabs.Settings.SearchEngineName}";
    }

    private void BuildBookmarks()
    {
        BookmarksList.Items.Clear();
        var items = _tabs.Bookmarks.Items;
        // Bookmarks section is always visible — with a "+" tile to add the current page.
        foreach (var bm in items.Take(24))
        {
            var mark = bm;
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(NewTabPage.Favicon(bm.Host, 18));
            var title = Kit.Text(bm.Title, 12.5);
            title.Margin = new Thickness(9, 0, 0, 0);
            title.VerticalAlignment = VerticalAlignment.Center;
            title.MaxWidth = 150;
            title.TextWrapping = TextWrapping.NoWrap;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            panel.Children.Add(title);

            var chip = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(11, 8, 13, 8), Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand, Child = panel, ToolTip = bm.Url, BorderThickness = new Thickness(1)
            };
            chip.SetResourceReference(Border.BackgroundProperty, "Panel");
            chip.SetResourceReference(Border.BorderBrushProperty, "Hair");
            chip.MouseEnter += (_, _) => chip.SetResourceReference(Border.BorderBrushProperty, "Hair2");
            chip.MouseLeave += (_, _) => chip.SetResourceReference(Border.BorderBrushProperty, "Hair");
            chip.MouseLeftButtonUp += (_, _) => _tabs.NavigateActive(mark.Url);
            chip.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { _tabs.NewTab(mark.Url, activate: false); e.Handled = true; } };
            chip.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                var menu = new ContextMenu();
                var open = new MenuItem { Header = "Open in new tab", Icon = "" };
                open.Click += (_, _) => _tabs.NewTab(mark.Url, activate: false);
                menu.Items.Add(open);
                var del = new MenuItem { Header = "Delete", Icon = "" };
                del.Click += (_, _) => { _tabs.Bookmarks.Remove(mark); BuildBookmarks(); };
                menu.Items.Add(del);
                menu.PlacementTarget = chip;
                menu.IsOpen = true;
            };
            BookmarksList.Items.Add(chip);
        }

        var addPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        addPanel.Children.Add(new TextBlock { Text = "+", FontSize = 16, Margin = new Thickness(0, -2, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Kit.Res("Faint") });
        addPanel.Children.Add(Kit.Text("Add current page", 12.5, "Dim"));
        var add = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(11, 8, 13, 8), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, Child = addPanel };
        add.MouseEnter += (_, _) => add.SetResourceReference(Border.BackgroundProperty, "Hover");
        add.MouseLeave += (_, _) => add.Background = System.Windows.Media.Brushes.Transparent;
        add.MouseLeftButtonUp += (_, _) =>
        {
            if (_tabs.ActiveTab is { IsWeb: true } t) { _tabs.Bookmarks.Add(t.Url, t.Title); BuildBookmarks(); }
            else (Application.Current.MainWindow as MainWindow)?.ShowToast("Open a site first to bookmark it");
        };
        BookmarksList.Items.Add(add);
    }

    private void BuildResume()
    {
        ResumeGrid.Children.Clear();
        var open = new HashSet<string>(_tabs.AllTabs().Select(t => t.Url), StringComparer.OrdinalIgnoreCase);
        var sites = _tabs.History.TopSites(8, open);
        ResumeHeader.Visibility = ResumeGrid.Visibility = sites.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in sites)
        {
            var host = UrlHelper.DisplayHost(entry.Url);
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Favicon(host, 18));
            var hostText = new TextBlock { Text = host, Style = (Style)FindResource("Mono"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(hostText);

            var body = new StackPanel();
            body.Children.Add(head);
            var title = Kit.Text(string.IsNullOrWhiteSpace(entry.Title) ? UrlHelper.Pretty(entry.Url) : entry.Title, 13);
            title.Margin = new Thickness(0, 10, 0, 0);
            title.MaxHeight = 36;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            body.Children.Add(title);

            var card = HoverCard(body);
            var url = entry.Url;
            card.MouseLeftButtonUp += (_, _) => _tabs.NavigateActive(url);
            ResumeGrid.Children.Add(card);
        }
    }

    private void BuildSpaces()
    {
        SpacesGrid.Children.Clear();
        foreach (var space in _tabs.Spaces)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = space.Brush, VerticalAlignment = VerticalAlignment.Center });
            var name = Kit.Text(space.Name, 13, weight: FontWeights.Medium);
            name.Margin = new Thickness(8, 0, 0, 0);
            head.Children.Add(name);

            var body = new StackPanel();
            body.Children.Add(head);
            var stat = new TextBlock { Text = space.Stat, Style = (Style)FindResource("Mono"), FontSize = 11, Margin = new Thickness(0, 10, 0, 0) };
            stat.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            body.Children.Add(stat);

            var card = HoverCard(body);
            if (space == _space) card.SetResourceReference(Border.BorderBrushProperty, "Hair2");
            var target = space;
            card.MouseLeftButtonUp += (_, _) => _tabs.SwitchSpace(target);
            SpacesGrid.Children.Add(card);
        }
    }

    private static Border HoverCard(UIElement body)
    {
        var card = Kit.Card(body, new Thickness(14));
        card.Margin = new Thickness(0, 0, 10, 10);
        card.Cursor = Cursors.Hand;
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "Hair2");
        card.MouseLeave += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "Hair");
        return card;
    }

    internal static FrameworkElement Favicon(string host, double size)
    {
        if (FaviconService.TryGet(host) is { } image)
            return new Image { Source = image, Width = size - 2, Height = size - 2, VerticalAlignment = VerticalAlignment.Center };

        int hue = ColorUtil.HueFor(host);
        var letter = host.StartsWith("www.") ? host[4..] : host;
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(5), Background = ColorUtil.HueBrush(hue),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = letter.Length > 0 ? char.ToUpperInvariant(letter[0]).ToString() : "•",
                Foreground = ColorUtil.HueForeground(hue), FontSize = size * 0.52, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private void SearchCard_Click(object sender, MouseButtonEventArgs e) => _openPalette("");

    private void Headline_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_space == null) return;
        _space.Headline = Headline.Text.Trim();
        _tabs.ScheduleSave();
    }

    private void Headline_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape) Keyboard.ClearFocus();
    }
}
