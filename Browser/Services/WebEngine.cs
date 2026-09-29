using Browser.Core;
using Browser.Models;
using Microsoft.Web.WebView2.Core;

namespace Browser.Services;

/// <summary>
/// Одно окружение Chromium на всё приложение: общий browser-процесс, GPU-процесс и сетевой стек.
/// Вкладки добавляют только лёгкие renderer-процессы, и те делятся между вкладками одного сайта.
/// </summary>
public static class WebEngine
{
    private static Task<CoreWebView2Environment>? _environment;

    public static Task<CoreWebView2Environment> GetAsync(BrowserSettings settings)
        => _environment ??= CreateAsync(settings);

    public static CoreWebView2Environment? Current
        => _environment is { IsCompletedSuccessfully: true } task ? task.Result : null;

    private static async Task<CoreWebView2Environment> CreateAsync(BrowserSettings settings)
    {
        var args = new List<string>
        {
            // Фоновые вкладки и так скрыты, но пусть Chromium агрессивнее экономит на них.
            "--enable-features=IntensiveWakeUpThrottling,QuickIntensiveWakeUpThrottlingAfterLoading"
        };
        if (!settings.HardwareAcceleration) args.Add("--disable-gpu");

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = string.Join(' ', args),
            Language = "ru-RU",
            AreBrowserExtensionsEnabled = true,
            EnableTrackingPrevention = true
        };
        return await CoreWebView2Environment.CreateAsync(null, AppPaths.Engine, options);
    }

    public static string RuntimeVersion
    {
        get
        {
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { return ""; }
        }
    }

    public static bool IsRuntimeInstalled => RuntimeVersion.Length > 0;

    public static void ApplyProfile(CoreWebView2 core, BrowserSettings settings, bool dark)
    {
        try
        {
            var profile = core.Profile;
            profile.PreferredColorScheme = settings.Theme switch
            {
                ThemeMode.System => CoreWebView2PreferredColorScheme.Auto,
                _ => dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light
            };
            profile.PreferredTrackingPreventionLevel = ShieldService.TrackingLevel(settings.Shield);
        }
        catch
        {
            // Старый рантайм без этих свойств — просто пропускаем.
        }
    }

    public static void ApplySettings(CoreWebView2 core, BrowserSettings settings)
    {
        var s = core.Settings;
        s.IsStatusBarEnabled = false;
        s.AreDevToolsEnabled = true;
        s.AreDefaultContextMenusEnabled = true;
        s.IsZoomControlEnabled = true;
        // У Strata свои горячие клавиши на все действия (поиск, печать, масштаб, DevTools, обновление…).
        // Встроенные акселераторы Chromium иначе перехватывают часть комбинаций (например, Ctrl K) молча,
        // ещё до того, как они доходят до обработчика окна — поэтому отключаем их и решаем всё сами.
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsPasswordAutosaveEnabled = settings.SavePasswords;
        s.IsGeneralAutofillEnabled = settings.Autofill;
        s.IsSwipeNavigationEnabled = true;
        s.IsPinchZoomEnabled = true;
    }
}
