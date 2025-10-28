using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace Browser
{
    public class NavigationService
    {
        private readonly Stack<string> _backStack = new();
        private readonly Stack<string> _forwardStack = new();
        private string? _currentUrl;

        public event Action<string>? NavigationStarted;
        public event Action<string, string>? NavigationCompleted;
        public event Action<string, string>? NavigationFailed;

        public bool CanGoBack => _backStack.Count > 0;
        public bool CanGoForward => _forwardStack.Count > 0;

        public NavigationService()
        {
            ConfigureBrowserEmulation();
        }

        public async Task NavigateAsync(string url)
        {
            try
            {
                if (!Url.TryCreate(url, out var validatedUrl))
                {
                    NavigationFailed?.Invoke(url, "Invalid URL format");
                    return;
                }

                NavigationStarted?.Invoke(validatedUrl!.Value);

                if (!string.IsNullOrEmpty(_currentUrl))
                {
                    _backStack.Push(_currentUrl);
                }

                _forwardStack.Clear();
                _currentUrl = validatedUrl.Value;

                // Simulate network delay for better UX
                await Task.Delay(100);

                NavigationCompleted?.Invoke(validatedUrl.Value, $"Loaded: {validatedUrl.Host}");
            }
            catch (Exception ex)
            {
                NavigationFailed?.Invoke(url, ex.Message);
            }
        }

        public void GoBack()
        {
            if (_backStack.Count > 0 && _currentUrl != null)
            {
                _forwardStack.Push(_currentUrl);
                _currentUrl = _backStack.Pop();
                NavigationCompleted?.Invoke(_currentUrl, "Back navigation");
            }
        }

        public void GoForward()
        {
            if (_forwardStack.Count > 0 && _currentUrl != null)
            {
                _backStack.Push(_currentUrl);
                _currentUrl = _forwardStack.Pop();
                NavigationCompleted?.Invoke(_currentUrl, "Forward navigation");
            }
        }

        public void ClearHistory()
        {
            _backStack.Clear();
            _forwardStack.Clear();
        }

        private void ConfigureBrowserEmulation()
        {
            try
            {
                var appName = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";

                using (var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                {
                    key?.SetValue(appName, 11001, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Browser configuration: {ex.Message}");
            }
        }
    }
}