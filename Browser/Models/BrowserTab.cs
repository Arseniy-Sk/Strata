using System.Windows.Media;
using Browser.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Browser.Models;

/// <summary>
/// Live   — the engine is running (a visible or background tab).
/// Frozen — the process is suspended (TrySuspend): JS and timers are paused, memory shrinks.
/// Asleep — the WebView is destroyed; only the address, title, and icon remain.
/// </summary>
public enum TabState { Live, Frozen, Asleep }

public enum InternalPage { None, NewTab, Settings, History, Downloads, Extensions, About }

public sealed class BrowserTab : ObservableObject
{
    private string _title = "New tab";
    private string _url = UrlHelper.NewTabUrl;
    private ImageSource? _favicon;
    private bool _isActive, _isLoading, _isAudible, _isMuted, _isCollapsed, _isPinned;
    private bool _canGoBack, _canGoForward, _isInSplit, _isReader, _hasChildren, _crashed, _isTranslated;
    private int _blockedCount, _depth;
    private TabState _state = TabState.Asleep;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Title
    {
        get => _title;
        set { if (Set(ref _title, string.IsNullOrWhiteSpace(value) ? HostOrTitleFallback() : value)) OnPropertyChanged(nameof(Letter)); }
    }

    public string Url
    {
        get => _url;
        set
        {
            if (!Set(ref _url, value)) return;
            Host = UrlHelper.DisplayHost(value);
            Page = UrlHelper.PageOf(value);
            OnPropertyChanged(nameof(Host));
            OnPropertyChanged(nameof(Page));
            OnPropertyChanged(nameof(IsWeb));
            OnPropertyChanged(nameof(Letter));
            OnPropertyChanged(nameof(Hue));
            OnPropertyChanged(nameof(HueBrush));
            OnPropertyChanged(nameof(HueForeground));
            OnPropertyChanged(nameof(IsSecure));
        }
    }

    public string Host { get; private set; } = "strata://newtab";
    public InternalPage Page { get; private set; } = InternalPage.NewTab;
    public bool IsWeb => Page == InternalPage.None;
    public bool IsSecure => _url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public ImageSource? Favicon { get => _favicon; set { if (Set(ref _favicon, value)) OnPropertyChanged(nameof(HasFavicon)); } }
    public bool HasFavicon => _favicon != null;

    public string Letter
    {
        get
        {
            if (Page != InternalPage.None) return UrlHelper.PageGlyph(Page);
            var source = Host.StartsWith("www.") ? Host[4..] : Host;
            foreach (var ch in source)
                if (char.IsLetterOrDigit(ch)) return char.ToUpperInvariant(ch).ToString();
            return "•";
        }
    }

    public int Hue => Page != InternalPage.None ? 196 : ColorUtil.HueFor(Host);
    public Brush HueBrush => ColorUtil.HueBrush(Hue);
    public Brush HueForeground => ColorUtil.HueForeground(Hue);

    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }
    public bool IsAudible { get => _isAudible; set { if (Set(ref _isAudible, value)) OnPropertyChanged(nameof(ShowAudio)); } }
    public bool IsMuted { get => _isMuted; set { if (Set(ref _isMuted, value)) OnPropertyChanged(nameof(ShowAudio)); } }
    public bool ShowAudio => _isAudible || _isMuted;
    public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }
    public bool CanGoBack { get => _canGoBack; set => Set(ref _canGoBack, value); }
    public bool CanGoForward { get => _canGoForward; set => Set(ref _canGoForward, value); }
    public bool IsInSplit { get => _isInSplit; set => Set(ref _isInSplit, value); }
    public bool IsReader { get => _isReader; set => Set(ref _isReader, value); }
    public bool IsTranslated { get => _isTranslated; set => Set(ref _isTranslated, value); }
    public bool Crashed { get => _crashed; set => Set(ref _crashed, value); }
    public int BlockedCount { get => _blockedCount; set => Set(ref _blockedCount, value); }

    public TabState State
    {
        get => _state;
        set { if (Set(ref _state, value)) { OnPropertyChanged(nameof(IsAsleep)); OnPropertyChanged(nameof(IsFrozen)); } }
    }

    /// <summary>Internal pages don't hold an engine, so the UI never treats them as "asleep".</summary>
    public bool IsAsleep => _state == TabState.Asleep && IsWeb;
    public bool IsFrozen => _state == TabState.Frozen;

    // Tree
    public BrowserTab? Parent { get; set; }
    public List<BrowserTab> Children { get; } = new();
    public Space? Space { get; set; }
    public int Depth { get => _depth; set => Set(ref _depth, value); }
    public bool IsCollapsed { get => _isCollapsed; set => Set(ref _isCollapsed, value); }
    public bool HasChildren { get => _hasChildren; private set => Set(ref _hasChildren, value); }
    public void RefreshHasChildren() => HasChildren = Children.Count > 0;

    // Engine
    public WebView2? View { get; set; }
    public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;
    public double RestoreScrollY { get; set; }
    public DateTime BackgroundClickUtc { get; set; }
    public string? PendingNavigation { get; set; }

    private string HostOrTitleFallback() => Page == InternalPage.None ? Host : UrlHelper.PageTitle(Page);

    public override string ToString() => Title;
}
