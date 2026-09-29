using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Browser.Core;
using Browser.Models;

namespace Browser.Services;

public sealed class Bookmark : ObservableObject
{
    private string _title = "";
    private string _url = "";

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get => _title; set { if (Set(ref _title, value)) OnPropertyChanged(nameof(Letter)); } }
    public string Url { get => _url; set { if (Set(ref _url, value)) { OnPropertyChanged(nameof(Host)); OnPropertyChanged(nameof(Letter)); OnPropertyChanged(nameof(Hue)); OnPropertyChanged(nameof(HueBrush)); OnPropertyChanged(nameof(HueForeground)); } } }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public string Host => UrlHelper.DisplayHost(_url);
    [JsonIgnore] public int Hue => ColorUtil.HueFor(Host);
    [JsonIgnore] public Brush HueBrush => ColorUtil.HueBrush(Hue);
    [JsonIgnore] public Brush HueForeground => ColorUtil.HueForeground(Hue);

    [JsonIgnore]
    public string Letter
    {
        get
        {
            var s = Host.StartsWith("www.") ? Host[4..] : Host;
            foreach (var ch in s) if (char.IsLetterOrDigit(ch)) return char.ToUpperInvariant(ch).ToString();
            return "•";
        }
    }
}

/// <summary>Bookmarks: the bar under the address bar and the grid on the start page.</summary>
public sealed class BookmarkService
{
    private const string FileName = "bookmarks.json";
    private readonly Debouncer _save = new(TimeSpan.FromSeconds(2));

    public ObservableCollection<Bookmark> Items { get; }

    public event Action? Changed;

    public BookmarkService()
    {
        Items = new ObservableCollection<Bookmark>(AppPaths.ReadJson<List<Bookmark>>(FileName) ?? new List<Bookmark>());
        Items.CollectionChanged += (_, _) => Changed?.Invoke();
    }

    public bool Contains(string url) => Items.Any(b => UrlHelper.SameDocument(b.Url, url));

    public Bookmark? Find(string url) => Items.FirstOrDefault(b => UrlHelper.SameDocument(b.Url, url));

    public Bookmark Add(string url, string title)
    {
        var existing = Find(url);
        if (existing != null) return existing;
        var bookmark = new Bookmark { Url = url, Title = string.IsNullOrWhiteSpace(title) ? UrlHelper.Pretty(url) : title };
        Items.Insert(0, bookmark);
        ScheduleSave();
        return bookmark;
    }

    public void Remove(Bookmark bookmark)
    {
        Items.Remove(bookmark);
        ScheduleSave();
    }

    public bool Toggle(string url, string title)
    {
        var existing = Find(url);
        if (existing != null) { Remove(existing); return false; }
        Add(url, title);
        return true;
    }

    public void Move(Bookmark bookmark, int newIndex)
    {
        int old = Items.IndexOf(bookmark);
        if (old < 0 || newIndex < 0 || newIndex >= Items.Count) return;
        Items.Move(old, newIndex);
        ScheduleSave();
    }

    /// <summary>Merge on import — no duplicates by address.</summary>
    public int Merge(IEnumerable<(string Url, string Title)> incoming)
    {
        int added = 0;
        foreach (var (url, title) in incoming)
        {
            if (string.IsNullOrWhiteSpace(url) || Contains(url)) continue;
            Items.Add(new Bookmark { Url = url, Title = string.IsNullOrWhiteSpace(title) ? UrlHelper.Pretty(url) : title });
            added++;
        }
        if (added > 0) ScheduleSave();
        return added;
    }

    public void Rename(Bookmark bookmark, string title, string url)
    {
        bookmark.Title = title;
        bookmark.Url = url;
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        Changed?.Invoke();
        _save.Run(Save);
    }

    public void Save() => AppPaths.WriteJson(FileName, Items.ToList());
    public void Flush() => _save.Flush();
}
