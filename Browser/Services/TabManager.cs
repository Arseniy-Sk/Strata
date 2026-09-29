using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Browser.Core;
using Browser.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Browser.Services;

public interface IWebViewHost
{
    void AttachView(WebView2 view);
    void DetachView(WebView2 view);
    void RefreshLayout();
    void SetFullScreen(bool fullScreen);
    void CloseWindow();
}

/// <summary>
/// The browser's brain: spaces, the tab tree, and waking/sleeping engines.
///
/// A background tab's life cycle:
///   visible → hidden (Chromium itself throttles timers and rendering, MemoryUsageTargetLevel=Low)
///   → after FreezeAfter, "frozen" (TrySuspend: process is paused, memory shrinks)
///   → after SleepAfter, "asleep" (WebView destroyed, 0 MB)
/// Plus two safety valves: a live-tab limit (LRU) and a memory budget across all engine processes.
/// </summary>
public sealed class TabManager : ObservableObject
{
    private readonly IWebViewHost _host;
    private readonly Dictionary<BrowserTab, Task<CoreWebView2?>> _init = new();
    private readonly Stack<(string Url, string Title, Space? Space)> _closed = new();
    private readonly DispatcherTimer _sleepTimer;
    private readonly DispatcherTimer _memoryTimer;
    private readonly Debouncer _saveSession = new(TimeSpan.FromSeconds(4));

    private Space _activeSpace = null!;
    private BrowserTab? _activeTab;
    private BrowserTab? _splitLeft, _splitRight;
    private long _memoryBytes;
    private int _processCount, _totalTabs, _asleepTabs, _liveTabs, _totalBlocked;
    private string _hoverText = "";
    private bool _profileApplied;

    public BrowserSettings Settings { get; }
    public HistoryService History { get; } = new();
    public DownloadService Downloads { get; } = new();
    public NotesService Notes { get; } = new();
    public BookmarkService Bookmarks { get; } = new();
    public Import.PasswordVault Vault { get; } = new();

    public ObservableCollection<Space> Spaces { get; } = new();
    public ObservableCollection<BrowserTab> Pinned { get; } = new();

    public event Action? ActiveTabChanged;
    public event Action<string>? Toast;

    public TabManager(IWebViewHost host, BrowserSettings settings)
    {
        _host = host;
        Settings = settings;
        Settings.PropertyChanged += OnSettingChanged;

        _sleepTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        _sleepTimer.Tick += (_, _) => SleepTick();
        _sleepTimer.Start();

        _memoryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
        _memoryTimer.Tick += (_, _) => MemoryTick();
        _memoryTimer.Start();
    }

    // ------------------------------------------------------------------ state

    public Space ActiveSpace
    {
        get => _activeSpace;
        private set
        {
            if (_activeSpace == value) return;
            if (_activeSpace != null) _activeSpace.IsActive = false;
            _activeSpace = value;
            value.IsActive = true;
            OnPropertyChanged();
        }
    }

    public BrowserTab? ActiveTab
    {
        get => _activeTab;
        private set { if (Set(ref _activeTab, value)) ActiveTabChanged?.Invoke(); }
    }

    public BrowserTab? SplitLeft { get => _splitLeft; private set => Set(ref _splitLeft, value); }
    public BrowserTab? SplitRight { get => _splitRight; private set { if (Set(ref _splitRight, value)) OnPropertyChanged(nameof(IsSplit)); } }
    public bool IsSplit => _splitRight != null;

    public long MemoryBytes { get => _memoryBytes; private set { if (Set(ref _memoryBytes, value)) OnPropertyChanged(nameof(MemoryText)); } }
    public string MemoryText => $"memory {Format.Bytes(_memoryBytes)} / {(Settings.MemoryBudgetMb > 0 ? Format.Bytes(Settings.MemoryBudgetMb * 1024L * 1024L) : "∞")}";
    public double MemoryRatio => Settings.MemoryBudgetMb > 0 ? Math.Min(1, _memoryBytes / (Settings.MemoryBudgetMb * 1048576.0)) : 0;
    public int ProcessCount { get => _processCount; private set => Set(ref _processCount, value); }
    public int TotalTabs { get => _totalTabs; private set { if (Set(ref _totalTabs, value)) OnPropertyChanged(nameof(CountText)); } }
    public int AsleepTabs { get => _asleepTabs; private set { if (Set(ref _asleepTabs, value)) OnPropertyChanged(nameof(CountText)); } }
    public int LiveTabs { get => _liveTabs; private set { if (Set(ref _liveTabs, value)) OnPropertyChanged(nameof(CountText)); } }
    public string CountText => $"{Plural.Tabs(_totalTabs)} · {Plural.Asleep(_asleepTabs)} · {_liveTabs} in memory";
    public int TotalBlocked { get => _totalBlocked; private set => Set(ref _totalBlocked, value); }
    public string HoverText { get => _hoverText; private set => Set(ref _hoverText, value); }

    public bool CanReopenClosed => _closed.Count > 0;

    public IEnumerable<BrowserTab> AllTabs() => Pinned.Concat(Spaces.SelectMany(s => s.AllTabs()));

    private bool IsShown(BrowserTab tab) => tab == _activeTab || tab == _splitLeft || tab == _splitRight;

    // ------------------------------------------------------------------ startup and sessions

