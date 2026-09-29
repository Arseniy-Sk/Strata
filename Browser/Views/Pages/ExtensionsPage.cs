using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Browser.Core;
using Browser.Models;
using Browser.Services;
using Microsoft.Win32;

namespace Browser.Views.Pages;

/// <summary>Unpacked Chromium extensions. The engine loads them into the shared profile used by all tabs.</summary>
public sealed class ExtensionsPage : UserControl, IPage
{
    private readonly TabManager _tabs;
    private readonly UniformGrid _grid = new();
    private readonly TextBlock _status;

    public ExtensionsPage(TabManager tabs)
    {
        _tabs = tabs;
        SetResourceReference(BackgroundProperty, "Bg");

        var body = new StackPanel { MaxWidth = 940, Margin = new Thickness(32, 32, 32, 56) };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = "Extensions", Style = (Style)FindResource("PageTitle") });
        var add = new Button { Content = "Load unpacked…", Style = (Style)FindResource("OutlineButton"), VerticalAlignment = VerticalAlignment.Center };
        add.Click += async (_, _) => await AddAsync();
        Grid.SetColumn(add, 1);
        head.Children.Add(add);
        body.Children.Add(head);
        body.Children.Add(new TextBlock
        {
            Text = "Each extension declares its own permissions. Chromium extensions are supported as a folder with a manifest.json — for example uBlock Origin Lite or Bitwarden, unpacked from a .crx/.zip.",
            Style = (Style)FindResource("PageLead")
        });
        _status = Kit.Text("", 13, "Faint");
        body.Children.Add(_status);
        Kit.AutoColumns(_grid, 290);
        body.Children.Add(_grid);

        Content = new ScrollViewer { Style = (Style)FindResource("SlimScroll"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Focusable = false };
    }

    public async void OnShown(BrowserTab tab) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        _grid.Children.Clear();
        var profile = _tabs.AnyProfile;
        if (profile == null)
        {
            _status.Text = "The engine is asleep right now: open any site to view and manage extensions.";
            return;
        }

        try
        {
            var list = await profile.GetBrowserExtensionsAsync();
            _status.Text = list.Count == 0 ? "No extensions yet." : "";
            foreach (var ext in list)
            {
                var extension = ext;
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                int hue = ColorUtil.HueFor(extension.Name);
                row.Children.Add(new Border
                {
                    Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = ColorUtil.HueBrush(hue), Margin = new Thickness(0, 0, 10, 0),
                    Child = new TextBlock { Text = extension.Name.Length > 0 ? extension.Name[..1].ToUpperInvariant() : "E", Foreground = ColorUtil.HueForeground(hue), FontWeight = FontWeights.SemiBold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                });
                var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var title = Kit.Text(extension.Name, 13);
                title.TextWrapping = TextWrapping.NoWrap;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                names.Children.Add(title);
                names.Children.Add(new TextBlock { Text = extension.Id, Style = (Style)FindResource("Mono") });
                Grid.SetColumn(names, 1);
                row.Children.Add(names);

                var toggle = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = extension.IsEnabled, VerticalAlignment = VerticalAlignment.Center };
                toggle.Click += async (_, _) =>
                {
                    try { await extension.EnableAsync(toggle.IsChecked == true); }
                    catch (Exception ex) { _status.Text = ex.Message; }
                };
                Grid.SetColumn(toggle, 2);
                row.Children.Add(toggle);

                var card = new StackPanel();
                card.Children.Add(row);
                var remove = new Button { Content = "Remove", Style = (Style)FindResource("OutlineButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
                remove.SetResourceReference(ForegroundProperty, "Faint");
                remove.Click += async (_, _) =>
                {
                    try { await extension.RemoveAsync(); await RefreshAsync(); }
                    catch (Exception ex) { _status.Text = ex.Message; }
                };
                card.Children.Add(remove);

                var border = Kit.Card(card, new Thickness(16));
                border.Margin = new Thickness(0, 0, 10, 10);
                _grid.Children.Add(border);
            }
        }
        catch (Exception ex)
        {
            _status.Text = "The engine doesn't support extensions: " + ex.Message;
        }
    }

    private async Task AddAsync()
    {
        var profile = _tabs.AnyProfile;
        if (profile == null)
        {
            _status.Text = "Open any site first — extensions are installed through a running engine.";
            return;
        }
        var dialog = new OpenFolderDialog { Title = "Extension folder (with manifest.json)" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            await profile.AddBrowserExtensionAsync(dialog.FolderName);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _status.Text = "Failed to load: " + ex.Message;
        }
    }
}
