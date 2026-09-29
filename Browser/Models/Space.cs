using System.Collections.ObjectModel;
using System.Windows.Media;
using Browser.Core;

namespace Browser.Models;

/// <summary>A space — an independent set of tab trees (Work, Research, Personal…).</summary>
public sealed class Space : ObservableObject
{
    private string _name = "Space";
    private int _hue = 196;
    private string _headline = "";
    private bool _isActive;
    private int _tabCount, _asleepCount;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) OnPropertyChanged(nameof(Glyph)); }
    }

    public string Glyph => string.IsNullOrEmpty(_name) ? "•" : char.ToUpperInvariant(_name[0]).ToString();

    public int Hue
    {
        get => _hue;
        set { if (Set(ref _hue, value)) { OnPropertyChanged(nameof(Brush)); OnPropertyChanged(nameof(Foreground)); } }
    }

    public Brush Brush => ColorUtil.HueBrush(_hue);
    public Brush Foreground => ColorUtil.HueForeground(_hue);

    public string Headline { get => _headline; set => Set(ref _headline, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    public List<BrowserTab> Roots { get; } = new();

    /// <summary>The flat visible list (accounting for collapsed branches) — what the virtualized ListBox actually shows.</summary>
    public ObservableCollection<BrowserTab> VisibleTabs { get; } = new();

    public BrowserTab? LastActive { get; set; }

    public int TabCount { get => _tabCount; private set { if (Set(ref _tabCount, value)) OnPropertyChanged(nameof(Stat)); } }
    public int AsleepCount { get => _asleepCount; private set { if (Set(ref _asleepCount, value)) OnPropertyChanged(nameof(Stat)); } }
    public string Stat => $"{Plural.Tabs(_tabCount)} · {Plural.Asleep(_asleepCount)}";

    public IEnumerable<BrowserTab> AllTabs()
    {
        var stack = new Stack<BrowserTab>();
        for (int i = Roots.Count - 1; i >= 0; i--) stack.Push(Roots[i]);
        while (stack.Count > 0)
        {
            var tab = stack.Pop();
            yield return tab;
            for (int i = tab.Children.Count - 1; i >= 0; i--) stack.Push(tab.Children[i]);
        }
    }

    public void RefreshCounts()
    {
        int total = 0, asleep = 0;
        foreach (var t in AllTabs())
        {
            total++;
            if (t.IsAsleep) asleep++;
        }
        TabCount = total;
        AsleepCount = asleep;
    }

    /// <summary>Rebuilds the visible list with minimal operations, so the ListBox doesn't reset scroll and containers.</summary>
    public void SyncVisible()
    {
        var target = new List<BrowserTab>(VisibleTabs.Count + 4);
        void Walk(List<BrowserTab> list, int depth)
        {
            foreach (var tab in list)
            {
                tab.Depth = depth;
                tab.RefreshHasChildren();
                target.Add(tab);
                if (!tab.IsCollapsed && tab.Children.Count > 0) Walk(tab.Children, depth + 1);
            }
        }
        Walk(Roots, 0);
        CollectionSync.Sync(VisibleTabs, target);
        RefreshCounts();
    }
}