    public void Restore()
    {
        var data = Settings.Startup == StartupMode.RestoreSession ? SessionStore.LoadCurrent() : null;
        if (data != null && data.Spaces.Count > 0)
        {
            foreach (var sd in data.Spaces) Spaces.Add(BuildSpace(sd, keepIds: true));
            foreach (var td in data.Pinned)
            {
                var tab = BuildTab(td, null, null, keepIds: true);
                tab.IsPinned = true;
                Pinned.Add(tab);
            }
            var space = Spaces.FirstOrDefault(s => s.Id == data.ActiveSpaceId) ?? Spaces[0];
            ActiveSpace = space;
            var active = AllTabs().FirstOrDefault(t => t.Id == data.ActiveTabId);
            if (active != null && (active.Space == space || active.IsPinned)) Activate(active);
            else SwitchSpace(space, force: true);
        }
        else
        {
            Spaces.Add(new Space { Name = "Work", Hue = 196, Headline = "Calm focus on the task at hand" });
            Spaces.Add(new Space { Name = "Research", Hue = 300, Headline = "Information architecture of tabs" });
            Spaces.Add(new Space { Name = "Personal", Hue = 62, Headline = "Evening: trips and recipes" });
            ActiveSpace = Spaces[0];
            NewTab();
        }

        RefreshCounts();
        LoadFaviconsLazily();
    }

    private Space BuildSpace(SpaceData sd, bool keepIds)
    {
        var space = new Space
        {
            Id = keepIds && !string.IsNullOrEmpty(sd.Id) ? sd.Id : Guid.NewGuid().ToString("N"),
            Name = sd.Name,
            Hue = sd.Hue,
            Headline = sd.Headline
        };
        foreach (var td in sd.Tabs) space.Roots.Add(BuildTab(td, space, null, keepIds));
        space.LastActive = space.AllTabs().FirstOrDefault(t => t.Id == sd.LastActiveId);
        space.SyncVisible();
        return space;
    }

    private static BrowserTab BuildTab(TabData td, Space? space, BrowserTab? parent, bool keepIds)
    {
        var tab = new BrowserTab
        {
            Id = keepIds && !string.IsNullOrEmpty(td.Id) ? td.Id : Guid.NewGuid().ToString("N"),
            Url = td.Url,
            Space = space,
            Parent = parent,
            State = TabState.Asleep,
            RestoreScrollY = td.ScrollY
        };
        tab.Title = td.Title;
        tab.IsCollapsed = td.Collapsed;
        if (td.Children != null)
            foreach (var child in td.Children) tab.Children.Add(BuildTab(child, space, tab, keepIds));
        return tab;
    }

