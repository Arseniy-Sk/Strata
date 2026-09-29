using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Browser.Models;

namespace Browser.Views.Pages;

public interface IPage
{
    void OnShown(BrowserTab tab);
}

/// <summary>Small layout builders for pages assembled in code.</summary>
internal static class Kit
{
    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public static TextBlock Caps(string text, Thickness margin = default) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("Caps"),
        Margin = margin
    };

    public static void Dynamic(FrameworkElement element, DependencyProperty property, string key)
        => element.SetResourceReference(property, key);

    public static TextBlock Text(string text, double size, string brush = "Text", FontWeight? weight = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (weight != null) block.FontWeight = weight.Value;
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    /// <summary>A section header with a trailing line: "CONTINUE ———".</summary>
    public static Grid SectionHeader(string text, Thickness margin)
    {
        var grid = new Grid { Margin = margin };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(Caps(text));
        var line = new Border { Height = 1, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        line.SetResourceReference(Border.BackgroundProperty, "Hair");
        Grid.SetColumn(line, 1);
        grid.Children.Add(line);
        return grid;
    }

    public static Border Card(UIElement child, Thickness padding)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(1),
            Padding = padding,
            Child = child
        };
        border.SetResourceReference(Border.BackgroundProperty, "Panel");
        border.SetResourceReference(Border.BorderBrushProperty, "Hair");
        return border;
    }

    /// <summary>As many columns as fit at the card's minimum width (like grid auto-fill).</summary>
    public static void AutoColumns(System.Windows.Controls.Primitives.UniformGrid grid, double minWidth)
    {
        void Update() => grid.Columns = Math.Max(1, (int)(grid.ActualWidth / minWidth));
        grid.SizeChanged += (_, _) => Update();
        grid.Loaded += (_, _) => Update();
    }
}
