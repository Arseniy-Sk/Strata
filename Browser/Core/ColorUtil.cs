using System.Windows.Media;

namespace Browser.Core;

/// <summary>Цвета дизайна Strata заданы в OKLCH — переводим их в sRGB один раз и кэшируем кисти.</summary>
public static class ColorUtil
{
    private static readonly Dictionary<(int, int), SolidColorBrush> BrushCache = new();

    public static Color Oklch(double l, double c, double hDeg, byte alpha = 255)
    {
        double h = hDeg * Math.PI / 180.0;
        double a = c * Math.Cos(h), b = c * Math.Sin(h);

        double l_ = l + 0.3963377774 * a + 0.2158037573 * b;
        double m_ = l - 0.1055613458 * a - 0.0638541728 * b;
        double s_ = l - 0.0894841775 * a - 1.2914855480 * b;

        double L = l_ * l_ * l_, M = m_ * m_ * m_, S = s_ * s_ * s_;

        double r = +4.0767416621 * L - 3.3077115913 * M + 0.2309699292 * S;
        double g = -1.2684380046 * L + 2.6097574011 * M - 0.3413193965 * S;
        double bl = -0.0041960863 * L - 0.7034186147 * M + 1.7076147010 * S;

        return Color.FromArgb(alpha, ToByte(r), ToByte(g), ToByte(bl));
    }

    private static byte ToByte(double v)
    {
        v = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
    }

    /// <summary>Фон «фавиконки-буквы»: oklch(0.55 0.13 hue).</summary>
    public static SolidColorBrush HueBrush(int hue) => Cached(hue, 0, () => Oklch(0.55, 0.13, hue));

    /// <summary>Текст на фавиконке: oklch(0.97 0.02 hue).</summary>
    public static SolidColorBrush HueForeground(int hue) => Cached(hue, 1, () => Oklch(0.97, 0.02, hue));

    private static SolidColorBrush Cached(int hue, int kind, Func<Color> make)
    {
        lock (BrushCache)
        {
            if (!BrushCache.TryGetValue((hue, kind), out var brush))
            {
                brush = new SolidColorBrush(make());
                brush.Freeze();
                BrushCache[(hue, kind)] = brush;
            }
            return brush;
        }
    }

    /// <summary>Стабильный оттенок для домена — одна и та же буква-иконка между запусками.</summary>
    public static int HueFor(string? key)
    {
        if (string.IsNullOrEmpty(key)) return 220;
        unchecked
        {
            uint hash = 2166136261;
            foreach (char ch in key) hash = (hash ^ ch) * 16777619;
            return (int)(hash % 360);
        }
    }

    public static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B);

    /// <summary>Смешение цвета с прозрачностью в oklab-подобной манере color-mix(in oklab, c p%, transparent).</summary>
    public static Color Mix(Color c, double percent) => WithAlpha(c, percent / 100.0);
}
