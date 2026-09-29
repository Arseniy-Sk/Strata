using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using Browser.Core;
using Browser.Models;
using Browser.Services;

namespace Browser;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // Single instance: a relaunch hands its links to the already-open window and exits.
        if (!SingleInstance.Claim(e.Args))
        {
            Shutdown();
            return;
        }

        // The on-disk JIT profile speeds up subsequent launches.
        ProfileOptimization.SetProfileRoot(AppPaths.Root);
        ProfileOptimization.StartProfile("startup.jitprofile");

        DispatcherUnhandledException += OnUnhandledException;

        var settings = BrowserSettings.Load();
        ThemeManager.Initialize(settings);

        if (!WebEngine.IsRuntimeInstalled)
        {
            var answer = MessageBox.Show(
                "Strata needs the Microsoft Edge WebView2 Runtime to run.\n\nOpen the download page?",
                "Strata", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
                Native.OpenExternal("https://developer.microsoft.com/microsoft-edge/webview2/");
            Shutdown();
            return;
        }

        // The engine starts up in parallel with building the window — the first page opens without waiting.
        _ = WebEngine.GetAsync(settings);

        var window = new MainWindow(settings, e.Args);
        MainWindow = window;
        window.Show();

        SingleInstance.Listen(urls => Dispatcher.BeginInvoke(() => window.ActivateFromSecondInstance(urls)));
        Exit += (_, _) => SingleInstance.Release();
    }

    /// <summary>Run synchronously on the UI thread and return the result (for background import operations).</summary>
    public static T Dispatch<T>(Func<T> func) => Current.Dispatcher.Invoke(func);
    public static void Dispatch(Action action) => Current.Dispatcher.Invoke(action);

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine(e.Exception);
        e.Handled = true;
    }
}
