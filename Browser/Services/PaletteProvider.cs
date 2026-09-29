using System.Windows.Media;
using Browser.Core;
using Browser.Models;

namespace Browser.Services;

public sealed class PaletteItem
{
    public bool IsHeader { get; init; }
    public string Title { get; init; } = "";
    public string Sub { get; init; } = "";
    public string Hint { get; init; } = "";
    public string Letter { get; init; } = "";
    public Brush? HueBrush { get; init; }
    public Brush? HueForeground { get; init; }
    public ImageSource? Favicon { get; init; }
    public bool IsIcon { get; init; }
    public Action? Run { get; init; }

    public bool HasFavicon => Favicon != null;
    public bool Selectable => !IsHeader;
}

/// <summary>Unified search over tabs, history, actions, and pages — for the palette (Ctrl K) and address bar.</summary>
public sealed class PaletteProvider
{
    private readonly TabManager _tabs;
    private readonly Func<IReadOnlyList<(string Title, string Sub, string Hint, string Glyph, int Hue, Action Run)>> _actions;

    public PaletteProvider(TabManager tabs, Func<IReadOnlyList<(string, string, string, string, int, Action)>> actions)
    {
        _tabs = tabs;
        _actions = actions;
    }

    public List<PaletteItem> Query(string raw, bool compact, Action<string> navigate)
    {
        var items = new List<PaletteItem>();
        var query = raw.Trim();
        char prefix = query.Length > 0 ? query[0] : '\0';
        bool onlyTabs = prefix == '#', onlyHistory = prefix == '@', onlyActions = prefix == '>';
        if (onlyTabs || onlyHistory || onlyActions) query = query[1..].TrimStart();

        // First row — "go to" or "search".
        if (query.Length > 0 && !onlyTabs && !onlyActions)
        {
            var url = UrlHelper.Resolve(query, _tabs.Settings);
            bool isSearch = url.StartsWith(_tabs.Settings.SearchUrlPrefix, StringComparison.Ordinal);
            items.Add(new PaletteItem
            {
                Title = isSearch ? $"Search “{query}”" : UrlHelper.Pretty(url),
                Sub = isSearch ? _tabs.Settings.SearchEngineName : "go to address",
                Hint = "↵",
                Letter = isSearch ? "" : "",
                IsIcon = true,
                HueBrush = ColorUtil.HueBrush(196),
                HueForeground = ColorUtil.HueForeground(196),
                Run = () => navigate(query)
            });
        }

        if (!onlyHistory && !onlyActions)
        {
            var tabs = _tabs.AllTabs().Where(t => t.IsWeb || !compact);
            tabs = query.Length == 0
                ? tabs.Where(t => t != _tabs.ActiveTab).OrderByDescending(t => t.LastActiveUtc)
                : tabs.Where(t => Contains(t.Title, query) || Contains(t.Url, query));
            var found = tabs.Take(compact ? 3 : onlyTabs ? 20 : 5).ToList();
            if (found.Count > 0)
            {
                if (!compact) items.Add(Header("OPEN TABS"));
                foreach (var t in found)
                {
                    var tab = t;
                    items.Add(new PaletteItem
                    {
                        Title = tab.Title,
                        Sub = $"{tab.Host} · {(tab.IsPinned ? "pinned" : tab.Space?.Name)}{(tab.IsAsleep ? " · asleep" : "")}",
                        Hint = compact ? "tab" : "↵",
                        Letter = tab.Letter,
                        HueBrush = tab.HueBrush,
                        HueForeground = tab.HueForeground,
                        Favicon = tab.Favicon,
                        Run = () => _tabs.Activate(tab)
                    });
                }
            }
        }

        if (!onlyTabs && !onlyActions && (query.Length > 0 || onlyHistory))
        {
            var open = new HashSet<string>(_tabs.AllTabs().Select(t => t.Url.TrimEnd('/')), StringComparer.OrdinalIgnoreCase);
            var history = _tabs.History.Search(query, compact ? 8 : onlyHistory ? 30 : 6)
                .Where(h => !open.Contains(h.Url.TrimEnd('/'))).Take(compact ? 6 : onlyHistory ? 30 : 5).ToList();
            if (history.Count > 0)
            {
                if (!compact) items.Add(Header("HISTORY"));
                foreach (var h in history)
                {
                    var entry = h;
                    var host = UrlHelper.DisplayHost(entry.Url);
                    int hue = ColorUtil.HueFor(host);
                    items.Add(new PaletteItem
                    {
                        Title = string.IsNullOrWhiteSpace(entry.Title) ? UrlHelper.Pretty(entry.Url) : entry.Title,
                        Sub = UrlHelper.Pretty(entry.Url),
                        Hint = entry.VisitedUtc.ToLocalTime().ToString("dd.MM HH:mm"),
                        Letter = LetterOf(host),
                        HueBrush = ColorUtil.HueBrush(hue),
                        HueForeground = ColorUtil.HueForeground(hue),
                        Favicon = FaviconService.TryGet(host),
                        Run = () => navigate(entry.Url)
                    });
                }
            }
        }

        if (!compact && !onlyTabs && !onlyHistory)
        {
            var actions = _actions().Where(a => query.Length == 0 || Contains(a.Title, query) || Contains(a.Sub, query)).ToList();
            if (actions.Count > 0)
            {
                items.Add(Header("ACTIONS"));
                foreach (var a in actions.Take(onlyActions ? 40 : query.Length == 0 ? 6 : 8))
                    items.Add(new PaletteItem
                    {
                        Title = a.Title, Sub = a.Sub, Hint = a.Hint, Letter = a.Glyph, IsIcon = true,
                        HueBrush = ColorUtil.HueBrush(a.Hue), HueForeground = ColorUtil.HueForeground(a.Hue), Run = a.Run
                    });
            }
        }

        return items;
    }

    private static string LetterOf(string host)
    {
        var h = host.StartsWith("www.") ? host[4..] : host;
        foreach (var ch in h) if (char.IsLetterOrDigit(ch)) return char.ToUpperInvariant(ch).ToString();
        return "•";
    }

    private static PaletteItem Header(string text) => new() { IsHeader = true, Title = text };

    private static bool Contains(string? haystack, string needle)
        => haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}
