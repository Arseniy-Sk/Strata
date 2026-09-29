using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Browser.Core;
using Browser.Models;
using Browser.Services;

namespace Browser.Views.Pages;

/// <summary>History and sessions. The history list is virtualized: day headers are rows just like entries.</summary>
public sealed class HistoryPage : UserControl, IPage
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("en-US");
    private readonly TabManager _tabs;
    private readonly UniformGrid _sessions = new();
    private readonly ListBox _list;
    private readonly TextBox _search;
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly TextBlock _sessionsHeader;

    private sealed record Row(bool IsDay, string Text, HistoryEntry? Entry);

    public HistoryPage(TabManager tabs)
    {
        _tabs = tabs;
        SetResourceReference(BackgroundProperty, "Bg");

        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(new TextBlock { Text = "History and sessions", Style = (Style)FindResource("PageTitle") });
        top.Children.Add(new TextBlock
        {
            Text = "A session is saved every time the window closes. Any one can be reopened as a new space — its tabs come back asleep.",
            Style = (Style)FindResource("PageLead")
        });
        _sessionsHeader = Kit.Caps("SESSIONS", new Thickness(0, 0, 0, 10));
        top.Children.Add(_sessionsHeader);
        Kit.AutoColumns(_sessions, 250);
        _sessions.Margin = new Thickness(0, 0, 0, 22);
        top.Children.Add(_sessions);

        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _search = new TextBox { Style = (Style)FindResource("FieldTextBox"), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 280 };
        var searchHost = new Grid();
        searchHost.Children.Add(_search);
        var placeholder = Kit.Text("Search history", 12.5, "Faint");
        placeholder.Margin = new Thickness(11, 0, 0, 0);
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.IsHitTestVisible = false;
        searchHost.Children.Add(placeholder);
        _search.TextChanged += (_, _) =>
        {
            placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _searchDelay.Stop();
            _searchDelay.Start();
        };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Fill(); };
        toolbar.Children.Add(searchHost);

        var clear = new Button { Content = "Clear history", Style = (Style)FindResource("OutlineButton") };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(Window.GetWindow(this)!, "Delete all browsing history?", "Strata", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _tabs.History.Clear();
            Fill();
        };
        Grid.SetColumn(clear, 1);
        toolbar.Children.Add(clear);
        top.Children.Add(toolbar);

        _list = new ListBox { Style = (Style)FindResource("BareList"), ItemTemplate = BuildRowTemplate() };
        _list.PreviewMouseLeftButtonUp += OnRowClick;
        _list.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle && RowFrom(e) is { Entry: { } entry })
            {
                _tabs.NewTab(entry.Url, activate: false);
                e.Handled = true;
            }
        };

        var layout = new Grid { MaxWidth = 900, Margin = new Thickness(32, 32, 32, 0) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.Children.Add(top);
        Grid.SetRow(_list, 1);
        layout.Children.Add(_list);
        Content = layout;
    }

    public void OnShown(BrowserTab tab)
    {
        FillSessions();
        Fill();
    }

    private void FillSessions()
    {
        _sessions.Children.Clear();
        var archive = SessionStore.LoadArchive();
        _sessionsHeader.Visibility = _sessions.Visibility = archive.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var session in archive.Take(9))
        {
            var body = new StackPanel();
            var head = new Grid();
            var name = Kit.Text(string.IsNullOrWhiteSpace(session.Name) ? "Session" : session.Name, 13, weight: FontWeights.Medium);
            name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Margin = new Thickness(0, 0, 60, 0);
            head.Children.Add(name);
            head.Children.Add(new TextBlock { Text = Plural.Tabs(session.CountTabs()), Style = (Style)FindResource("Mono"), HorizontalAlignment = HorizontalAlignment.Right });
            body.Children.Add(head);

            var when = new TextBlock { Text = Describe(session.SavedUtc.ToLocalTime()), Style = (Style)FindResource("Mono"), FontSize = 11, Margin = new Thickness(0, 8, 0, 10) };
            when.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            body.Children.Add(when);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var restore = new Button { Content = "Restore", Style = (Style)FindResource("OutlineButton") };
            var data = session;
            restore.Click += (_, _) => _tabs.RestoreArchived(data);
            buttons.Children.Add(restore);
            var remove = new Button { Content = "Delete", Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(6, 0, 0, 0) };
            remove.SetResourceReference(ForegroundProperty, "Faint");
            remove.Click += (_, _) => { SessionStore.RemoveArchived(data); FillSessions(); };
            buttons.Children.Add(remove);
            body.Children.Add(buttons);

            var card = Kit.Card(body, new Thickness(14));
            card.Margin = new Thickness(0, 0, 10, 10);
            _sessions.Children.Add(card);
        }
    }

    private void Fill()
    {
        var query = _search.Text.Trim();
        var rows = new List<Row>();
        DateTime? day = null;
        var entries = _tabs.History.Entries;
        for (int i = entries.Count - 1; i >= 0 && rows.Count < 5000; i--)
        {
            var e = entries[i];
            if (query.Length > 0 && e.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                                 && e.Url.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var local = e.VisitedUtc.ToLocalTime();
            if (day != local.Date)
            {
                day = local.Date;
                rows.Add(new Row(true, DayName(local.Date), null));
            }
            rows.Add(new Row(false, string.IsNullOrWhiteSpace(e.Title) ? UrlHelper.Pretty(e.Url) : e.Title, e));
        }
        _list.ItemsSource = rows;
    }

    private static string DayName(DateTime date)
    {
        var today = DateTime.Today;
        if (date == today) return "TODAY";
        if (date == today.AddDays(-1)) return "YESTERDAY";
        return date.ToString("dddd, d MMMM", Ru).ToUpper(Ru);
    }

    private static string Describe(DateTime local)
    {
        var day = local.Date == DateTime.Today ? "today" : local.Date == DateTime.Today.AddDays(-1) ? "yesterday" : local.ToString("d MMM", Ru);
        return $"{day} · {local:HH:mm}".ToUpper(Ru);
    }

    private static DataTemplate BuildRowTemplate() => new() { VisualTree = new FrameworkElementFactory(typeof(RowView)) };

    private static Row? RowFrom(RoutedEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;
        while (node != null)
        {
            if (node is FrameworkElement { DataContext: Row row }) return row;
            node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (RowFrom(e) is not { Entry: { } entry }) return;
        if (e.OriginalSource is FrameworkElement { Tag: "remove" })
        {
            _tabs.History.Remove(entry);
            Fill();
            return;
        }
        if (_tabs.FindOpenTab(entry.Url) is { } open) _tabs.Activate(open);
        else _tabs.NewTab(entry.Url);
    }

    /// <summary>A history row: either a day header or an entry. Built once per container and updated via DataContext.</summary>
    private sealed class RowView : Grid
    {
        private readonly TextBlock _day = Kit.Caps("", new Thickness(8, 18, 0, 8));
        private readonly Border _item = new() { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 9, 8, 9), Background = System.Windows.Media.Brushes.Transparent, Cursor = Cursors.Hand };
        private readonly ContentControl _fav = new() { Width = 18, Height = 18, Focusable = false, Margin = new Thickness(0, 0, 11, 0) };
        private readonly TextBlock _title = Kit.Text("", 13);
        private readonly TextBlock _host = new();
        private readonly TextBlock _time = new();
        private readonly TextBlock _remove = new() { Text = "", FontSize = 9, Tag = "remove", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden, ToolTip = "Remove from history" };

        public RowView()
        {
            _title.TextWrapping = TextWrapping.NoWrap;
            _title.TextTrimming = TextTrimming.CharacterEllipsis;
            _title.VerticalAlignment = VerticalAlignment.Center;
            _host.Style = (Style)Application.Current.FindResource("Mono");
            _host.FontSize = 11;
            _host.Margin = new Thickness(12, 0, 0, 0);
            _host.VerticalAlignment = VerticalAlignment.Center;
            _host.MaxWidth = 220;
            _time.Style = (Style)Application.Current.FindResource("Mono");
            _time.FontSize = 11;
            _time.Width = 44;
            _time.TextAlignment = TextAlignment.Right;
            _time.VerticalAlignment = VerticalAlignment.Center;
            _remove.FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("IconFont");
            _remove.SetResourceReference(TextBlock.ForegroundProperty, "Faint");

            var grid = new Grid();
            foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            grid.Children.Add(_fav);
            Grid.SetColumn(_title, 1); grid.Children.Add(_title);
            Grid.SetColumn(_host, 2); grid.Children.Add(_host);
            Grid.SetColumn(_time, 3); grid.Children.Add(_time);
            Grid.SetColumn(_remove, 4); grid.Children.Add(_remove);
            _item.Child = grid;
            _item.MouseEnter += (_, _) => { _item.SetResourceReference(Border.BackgroundProperty, "Hover"); _remove.Visibility = Visibility.Visible; };
            _item.MouseLeave += (_, _) => { _item.Background = System.Windows.Media.Brushes.Transparent; _remove.Visibility = Visibility.Hidden; };

            Children.Add(_day);
            Children.Add(_item);
            DataContextChanged += (_, _) => Bind();
        }

        private void Bind()
        {
            if (DataContext is not Row row) return;
            _day.Visibility = row.IsDay ? Visibility.Visible : Visibility.Collapsed;
            _item.Visibility = row.IsDay ? Visibility.Collapsed : Visibility.Visible;
            if (row.IsDay) { _day.Text = row.Text; return; }
            var entry = row.Entry!;
            var host = UrlHelper.DisplayHost(entry.Url);
            _title.Text = row.Text;
            _host.Text = host;
            _time.Text = entry.VisitedUtc.ToLocalTime().ToString("HH:mm");
            _fav.Content = NewTabPage.Favicon(host, 18);
            _item.ToolTip = entry.Url;
        }
    }
}
