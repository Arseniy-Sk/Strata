using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Browser
{
    public partial class App : Application
    {
        private static IServiceProvider? _serviceProvider;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            try
            {
                var services = new ServiceCollection();

                // Load settings first
                var settings = BrowserSettings.Load();
                services.AddSingleton(settings);

                // Add other services
                services.AddSingleton<NavigationService>();
                services.AddSingleton<MainViewModel>();
                services.AddTransient<MainWindow>(); // Transient for multiple windows
                services.AddTransient<SettingsWindow>();
                services.AddTransient<DevToolsWindow>();

                _serviceProvider = services.BuildServiceProvider();

                var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start application: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        public static IServiceProvider? GetServiceProvider()
        {
            return _serviceProvider;
        }
    }
}