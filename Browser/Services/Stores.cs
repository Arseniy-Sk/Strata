using System.Collections.ObjectModel;
using Browser.Core;
using Browser.Models;

namespace Browser.Services;

public sealed class NotesService
{
    private const string FileName = "notes.json";
    private readonly Debouncer _save = new(TimeSpan.FromSeconds(2));

    public ObservableCollection<Note> Items { get; }

    public NotesService()
    {
        Items = new ObservableCollection<Note>(AppPaths.ReadJson<List<Note>>(FileName) ?? new List<Note>());
    }

    public void Add(string text, BrowserTab? tab)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Items.Insert(0, new Note
        {
            Text = text.Trim(),
            Host = tab?.IsWeb == true ? tab.Host : null,
            Url = tab?.IsWeb == true ? tab.Url : null,
            CreatedUtc = DateTime.UtcNow
        });
        _save.Run(Save);
    }

    public void Remove(Note note)
    {
        Items.Remove(note);
        _save.Run(Save);
    }

    public void Save() => AppPaths.WriteJson(FileName, Items.ToList());
    public void Flush() => _save.Flush();
}

public static class SessionStore
{
    private const string CurrentFile = "session.json";
    private const string ArchiveFile = "sessions.json";
    private const int ArchiveLimit = 12;

    public static SessionData? LoadCurrent() => AppPaths.ReadJson<SessionData>(CurrentFile);

    public static void SaveCurrent(SessionData data) => AppPaths.WriteJson(CurrentFile, data);

    public static List<SessionData> LoadArchive() => AppPaths.ReadJson<List<SessionData>>(ArchiveFile) ?? new List<SessionData>();

    public static void Archive(SessionData data)
    {
        if (data.CountTabs() == 0) return;
        var list = LoadArchive();
        list.Insert(0, data);
        if (list.Count > ArchiveLimit) list.RemoveRange(ArchiveLimit, list.Count - ArchiveLimit);
        AppPaths.WriteJson(ArchiveFile, list);
    }

    public static void RemoveArchived(SessionData data)
    {
        var list = LoadArchive();
        list.RemoveAll(s => s.SavedUtc == data.SavedUtc);
        AppPaths.WriteJson(ArchiveFile, list);
    }
}