    /// <summary>Sleeping tabs' icons are read from disk when the UI is idle, not at startup.</summary>
    private void LoadFaviconsLazily()
    {
        var queue = new Queue<BrowserTab>(AllTabs().Where(t => t.IsWeb && t.Favicon == null));
        void Step()
        {
            for (int i = 0; i < 12 && queue.Count > 0; i++)
            {
                var tab = queue.Dequeue();
                tab.Favicon ??= FaviconService.TryGet(tab.Host);
            }
            if (queue.Count > 0) Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Step);
        }
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Step);
    }

    public SessionData BuildSession()
    {
        static TabData? ToData(BrowserTab t)
        {
            var children = t.Children.Select(ToData).Where(c => c != null).Cast<TabData>().ToList();
            if (!t.IsWeb)
                return children.Count == 0 ? null : new TabData { Id = t.Id, Url = t.Url, Title = t.Title, Children = children };
            return new TabData
            {
                Id = t.Id, Url = t.Url, Title = t.Title, Collapsed = t.IsCollapsed,
                ScrollY = t.RestoreScrollY, Children = children.Count > 0 ? children : null
            };
        }

        return new SessionData
        {
            Name = string.Join(" + ", Spaces.Where(s => s.Roots.Count > 0).Select(s => s.Name).Take(3)),
            SavedUtc = DateTime.UtcNow,
            ActiveSpaceId = ActiveSpace?.Id,
            ActiveTabId = ActiveTab?.Id,
            Pinned = Pinned.Select(ToData).Where(d => d != null).Cast<TabData>().ToList(),
            Spaces = Spaces.Select(s => new SpaceData
            {
                Id = s.Id, Name = s.Name, Hue = s.Hue, Headline = s.Headline, LastActiveId = s.LastActive?.Id,
                Tabs = s.Roots.Select(ToData).Where(d => d != null).Cast<TabData>().ToList()
            }).ToList()
        };
    }

    public void ScheduleSave() => _saveSession.Run(() => SessionStore.SaveCurrent(BuildSession()));

    public void Shutdown()
    {
        _sleepTimer.Stop();
        _memoryTimer.Stop();
        var session = BuildSession();
        SessionStore.SaveCurrent(session);
        SessionStore.Archive(session);
        History.Flush();
        Notes.Flush();
        Bookmarks.Flush();
        Settings.Save();
    }

    public void RestoreArchived(SessionData data)
    {
        Space? first = null;
        foreach (var sd in data.Spaces.Where(s => s.Tabs.Count > 0))
        {
            var space = BuildSpace(sd, keepIds: false);
            space.Name = $"{sd.Name} · {data.SavedUtc.ToLocalTime():dd.MM}";
            Spaces.Add(space);
            first ??= space;
        }
        LoadFaviconsLazily();
        if (first != null) SwitchSpace(first);
        RefreshCounts();
        ScheduleSave();
    }

    // ------------------------------------------------------------------ spaces

    public void SwitchSpace(Space space, bool force = false)
    {
        if (!force && space == ActiveSpace && ActiveTab?.Space == space) return;
        ActiveSpace = space;
        var target = space.LastActive is { } last && last.Space == space ? last : space.VisibleTabs.FirstOrDefault();
        if (target == null) NewTab(space: space);
        else Activate(target);
        ScheduleSave();
    }

    public void CycleSpace(int direction)
    {
        if (Spaces.Count < 2) return;
        int i = Spaces.IndexOf(ActiveSpace);
        SwitchSpace(Spaces[(i + direction + Spaces.Count) % Spaces.Count]);
    }

    public Space NewSpace(string? name = null)
    {
        int[] hues = { 196, 300, 62, 150, 246, 32, 12, 270 };
        var space = new Space
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Space {Spaces.Count + 1}" : name,
            Hue = hues[Spaces.Count % hues.Length],
            Headline = "A new space — a clean slate"
        };
        Spaces.Add(space);
        SwitchSpace(space);
        return space;
    }

    public void DeleteSpace(Space space)
    {
        if (Spaces.Count <= 1) return;
        foreach (var tab in space.AllTabs().ToList()) DestroyView(tab);
        int index = Spaces.IndexOf(space);
        Spaces.Remove(space);
        if (ActiveSpace == space || ActiveTab?.Space == space)
        {
            ActiveTab = null;
            SwitchSpace(Spaces[Math.Min(index, Spaces.Count - 1)], force: true);
        }
        RefreshCounts();
        ScheduleSave();
    }

    // ------------------------------------------------------------------ tabs

    public BrowserTab NewTab(string? url = null, BrowserTab? parent = null, bool activate = true,
        Space? space = null, bool navigate = true, BrowserTab? after = null)
    {
        url ??= UrlHelper.NewTabUrl;
        var tab = new BrowserTab { Url = url, State = TabState.Asleep };
        tab.Title = tab.IsWeb ? UrlHelper.Pretty(url) : UrlHelper.PageTitle(tab.Page);
        if (tab.IsWeb) tab.Favicon = FaviconService.TryGet(tab.Host);

        space ??= parent?.Space ?? after?.Space ?? ActiveSpace;
        tab.Space = space;

        if (after != null && after.Space == space)
        {
            var list = after.Parent?.Children ?? space.Roots;
            tab.Parent = after.Parent;
            list.Insert(list.IndexOf(after) + 1, tab);
        }
        else if (parent != null && parent.Space == space)
        {
            if (Settings.TabTree)
            {
                tab.Parent = parent;
                parent.Children.Add(tab);
                parent.IsCollapsed = false;
            }
            else
            {
                var list = parent.Parent?.Children ?? space.Roots;
                tab.Parent = parent.Parent;
                list.Insert(list.IndexOf(parent) + 1, tab);
            }
        }
        else
        {
            space.Roots.Add(tab);
        }

        tab.LastActiveUtc = DateTime.UtcNow;
        space.SyncVisible();

        if (tab.IsWeb && navigate) _ = LoadAsync(tab, url);
        if (activate) Activate(tab);

        RefreshCounts();
        ScheduleSave();
        return tab;
    }

    public void OpenPage(InternalPage page)
    {
        var url = UrlHelper.PageUrl(page);
        var existing = AllTabs().FirstOrDefault(t => t.Page == page && (t.Space == ActiveSpace || t.IsPinned));
        if (existing != null) { Activate(existing); return; }
        if (ActiveTab is { Page: InternalPage.NewTab } blank) Navigate(blank, url);
        else NewTab(url);
    }

    public void Activate(BrowserTab tab)
    {
        var previous = ActiveTab;
        if (previous == tab && (tab.View != null || !tab.IsWeb))
        {
            _host.RefreshLayout();
            return;
        }

        if (previous != null)
        {
            previous.IsActive = false;
            previous.LastActiveUtc = DateTime.UtcNow;
        }

        if (IsSplit && tab != _splitLeft && tab != _splitRight) EndSplit(refresh: false);

        if (tab.Space != null)
        {
            if (tab.Space != ActiveSpace) ActiveSpace = tab.Space;
            tab.Space.LastActive = tab;
            RevealInTree(tab);
        }

        tab.IsActive = true;
        tab.LastActiveUtc = DateTime.UtcNow;
        ActiveTab = tab;
        HoverText = "";

        if (tab.IsWeb) Wake(tab);
        _host.RefreshLayout();
        ScheduleSave();
    }

    private void Wake(BrowserTab tab)
    {
        if (tab.View?.CoreWebView2 is { } core)
        {
            tab.State = TabState.Live;
            try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
        }
        else if (!_init.ContainsKey(tab))
        {
            tab.Crashed = false;
            _ = LoadAsync(tab, tab.Url);
        }
    }

    private static void RevealInTree(BrowserTab tab)
    {
        bool changed = false;
        for (var p = tab.Parent; p != null; p = p.Parent)
            if (p.IsCollapsed) { p.IsCollapsed = false; changed = true; }
        if (changed) tab.Space?.SyncVisible();
    }

    public void Close(BrowserTab tab)
    {
        var space = tab.Space;
        bool wasActive = tab == ActiveTab;

        if (tab.IsWeb)
        {
            _closed.Push((tab.Url, tab.Title, space));
            OnPropertyChanged(nameof(CanReopenClosed));
        }

        BrowserTab? next = null;
        if (wasActive)
        {
            if (IsShown(tab) && IsSplit) next = tab == _splitLeft ? _splitRight : _splitLeft;
            else if (tab.IsPinned) next = ActiveSpace.LastActive is { } l && l != tab ? l : ActiveSpace.VisibleTabs.FirstOrDefault();
            else if (space != null)
            {
                var visible = space.VisibleTabs;
                int i = visible.IndexOf(tab);
                next = tab.Children.FirstOrDefault()
                       ?? (i + 1 < visible.Count ? visible[i + 1] : null)
                       ?? (i > 0 ? visible[i - 1] : null);
            }
        }

        if (IsShown(tab) && IsSplit) EndSplit(refresh: false);

        if (tab.IsPinned) Pinned.Remove(tab);
        else DetachFromTree(tab, promoteChildren: true);

        DestroyView(tab);
        if (space?.LastActive == tab) space.LastActive = null;

        if (wasActive)
        {
            ActiveTab = null;
            if (next != null) Activate(next);
            else if (space != null && space.Roots.Count > 0) Activate(space.VisibleTabs[0]);
            else if (Settings.KeepWindowOnLastTab || Spaces.Sum(s => s.Roots.Count) + Pinned.Count > 0) NewTab(space: space ?? ActiveSpace);
            else { _host.CloseWindow(); return; }
        }
        else
        {
            _host.RefreshLayout();
        }

        RefreshCounts();
        ScheduleSave();
    }

    public void CloseBranch(BrowserTab tab)
    {
        foreach (var child in tab.Children.ToList()) CloseBranch(child);
        Close(tab);
    }

    public void CloseOthers(BrowserTab keep)
    {
        if (keep.Space == null) return;
        foreach (var tab in keep.Space.AllTabs().Where(t => t != keep).ToList())
        {
            DestroyView(tab);
            DetachFromTree(tab, promoteChildren: true);
        }
        keep.Space.SyncVisible();
        Activate(keep);
        RefreshCounts();
        ScheduleSave();
    }

    public void ReopenClosed()
    {
        while (_closed.Count > 0)
        {
            var (url, _, space) = _closed.Pop();
            OnPropertyChanged(nameof(CanReopenClosed));
            var target = space != null && Spaces.Contains(space) ? space : ActiveSpace;
            NewTab(url, space: target);
            return;
        }
    }

    public void Duplicate(BrowserTab tab) => NewTab(tab.Url, after: tab.IsPinned ? null : tab);

    public void TogglePin(BrowserTab tab)
    {
        if (!tab.IsPinned)
        {
            if (tab.Space?.LastActive == tab) tab.Space.LastActive = null;
            DetachFromTree(tab, promoteChildren: true);
            tab.Space = null;
            tab.IsPinned = true;
            Pinned.Add(tab);
        }
        else
        {
            Pinned.Remove(tab);
            tab.IsPinned = false;
            tab.Space = ActiveSpace;
            ActiveSpace.Roots.Add(tab);
            ActiveSpace.SyncVisible();
            if (tab == ActiveTab) ActiveSpace.LastActive = tab;
        }
        RefreshCounts();
        ScheduleSave();
    }

    public void MoveToSpace(BrowserTab tab, Space target)
    {
        if (tab.Space == target || tab.IsPinned) return;
        var source = tab.Space!;
        DetachFromTree(tab, promoteChildren: false);
        foreach (var t in Subtree(tab)) t.Space = target;
        target.Roots.Add(tab);
        target.SyncVisible();
        if (source.LastActive != null && source.LastActive.Space != source) source.LastActive = null;
        if (tab == ActiveTab || Subtree(tab).Contains(ActiveTab)) Activate(ActiveTab!);
        RefreshCounts();
        ScheduleSave();
    }

    /// <summary>Drag-and-drop tab move: before/after the target, or into it.</summary>
    public void Move(BrowserTab tab, BrowserTab target, bool asChild, bool before)
    {
        if (tab == target || tab.IsPinned || target.IsPinned || target.Space == null) return;
        for (var p = target.Parent; p != null; p = p.Parent) if (p == tab) return; // can't move into its own branch

        var source = tab.Space;
        DetachFromTree(tab, promoteChildren: false);
        foreach (var t in Subtree(tab)) t.Space = target.Space;

        if (asChild)
        {
            tab.Parent = target;
            target.Children.Insert(0, tab);
            target.IsCollapsed = false;
        }
        else
        {
            var list = target.Parent?.Children ?? target.Space.Roots;
            tab.Parent = target.Parent;
            int index = list.IndexOf(target);
            list.Insert(before ? index : index + 1, tab);
        }

        target.Space.SyncVisible();
        if (source != null && source != target.Space) source.SyncVisible();
        ScheduleSave();
    }

    private static IEnumerable<BrowserTab> Subtree(BrowserTab tab)
    {
        yield return tab;
        foreach (var child in tab.Children)
            foreach (var t in Subtree(child)) yield return t;
    }

    private static void DetachFromTree(BrowserTab tab, bool promoteChildren)
    {
        var space = tab.Space;
        if (space == null) return;
        var list = tab.Parent?.Children ?? space.Roots;
        int index = list.IndexOf(tab);
        if (index < 0) return;
        list.RemoveAt(index);

        if (promoteChildren)
        {
            foreach (var child in tab.Children)
            {
                child.Parent = tab.Parent;
                list.Insert(index++, child);
            }
            tab.Children.Clear();
        }
        tab.Parent?.RefreshHasChildren();
        tab.Parent = null;
        space.SyncVisible();
    }

    public void ToggleCollapse(BrowserTab tab)
    {
        if (tab.Children.Count == 0) return;
        tab.IsCollapsed = !tab.IsCollapsed;
        if (tab.IsCollapsed && ActiveTab != null && Subtree(tab).Skip(1).Contains(ActiveTab)) Activate(tab);
        tab.Space?.SyncVisible();
        ScheduleSave();
    }

    public void SetAllCollapsed(bool collapsed)
    {
        foreach (var tab in ActiveSpace.AllTabs().Where(t => t.Children.Count > 0)) tab.IsCollapsed = collapsed;
        if (collapsed && ActiveTab is { Space: not null } active)
        {
            var root = active;
            while (root.Parent != null) root = root.Parent;
            if (root != active) Activate(root);
        }
        ActiveSpace.SyncVisible();
        ScheduleSave();
    }

    public void SelectRelative(int offset)
    {
        var list = ActiveSpace.VisibleTabs;
        if (list.Count == 0) return;
        int index = ActiveTab == null ? -1 : list.IndexOf(ActiveTab);
        if (index < 0) { Activate(list[0]); return; }
        Activate(list[(index + offset + list.Count) % list.Count]);
    }

    public void SelectIndex(int index)
    {
        var list = ActiveSpace.VisibleTabs;
        if (list.Count == 0) return;
        Activate(index < 0 || index >= list.Count ? list[^1] : list[index]);
    }

    public BrowserTab? FindOpenTab(string url)
        => AllTabs().FirstOrDefault(t => t.IsWeb && UrlHelper.SameDocument(t.Url, url));

    // ------------------------------------------------------------------ navigation

    public void NavigateActive(string input)
    {
        var url = UrlHelper.Resolve(input, Settings);
        var tab = ActiveTab;

        if (Settings.WarnDuplicates && UrlHelper.PageOf(url) == InternalPage.None && FindOpenTab(url) is { } dup && dup != tab)
        {
            var blank = tab is { Page: InternalPage.NewTab } ? tab : null;
            Activate(dup);
            if (blank != null) Close(blank);
            Toast?.Invoke("This page is already open — switched to it");
            return;
        }

        if (tab == null) NewTab(url);
        else Navigate(tab, url);
    }

    public void Navigate(BrowserTab tab, string url)
    {
        var page = UrlHelper.PageOf(url);
        if (page != InternalPage.None)
        {
            if (page != InternalPage.NewTab && AllTabs().FirstOrDefault(t => t.Page == page && t != tab) is { } open)
            {
                Activate(open);
                return;
            }
            DestroyView(tab);
            tab.Url = UrlHelper.PageUrl(page);
            tab.Title = UrlHelper.PageTitle(page);
            tab.Favicon = null;
            tab.BlockedCount = 0;
            _host.RefreshLayout();
            ScheduleSave();
            return;
        }

        bool wasInternal = !tab.IsWeb;
        tab.Url = url;
        if (wasInternal)
        {
            tab.Title = UrlHelper.Pretty(url);
            tab.Favicon = FaviconService.TryGet(tab.Host);
        }

        if (tab.View?.CoreWebView2 is { } core) SafeNavigate(core, url);
        else _ = LoadAsync(tab, url);

        _host.RefreshLayout();
        ScheduleSave();
    }

    private void SafeNavigate(CoreWebView2 core, string url)
    {
        try { core.Navigate(url); }
        catch (ArgumentException) { core.Navigate(Settings.SearchUrlPrefix + Uri.EscapeDataString(url)); }
    }

    private async Task LoadAsync(BrowserTab tab, string url)
    {
        tab.IsLoading = true;
        var core = await EnsureViewAsync(tab);
        if (core == null)
        {
            tab.IsLoading = false;
            return;
        }
        if (tab.Url != url && tab.IsWeb) url = tab.Url; // the address changed while the engine was loading
        if (!tab.IsWeb) return;
        tab.State = TabState.Live;
        SafeNavigate(core, url);
    }

    public void GoBack() { if (ActiveTab?.View?.CoreWebView2 is { CanGoBack: true } c) c.GoBack(); }
    public void GoForward() { if (ActiveTab?.View?.CoreWebView2 is { CanGoForward: true } c) c.GoForward(); }

    public void Reload()
    {
        var tab = ActiveTab;
        if (tab == null || !tab.IsWeb) return;
        if (tab.View?.CoreWebView2 is { } core) core.Reload();
        else Wake(tab);
    }

    public void Stop() => ActiveTab?.View?.CoreWebView2?.Stop();

    public async void ToggleReader()
    {
        var tab = ActiveTab;
        if (tab?.View?.CoreWebView2 is not { } core) return;
        if (tab.IsReader) { core.Reload(); return; }
        var accent = Application.Current.Resources["AccentColor"] is System.Windows.Media.Color c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : "#1FB5BC";
        var result = await core.ExecuteScriptAsync(ReaderScript.Build(ThemeManager.IsDark, accent));
        if (result == "true") tab.IsReader = true;
        else Toast?.Invoke("No readable article found on this page");
    }

    public void ToggleMute(BrowserTab tab)
    {
        if (tab.View?.CoreWebView2 is { } core) core.IsMuted = !core.IsMuted;
    }

    public void OpenDevTools() => ActiveTab?.View?.CoreWebView2?.OpenDevToolsWindow();

    // ------------------------------------------------------------------ translation, PiP, autofill, bookmarks

    /// <summary>Translate/restore the whole page. Text is translated through C#, bypassing the site's CSP.</summary>
    public async void TranslatePage()
    {
        var tab = ActiveTab;
        if (tab?.View?.CoreWebView2 is not { } core) return;
        var result = (await core.ExecuteScriptAsync(TranslationService.PageCollectScript)).Trim('"');
        if (result.StartsWith("on:")) { tab.IsTranslated = true; Toast?.Invoke("Translating page…"); }
        else if (result == "off") { tab.IsTranslated = false; Toast?.Invoke("Showing original"); }
    }

    public async void TogglePictureInPicture()
    {
        if (ActiveTab?.View?.CoreWebView2 is not { } core) return;
        var result = (await core.ExecuteScriptAsync(TranslationService.PictureInPictureScript)).Trim('"');
        if (result == "novideo") Toast?.Invoke("No video on this page");
        else if (result.StartsWith("fail")) Toast?.Invoke("Couldn't pop the video out into a window");
    }

    private async void OnWebMessage(BrowserTab tab, CoreWebView2 core, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.TryGetWebMessageAsString() ?? ""; } catch { return; }
        if (raw == "strata:bg") { tab.BackgroundClickUtc = DateTime.UtcNow; return; }
        if (raw.Length == 0 || raw[0] != '{') return;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type == "tr-quick")
            {
                var text = doc.RootElement.GetProperty("text").GetString() ?? "";
                var translated = await TranslationService.TranslateAsync(text, Settings.UiLanguage);
                if (tab.View?.CoreWebView2 == core && !string.Equals(translated, text, StringComparison.Ordinal))
                    await core.ExecuteScriptAsync(TranslationService.ShowBubbleScript(translated));
            }
            else if (type == "tr-page")
            {
                int start = doc.RootElement.GetProperty("start").GetInt32();
                var texts = doc.RootElement.GetProperty("texts").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                var outp = new string[texts.Length];
                await Parallel.ForEachAsync(Enumerable.Range(0, texts.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = 6 },
                    async (i, _) => outp[i] = await SafeTranslate(texts[i]));
                if (tab.View?.CoreWebView2 == core && tab.IsTranslated)
                    await core.ExecuteScriptAsync(TranslationService.ApplyPageScript(start, outp));
            }
        }
        catch { /* malformed message */ }
    }

    private async Task<string> SafeTranslate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        try { return await TranslationService.TranslateAsync(text, Settings.UiLanguage); }
        catch { return text; }
    }

    private void TryAutofill(BrowserTab tab, CoreWebView2 core)
    {
        if (!Settings.Autofill || !tab.IsWeb) return;
        var login = Vault.ForHost(tab.Host);
        if (login == null) return;
        var user = System.Text.Json.JsonSerializer.Serialize(login.Username);
        var pass = System.Text.Json.JsonSerializer.Serialize(login.Password);
        // Fill the password field and the nearest login/email field to it.
        var script = $$"""
        (() => {
          const pw = document.querySelector('input[type=password]:not([disabled])');
          if (!pw) return;
          const set = (el, v) => { const d = Object.getOwnPropertyDescriptor(el.__proto__, 'value')?.set || Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set; d.call(el, v); el.dispatchEvent(new Event('input',{bubbles:true})); el.dispatchEvent(new Event('change',{bubbles:true})); };
          if (!pw.value) set(pw, {{pass}});
          const form = pw.form || document;
          const cand = [...form.querySelectorAll('input')].filter(i => /text|email|tel|^$/.test(i.type) && i.offsetParent);
          const before = cand.filter(i => i.compareDocumentPosition(pw) & Node.DOCUMENT_POSITION_FOLLOWING).pop() || cand[0];
          if (before && !before.value) set(before, {{user}});
        })();
        """;
        _ = core.ExecuteScriptAsync(script);
    }

    public bool ToggleBookmarkActive()
    {
        var tab = ActiveTab;
        if (tab is not { IsWeb: true }) return false;
        return Bookmarks.Toggle(tab.Url, tab.Title);
    }

    public bool IsActiveBookmarked => ActiveTab is { IsWeb: true } tab && Bookmarks.Contains(tab.Url);

    public CoreWebView2? AnyCore => LiveCores().FirstOrDefault();

    public void Print() { if (ActiveTab?.View?.CoreWebView2 is { } core) core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); }

    public void Zoom(double delta)
    {
        if (ActiveTab?.View is not { } view) return;
        view.ZoomFactor = delta == 0 ? 1 : Math.Clamp(Math.Round((view.ZoomFactor + delta) * 10) / 10, 0.25, 5);
        Toast?.Invoke($"Zoom {view.ZoomFactor * 100:0}%");
    }

    // ------------------------------------------------------------------ split screen

    public void ToggleSplit()
    {
        if (IsSplit) { EndSplit(); return; }
        var active = ActiveTab;
        if (active is not { IsWeb: true }) { Toast?.Invoke("Only web pages can be split"); return; }

        var list = (active.Space ?? ActiveSpace).VisibleTabs;
        int index = list.IndexOf(active);
        BrowserTab? partner = null;
        for (int step = 1; step < list.Count && partner == null; step++)
        {
            var candidate = list[(index + step + list.Count) % list.Count];
            if (candidate.IsWeb && candidate != active) partner = candidate;
        }
        if (partner == null) { Toast?.Invoke("Splitting needs a second tab with a site"); return; }
        SplitWith(partner);
    }

    public void SplitWith(BrowserTab partner)
    {
        var active = ActiveTab;
        if (active is not { IsWeb: true } || !partner.IsWeb || partner == active) return;
        if (IsSplit) EndSplit(refresh: false);
        SplitLeft = active;
        SplitRight = partner;
        active.IsInSplit = partner.IsInSplit = true;
        partner.LastActiveUtc = DateTime.UtcNow;
        Wake(partner);
        _host.RefreshLayout();
    }

    public void EndSplit(bool refresh = true)
    {
        if (_splitLeft != null) _splitLeft.IsInSplit = false;
        if (_splitRight != null) _splitRight.IsInSplit = false;
        var hidden = _splitLeft == ActiveTab ? _splitRight : _splitLeft;
        if (hidden != null) hidden.LastActiveUtc = DateTime.UtcNow;
        SplitLeft = null;
        SplitRight = null;
        if (refresh) _host.RefreshLayout();
    }

    // ------------------------------------------------------------------ engine

    private Task<CoreWebView2?> EnsureViewAsync(BrowserTab tab)
    {
        if (tab.View?.CoreWebView2 is { } ready) return Task.FromResult<CoreWebView2?>(ready);
        if (_init.TryGetValue(tab, out var pending)) return pending;
        var task = CreateViewAsync(tab);
        _init[tab] = task;
        return task;
    }

    private async Task<CoreWebView2?> CreateViewAsync(BrowserTab tab)
    {
        var bg = ThemeManager.Background;
        var view = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, bg.R, bg.G, bg.B),
            Visibility = Visibility.Collapsed,
            Tag = tab
        };
        tab.View = view;
        _host.AttachView(view);
        _host.RefreshLayout();

        try
        {
            var env = await WebEngine.GetAsync(Settings);
            await view.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            _init.Remove(tab);
            if (tab.View == view) DestroyView(tab);
            Toast?.Invoke("The engine failed to start: " + ex.Message);
            return null;
        }

        _init.Remove(tab);
        if (tab.View != view || view.CoreWebView2 == null) return null; // the tab was closed/slept during startup

        Wire(tab, view.CoreWebView2);
        tab.State = TabState.Live;
        if (!IsShown(tab) && Settings.EfficiencyMode)
        {
            try { view.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
        }
        RefreshCounts();
        return view.CoreWebView2;
    }

    private void Wire(BrowserTab tab, CoreWebView2 core)
    {
        WebEngine.ApplySettings(core, Settings);
        if (!_profileApplied)
        {
            WebEngine.ApplyProfile(core, Settings, ThemeManager.IsDark);
            _profileApplied = true;
        }

        // Middle-click and Ctrl+click on a link open the tab in the background. The NewWindowRequested
        // event doesn't say how the link was opened, so the page sends a short signal ahead of time.
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(
            "addEventListener('mousedown',e=>{if(e.button===1||(e.button===0&&(e.ctrlKey||e.metaKey)))" +
            "{try{chrome.webview.postMessage('strata:bg')}catch(_){}}},true);");
        if (Settings.QuickTranslate)
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(TranslationService.QuickListenScript);
        if (Settings.VideoPopoutButton)
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(TranslationService.HoverPipScript);
        core.WebMessageReceived += (_, e) => OnWebMessage(tab, core, e);

        ShieldService.Attach(core, Settings);
        if (ShieldService.Enabled(Settings))
        {
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(ShieldService.CosmeticCss);
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(ShieldService.YoutubeAdSkipScript);
        }
        core.WebResourceRequested += (_, e) =>
        {
            if (!ShieldService.IsBlocked(e.Request.Uri, tab.Host)) return;
            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            tab.BlockedCount++;
            TotalBlocked++;
        };

        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith(UrlHelper.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                var target = e.Uri;
                Application.Current.Dispatcher.BeginInvoke(() => Navigate(tab, target));
                return;
            }
            tab.IsLoading = true;
            tab.IsReader = false;
            tab.IsTranslated = false;
            if (!e.IsRedirected) tab.BlockedCount = 0;
        };

        core.SourceChanged += (_, _) =>
        {
            if (tab.View?.CoreWebView2 != core) return;
            var oldHost = tab.Host;
            tab.Url = core.Source;
            if (tab.Host != oldHost) tab.Favicon = FaviconService.TryGet(tab.Host);
            ScheduleSave();
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = core.DocumentTitle;
            History.UpdateTitle(core.Source, core.DocumentTitle);
            ScheduleSave();
        };

        core.NavigationCompleted += (_, e) =>
        {
            tab.IsLoading = false;
            tab.CanGoBack = core.CanGoBack;
            tab.CanGoForward = core.CanGoForward;
            if (!e.IsSuccess) return;
            History.Add(core.Source, core.DocumentTitle);
            if (tab.RestoreScrollY > 0)
            {
                var y = tab.RestoreScrollY.ToString(CultureInfo.InvariantCulture);
                tab.RestoreScrollY = 0;
                _ = core.ExecuteScriptAsync($"window.scrollTo(0,{y})");
            }
            TryAutofill(tab, core);
        };

        core.HistoryChanged += (_, _) =>
        {
            tab.CanGoBack = core.CanGoBack;
            tab.CanGoForward = core.CanGoForward;
        };

        core.FaviconChanged += async (_, _) =>
        {
            var host = tab.Host;
            var image = await FaviconService.FetchAsync(core, host);
            if (tab.Host == host && image != null) tab.Favicon = image;
        };

        core.NewWindowRequested += (_, e) => HandleNewWindow(e, tab);

        core.WindowCloseRequested += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => Close(tab));
        core.IsDocumentPlayingAudioChanged += (_, _) => tab.IsAudible = core.IsDocumentPlayingAudio;
        core.IsMutedChanged += (_, _) => tab.IsMuted = core.IsMuted;
        core.StatusBarTextChanged += (_, _) => { if (IsShown(tab)) HoverText = core.StatusBarText; };
        core.DownloadStarting += (_, e) => Downloads.Handle(e, tab);
        core.ContainsFullScreenElementChanged += (_, _) => _host.SetFullScreen(core.ContainsFullScreenElement);

        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                or CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    DestroyView(tab);
                    tab.Crashed = true;
                    _host.RefreshLayout();
                    RefreshCounts();
                });
            }
        };
    }

    /// <summary>
    /// Decides what a new window should become: a real popup window (a sized window.open —
    /// Google sign-in, "Share", OAuth) or a regular tab (target=_blank links).
    /// </summary>
    public async void HandleNewWindow(CoreWebView2NewWindowRequestedEventArgs e, BrowserTab? source = null, bool fromPopup = false)
    {
        var f = e.WindowFeatures;
        bool isPopup = f != null && (f.HasSize || !f.ShouldDisplayToolbar);

        var deferral = e.GetDeferral();
        try
        {
            if (isPopup)
            {
                var popup = new Views.PopupWindow(this, f);
                popup.Show(); // the window must be realized (HWND) or the engine kills the source process
                var core = await popup.InitAsync();
                if (core != null)
                {
                    e.NewWindow = core;
                    e.Handled = true;
                }
                else
                {
                    popup.Close();
                    e.Handled = true;
                    NewTab(e.Uri);
                }
                return;
            }

            bool background = Native.IsCtrlDown || Native.IsMiddleDown
                              || (source != null && DateTime.UtcNow - source.BackgroundClickUtc < TimeSpan.FromSeconds(1.5));
            if (source != null) source.BackgroundClickUtc = default;
            var parent = source is { IsPinned: false } && !fromPopup ? source : null;
            var child = NewTab(e.Uri, parent: parent, activate: !background, navigate: false);
            var childCore = await EnsureViewAsync(child);
            if (childCore != null)
            {
                e.NewWindow = childCore;
                e.Handled = true;
            }
            else
            {
                e.Handled = true;
                Navigate(child, e.Uri);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void DestroyView(BrowserTab tab)
    {
        _init.Remove(tab);
        var view = tab.View;
        tab.View = null;
        tab.State = TabState.Asleep;
        tab.IsLoading = false;
        tab.IsAudible = false;
        tab.IsMuted = false;
        if (view == null) return;
        _host.DetachView(view);
        try { view.Dispose(); } catch { }
    }

    /// <summary>Unload a tab from memory. The scroll position is remembered and restored on waking.</summary>
    public async Task SleepAsync(BrowserTab tab)
    {
        if (IsShown(tab) || !tab.IsWeb || tab.View?.CoreWebView2 is not { } core) return;
        try
        {
            var result = await core.ExecuteScriptAsync("window.scrollY");
            if (double.TryParse(result, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) tab.RestoreScrollY = y;
        }
        catch { }

        if (IsShown(tab) || tab.View?.CoreWebView2 != core) return;
        DestroyView(tab);
        tab.Space?.RefreshCounts();
        RefreshCounts();
    }

    private async Task FreezeAsync(BrowserTab tab)
    {
        if (tab.View?.CoreWebView2 is not { } core || IsShown(tab)) return;
        try
        {
            if (Settings.EfficiencyMode) core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            if (await core.TrySuspendAsync() && !IsShown(tab) && tab.View?.CoreWebView2 == core)
                tab.State = TabState.Frozen;
        }
        catch
        {
            // Suspension isn't possible (e.g. media capture is active) — leave it as is.
        }
    }

    /// <summary>Called by the window when the visible tabs change: hidden ones go into power-save, shown ones wake up.</summary>
    public void OnViewHidden(BrowserTab tab)
    {
        tab.LastActiveUtc = DateTime.UtcNow;
        if (!Settings.EfficiencyMode || tab.View?.CoreWebView2 is not { } core) return;
        try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
    }

    public void SleepAllBackground()
    {
        int count = 0;
        foreach (var tab in AllTabs().Where(t => t.View != null && !IsShown(t) && !(Settings.KeepAudioAwake && t.IsAudible)).ToList())
        {
            _ = SleepAsync(tab);
            count++;
        }
        Toast?.Invoke(count == 0 ? "All background tabs are already asleep" : $"Put to sleep: {Plural.Tabs(count)}");
    }

    private void SleepTick()
    {
        var now = DateTime.UtcNow;
        var live = AllTabs().Where(t => t.View != null && !IsShown(t)).ToList();
        var candidates = new List<BrowserTab>();

        foreach (var tab in live)
        {
            if (Settings.KeepAudioAwake && tab.IsAudible) continue;
            if (Downloads.HasActiveFor(tab)) continue;
            var idle = (now - tab.LastActiveUtc).TotalMinutes;

            if (Settings.SleepAfterMinutes > 0 && idle >= Settings.SleepAfterMinutes)
            {
                _ = SleepAsync(tab);
                continue;
            }
            if (Settings.FreezeAfterMinutes > 0 && tab.State == TabState.Live && !tab.IsLoading && idle >= Settings.FreezeAfterMinutes)
                _ = FreezeAsync(tab);
            candidates.Add(tab);
        }

        if (Settings.MaxLiveTabs > 0)
        {
            int liveCount = AllTabs().Count(t => t.View != null);
            int excess = liveCount - Settings.MaxLiveTabs;
            foreach (var tab in candidates.OrderBy(t => t.LastActiveUtc).Take(Math.Max(0, excess)))
                _ = SleepAsync(tab);
        }
    }

    private void MemoryTick()
    {
        long total = Native.OwnPrivateWorkingSet();
        int processes = 1;
        if (WebEngine.Current is { } env && LiveTabs > 0)
        {
            try
            {
                foreach (var info in env.GetProcessInfos())
                {
                    total += Native.PrivateWorkingSet(info.ProcessId);
                    processes++;
                }
            }
            catch { }
        }
        MemoryBytes = total;
        ProcessCount = processes;
        RefreshCounts();
        OnPropertyChanged(nameof(MemoryRatio));

        // Budget exceeded — put the oldest background tab to sleep (one per tick, no jolts).
        if (Settings.MemoryBudgetMb > 0 && total > Settings.MemoryBudgetMb * 1048576L)
        {
            var victim = AllTabs()
                .Where(t => t.View != null && !IsShown(t) && !(Settings.KeepAudioAwake && t.IsAudible) && !Downloads.HasActiveFor(t))
                .MinBy(t => t.LastActiveUtc);
            if (victim != null) _ = SleepAsync(victim);
        }
    }

    public void RefreshCounts()
    {
        int total = 0, asleep = 0, live = 0;
        foreach (var tab in AllTabs())
        {
            if (!tab.IsWeb) { total++; continue; }
            total++;
            if (tab.View == null) asleep++;
            else live++;
        }
        TotalTabs = total;
        AsleepTabs = asleep;
        LiveTabs = live;
        foreach (var space in Spaces) space.RefreshCounts();
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BrowserSettings.Shield):
                foreach (var core in LiveCores())
                {
                    ShieldService.Detach(core);
                    ShieldService.Attach(core, Settings);
                }
                ApplyProfileToAny();
                break;
            case nameof(BrowserSettings.Theme):
                ApplyProfileToAny();
                break;
            case nameof(BrowserSettings.SavePasswords):
            case nameof(BrowserSettings.Autofill):
                foreach (var core in LiveCores()) WebEngine.ApplySettings(core, Settings);
                break;
            case nameof(BrowserSettings.MemoryBudgetMb):
                OnPropertyChanged(nameof(MemoryText));
                break;
        }
    }

    public void ApplyProfileToAny()
    {
        var core = LiveCores().FirstOrDefault();
        if (core != null) WebEngine.ApplyProfile(core, Settings, ThemeManager.IsDark);
        else _profileApplied = false;

        var bg = ThemeManager.Background;
        foreach (var tab in AllTabs().Where(t => t.View != null))
            tab.View!.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, bg.R, bg.G, bg.B);
    }

    private IEnumerable<CoreWebView2> LiveCores()
        => AllTabs().Select(t => t.View?.CoreWebView2).Where(c => c != null).Cast<CoreWebView2>();

    public async Task ClearBrowsingDataAsync()
    {
        var core = LiveCores().FirstOrDefault();
        if (core == null) return;
        await core.Profile.ClearBrowsingDataAsync(
            CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.DiskCache |
            CoreWebView2BrowsingDataKinds.CacheStorage | CoreWebView2BrowsingDataKinds.LocalStorage |
            CoreWebView2BrowsingDataKinds.IndexedDb | CoreWebView2BrowsingDataKinds.ServiceWorkers);
    }

    public CoreWebView2Profile? AnyProfile => LiveCores().FirstOrDefault()?.Profile;
}
