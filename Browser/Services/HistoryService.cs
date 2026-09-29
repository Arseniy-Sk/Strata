using Browser.Core;
using Browser.Models;

namespace Browser.Services;

public sealed class HistoryService
{
    private const string FileName = "history.json";
    private const int Limit = 20000;

    private readonly List<HistoryEntry> _entries;
    private readonly Debouncer _save = new(TimeSpan.FromSeconds(5));

    public event Action? Changed;

    public HistoryService()
    {
        _entries = AppPaths.ReadJson<List<HistoryEntry>>(FileName) ?? new List<HistoryEntry>();
    }

    public IReadOnlyList<HistoryEntry> Entries => _entries;

    public void Add(string url, string title)
    {
        if (url.StartsWith(UrlHelper.Scheme) || url.StartsWith("about:") || url.StartsWith("data:")) return;

        var last = _entries.Count > 0 ? _entries[^1] : null;
        if (last != null && UrlHelper.SameDocument(last.Url, url) && DateTime.UtcNow - last.VisitedUtc < TimeSpan.FromMinutes(30))
        {
            last.VisitedUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(title)) last.Title = title;
        }
        else
        {
            _entries.Add(new HistoryEntry { Url = url, Title = title, VisitedUtc = DateTime.UtcNow });
            if (_entries.Count > Limit) _entries.RemoveRange(0, _entries.Count - Limit);
        }
        ScheduleSave();
    }

    /// <summary>Добавить запись из импорта, сохранив исходную дату посещения.</summary>
    public void AddImported(string url, string title, DateTime visitedUtc)
    {
        if (url.StartsWith(UrlHelper.Scheme) || !url.StartsWith("http")) return;
        _entries.Add(new HistoryEntry { Url = url, Title = title, VisitedUtc = visitedUtc });
    }

    /// <summary>После пакетного импорта: отсортировать по дате, обрезать лимит, сохранить.</summary>
    public void FinishImport()
    {
        _entries.Sort((a, b) => a.VisitedUtc.CompareTo(b.VisitedUtc));
        if (_entries.Count > Limit) _entries.RemoveRange(0, _entries.Count - Limit);
        Changed?.Invoke();
        _save.Run(Save);
    }

    public void UpdateTitle(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        for (int i = _entries.Count - 1; i >= Math.Max(0, _entries.Count - 20); i--)
        {
            if (UrlHelper.SameDocument(_entries[i].Url, url))
            {
                if (_entries[i].Title == title) return;
                _entries[i].Title = title;
                ScheduleSave();
                return;
            }
        }
    }

    /// <summary>Свежие уникальные записи, подходящие под запрос. Линейный проход с конца — дёшево даже на 20 000 записей.</summary>
    public List<HistoryEntry> Search(string query, int max)
    {
        var result = new List<HistoryEntry>(max);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = _entries.Count - 1; i >= 0 && result.Count < max; i--)
        {
            var e = _entries[i];
            if (query.Length > 0
                && e.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                && e.Url.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (seen.Add(e.Url.TrimEnd('/'))) result.Add(e);
        }
        return result;
    }

    /// <summary>Частые сайты за последний месяц — для карточек «Продолжить».</summary>
    public List<HistoryEntry> TopSites(int max, ICollection<string> exclude)
    {
        var since = DateTime.UtcNow.AddDays(-30);
        return _entries.Where(e => e.VisitedUtc > since)
            .GroupBy(e => UrlHelper.DisplayHost(e.Url), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Count: g.Count(), Last: g.MaxBy(e => e.VisitedUtc)!))
            .Where(x => !exclude.Contains(x.Last.Url))
            .OrderByDescending(x => x.Count).ThenByDescending(x => x.Last.VisitedUtc)
            .Take(max).Select(x => x.Last).ToList();
    }

    public void Remove(HistoryEntry entry)
    {
        _entries.Remove(entry);
        ScheduleSave();
    }

    public void Clear()
    {
        _entries.Clear();
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        Changed?.Invoke();
        _save.Run(Save);
    }

    public void Save()
    {
        var snapshot = _entries.ToList();
        AppPaths.WriteJson(FileName, snapshot);
    }

    public void Flush() => _save.Flush();
}
