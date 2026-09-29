using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Browser.Core;
using Browser.Models;
using Browser.Services;
using Browser.Views.Pages;
using Microsoft.Web.WebView2.Wpf;

namespace Browser;

public partial class MainWindow : Window, IWebViewHost
{
    private readonly BrowserSettings _settings;
    private readonly PaletteProvider _palette;
    private readonly Dictionary<InternalPage, FrameworkElement> _pages = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
    private readonly string[] _startupArgs;

    private bool _fullScreen;
    private Rect _restoreBounds;
    private WindowState _restoreState;
    private bool _maxToggleLatched;

    // Floating translator panel
    private readonly DispatcherTimer _translatorDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _translatorDragging;
    private Point _translatorDragStart;
    private double _translatorLeft = double.NaN, _translatorTop = double.NaN;
    private int _translatorRequestId;

    public TabManager Tabs { get; }

    public MainWindow(BrowserSettings settings, string[] args)
    {
        _settings = settings;
        _startupArgs = args;
        InitializeComponent();

        Tabs = new TabManager(this, settings);
        _palette = new PaletteProvider(Tabs, BuildActions);
        DataContext = this;

        Tabs.ActiveTabChanged += OnActiveTabChanged;
        Tabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TabManager.MemoryBytes) or nameof(TabManager.LiveTabs) or nameof(TabManager.ProcessCount))
                UpdateMemoryCard();
            if (e.PropertyName is nameof(TabManager.IsSplit)) UpdateToolbarState();
        };
        Tabs.Toast += ShowToast;
        Tabs.Downloads.Started += item =>
        {
            ShowToast($"Downloading: {item.FileName}");
            UpdateDownloadDot();
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DownloadItem.State)) UpdateDownloadDot(); };
        };

        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastPopup.IsOpen = false; };
        _translatorDebounce.Tick += (_, _) => { _translatorDebounce.Stop(); _ = RunTranslatorAsync(); };
        ThemeManager.Changed += () => { Tabs.ApplyProfileToAny(); ApplyWindowMaterial(); };
        Tabs.Bookmarks.Changed += () => Dispatcher.BeginInvoke(() => { ApplyBookmarksBar(); UpdateToolbarState(); });

        ApplySidebar();
        InitTranslatorLanguages();
        StatusRight.Text = $"STRATA 1.0 · CHROMIUM {WebEngine.RuntimeVersion.Split(' ')[0]}";
        StatusRow.Height = _settings.ShowStatusBar ? new GridLength(26) : new GridLength(0);
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BrowserSettings.ShowStatusBar) && !_fullScreen)
                StatusRow.Height = _settings.ShowStatusBar ? new GridLength(26) : new GridLength(0);
            if (e.PropertyName == nameof(BrowserSettings.SidebarOpen)) ApplySidebar();
            if (e.PropertyName == nameof(BrowserSettings.BookmarksBar)) ApplyBookmarksBar();
            if (e.PropertyName == nameof(BrowserSettings.LiquidGlass)) { ThemeManager.Apply(); ApplyWindowMaterial(); }
            if (e.PropertyName is nameof(BrowserSettings.Theme) or nameof(BrowserSettings.Accent) or nameof(BrowserSettings.Density))
                ThemeManager.Apply();
        };

        RestoreWindowPlacement();
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => OnStateChanged();
        LocationChanged += (_, _) => RepositionPopups();
        SizeChanged += (_, _) => RepositionPopups();
        Deactivated += (_, _) => { SuggestPopup.IsOpen = false; };
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;

        Loaded += (_, _) =>
        {
            Tabs.Restore();
            foreach (var arg in _startupArgs.Where(a => !a.StartsWith('-')))
                Tabs.NewTab(UrlHelper.Resolve(arg, _settings));
            UpdateMemoryCard();
            ApplyBookmarksBar();
            if (Tabs.ActiveTab?.Page == InternalPage.NewTab) FocusAddress();
        };
    }

    // ================================================================== IWebViewHost

    public void AttachView(WebView2 view)
    {
        Grid.SetRow(view, 1);
        Grid.SetColumnSpan(view, 3);
        WebHost.Children.Insert(0, view);
    }

    public void DetachView(WebView2 view) => WebHost.Children.Remove(view);

    public void CloseWindow()
    {
        Close();
    }

    public void RefreshLayout()
    {
        var active = Tabs.ActiveTab;
        bool split = Tabs.IsSplit;

        foreach (var view in WebHost.Children.OfType<WebView2>())
        {
            if (view.Tag is not BrowserTab tab) continue;
            bool show = tab.IsWeb && (split ? tab == Tabs.SplitLeft || tab == Tabs.SplitRight : tab == active);
            if (show)
            {
                Grid.SetColumn(view, split && tab == Tabs.SplitRight ? 2 : 0);
                Grid.SetColumnSpan(view, split ? 1 : 3);
            }
            var visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (view.Visibility != visibility)
            {
                view.Visibility = visibility;
                if (!show) Tabs.OnViewHidden(tab);
            }
        }

        // Split
        SplitHeaderRow.Height = split ? new GridLength(30) : new GridLength(0);
        LeftPane.Width = new GridLength(1, GridUnitType.Star);
        SplitterPane.Width = split ? new GridLength(5) : new GridLength(0);
        RightPane.Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PaneSplitter.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        LeftHeader.Visibility = RightHeader.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        LeftHeader.DataContext = Tabs.SplitLeft;
        RightHeader.DataContext = Tabs.SplitRight;
        LeftHeader.BorderBrush = (Brush)FindResource(split && active == Tabs.SplitLeft ? "Accent" : "Hair");
        RightHeader.BorderBrush = (Brush)FindResource(split && active == Tabs.SplitRight ? "Accent" : "Hair");

        // Internal pages
        if (active is { IsWeb: false } && !split)
        {
            var page = GetPage(active.Page);
            if (PageHost.Content != page) PageHost.Content = page;
            PageHost.Visibility = Visibility.Visible;
            (page as IPage)?.OnShown(active);
        }
        else
        {
            PageHost.Visibility = Visibility.Collapsed;
        }

        CrashPanel.Visibility = active is { IsWeb: true, Crashed: true, View: null } && !split ? Visibility.Visible : Visibility.Collapsed;

        // Rail
        RailHistory.Tag = active?.Page == InternalPage.History ? "active" : null;
        RailDownloads.Tag = active?.Page == InternalPage.Downloads ? "active" : null;
        RailExtensions.Tag = active?.Page == InternalPage.Extensions ? "active" : null;
        RailSettings.Tag = active?.Page == InternalPage.Settings ? "active" : null;
        RailAbout.Tag = active?.Page == InternalPage.About ? "active" : null;

        UpdateToolbarState();
        UpdateAddressText();
        Title = active == null ? "Strata" : $"{active.Title} — Strata";

        if (active?.View is { CoreWebView2: not null } activeView && !AddressBox.IsKeyboardFocusWithin && !PalettePopup.IsOpen)
            activeView.Focus();
    }

    public void SetFullScreen(bool fullScreen)
    {
        if (_fullScreen == fullScreen) return;
        _fullScreen = fullScreen;
        var collapsed = fullScreen ? Visibility.Collapsed : Visibility.Visible;

        TitleRow.Height = fullScreen ? new GridLength(0) : new GridLength(38);
        StatusRow.Height = fullScreen || !_settings.ShowStatusBar ? new GridLength(0) : new GridLength(26);
        ToolbarRow.Height = fullScreen ? new GridLength(0) : new GridLength(44);
        TitleBar.Visibility = Toolbar.Visibility = StatusBar.Visibility = collapsed;
        RailColumn.Width = fullScreen ? new GridLength(0) : new GridLength(54);
        Rail.Visibility = collapsed;
        ApplySidebar();
        PanelColumn.Width = fullScreen || NotesPanel.Visibility != Visibility.Visible ? new GridLength(0) : new GridLength(300);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (fullScreen)
        {
            _restoreState = WindowState;
            _restoreBounds = new Rect(Left, Top, Width, Height);
            WindowState = WindowState.Normal;
            var monitor = Native.MonitorFromWindow(hwnd, 2);
            var info = new Native.MonitorInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>() };
            Native.GetMonitorInfo(monitor, ref info);
            var dpi = VisualTreeHelper.GetDpi(this);
            Root.Margin = new Thickness(0);
            Left = info.rcMonitor.Left / dpi.DpiScaleX;
            Top = info.rcMonitor.Top / dpi.DpiScaleY;
            Width = (info.rcMonitor.Right - info.rcMonitor.Left) / dpi.DpiScaleX;
            Height = (info.rcMonitor.Bottom - info.rcMonitor.Top) / dpi.DpiScaleY;
            ResizeMode = ResizeMode.NoResize;
        }
        else
        {
            ResizeMode = ResizeMode.CanResize;
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
            WindowState = _restoreState;
            OnStateChanged();
        }
    }

    // ================================================================== window

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            // Transparent composition background — otherwise WPF paints black over the DWM material.
            if (source.CompositionTarget != null) source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
            source.AddHook(WndProc);
        }
        Native.HideNativeCaptionButtons(new WindowInteropHelper(this).Handle);
        ApplyWindowMaterial();
    }

    /// <summary>
    /// Rounded corners, a dark frame and "liquid glass": the system Mica material behind the window.
    /// Chrome panels are semi-transparent, so the material shows through them — a frosted-glass effect.
    /// </summary>
    private void ApplyWindowMaterial()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int round = 2; // DWMWCP_ROUND
        Native.DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        int dark = ThemeManager.IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));

        bool glass = _settings.LiquidGlass;
        // The material fills the whole client area only when the DWM frame is extended.
        Native.ExtendFrame(hwnd, glass);
        // DWMWA_SYSTEMBACKDROP_TYPE=38: 3 = Acrylic (liquid glass, heavy blur), 1 = None.
        int backdrop = glass ? 3 : 1;
        Native.DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));

        // For the material to show, the window and root grid must be transparent in the chrome areas.
        Background = System.Windows.Media.Brushes.Transparent;
        Root.SetResourceReference(BackgroundProperty, glass ? "GlassBase" : "Bg");
    }

    private void OnStateChanged()
    {
        if (_fullScreen) return;
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        if (WindowState == WindowState.Maximized)
        {
            // A maximized window with WindowChrome overshoots the screen by the frame width — compensate.
            var dpi = VisualTreeHelper.GetDpi(this);
            double frame = (Native.GetSystemMetrics(32) + Native.GetSystemMetrics(92)) / dpi.DpiScaleX;
            Root.Margin = new Thickness(frame);
        }
        else
        {
            Root.Margin = new Thickness(0);
        }
    }

    /// <summary>Hand the system the maximize-button hit zone — that's how Windows 11 Snap Layouts work.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x84, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2, WM_NCMOUSELEAVE = 0x2A2, HTMAXBUTTON = 9;
        switch (msg)
        {
            case WM_NCHITTEST when !_fullScreen:
                if (IsOverMaxButton(lParam))
                {
                    MaxButton.Tag = "hover";
                    handled = true;
                    return HTMAXBUTTON;
                }
                MaxButton.Tag = null;
                break;
            case WM_NCMOUSELEAVE:
                MaxButton.Tag = null;
                break;
            case WM_NCLBUTTONDOWN when wParam.ToInt32() == HTMAXBUTTON:
                handled = true;
                break;
            case WM_NCLBUTTONUP when wParam.ToInt32() == HTMAXBUTTON:
                handled = true;
                ToggleMaximize();
                break;
        }
        return IntPtr.Zero;
    }

    private bool IsOverMaxButton(IntPtr lParam)
    {
        if (!MaxButton.IsVisible) return false;
        int x = (short)(lParam.ToInt64() & 0xFFFF), y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        try
        {
            var point = MaxButton.PointFromScreen(new Point(x, y));
            return point.X >= 0 && point.Y >= 0 && point.X < MaxButton.ActualWidth && point.Y < MaxButton.ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    private void RestoreWindowPlacement()
    {
        var s = _settings;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var saved = new Rect(double.IsNaN(s.WindowLeft) ? 0 : s.WindowLeft, double.IsNaN(s.WindowTop) ? 0 : s.WindowTop,
            Math.Max(MinWidth, s.WindowWidth), Math.Max(MinHeight, s.WindowHeight));

        Width = saved.Width;
        Height = saved.Height;
        // Only take the position if the window's title bar is at least partially visible.
        if (!double.IsNaN(s.WindowLeft) && screen.IntersectsWith(new Rect(saved.Left, saved.Top, saved.Width, 38)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.Left;
            Top = saved.Top;
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        PalettePopup.IsOpen = SuggestPopup.IsOpen = ToastPopup.IsOpen = false;
        if (_fullScreen) SetFullScreen(false);
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
        }
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (_settings.ClearOnExit)
        {
            try { Tabs.ClearBrowsingDataAsync().Wait(1500); } catch { }
        }
        Tabs.Shutdown();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Single toggle point for maximize/restore. One physical click on the max button can reach
    /// here twice — once from the WPF Click event and once from the system HTMAXBUTTON hook
    /// (needed for Snap Layouts) — and the two may arrive in separate input operations. The latch
    /// is cleared at Background priority, which runs only after every Input-priority operation has
    /// drained, so both calls from one click are guaranteed to collapse into a single toggle.
    /// </summary>
    private void ToggleMaximize()
    {
        if (_maxToggleLatched) return;
        _maxToggleLatched = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _maxToggleLatched = false));
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void ApplySidebar()
    {
        bool open = _settings.SidebarOpen && !_fullScreen;
        SidebarColumn.Width = open ? new GridLength(Math.Clamp(_settings.SidebarWidth, 200, 480)) : new GridLength(0);
        Sidebar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = Sidebar.Visibility;
        SidebarToggle.Foreground = (Brush)FindResource(open ? "Dim" : "AccentText");
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.SidebarOpen = !_settings.SidebarOpen;
        ApplySidebar();
        _settings.Save();
    }

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _settings.SidebarWidth = SidebarColumn.ActualWidth;
        if (SidebarColumn.ActualWidth < 120) _settings.SidebarOpen = false;
        ApplySidebar();
        _settings.Save();
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = ThemeManager.IsDark ? ThemeMode.Light : ThemeMode.Dark;
        _settings.Save();
    }

    private void PanelToggle_Click(object sender, RoutedEventArgs e)
    {
        bool open = NotesPanel.Visibility != Visibility.Visible;
        NotesPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PanelColumn.Width = open ? new GridLength(300) : new GridLength(0);
        PanelToggle.Foreground = (Brush)FindResource(open ? "AccentText" : "Dim");
        if (open) NoteInput.Focus();
    }

    // ================================================================== state

    private void OnActiveTabChanged()
    {
        if (_watchedTab != null) _watchedTab.PropertyChanged -= OnActiveTabPropertyChanged;
        _watchedTab = Tabs.ActiveTab;
        if (_watchedTab != null) _watchedTab.PropertyChanged += OnActiveTabPropertyChanged;
        UpdateToolbarState();
        UpdateAddressText();
    }

    private BrowserTab? _watchedTab;

    private void OnActiveTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BrowserTab.Url):
                UpdateAddressText();
                UpdateToolbarState();
                break;
            case nameof(BrowserTab.Title):
                Title = $"{Tabs.ActiveTab?.Title} — Strata";
                break;
            case nameof(BrowserTab.IsLoading):
            case nameof(BrowserTab.IsReader):
            case nameof(BrowserTab.IsPinned):
                UpdateToolbarState();
                break;
        }
    }

    private void UpdateToolbarState()
    {
        var tab = Tabs.ActiveTab;
        bool web = tab?.IsWeb == true;
        ReloadButton.Content = tab?.IsLoading == true ? "" : "";
        ReloadButton.ToolTip = tab?.IsLoading == true ? "Stop (Esc)" : "Reload (F5)";
        ReloadButton.IsEnabled = web;
        ReaderButton.IsEnabled = web;
        ReaderButton.Tag = tab?.IsReader == true ? "active" : null;
        SplitButton.Tag = Tabs.IsSplit ? "active" : null;
        TranslateButton.IsEnabled = web;
        TranslateButton.Foreground = (Brush)FindResource(tab?.IsTranslated == true ? "AccentText" : "Dim");
        BookmarkButton.IsEnabled = web;
        bool marked = Tabs.IsActiveBookmarked;
        BookmarkButton.Content = marked ? "" : "";
        BookmarkButton.Foreground = (Brush)FindResource(marked ? "AccentText" : "Dim");
        BookmarkButton.ToolTip = marked ? "Remove bookmark (Ctrl D)" : "Bookmark (Ctrl D)";
        ShieldChip.Visibility = web ? Visibility.Visible : Visibility.Collapsed;
        ShieldChip.Opacity = _settings.Shield == ShieldLevel.Off ? 0.45 : 1;
    }

    private void UpdateAddressText()
    {
        if (AddressBox.IsKeyboardFocusWithin) return;
        var tab = Tabs.ActiveTab;
        _suppressSuggest = true;
        AddressBox.Text = tab == null || tab.Page == InternalPage.NewTab ? "" : UrlHelper.Pretty(tab.Url);
        _suppressSuggest = false;
    }

    private void UpdateMemoryCard()
    {
        MemoryCardText.Text = $"{Format.Bytes(Tabs.MemoryBytes)} · {Tabs.ProcessCount} proc.";
        MemoryCardSub.Text = $"{Tabs.LiveTabs} loaded · {Tabs.AsleepTabs} asleep · limit {(_settings.MemoryBudgetMb > 0 ? Format.Bytes(_settings.MemoryBudgetMb * 1048576L) : "none")}";
    }

    private void UpdateDownloadDot()
        => DownloadDot.Visibility = Tabs.Downloads.ActiveCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Relaunching Strata: bring the window forward and open the passed links.</summary>
    public void ActivateFromSecondInstance(string[] urls)
    {
        if (WindowState == WindowState.Minimized) WindowState = _restoreState == WindowState.Minimized ? WindowState.Normal : _restoreState;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        foreach (var url in urls) Tabs.NewTab(UrlHelper.Resolve(url, _settings));
        if (urls.Length == 0 && Tabs.ActiveTab?.Page == InternalPage.NewTab) FocusAddress();
    }

    public void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastPopup.IsOpen = false;
        RepositionPopups();
        ToastPopup.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void RepositionPopups()
    {
        // The palette backdrop size is kept via an XAML binding to Root (see PaletteBackdrop) —
        // that's more reliable than a one-off assignment here, which could land on a layout not yet ready.
        if (PalettePopup.IsOpen)
        {
            PalettePopup.HorizontalOffset += 0.1;
            PalettePopup.HorizontalOffset -= 0.1;
        }
        if (ToastPopup.Child is FrameworkElement toast)
        {
            toast.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            ToastPopup.HorizontalOffset = (Root.ActualWidth - toast.DesiredSize.Width) / 2 + 60;
            ToastPopup.VerticalOffset = Root.ActualHeight - toast.DesiredSize.Height - 30;
        }
        if (SuggestPopup.IsOpen)
        {
            SuggestPopup.HorizontalOffset += 0.1;
            SuggestPopup.HorizontalOffset -= 0.1;
        }
        if (TranslatorPopup.IsOpen) PlaceTranslatorPopup();
    }

    /// <summary>Floating panel — bottom-right by default; after being dragged it keeps its spot, clamped to the window size.</summary>
    private void PlaceTranslatorPopup()
    {
        double width = TranslatorCard.ActualWidth > 0 ? TranslatorCard.ActualWidth : 360;
        double height = TranslatorCard.ActualHeight > 0 ? TranslatorCard.ActualHeight : 360;
        double left = double.IsNaN(_translatorLeft) ? Root.ActualWidth - width - 20 : _translatorLeft;
        double top = double.IsNaN(_translatorTop) ? Root.ActualHeight - height - 60 : _translatorTop;
        left = Math.Clamp(left, 8, Math.Max(8, Root.ActualWidth - width - 8));
        top = Math.Clamp(top, 8, Math.Max(8, Root.ActualHeight - height - 8));
        TranslatorPopup.HorizontalOffset = left;
        TranslatorPopup.VerticalOffset = top;
    }

    // ================================================================== translator (floating panel)

    private void InitTranslatorLanguages()
    {
        var fromItem = new ComboBoxItem { Content = "Detect language", Tag = "auto" };
        TranslatorFrom.Items.Add(fromItem);
        foreach (var (code, name) in TranslationService.Languages)
            TranslatorFrom.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        TranslatorFrom.SelectedIndex = 0;

        foreach (var (code, name) in TranslationService.Languages)
            TranslatorTo.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        SelectLanguage(TranslatorTo, _settings.UiLanguage);
        if (TranslatorTo.SelectedItem == null) TranslatorTo.SelectedIndex = 0;
    }

    private static void SelectLanguage(ComboBox combo, string code)
    {
        foreach (var obj in combo.Items)
            if (obj is ComboBoxItem { } item && (string)item.Tag == code) { combo.SelectedItem = item; return; }
    }

    private void TranslatorTool_Click(object sender, RoutedEventArgs e) => ToggleTranslatorPanel();

    public void ToggleTranslatorPanel()
    {
        if (TranslatorPopup.IsOpen) CloseTranslatorPanel();
        else OpenTranslatorPanel();
    }

    public void OpenTranslatorPanel(string? seedText = null)
    {
        if (_fullScreen) return;
        PlaceTranslatorPopup();
        TranslatorPopup.IsOpen = true;
        PlaceTranslatorPopup(); // again — after the first open the card's ActualWidth/Height are known
        TranslatorToolButton.Foreground = (Brush)FindResource("AccentText");
        if (!string.IsNullOrEmpty(seedText))
        {
            TranslatorInput.Text = seedText;
            _ = RunTranslatorAsync();
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            TakeWin32FocusFor(TranslatorInput);
            TranslatorInput.Focus();
            Keyboard.Focus(TranslatorInput);
            TranslatorInput.CaretIndex = TranslatorInput.Text.Length;
        });
    }

    private void CloseTranslatorPanel()
    {
        TranslatorPopup.IsOpen = false;
        TranslatorToolButton.Foreground = (Brush)FindResource("Dim");
        FocusContent();
    }

    private void TranslatorClose_Click(object sender, RoutedEventArgs e) => CloseTranslatorPanel();

    private void TranslatorCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void TranslatorHeader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _translatorDragging = true;
        _translatorDragStart = e.GetPosition(Root);
        (sender as UIElement)?.CaptureMouse();
        e.Handled = true;
    }

    private void TranslatorHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_translatorDragging || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(Root);
        var delta = pos - _translatorDragStart;
        _translatorDragStart = pos;
        _translatorLeft = (double.IsNaN(_translatorLeft) ? TranslatorPopup.HorizontalOffset : _translatorLeft) + delta.X;
        _translatorTop = (double.IsNaN(_translatorTop) ? TranslatorPopup.VerticalOffset : _translatorTop) + delta.Y;
        PlaceTranslatorPopup();
    }

    private void TranslatorHeader_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _translatorDragging = false;
        (sender as UIElement)?.ReleaseMouseCapture();
    }

    private void TranslatorInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        _translatorDebounce.Stop();
        _translatorDebounce.Start();
    }

    /// <summary>The panel lives in a separate (non-activating) popup HWND — clicking the field
    /// alone doesn't always take Win32 keyboard focus away from the page engine, so we take it explicitly.</summary>
    private void TranslatorInput_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TranslatorInput.IsKeyboardFocusWithin) return;
        e.Handled = true;
        TakeWin32FocusFor(TranslatorInput);
        TranslatorInput.Focus();
        Keyboard.Focus(TranslatorInput);
    }

    private void TranslatorLang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _translatorDebounce.Stop();
        _ = RunTranslatorAsync();
    }

    private async Task RunTranslatorAsync()
    {
        var text = TranslatorInput.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            TranslatorOutput.Text = "";
            TranslatorStatus.Text = "";
            return;
        }

        var from = TranslatorFrom.SelectedItem is ComboBoxItem { } fi ? (string)fi.Tag : "auto";
        var to = TranslatorTo.SelectedItem is ComboBoxItem { } ti ? (string)ti.Tag : "ru";
        int requestId = ++_translatorRequestId;
        TranslatorStatus.Text = "Translating…";
        try
        {
            var result = await TranslationService.TranslateAsync(text, to, from);
            if (requestId != _translatorRequestId) return; // a newer response already arrived
            TranslatorOutput.Text = result;
            TranslatorStatus.Text = $"{text.Length} chars";
        }
        catch
        {
            if (requestId == _translatorRequestId) TranslatorStatus.Text = "No connection";
        }
    }

    private void TranslatorSwap_Click(object sender, RoutedEventArgs e)
    {
        if (TranslatorFrom.SelectedItem is not ComboBoxItem fromItem || TranslatorTo.SelectedItem is not ComboBoxItem toItem) return;
        var fromCode = (string)fromItem.Tag;
        if (fromCode == "auto") return; // nothing to swap with "detect language"

        var toCode = (string)toItem.Tag;
        SelectLanguage(TranslatorFrom, toCode);
        SelectLanguage(TranslatorTo, fromCode);
        (TranslatorInput.Text, TranslatorOutput.Text) = (TranslatorOutput.Text, TranslatorInput.Text);
    }

    private void TranslatorCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TranslatorOutput.Text)) return;
        Clipboard.SetText(TranslatorOutput.Text);
        ShowToast("Translation copied");
    }

    private FrameworkElement GetPage(InternalPage page)
    {
        if (_pages.TryGetValue(page, out var existing)) return existing;
        FrameworkElement created = page switch
        {
            InternalPage.Settings => new SettingsPage(Tabs),
            InternalPage.History => new HistoryPage(Tabs),
            InternalPage.Downloads => new DownloadsPage(Tabs),
            InternalPage.Extensions => new ExtensionsPage(Tabs),
            InternalPage.About => new AboutPage(Tabs),
            _ => new NewTabPage(Tabs, OpenPalette)
        };
        _pages[page] = created;
        return created;
    }

    // ================================================================== keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0 || Native.IsCtrlDown;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 || Native.IsShiftDown;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0 || Native.IsAltDown;

        bool Handle(Action action) { action(); e.Handled = true; return true; }

        if (ctrl && !alt)
        {
            switch (key)
            {
                case Key.T when shift: Handle(Tabs.ReopenClosed); return;
                case Key.T: Handle(() => { Tabs.NewTab(); FocusAddress(); }); return;
                case Key.W or Key.F4: Handle(() => { if (Tabs.ActiveTab != null) Tabs.Close(Tabs.ActiveTab); }); return;
                case Key.Tab: Handle(() => Tabs.SelectRelative(shift ? -1 : 1)); return;
                case Key.PageDown: Handle(() => Tabs.SelectRelative(1)); return;
                case Key.PageUp: Handle(() => Tabs.SelectRelative(-1)); return;
                case Key.L: Handle(FocusAddress); return;
                case Key.K or Key.E: Handle(() => OpenPalette()); return;
                case Key.B: Handle(() => SidebarToggle_Click(this, new RoutedEventArgs())); return;
                case Key.Oem5: Handle(Tabs.ToggleSplit); return; // «\»
                case Key.D: Handle(() => Bookmark_Click(this, new RoutedEventArgs())); return;
                case Key.P when shift: Handle(() => { if (Tabs.ActiveTab != null) Tabs.TogglePin(Tabs.ActiveTab); UpdateToolbarState(); }); return;
                case Key.H: Handle(() => Tabs.OpenPage(InternalPage.History)); return;
                case Key.J: Handle(() => Tabs.OpenPage(InternalPage.Downloads)); return;
                case Key.OemComma: Handle(() => Tabs.OpenPage(InternalPage.Settings)); return;
                case Key.S when shift: Handle(Tabs.SleepAllBackground); return;
                case Key.I when shift: Handle(Tabs.OpenDevTools); return;
                case Key.R when !shift: Handle(Tabs.Reload); return;
                case Key.F when !shift && Tabs.ActiveTab?.IsWeb == true: Handle(StartFind); return;
                case Key.N when shift: Handle(() => Tabs.NewSpace()); return;
                case >= Key.D1 and <= Key.D8: Handle(() => Tabs.SelectIndex(key - Key.D1)); return;
                case Key.D9: Handle(() => Tabs.SelectIndex(-1)); return;
            }
        }

        if (ctrl && alt)
        {
            switch (key)
            {
                case Key.R: Handle(Tabs.ToggleReader); return;
                case Key.T: Handle(Tabs.TranslatePage); return;
                case Key.P: Handle(Tabs.TogglePictureInPicture); return;
                case Key.Y: Handle(ToggleTranslatorPanel); return;
                case Key.Right: Handle(() => Tabs.CycleSpace(1)); return;
                case Key.Left: Handle(() => Tabs.CycleSpace(-1)); return;
            }
        }

        if (alt && !ctrl)
        {
            switch (key)
            {
                case Key.Left: Handle(Tabs.GoBack); return;
                case Key.Right: Handle(Tabs.GoForward); return;
                case Key.D: Handle(FocusAddress); return;
                case Key.Home: Handle(() => Tabs.NavigateActive(UrlHelper.NewTabUrl)); return;
            }
        }

        switch (key)
        {
            case Key.F5: Handle(Tabs.Reload); return;
            case Key.F6: Handle(FocusAddress); return;
            case Key.F11: Handle(() => SetFullScreen(!_fullScreen)); return;
            case Key.F12: Handle(Tabs.OpenDevTools); return;
            case Key.BrowserBack: Handle(Tabs.GoBack); return;
            case Key.BrowserForward: Handle(Tabs.GoForward); return;
            case Key.Escape when _fullScreen && !(Tabs.ActiveTab?.View?.CoreWebView2?.ContainsFullScreenElement ?? false):
                Handle(() => SetFullScreen(false)); return;
            case Key.Escape when TranslatorPopup.IsOpen && !AddressBox.IsKeyboardFocusWithin:
                Handle(CloseTranslatorPanel); return;
            case Key.Escape when Tabs.ActiveTab?.IsLoading == true && !AddressBox.IsKeyboardFocusWithin:
                Handle(Tabs.Stop); return;
        }
    }

    /// <summary>
    /// If the page currently has keyboard focus, Win32 focus lives in the engine's HWND (a different
    /// process), and WPF elements won't receive input until focus returns to the window.
    /// </summary>
    private void TakeWin32Focus()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) Native.SetFocus(hwnd);
    }

    /// <summary>
    /// Elements inside a Popup (AllowsTransparency=True) live in THEIR OWN separate HWND, not in MainWindow —
    /// the regular TakeWin32Focus() gives focus to the main window, not this HWND, so keyboard input
    /// never reaches a field inside the popup. We take focus on the HWND of this specific visual instead.
    /// </summary>
    private static void TakeWin32FocusFor(Visual visual)
    {
        if (PresentationSource.FromVisual(visual) is HwndSource source && source.Handle != IntPtr.Zero)
            Native.SetFocus(source.Handle);
    }

    private void FocusAddress()
    {
        if (_fullScreen) return;
        TakeWin32Focus();
        AddressBox.Focus();
        Keyboard.Focus(AddressBox);
        AddressBox.SelectAll();
    }

    // ================================================================== address bar

    private bool _suppressSuggest;

    private void AddressBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Omnibox.BorderBrush = (Brush)FindResource("Accent");
        var tab = Tabs.ActiveTab;
        _suppressSuggest = true;
        if (tab is { IsWeb: true }) AddressBox.Text = tab.Url;
        _suppressSuggest = false;
        AddressBox.SelectAll();
    }

    private void AddressBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (AddressBox.IsKeyboardFocusWithin) return;
        e.Handled = true;
        AddressBox.Focus();
    }

    private void AddressBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Omnibox.BorderBrush = (Brush)FindResource("Hair");
        if (!SuggestList.IsKeyboardFocusWithin) SuggestPopup.IsOpen = false;
        UpdateAddressText();
    }

    private CancellationTokenSource? _suggestCts;

    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSuggest || !AddressBox.IsKeyboardFocusWithin) return;
        var text = AddressBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            SuggestPopup.IsOpen = false;
            return;
        }
        var items = _palette.Query(text, compact: true, NavigateFromInput);
        SuggestList.ItemsSource = items;
        SuggestList.SelectedIndex = items.Count > 0 ? 0 : -1;
        SuggestCard.Width = Omnibox.ActualWidth + 14;
        SuggestPopup.IsOpen = items.Count > 0;
        FetchSuggestions(text, items, SuggestList, () => SuggestPopup.IsOpen = true);
    }

    /// <summary>Fetches live search-engine suggestions and appends them to the local list.</summary>
    private async void FetchSuggestions(string text, List<PaletteItem> baseItems, System.Windows.Controls.ListBox list, Action open)
    {
        if (!_settings.SearchSuggestions || text.StartsWith("http") || UrlHelper.PageOf(UrlHelper.Resolve(text, _settings)) != InternalPage.None) return;
        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        List<string> suggestions;
        try { suggestions = await SuggestService.QueryAsync(text, cts.Token); }
        catch { return; }
        if (cts.IsCancellationRequested || list.ItemsSource != baseItems) return;

        var existing = new HashSet<string>(baseItems.Select(i => i.Title), StringComparer.OrdinalIgnoreCase);
        var merged = new List<PaletteItem>(baseItems);
        bool withHeaders = baseItems.Any(i => i.IsHeader);
        bool headerAdded = false;
        foreach (var s in suggestions.Take(7))
        {
            if (!existing.Add(s)) continue;
            if (withHeaders && !headerAdded) { merged.Add(new PaletteItem { IsHeader = true, Title = "SUGGESTIONS" }); headerAdded = true; }
            var query = s;
            merged.Add(new PaletteItem
            {
                Title = s, Sub = _settings.SearchEngineName, Hint = "search", Letter = "", IsIcon = true,
                HueBrush = Core.ColorUtil.HueBrush(196), HueForeground = Core.ColorUtil.HueForeground(196),
                Run = () => NavigateFromInput(query)
            });
        }
        if (merged.Count == baseItems.Count) return;
        int sel = list.SelectedIndex;
        list.ItemsSource = merged;
        list.SelectedIndex = sel >= 0 ? sel : 0;
        open();
    }

    private void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (SuggestPopup.IsOpen && SuggestList.SelectedItem is PaletteItem { Run: not null } item && SuggestList.SelectedIndex > 0)
                {
                    SuggestPopup.IsOpen = false;
                    item.Run();
                }
                else
                {
                    NavigateFromInput(AddressBox.Text);
                }
                break;
            case Key.Escape:
                e.Handled = true;
                if (SuggestPopup.IsOpen) SuggestPopup.IsOpen = false;
                else
                {
                    Keyboard.ClearFocus();
                    FocusContent();
                }
                break;
            case Key.Down when SuggestPopup.IsOpen:
                e.Handled = true;
                MoveSelection(SuggestList, 1);
                break;
            case Key.Up when SuggestPopup.IsOpen:
                e.Handled = true;
                MoveSelection(SuggestList, -1);
                break;
        }
    }

    private void SuggestList_Click(object sender, MouseButtonEventArgs e)
    {
        if (ItemFromEvent<PaletteItem>(e) is { Run: not null } item)
        {
            SuggestPopup.IsOpen = false;
            item.Run();
        }
    }

    private void NavigateFromInput(string input)
    {
        SuggestPopup.IsOpen = false;
        PalettePopup.IsOpen = false;
        if (string.IsNullOrWhiteSpace(input)) return;
        Tabs.NavigateActive(input);
        FocusContent();
    }

    private void FocusContent()
    {
        Keyboard.ClearFocus();
        UpdateAddressText();
        if (Tabs.ActiveTab?.View is { } view) view.Focus();
    }

    private static void MoveSelection(ListBox list, int direction)
    {
        if (list.Items.Count == 0) return;
        int index = list.SelectedIndex;
        for (int i = 0; i < list.Items.Count; i++)
        {
            index = (index + direction + list.Items.Count) % list.Items.Count;
            if (list.Items[index] is PaletteItem { IsHeader: false }) break;
        }
        list.SelectedIndex = index;
        list.ScrollIntoView(list.SelectedItem);
    }

    // ================================================================== palette

    private void OpenPalette_Click(object sender, MouseButtonEventArgs e) => OpenPalette();

    public void OpenPalette(string initial = "")
    {
        if (_fullScreen) return;
        SuggestPopup.IsOpen = false;
        RepositionPopups();
        PaletteInput.Text = initial;
        RefreshPalette();
        PalettePopup.IsOpen = true;

        var anim = new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        PaletteScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        PaletteScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            TakeWin32FocusFor(PaletteInput);
            PaletteInput.Focus();
            Keyboard.Focus(PaletteInput);
            PaletteInput.CaretIndex = PaletteInput.Text.Length;
        });
    }

    private void ClosePalette()
    {
        PalettePopup.IsOpen = false;
        FocusContent();
    }

    private void RefreshPalette()
    {
        var items = _palette.Query(PaletteInput.Text, compact: false, NavigateFromInput);
        PaletteList.ItemsSource = items;
        PaletteList.SelectedIndex = items.FindIndex(i => !i.IsHeader);
        FetchSuggestions(PaletteInput.Text, items, PaletteList, () => { });
    }

    private void PaletteInput_TextChanged(object sender, TextChangedEventArgs e) => RefreshPalette();

    private void PaletteInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                ClosePalette();
                break;
            case Key.Down:
                e.Handled = true;
                MoveSelection(PaletteList, 1);
                break;
            case Key.Up:
                e.Handled = true;
                MoveSelection(PaletteList, -1);
                break;
            case Key.Enter:
                e.Handled = true;
                if (PaletteList.SelectedItem is PaletteItem { Run: not null } item)
                {
                    PalettePopup.IsOpen = false;
                    item.Run();
                    if (!AddressBox.IsKeyboardFocusWithin) FocusContent();
                }
                else if (PaletteInput.Text.Length > 0)
                {
                    NavigateFromInput(PaletteInput.Text);
                }
                break;
        }
    }

    private void PaletteList_Click(object sender, MouseButtonEventArgs e)
    {
        if (ItemFromEvent<PaletteItem>(e) is { Run: not null } item)
        {
            PalettePopup.IsOpen = false;
            item.Run();
        }
    }

    private void PaletteBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => ClosePalette();
    private void PaletteCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private IReadOnlyList<(string, string, string, string, int, Action)> BuildActions()
    {
        var tab = Tabs.ActiveTab;
        var list = new List<(string, string, string, string, int, Action)>
        {
            ("New tab", "the space's start page", "Ctrl T", "", 196, () => { Tabs.NewTab(); FocusAddress(); }),
            ("Split screen", "active tab + the next one", "Ctrl \\", "", 196, Tabs.ToggleSplit),
            ("Reader mode", "strip the page down to its content", "Ctrl Alt R", "", 246, Tabs.ToggleReader),
            ("Sleep all background tabs", "free up memory", "Ctrl Shift S", "", 300, Tabs.SleepAllBackground),
            ("Toggle theme", ThemeManager.IsDark ? "switch to light" : "switch to dark", "", "", 62, () => ThemeToggle_Click(this, new RoutedEventArgs())),
            ("New space", "an empty set of tabs", "Ctrl Shift N", "", 150, () => Tabs.NewSpace()),
            ("Reopen closed tab", "bring back the last one you closed", "Ctrl Shift T", "", 220, Tabs.ReopenClosed),
            ("Collapse all branches", "the current space's tab tree", "", "", 220, () => Tabs.SetAllCollapsed(true)),
            ("Sidebar", "show or hide the tab list", "Ctrl B", "", 220, () => SidebarToggle_Click(this, new RoutedEventArgs())),
            ("Notes", "right-hand panel", "", "", 32, () => PanelToggle_Click(this, new RoutedEventArgs())),
            ("Full screen", "hide the whole interface", "F11", "", 220, () => SetFullScreen(!_fullScreen)),
            ("History and sessions", "strata://history", "Ctrl H", "", 220, () => Tabs.OpenPage(InternalPage.History)),
            ("Downloads", "strata://downloads", "Ctrl J", "", 220, () => Tabs.OpenPage(InternalPage.Downloads)),
            ("Settings", "strata://settings", "Ctrl ,", "", 220, () => Tabs.OpenPage(InternalPage.Settings)),
            ("Extensions", "strata://extensions", "", "", 220, () => Tabs.OpenPage(InternalPage.Extensions)),
            ("About Strata", "strata://about", "", "", 220, () => Tabs.OpenPage(InternalPage.About)),
        };
        if (tab is { IsWeb: true })
        {
            list.Add((tab.IsPinned ? "Unpin tab" : "Pin tab", tab.Title, "Ctrl D", "", 196, () => Tabs.TogglePin(tab)));
            list.Add(("Developer tools", tab.Host, "F12", "", 150, Tabs.OpenDevTools));
            list.Add(("Print", tab.Title, "Ctrl P", "", 220, Tabs.Print));
            list.Add(("Copy address", tab.Url, "", "", 220, () => Clipboard.SetText(tab.Url)));
            list.Add(("Sleep this tab", "unload it from memory", "", "", 300, () => _ = Tabs.SleepAsync(tab)));
        }
        foreach (var space in Tabs.Spaces.Where(s => s != Tabs.ActiveSpace))
        {
            var target = space;
            list.Add(($"Space: {space.Name}", space.Stat, "", space.Glyph, space.Hue, () => Tabs.SwitchSpace(target)));
        }
        return list;
    }

    // ================================================================== tab tree

    private Point _dragStart;
    private BrowserTab? _dragTab;

    private static T? ItemFromEvent<T>(RoutedEventArgs e) where T : class
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null)
        {
            if (element is FrameworkElement { DataContext: T item }) return item;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private static Button? ButtonFromEvent(RoutedEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not ListBoxItem)
        {
            if (element is Button button) return button;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private void TabList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragTab = ButtonFromEvent(e) == null ? ItemFromEvent<BrowserTab>(e) : null;
        _dragStart = e.GetPosition(TabList);
    }

    private void TabList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var tab = ItemFromEvent<BrowserTab>(e);
        if (tab == null) return;
        switch (ButtonFromEvent(e)?.Tag as string)
        {
            case "close": Tabs.Close(tab); e.Handled = true; return;
            case "collapse": Tabs.ToggleCollapse(tab); e.Handled = true; return;
            case "audio": Tabs.ToggleMute(tab); e.Handled = true; return;
        }
        if (_dragTab == tab) Tabs.Activate(tab);
        _dragTab = null;
    }

    private void TabList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (ItemFromEvent<BrowserTab>(e) is { } tab)
        {
            Tabs.Close(tab);
            e.Handled = true;
        }
    }

    private void TabList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ButtonFromEvent(e) == null && ItemFromEvent<BrowserTab>(e) is { HasChildren: true } tab) Tabs.ToggleCollapse(tab);
    }

    private void TabList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab == null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(TabList) - _dragStart;
        if (Math.Abs(delta.Y) < 6 && Math.Abs(delta.X) < 6) return;
        var tab = _dragTab;
        _dragTab = null;
        DragDrop.DoDragDrop(TabList, new DataObject(typeof(BrowserTab), tab), DragDropEffects.Move);
        DropLine.Visibility = Visibility.Collapsed;
    }

    private (BrowserTab? Target, bool AsChild, bool Before, FrameworkElement? Row) DropTarget(DragEventArgs e)
    {
        var hit = TabList.InputHitTest(e.GetPosition(TabList)) as DependencyObject;
        while (hit is not null and not ListBoxItem) hit = VisualTreeHelper.GetParent(hit);
        if (hit is not ListBoxItem { DataContext: BrowserTab target } row) return (null, false, false, null);
        double y = e.GetPosition(row).Y / Math.Max(1, row.ActualHeight);
        return (target, y is > 0.3 and < 0.7, y <= 0.3, row);
    }

    private void TabList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(BrowserTab)) is not BrowserTab dragged) { e.Effects = DragDropEffects.None; return; }
        var (target, asChild, before, row) = DropTarget(e);
        if (target == null || row == null || target == dragged)
        {
            DropLine.Visibility = Visibility.Collapsed;
            return;
        }
        var origin = row.TranslatePoint(new Point(0, 0), TabList);
        double indent = (asChild ? target.Depth + 1 : target.Depth) * 14 + 10;
        Canvas.SetLeft(DropLine, origin.X + indent);
        Canvas.SetTop(DropLine, origin.Y + (asChild ? row.ActualHeight - 3 : before ? -1 : row.ActualHeight - 1));
        DropLine.Width = Math.Max(20, row.ActualWidth - indent);
        DropLine.Opacity = asChild ? 0.55 : 1;
        DropLine.Visibility = Visibility.Visible;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void TabList_DragLeave(object sender, DragEventArgs e) => DropLine.Visibility = Visibility.Collapsed;

    private void TabList_Drop(object sender, DragEventArgs e)
    {
        DropLine.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(typeof(BrowserTab)) is not BrowserTab dragged) return;
        var (target, asChild, before, _) = DropTarget(e);
        if (target != null) Tabs.Move(dragged, target, asChild, before);
    }

    private void TabList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemFromEvent<BrowserTab>(e) is { } tab) ShowTabMenu(tab, e);
    }

    private void Tab_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab) ShowTabMenu(tab, e);
    }

    private void ShowTabMenu(BrowserTab tab, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu();
        MenuItem Item(string header, string icon, Action action, string gesture = "", bool enabled = true)
        {
            var item = new MenuItem { Header = header, Icon = icon, InputGestureText = gesture, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        Item("New tab next to this", "", () => { Tabs.NewTab(after: tab.IsPinned ? null : tab); FocusAddress(); });
        if (tab.IsWeb)
        {
            Item("Reload", "", () => { if (tab.View?.CoreWebView2 is { } c) c.Reload(); else Tabs.Activate(tab); });
            Item("Duplicate", "", () => Tabs.Duplicate(tab));
        }
        Item(tab.IsPinned ? "Unpin" : "Pin", tab.IsPinned ? "" : "", () => { Tabs.TogglePin(tab); UpdateToolbarState(); }, "Ctrl D");
        if (tab.IsWeb && tab != Tabs.ActiveTab && Tabs.ActiveTab?.IsWeb == true)
            Item("Split screen with active tab", "", () => Tabs.SplitWith(tab));
        menu.Items.Add(new Separator());
        if (tab.IsWeb)
        {
            Item("Sleep", "", () => _ = Tabs.SleepAsync(tab), "", tab.View != null && tab != Tabs.ActiveTab);
            if (tab.ShowAudio) Item(tab.IsMuted ? "Unmute" : "Mute", tab.IsMuted ? "" : "", () => Tabs.ToggleMute(tab));
            Item("Copy address", "", () => Clipboard.SetText(tab.Url));
        }

        if (!tab.IsPinned && Tabs.Spaces.Count > 1)
        {
            var move = new MenuItem { Header = "Move to space", Icon = "" };
            foreach (var space in Tabs.Spaces.Where(s => s != tab.Space))
            {
                var target = space;
                var sub = new MenuItem { Header = space.Name };
                sub.Click += (_, _) => Tabs.MoveToSpace(tab, target);
                move.Items.Add(sub);
            }
            menu.Items.Add(move);
        }

        menu.Items.Add(new Separator());
        if (tab.HasChildren)
        {
            Item(tab.IsCollapsed ? "Expand branch" : "Collapse branch", "", () => Tabs.ToggleCollapse(tab));
            Item("Close branch", "", () => Tabs.CloseBranch(tab));
        }
        if (!tab.IsPinned) Item("Close others", "", () => Tabs.CloseOthers(tab));
        Item("Close", "", () => Tabs.Close(tab), "Ctrl W");

        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void CollapseAll_Click(object sender, MouseButtonEventArgs e)
    {
        bool anyExpanded = Tabs.ActiveSpace.AllTabs().Any(t => t.HasChildren && !t.IsCollapsed);
        Tabs.SetAllCollapsed(anyExpanded);
        CollapseAllText.Text = anyExpanded ? "EXPAND" : "COLLAPSE";
    }

    private void Pinned_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab) Tabs.Activate(tab);
    }

    private void Pinned_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.DataContext is BrowserTab tab)
        {
            Tabs.Close(tab);
            e.Handled = true;
        }
    }

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        Tabs.NewTab();
        FocusAddress();
    }

    private void SleepAll_Click(object sender, RoutedEventArgs e) => Tabs.SleepAllBackground();

    // ================================================================== spaces

    private void SpaceChip_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Space space) Tabs.SwitchSpace(space);
    }

    private void SpacePill_Click(object sender, MouseButtonEventArgs e) => Tabs.CycleSpace(1);

    private void NewSpace_Click(object sender, RoutedEventArgs e)
    {
        var space = Tabs.NewSpace();
        FocusAddress();
        ShowToast($"Created \u201c{space.Name}\u201d — right-click to rename");
    }

    private void SpaceChip_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Space space && e.Data.GetData(typeof(BrowserTab)) is BrowserTab tab)
        {
            Tabs.MoveToSpace(tab, space);
            ShowToast($"Tab moved to \u201c{space.Name}\u201d");
        }
    }

    private void SpaceChip_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Space space) return;
        e.Handled = true;
        var menu = new ContextMenu();

        var rename = new MenuItem { Header = "Rename", Icon = "" };
        rename.Click += (_, _) => RenameSpace(space);
        menu.Items.Add(rename);

        var colors = new MenuItem { Header = "Color", Icon = "" };
        foreach (var (name, hue) in new[] { ("Teal", 196), ("Blue", 246), ("Purple", 300), ("Green", 150), ("Yellow", 62), ("Orange", 32), ("Red", 12) })
        {
            var item = new MenuItem { Header = name, Icon = new Ellipse10(ColorUtil.HueBrush(hue)) };
            int h = hue;
            item.Click += (_, _) => { space.Hue = h; Tabs.ScheduleSave(); };
            colors.Items.Add(item);
        }
        menu.Items.Add(colors);

        var sleep = new MenuItem { Header = "Sleep all tabs", Icon = "" };
        sleep.Click += (_, _) => { foreach (var t in space.AllTabs().ToList()) _ = Tabs.SleepAsync(t); };
        menu.Items.Add(sleep);

        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "Delete space", Icon = "", IsEnabled = Tabs.Spaces.Count > 1 };
        delete.Click += (_, _) =>
        {
            if (space.TabCount == 0 || MessageBox.Show(this, $"Close \u201c{space.Name}\u201d and all its tabs ({space.TabCount})?", "Strata",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                Tabs.DeleteSpace(space);
        };
        menu.Items.Add(delete);

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Right;
        menu.IsOpen = true;
    }

    private void RenameSpace(Space space)
    {
        var box = new TextBox { Text = space.Name, Style = (Style)FindResource("FieldTextBox"), Width = 220 };
        var popup = new Popup
        {
            Child = new Border
            {
                Background = (Brush)FindResource("MenuBg"), BorderBrush = (Brush)FindResource("Hair2"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11), Padding = new Thickness(8), Child = box
            },
            PlacementTarget = SpaceList, Placement = PlacementMode.Right, AllowsTransparency = true, StaysOpen = false, HorizontalOffset = 8
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if (!string.IsNullOrWhiteSpace(box.Text)) space.Name = box.Text.Trim();
                popup.IsOpen = false;
                Tabs.ScheduleSave();
            }
            else if (e.Key == Key.Escape) popup.IsOpen = false;
        };
        popup.IsOpen = true;
        box.Focus();
        box.SelectAll();
    }

    // ================================================================== toolbar

    private void Back_Click(object sender, RoutedEventArgs e) => Tabs.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => Tabs.GoForward();

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.ActiveTab?.IsLoading == true) Tabs.Stop();
        else Tabs.Reload();
    }

    private void Reader_Click(object sender, RoutedEventArgs e) => Tabs.ToggleReader();
    private void Split_Click(object sender, RoutedEventArgs e) => Tabs.ToggleSplit();
    private void EndSplit_Click(object sender, RoutedEventArgs e) => Tabs.EndSplit();

    private void SplitHeader_Click(object sender, MouseButtonEventArgs e)
    {
        var tab = (sender as FrameworkElement)?.Tag as string == "left" ? Tabs.SplitLeft : Tabs.SplitRight;
        if (tab != null) Tabs.Activate(tab);
    }

    private void PinActive_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.ActiveTab == null) return;
        Tabs.TogglePin(Tabs.ActiveTab);
        UpdateToolbarState();
        ShowToast(Tabs.ActiveTab.IsPinned ? "Tab pinned — visible in every space" : "Tab unpinned");
    }

    // ================================================================== bookmarks and translation

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.ActiveTab is not { IsWeb: true }) return;
        bool added = Tabs.ToggleBookmarkActive();
        UpdateToolbarState();
        ShowToast(added ? "Added to bookmarks" : "Removed from bookmarks");
    }

    private void Translate_Click(object sender, RoutedEventArgs e) => Tabs.TranslatePage();

    private void ApplyBookmarksBar()
    {
        bool show = _settings.BookmarksBar && Tabs.Bookmarks.Items.Count > 0 && !_fullScreen;
        BookmarksBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BookmarksRow.Height = show ? GridLength.Auto : new GridLength(0);
    }

    private void BookmarkChip_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Services.Bookmark bm) return;
        if (Tabs.FindOpenTab(bm.Url) is { } open) Tabs.Activate(open);
        else Tabs.NavigateActive(bm.Url);
    }

    private void BookmarkChip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.DataContext is Services.Bookmark bm)
        {
            Tabs.NewTab(bm.Url, activate: false);
            e.Handled = true;
        }
    }

    private void BookmarkChip_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Services.Bookmark bm) return;
        e.Handled = true;
        var menu = new ContextMenu();
        void Item(string header, string icon, Action action)
        {
            var mi = new MenuItem { Header = header, Icon = icon };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        Item("Open", "", () => Tabs.NavigateActive(bm.Url));
        Item("Open in new tab", "", () => Tabs.NewTab(bm.Url, activate: false));
        Item("Copy address", "", () => Clipboard.SetText(bm.Url));
        menu.Items.Add(new Separator());
        Item("Delete bookmark", "", () => Tabs.Bookmarks.Remove(bm));
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void ShieldChip_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu();
        var header = new MenuItem
        {
            Header = $"Blocked here: {Tabs.ActiveTab?.BlockedCount ?? 0} · total: {Tabs.TotalBlocked}",
            Icon = "", IsEnabled = false
        };
        menu.Items.Add(header);
        menu.Items.Add(new Separator());
        foreach (var (level, name) in new[] { (ShieldLevel.Strict, "Strict"), (ShieldLevel.Balanced, "Balanced"), (ShieldLevel.Basic, "Basic"), (ShieldLevel.Off, "Off") })
        {
            var item = new MenuItem { Header = name, Icon = _settings.Shield == level ? "" : "" };
            item.Click += (_, _) =>
            {
                _settings.Shield = level;
                _settings.Save();
                UpdateToolbarState();
                Tabs.Reload();
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = ShieldChip;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var tab = Tabs.ActiveTab;
        bool web = tab?.IsWeb == true;
        var menu = new ContextMenu();
        void Item(string header, string icon, Action action, string gesture = "", bool enabled = true)
        {
            var item = new MenuItem { Header = header, Icon = icon, InputGestureText = gesture, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Item("New tab", "", () => { Tabs.NewTab(); FocusAddress(); }, "Ctrl T");
        Item("New space", "", () => Tabs.NewSpace(), "Ctrl Shift N");
        Item("Reopen closed tab", "", Tabs.ReopenClosed, "Ctrl Shift T", Tabs.CanReopenClosed);
        menu.Items.Add(new Separator());
        Item("Find on page", "", StartFind, "Ctrl F", web);
        Item("Translate page", "", Tabs.TranslatePage, "Ctrl Alt T", web);
        Item("Translator (panel)", "", ToggleTranslatorPanel, "Ctrl Alt Y");
        Item("Picture-in-picture (video)", "", Tabs.TogglePictureInPicture, "Ctrl Alt P", web);
        Item(Tabs.IsActiveBookmarked ? "Remove bookmark" : "Bookmark", "", () => Bookmark_Click(this, new RoutedEventArgs()), "Ctrl D", web);
        Item("Print", "", Tabs.Print, "Ctrl P", web);
        Item("Zoom in", "", () => Tabs.Zoom(0.1), "Ctrl +", web);
        Item("Zoom out", "", () => Tabs.Zoom(-0.1), "Ctrl −", web);
        Item("Reset zoom", "", () => Tabs.Zoom(0), "Ctrl 0", web);
        Item("Full screen", "", () => SetFullScreen(!_fullScreen), "F11");
        menu.Items.Add(new Separator());
        Item("History and sessions", "", () => Tabs.OpenPage(InternalPage.History), "Ctrl H");
        Item("Downloads", "", () => Tabs.OpenPage(InternalPage.Downloads), "Ctrl J");
        Item("Extensions", "", () => Tabs.OpenPage(InternalPage.Extensions));
        Item("Import from browsers", "", () => Tabs.OpenPage(InternalPage.Settings));
        Item("Developer tools", "", Tabs.OpenDevTools, "F12", web);
        menu.Items.Add(new Separator());
        Item("Settings", "", () => Tabs.OpenPage(InternalPage.Settings), "Ctrl ,");
        Item("About Strata", "", () => Tabs.OpenPage(InternalPage.About));

        menu.PlacementTarget = MenuButton;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = -190;
        menu.IsOpen = true;
    }

    private async void StartFind()
    {
        if (Tabs.ActiveTab?.View?.CoreWebView2 is not { } core) return;
        try
        {
            var options = core.Environment.CreateFindOptions();
            options.FindTerm = "";
            await core.Find.StartAsync(options);
        }
        catch
        {
            ShowToast("Find on page is unavailable in this engine version");
        }
    }

    private void RailHistory_Click(object sender, RoutedEventArgs e) => Tabs.OpenPage(InternalPage.History);
    private void RailDownloads_Click(object sender, RoutedEventArgs e) => Tabs.OpenPage(InternalPage.Downloads);
    private void RailExtensions_Click(object sender, RoutedEventArgs e) => Tabs.OpenPage(InternalPage.Extensions);
    private void RailSettings_Click(object sender, RoutedEventArgs e) => Tabs.OpenPage(InternalPage.Settings);
    private void RailAbout_Click(object sender, RoutedEventArgs e) => Tabs.OpenPage(InternalPage.About);

    // ================================================================== notes

    private void NoteInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Tabs.Notes.Add(NoteInput.Text, Tabs.ActiveTab);
        NoteInput.Clear();
        e.Handled = true;
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Note note) Tabs.Notes.Remove(note);
    }

    private void NoteLink_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url && url.Length > 0)
        {
            if (Tabs.FindOpenTab(url) is { } open) Tabs.Activate(open);
            else Tabs.NewTab(url);
        }
    }
}

/// <summary>Colored dot for color-picker menu items.</summary>
internal sealed class Ellipse10 : System.Windows.Shapes.Shape
{
    public Ellipse10(Brush fill)
    {
        Fill = fill;
        Width = Height = 10;
    }

    protected override Geometry DefiningGeometry => new EllipseGeometry(new Point(5, 5), 5, 5);
}
