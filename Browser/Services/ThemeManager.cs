using System.Windows;
using System.Windows.Media;
using Browser.Core;
using Browser.Models;
using Microsoft.Win32;

namespace Browser.Services;

/// <summary>Токены дизайна Strata → DynamicResource-кисти. Смена темы — это замена ~25 замороженных кистей.</summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; } = true;
    public static Color Background { get; private set; }
    public static event Action? Changed;

    private static BrowserSettings? _settings;

    public static void Initialize(BrowserSettings settings)
    {
        _settings = settings;
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _settings?.Theme == ThemeMode.System)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
    }

    public static void Apply()
    {
        if (_settings == null) return;
        var res = Application.Current.Resources;
        IsDark = _settings.Theme switch
        {
            ThemeMode.Light => false,
            ThemeMode.System => !SystemUsesLightTheme(),
            _ => true
        };

        var accent = AccentColor(_settings.Accent);

        if (IsDark)
        {
            Background = Hex("#0B0D10");
            Put(res, "Bg", Background);
            Put(res, "Chrome", White(.035));
            Put(res, "Panel", Rgba(18, 21, 26, .66));
            Put(res, "PaletteBg", Rgba(20, 23, 29, .97));
            Put(res, "MenuBg", Hex("#171A20"));
            Put(res, "Well", White(.05));
            Put(res, "Hair", White(.07));
            Put(res, "Hair2", White(.16));
            Put(res, "Hover", White(.06));
            Put(res, "Hover2", White(.12));
            Put(res, "Text", Hex("#E7EBF0"));
            Put(res, "Read", Hex("#D7DDE5"));
            Put(res, "Dim", Hex("#96A0AD"));
            Put(res, "Faint", Hex("#6C7683"));
            Put(res, "Backdrop", Rgba(4, 6, 9, .55));
            Put(res, "SwitchOff", White(.12));
            Put(res, "AccentSoft", ColorUtil.Mix(accent, 16));
            Put(res, "AccentSoft2", ColorUtil.Mix(accent, 26));
            Put(res, "AccentText", _settings.Accent == AccentKind.Teal ? ColorUtil.Oklch(0.80, 0.10, 196) : accent);
            Put(res, "Shadow", Color.FromArgb(230, 0, 0, 0));
        }
        else
        {
            Background = Hex("#FBFAF8");
            Put(res, "Bg", Background);
            Put(res, "Chrome", White(.74));
            Put(res, "Panel", White(.72));
            Put(res, "PaletteBg", White(.98));
            Put(res, "MenuBg", Hex("#FFFFFF"));
            Put(res, "Well", Ink(.045));
            Put(res, "Hair", Ink(.09));
            Put(res, "Hair2", Ink(.2));
            Put(res, "Hover", Ink(.05));
            Put(res, "Hover2", Ink(.1));
            Put(res, "Text", Hex("#15181D"));
            Put(res, "Read", Hex("#23272E"));
            Put(res, "Dim", Hex("#5B6570"));
            Put(res, "Faint", Hex("#8C949E"));
            Put(res, "Backdrop", Rgba(20, 24, 30, .22));
            Put(res, "SwitchOff", Ink(.14));
            Put(res, "AccentSoft", ColorUtil.Mix(accent, 14));
            Put(res, "AccentSoft2", ColorUtil.Mix(accent, 24));
            Put(res, "AccentText", _settings.Accent == AccentKind.Teal ? ColorUtil.Oklch(0.52, 0.10, 196) : Darken(accent));
            Put(res, "Shadow", Color.FromArgb(90, 20, 24, 30));
        }

        Put(res, "Accent", accent);
        Put(res, "Danger", Hex("#E5484D"));
        Put(res, "GlassBase", Color.FromArgb(0, 0, 0, 0));
        // Фон стартовой страницы: сквозь него виден материал окна, когда включено «жидкое стекло».
        Put(res, "StartBg", _settings.LiquidGlass ? Color.FromArgb(0, 0, 0, 0) : Background);

        // «Жидкое стекло»: панели становятся ещё прозрачнее, чтобы сквозь них просвечивал материал Mica.
        if (_settings.LiquidGlass)
        {
            if (IsDark)
            {
                Put(res, "Chrome", Rgba(12, 15, 20, .5));
                Put(res, "Panel", Rgba(14, 17, 22, .55));
                Put(res, "PaletteBg", Rgba(18, 21, 27, .78));
                Put(res, "MenuBg", Rgba(24, 28, 35, .86));
            }
            else
            {
                Put(res, "Chrome", White(.62));
                Put(res, "Panel", White(.6));
                Put(res, "PaletteBg", White(.82));
                Put(res, "MenuBg", White(.88));
            }
        }
        res["BgColor"] = Background;
        res["AccentColor"] = accent;
        res["ShadowColor"] = IsDark ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 60, 70, 80);

        double pad = _settings.Density switch { Density.Compact => 5, Density.Airy => 11, _ => 8 };
        res["TabRowPadding"] = new Thickness(8, pad, 6, pad);

        Changed?.Invoke();
    }

    public static Color AccentColor(AccentKind kind) => kind switch
    {
        AccentKind.Blue => Hex("#4F6BFF"),
        AccentKind.Violet => Hex("#B78CFF"),
        AccentKind.Orange => Hex("#FF6A3D"),
        _ => ColorUtil.Oklch(0.72, 0.12, 196)
    };

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    private static void Put(ResourceDictionary res, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Color White(double a) => Color.FromArgb((byte)(a * 255), 255, 255, 255);
    private static Color Ink(double a) => Color.FromArgb((byte)(a * 255), 12, 16, 22);
    private static Color Rgba(byte r, byte g, byte b, double a) => Color.FromArgb((byte)(a * 255), r, g, b);
    private static Color Darken(Color c) => Color.FromRgb((byte)(c.R * .75), (byte)(c.G * .75), (byte)(c.B * .75));
}
