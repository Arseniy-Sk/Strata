using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Browser.Core;
using Browser.Models;
using Browser.Services;
using Browser.Views.Pages;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Browser.Views;

/// <summary>
/// A real popup window for a sized window.open (Google OAuth sign-in, "Share" dialogs, etc.).
/// Shares the engine environment with the main window, so cookies/session are shared, and
/// window.opener, window.close(), and postMessage between the window and its opener work normally.
/// </summary>
public sealed class PopupWindow : Window
{
    private readonly TabManager _tabs;
    private readonly WebView2 _view;
    private readonly TextBlock _title = new() { FontSize = 12, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _host = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
    private readonly ContentControl _fav = new() { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
    private string _url = "";
    private bool _closing;

    public PopupWindow(TabManager tabs, CoreWebView2WindowFeatures? features)
    {
        _tabs = tabs;
        _view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, ThemeManager.Background.R, ThemeManager.Background.G, ThemeManager.Background.B) };

        Title = "Strata";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current.MainWindow;
        ResizeMode = ResizeMode.CanResize;
        Background = (Brush)Application.Current.FindResource("Bg");
        Foreground = (Brush)Application.Current.FindResource("Text");
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        SnapsToDevicePixels = true;

        // Size and position from window.open ("width=500,height=600,left=…").
        double w = features is { HasSize: true } ? Math.Clamp(features.Width, 320, 1400) : 640;
        double h = features is { HasSize: true } ? Math.Clamp(features.Height, 240, 1000) : 720;
        Width = w;
        Height = h + 36;
        MinWidth = 320;
        MinHeight = 240;
        if (features is { HasPosition: true })
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = features.Left;
            Top = features.Top;
        }

        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 34, ResizeBorderThickness = new Thickness(6), CornerRadius = default, GlassFrameThickness = new Thickness(0, 0, 0, 1), UseAeroCaptionButtons = false });
        Content = BuildChrome();
    }

    private UIElement BuildChrome()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        root.RowDefinitions.Add(new RowDefinition());

        var bar = new Border { Background = (Brush)Application.Current.FindResource("Chrome"), BorderBrush = (Brush)Application.Current.FindResource("Hair"), BorderThickness = new Thickness(0, 0, 0, 1) };
        var barGrid = new Grid { Margin = new Thickness(10, 0, 0, 0) };
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition());
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lock_ = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        lock_.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        _host.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        _host.FontFamily = (FontFamily)Application.Current.FindResource("MonoFont");

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_fav);
        left.Children.Add(_title);
        Grid.SetColumn(left, 0);

        var hostWrap = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        hostWrap.Children.Add(lock_);
        hostWrap.Children.Add(_host);
        Grid.SetColumn(hostWrap, 1);

        var expand = CaptionButton("", "Open as tab", () =>
        {
            var url = _url;
            _closing = true;
            Close();
            if (url.StartsWith("http")) _tabs.NewTab(url);
        });
        Grid.SetColumn(expand, 3);

        var close = CaptionButton("", "Close", Close, danger: true);
        Grid.SetColumn(close, 4);

        barGrid.Children.Add(left);
        barGrid.Children.Add(hostWrap);
        barGrid.Children.Add(expand);
        barGrid.Children.Add(close);
        bar.Child = barGrid;
        Grid.SetRow(bar, 0);

        Grid.SetRow(_view, 1);
        root.Children.Add(bar);
        root.Children.Add(_view);
        return root;
    }

    private Button CaptionButton(string glyph, string tip, Action action, bool danger = false)
    {
        var b = new Button
        {
            Content = glyph, ToolTip = tip, Width = 40, Height = 34, Focusable = false,
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 10,
            Style = (Style)Application.Current.FindResource(danger ? "CloseCaptionButton" : "CaptionButton")
        };
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        b.Click += (_, _) => action();
        return b;
    }

    /// <summary>Creates the engine in the shared environment and returns it — the caller assigns e.NewWindow.</summary>
    public async Task<CoreWebView2?> InitAsync()
    {
        try
        {
            var env = await WebEngine.GetAsync(_tabs.Settings);
            await _view.EnsureCoreWebView2Async(env);
        }
        catch
        {
            return null;
        }
        var core = _view.CoreWebView2;
        if (core == null) return null;

        WebEngine.ApplySettings(core, _tabs.Settings);
        ShieldService.Attach(core, _tabs.Settings);
        if (ShieldService.Enabled(_tabs.Settings))
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(ShieldService.CosmeticCss);
        if (_tabs.Settings.VideoPopoutButton)
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(TranslationService.HoverPipScript);
        core.WebResourceRequested += (_, e) =>
        {
            if (ShieldService.IsBlocked(e.Request.Uri, UrlHelper.DisplayHost(_url)))
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        };

        core.DocumentTitleChanged += (_, _) => { _title.Text = core.DocumentTitle; Title = core.DocumentTitle + " — Strata"; };
        core.SourceChanged += (_, _) => UpdateUrl(core.Source);
        core.NavigationCompleted += (_, _) => { _title.Text = string.IsNullOrEmpty(core.DocumentTitle) ? UrlHelper.DisplayHost(_url) : core.DocumentTitle; };
        core.FaviconChanged += async (_, _) =>
        {
            var img = await FaviconService.FetchAsync(core, UrlHelper.DisplayHost(_url));
            _fav.Content = img != null ? new Image { Source = img, Width = 16, Height = 16 } : NewTabPage.Favicon(UrlHelper.DisplayHost(_url), 16);
        };
        core.WindowCloseRequested += (_, _) => Dispatcher.BeginInvoke(() => { _closing = true; Close(); });
        core.DownloadStarting += (_, e) => _tabs.Downloads.Handle(e, null);

        // Nested windows from a popup: sized ones become a popup again, otherwise a tab in the main window.
        core.NewWindowRequested += (_, e) => _tabs.HandleNewWindow(e, fromPopup: true);

        _title.Text = "Loading…";
        return core;
    }

    private void UpdateUrl(string url)
    {
        _url = url;
        _host.Text = UrlHelper.DisplayHost(url);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_closing) { try { _view.CoreWebView2?.Stop(); } catch { } }
        try { _view.Dispose(); } catch { }
    }
}
