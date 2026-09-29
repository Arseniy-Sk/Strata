using System.IO;
using System.Net;
using Browser.Models;

namespace Browser.Core;

public static class UrlHelper
{
    public const string Scheme = "strata://";
    public const string NewTabUrl = "strata://newtab";

    private static readonly string[] KnownSchemes =
        { "http", "https", "file", "about", "data", "blob", "view-source", "mailto", "edge", "chrome", "javascript", "ftp" };

    public static string PageUrl(InternalPage page) => Scheme + page.ToString().ToLowerInvariant();

    public static InternalPage PageOf(string url)
    {
        if (!url.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return InternalPage.None;
        var name = url[Scheme.Length..].TrimEnd('/');
        int cut = name.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0) name = name[..cut];
        return Enum.TryParse<InternalPage>(name, true, out var page) && page != InternalPage.None ? page : InternalPage.NewTab;
    }

    public static string PageTitle(InternalPage page) => page switch
    {
        InternalPage.Settings => "Settings",
        InternalPage.History => "History and sessions",
        InternalPage.Downloads => "Downloads",
        InternalPage.Extensions => "Extensions",
        InternalPage.About => "About Strata",
        _ => "New tab"
    };

    public static string PageGlyph(InternalPage page) => page switch
    {
        InternalPage.Settings => "⚙",
        InternalPage.History => "◷",
        InternalPage.Downloads => "⤓",
        InternalPage.Extensions => "◫",
        InternalPage.About => "◆",
        _ => "+"
    };

    /// <summary>Turns user input into an address: a URL, domain, file path, or search query.</summary>
    public static string Resolve(string input, BrowserSettings settings)
    {
        var text = input.Trim();
        if (text.Length == 0) return NewTabUrl;

        if (text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            return text.ToLowerInvariant();

        if (Uri.TryCreate(text, UriKind.Absolute, out var abs) && KnownSchemes.Contains(abs.Scheme))
            return text;

        if (text.Length > 2 && text[1] == ':' && (text[2] == '\\' || text[2] == '/') && (File.Exists(text) || Directory.Exists(text)))
            return new Uri(text).AbsoluteUri;

        if (!text.Contains(' ') && LooksLikeHost(text, out bool local))
            return (local ? "http://" : "https://") + text;

        return settings.SearchUrlPrefix + Uri.EscapeDataString(text);
    }

    private static bool LooksLikeHost(string text, out bool local)
    {
        local = false;
        var hostPart = text;
        int slash = hostPart.IndexOfAny(new[] { '/', '?', '#' });
        if (slash >= 0) hostPart = hostPart[..slash];
        int colon = hostPart.LastIndexOf(':');
        if (colon > 0)
        {
            if (!int.TryParse(hostPart[(colon + 1)..], out _)) return false;
            hostPart = hostPart[..colon];
        }

        if (hostPart.Equals("localhost", StringComparison.OrdinalIgnoreCase)) { local = true; return true; }
        if (IPAddress.TryParse(hostPart, out _)) { local = true; return hostPart.Contains('.') || hostPart.Contains(':'); }

        int dot = hostPart.LastIndexOf('.');
        if (dot <= 0 || dot == hostPart.Length - 1) return false;
        var tld = hostPart[(dot + 1)..];
        if (tld.Length < 2 || !tld.All(char.IsLetter)) return false;
        return hostPart.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');
    }

    public static string DisplayHost(string url)
    {
        if (url.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (uri.IsFile) return "file";
        return string.IsNullOrEmpty(uri.Host) ? uri.Scheme : uri.Host;
    }

    /// <summary>A short address form for display: no scheme, no trailing slash.</summary>
    public static string Pretty(string url)
    {
        if (url.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return url;
        var text = url;
        if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) text = text[8..];
        else if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) text = text[7..];
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) text = text[4..];
        if (text.EndsWith('/')) text = text[..^1];
        try { return Uri.UnescapeDataString(text); } catch { return text; }
    }

    public static bool SameDocument(string a, string b)
        => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
