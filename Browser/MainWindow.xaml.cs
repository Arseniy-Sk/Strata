using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text;

namespace Browser
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly BrowserSettings _settings;
        private DevToolsWindow _devToolsWindow;

        [DllImport("urlmon.dll", CharSet = CharSet.Ansi)]
        private static extern int UrlMkSetSessionOption(int dwOption, string pBuffer, int dwBufferLength, int dwReserved);
        private const int URLMON_OPTION_USERAGENT = 0x10000001;

        // Импорт для подавления ошибок скриптов
        [DllImport("ole32.dll")]
        private static extern int CoInternetSetFeatureEnabled(
            int featureEntry,
            [MarshalAs(UnmanagedType.U4)] int dwFlags,
            bool fEnable);

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private const int FEATURE_DISABLE_NAVIGATION_SOUNDS = 21;
        private const int FEATURE_DISABLE_SCRIPT_ERROR_UI = 2;
        private const int SET_FEATURE_ON_PROCESS = 0x00000002;
        private const int INTERNET_OPTION_USER_AGENT = 0x00000001;

        public MainWindow(MainViewModel viewModel, BrowserSettings settings)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _settings = settings;
            DataContext = _viewModel;

            ConfigureBrowser();
            _viewModel.NavigationRequested += OnNavigationRequested;
            Loaded += (s, e) => _viewModel.NavigateCommand.Execute(_settings.HomePage);
        }

        // Конструктор для создания новых окон
        public MainWindow() : this(
            App.GetServiceProvider()?.GetService<MainViewModel>() ?? new MainViewModel(new NavigationService(), new BrowserSettings()),
            App.GetServiceProvider()?.GetService<BrowserSettings>() ?? new BrowserSettings())
        {
        }

        private void ConfigureBrowser()
        {
            SetModernUserAgent();
            ConfigureWebBrowserFeatures();
            DisableScriptErrors();
            SetBrowserCompatibilityMode();
        }

        private void SetModernUserAgent()
        {
            try
            {
                // Обновленный современный User-Agent
                string modernUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0";

                UrlMkSetSessionOption(URLMON_OPTION_USERAGENT, modernUserAgent, modernUserAgent.Length, 0);

                // Дополнительный метод установки User-Agent
                SetUserAgentWinInet(modernUserAgent);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"User Agent: {ex.Message}");
            }
        }

        private void SetUserAgentWinInet(string userAgent)
        {
            try
            {
                var userAgentBytes = Encoding.ASCII.GetBytes(userAgent + "\0");
                var pointer = Marshal.AllocHGlobal(userAgentBytes.Length);
                Marshal.Copy(userAgentBytes, 0, pointer, userAgentBytes.Length);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_USER_AGENT, pointer, userAgentBytes.Length);
                Marshal.FreeHGlobal(pointer);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WinInet User Agent: {ex.Message}");
            }
        }

        private void SetBrowserCompatibilityMode()
        {
            try
            {
                // Устанавливаем современный режим рендеринга для WebBrowser control
                var appName = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";

                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                {
                    key?.SetValue(appName, 11001, Microsoft.Win32.RegistryValueKind.DWord);
                }

                // Дополнительные настройки для совместимости
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_GPU_RENDERING"))
                {
                    key?.SetValue(appName, 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Browser compatibility: {ex.Message}");
            }
        }

        private void ConfigureWebBrowserFeatures()
        {
            MainWebBrowser.Navigating += MainWebBrowser_Navigating;
            MainWebBrowser.Navigated += MainWebBrowser_Navigated;
            MainWebBrowser.LoadCompleted += MainWebBrowser_LoadCompleted;
        }

        private void DisableScriptErrors()
        {
            try
            {
                // Метод 1: Глобальное отключение ошибок скриптов через Windows Features
                CoInternetSetFeatureEnabled(FEATURE_DISABLE_SCRIPT_ERROR_UI, SET_FEATURE_ON_PROCESS, true);

                // Метод 2: Отключение звуков навигации
                CoInternetSetFeatureEnabled(FEATURE_DISABLE_NAVIGATION_SOUNDS, SET_FEATURE_ON_PROCESS, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Disable script errors: {ex.Message}");
            }
        }

        private void OnNavigationRequested(string url)
        {
            try
            {
                if (IsSearchQuery(url) && !url.StartsWith("http"))
                {
                    string searchUrl = ConvertToSearchUrl(url);
                    _viewModel.CurrentUrl = searchUrl;
                    MainWebBrowser.Navigate(searchUrl);
                    return;
                }

                string properUrl = EnsureProperUrl(url);
                _viewModel.CurrentUrl = properUrl;

                // Используем Uri чтобы избежать проблем с кодировкой
                var uri = new Uri(properUrl);
                MainWebBrowser.Navigate(uri);
            }
            catch (Exception ex)
            {
                _viewModel.StatusMessage = $"Navigation failed: {ex.Message}";
            }
        }

        private bool IsSearchQuery(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            if (input.StartsWith("http://") || input.StartsWith("https://")) return false;
            if (input.Contains(".") && !input.Contains(" ")) return false;
            return input.Contains(" ") || input.Length > 3;
        }

        private string ConvertToSearchUrl(string query)
        {
            string encodedQuery = Uri.EscapeDataString(query);
            // Используем разные поисковые системы как fallback
            return $"https://www.bing.com/search?q={encodedQuery}";
        }

        private string EnsureProperUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return _settings.HomePage;

            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                return url.Contains(".") && !url.Contains(" ") ? "https://" + url : ConvertToSearchUrl(url);
            }

            return url;
        }

        private void SetBrowserSilent(WebBrowser browser, bool silent)
        {
            try
            {
                // Получаем ActiveXInstance через рефлексию
                var activeX = browser.GetType().InvokeMember("ActiveXInstance",
                    BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.NonPublic,
                    null, browser, null);

                if (activeX != null)
                {
                    var activeXType = activeX.GetType();

                    // Устанавливаем все необходимые свойства для подавления ошибок
                    var silentProperty = activeXType.GetProperty("Silent");
                    var errorsProperty = activeXType.GetProperty("ScriptErrorsSuppressed");
                    var registerProperty = activeXType.GetProperty("RegisterAsBrowser");
                    var webBrowserProperty = activeXType.GetProperty("WebBrowser");

                    silentProperty?.SetValue(activeX, silent, null);
                    errorsProperty?.SetValue(activeX, silent, null);
                    registerProperty?.SetValue(activeX, true, null);

                    // Дополнительная настройка через интерфейс WebBrowser
                    if (webBrowserProperty != null)
                    {
                        var webBrowser = webBrowserProperty.GetValue(activeX, null);
                        if (webBrowser != null)
                        {
                            var webBrowserType = webBrowser.GetType();
                            var putSilentMethod = webBrowserType.GetMethod("put_Silent");
                            putSilentMethod?.Invoke(webBrowser, new object[] { silent ? 1 : 0 });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Silent mode configuration error: {ex.Message}");
            }
        }

        private void MainWebBrowser_Navigating(object sender, NavigatingCancelEventArgs e)
        {
            _viewModel.IsLoading = true;
            _viewModel.StatusMessage = $"Loading {e.Uri}...";

            // Убедимся, что режим silent включен перед каждой навигацией
            SetBrowserSilent(MainWebBrowser, true);
        }

        private void MainWebBrowser_Navigated(object sender, NavigationEventArgs e)
        {
            // Применяем настройки после начала навигации
            SetBrowserSilent(MainWebBrowser, true);
        }

        private void MainWebBrowser_LoadCompleted(object sender, NavigationEventArgs e)
        {
            _viewModel.IsLoading = false;
            _viewModel.StatusMessage = "Done";

            try
            {
                // Повторно применяем silent режим после загрузки
                SetBrowserSilent(MainWebBrowser, true);

                // Пытаемся получить заголовок страницы
                string title = "Simple Browser";

                if (MainWebBrowser.Document is mshtml.HTMLDocument htmlDoc)
                {
                    title = htmlDoc.title ?? "";

                    if (string.IsNullOrEmpty(title))
                    {
                        // Альтернативный способ получения заголовка
                        try
                        {
                            var titleElement = htmlDoc.getElementsByTagName("title");
                            if (titleElement.length > 0)
                            {
                                title = ((mshtml.IHTMLElement)titleElement.item(0)).innerText;
                            }
                        }
                        catch { }
                    }

                    // Update DevTools if open
                    if (_devToolsWindow != null && _devToolsWindow.IsVisible)
                    {
                        _devToolsWindow.CurrentUrl = _viewModel.CurrentUrl;
                        _devToolsWindow.SetPageSource(htmlDoc.documentElement?.outerHTML ?? htmlDoc.body?.outerHTML ?? "");
                    }
                }

                _viewModel.PageTitle = string.IsNullOrEmpty(title) ? "Simple Browser" : $"{title} - Simple Browser";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Load completed: {ex.Message}");
                _viewModel.PageTitle = "Simple Browser";
            }
        }

        // Остальные методы остаются без изменений...
        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            var contextMenu = new ContextMenu
            {
                Background = System.Windows.Media.Brushes.White,
                Foreground = System.Windows.Media.Brushes.Black
            };

            var devToolsItem = new MenuItem { Header = "Developer Tools" };
            devToolsItem.Click += (s, args) => ShowDevTools();

            var viewSourceItem = new MenuItem { Header = "View Page Source" };
            viewSourceItem.Click += (s, args) => ViewPageSource();

            var settingsItem = new MenuItem { Header = "Settings" };
            settingsItem.Click += (s, args) => ShowSettings();

            var newWindowItem = new MenuItem { Header = "New Window" };
            newWindowItem.Click += (s, args) => CreateNewWindow();

            contextMenu.Items.Add(devToolsItem);
            contextMenu.Items.Add(viewSourceItem);
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(settingsItem);
            contextMenu.Items.Add(newWindowItem);

            contextMenu.PlacementTarget = sender as Button;
            contextMenu.IsOpen = true;
        }

        private void ShowDevTools()
        {
            try
            {
                if (_devToolsWindow == null || !_devToolsWindow.IsVisible)
                {
                    _devToolsWindow = new DevToolsWindow();
                    _devToolsWindow.Owner = this;
                    _devToolsWindow.CurrentUrl = _viewModel.CurrentUrl;

                    // Get page source if available
                    if (MainWebBrowser.Document is mshtml.HTMLDocument htmlDoc)
                    {
                        _devToolsWindow.SetPageSource(htmlDoc.documentElement?.outerHTML ?? htmlDoc.body?.outerHTML ?? "");
                    }
                }

                _devToolsWindow.Show();
                _devToolsWindow.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open Developer Tools: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ViewPageSource()
        {
            try
            {
                if (MainWebBrowser.Document is mshtml.HTMLDocument htmlDoc)
                {
                    string source = htmlDoc.documentElement?.outerHTML ?? htmlDoc.body?.outerHTML ?? "No source available";

                    var sourceWindow = new Window
                    {
                        Title = $"Page Source - {_viewModel.CurrentUrl}",
                        Width = 800,
                        Height = 600,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Owner = this,
                        Background = System.Windows.Media.Brushes.White
                    };

                    var textBox = new TextBox
                    {
                        Text = source,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        FontSize = 12,
                        IsReadOnly = true,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        BorderThickness = new Thickness(0)
                    };

                    sourceWindow.Content = textBox;
                    sourceWindow.Show();
                }
                else
                {
                    MessageBox.Show("No document source available", "Info",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to get page source: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowSettings()
        {
            try
            {
                var settingsWindow = new SettingsWindow(_settings);
                settingsWindow.Owner = this;

                if (settingsWindow.ShowDialog() == true)
                {
                    // Применяем новые настройки
                    SetModernUserAgent();
                    MessageBox.Show("Settings saved. Some changes may require restarting the browser.",
                        "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open settings: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CreateNewWindow()
        {
            try
            {
                var serviceProvider = App.GetServiceProvider();
                if (serviceProvider != null)
                {
                    var newWindow = serviceProvider.GetService<MainWindow>();
                    if (newWindow != null)
                    {
                        newWindow.Show();
                    }
                    else
                    {
                        var settings = serviceProvider.GetService<BrowserSettings>() ?? new BrowserSettings();
                        var navigationService = serviceProvider.GetService<NavigationService>() ?? new NavigationService();
                        var viewModel = serviceProvider.GetService<MainViewModel>() ?? new MainViewModel(navigationService, settings);

                        newWindow = new MainWindow(viewModel, settings);
                        newWindow.Show();
                    }
                }
                else
                {
                    var newWindow = new MainWindow();
                    newWindow.Show();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to create new window: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _settings?.Save();
            _devToolsWindow?.Close();
            base.OnClosing(e);
        }
    }
}