using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Browser.Core;
using Browser.Models;
using Browser.Services;

namespace Browser.Views.Pages;

public sealed class AboutPage : UserControl, IPage
{
    private readonly TabManager _tabs;
    private readonly TextBlock _stats;

    public AboutPage(TabManager tabs)
    {
        _tabs = tabs;
        SetResourceReference(BackgroundProperty, "Bg");
        var body = new StackPanel { MaxWidth = 920, Margin = new Thickness(32, 40, 32, 64) };

        // Mark and name
        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        var mark = new StackPanel { Width = 44, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        for (int i = 0; i < 3; i++)
        {
            var bar = new Border { Height = 9, CornerRadius = new CornerRadius(4), Margin = new Thickness(i * 8, i == 0 ? 0 : 5, 0, 0), Opacity = 1 - i * 0.35 };
            bar.SetResourceReference(Border.BackgroundProperty, "Accent");
            mark.Children.Add(bar);
        }
        brand.Children.Add(mark);
        var names = new StackPanel();
        var title = Kit.Text("S T R A T A", 38, weight: FontWeights.Bold);
        title.FontFamily = (FontFamily)FindResource("DisplayFont");
        names.Children.Add(title);
        var tagline = Kit.Text("A thousand tabs, one order", 14, "Dim");
        tagline.Margin = new Thickness(0, 4, 0, 0);
        names.Children.Add(tagline);
        brand.Children.Add(names);
        body.Children.Add(brand);

        var grid = new UniformGrid { Margin = new Thickness(0, 34, 0, 0) };
        Kit.AutoColumns(grid, 270);
        foreach (var (tag, head, text) in new[]
                 {
                     ("NAME", "Strata", "Layers: tabs sit in strata — spaces, branches, the sleeping. Short, and reads the same in Latin and Cyrillic."),
                     ("MARK", "Three offset layers", "Three bars with decreasing opacity and a stepped offset. Works as a 16 px icon and as a tab glyph."),
                     ("SPEED", "Sleep instead of closing", "Background tabs freeze, then unload. Hundreds of tabs in the tree cost as much as a handful of live ones."),
                     ("VOICE", "Calm and precise", "The interface speaks in nouns and numbers: \"20 min\", \"14 blocked\". No exclamation marks.")
                 })
        {
            var card = new StackPanel();
            card.Children.Add(Kit.Caps(tag));
            var h = Kit.Text(head, 15);
            h.Margin = new Thickness(0, 10, 0, 8);
            card.Children.Add(h);
            var t = Kit.Text(text, 13, "Dim");
            t.LineHeight = 20;
            card.Children.Add(t);
            var border = Kit.Card(card, new Thickness(18));
            border.CornerRadius = new CornerRadius(14);
            border.Margin = new Thickness(0, 0, 14, 14);
            grid.Children.Add(border);
        }
        body.Children.Add(grid);

        var swatches = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (name, color) in new[]
                 {
                     ("Strata Teal", ThemeManager.AccentColor(AccentKind.Teal)), ("Ink #0B0D10", (Color)ColorConverter.ConvertFromString("#0B0D10")),
                     ("Paper #FBFAF8", (Color)ColorConverter.ConvertFromString("#FBFAF8")), ("Indigo", ThemeManager.AccentColor(AccentKind.Blue)),
                     ("Lavender", ThemeManager.AccentColor(AccentKind.Violet)), ("Coral", ThemeManager.AccentColor(AccentKind.Orange))
                 })
        {
            var cell = new StackPanel { Width = 96, Margin = new Thickness(0, 0, 8, 8) };
            var chip = new Border { Height = 44, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(color), BorderThickness = new Thickness(1) };
            chip.SetResourceReference(Border.BorderBrushProperty, "Hair");
            cell.Children.Add(chip);
            cell.Children.Add(new TextBlock { Text = name, Style = (Style)FindResource("Mono"), Margin = new Thickness(0, 6, 0, 0) });
            swatches.Children.Add(cell);
        }
        body.Children.Add(swatches);

        _stats = new TextBlock { Style = (Style)FindResource("Mono"), FontSize = 11, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, LineHeight = 19, Margin = new Thickness(0, 22, 0, 0) };
        body.Children.Add(_stats);

        Content = new ScrollViewer { Style = (Style)FindResource("SlimScroll"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Focusable = false };
    }

    public void OnShown(BrowserTab tab)
    {
        _stats.Text =
            $"Strata 1.0 · .NET {Environment.Version} · WebView2 {WebEngine.RuntimeVersion}\n" +
            $"{Plural.Tabs(_tabs.TotalTabs)} across {_tabs.Spaces.Count} spaces · {_tabs.LiveTabs} in memory · {_tabs.AsleepTabs} asleep\n" +
            $"{Format.Bytes(_tabs.MemoryBytes)} across {_tabs.ProcessCount} proc. · blocked this session: {_tabs.TotalBlocked}\n" +
            $"Data: {AppPaths.Root}";
    }
}
