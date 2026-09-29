using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Browser.Core;
using Microsoft.Web.WebView2.Core;

namespace Browser.Services;

/// <summary>Иконки сайтов: память → диск → движок. Спящие вкладки показывают иконку без пробуждения.</summary>
public static class FaviconService
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? TryGet(string host)
    {
        if (string.IsNullOrEmpty(host) || host.StartsWith(UrlHelper.Scheme)) return null;
        if (Cache.TryGetValue(host, out var cached)) return cached;

        ImageSource? image = null;
        var path = PathFor(host);
        if (File.Exists(path))
        {
            try { image = Decode(File.ReadAllBytes(path)); }
            catch { image = null; }
        }
        Cache[host] = image;
        return image;
    }

    public static async Task<ImageSource?> FetchAsync(CoreWebView2 core, string host)
    {
        try
        {
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream == null) return TryGet(host);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            if (ms.Length == 0) return TryGet(host);

            var bytes = ms.ToArray();
            var image = Decode(bytes);
            Cache[host] = image;
            var path = PathFor(host);
            _ = Task.Run(() => { try { File.WriteAllBytes(path, bytes); } catch { } });
            return image;
        }
        catch
        {
            return TryGet(host);
        }
    }

    private static ImageSource Decode(byte[] bytes)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelWidth = 32;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string PathFor(string host)
    {
        var safe = string.Concat(host.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
        return Path.Combine(AppPaths.Favicons, safe + ".png");
    }
}
