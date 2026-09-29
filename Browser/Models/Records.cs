using System.IO;
using System.Windows.Media;
using Browser.Core;
using Microsoft.Web.WebView2.Core;

namespace Browser.Models;

public sealed class HistoryEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime VisitedUtc { get; set; }
}

public sealed class Note
{
    public string Text { get; set; } = "";
    public string? Host { get; set; }
    public string? Url { get; set; }
    public DateTime CreatedUtc { get; set; }
}

// ---- Sessions ----

public sealed class TabData
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Collapsed { get; set; }
    public double ScrollY { get; set; }
    public List<TabData>? Children { get; set; }
}

public sealed class SpaceData
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Hue { get; set; }
    public string Headline { get; set; } = "";
    public string? LastActiveId { get; set; }
    public List<TabData> Tabs { get; set; } = new();
}

public sealed class SessionData
{
    public string Name { get; set; } = "";
    public DateTime SavedUtc { get; set; }
    public string? ActiveSpaceId { get; set; }
    public string? ActiveTabId { get; set; }
    public List<SpaceData> Spaces { get; set; } = new();
    public List<TabData> Pinned { get; set; } = new();

    public int CountTabs()
    {
        static int Count(List<TabData>? list) => list?.Sum(t => 1 + Count(t.Children)) ?? 0;
        return Spaces.Sum(s => Count(s.Tabs));
    }
}

// ---- Downloads ----

public sealed class DownloadItem : ObservableObject
{
    private long _received, _total;
    private string _status = "";
    private CoreWebView2DownloadState _state = CoreWebView2DownloadState.InProgress;
    private bool _isPaused;

    public CoreWebView2DownloadOperation? Operation { get; set; }
    public BrowserTab? SourceTab { get; set; }
    public string Path { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    public string Extension
    {
        get
        {
            var ext = System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
            return string.IsNullOrEmpty(ext) ? "FILE" : ext.Length > 4 ? ext[..4] : ext;
        }
    }

    public Brush ExtensionBrush => ColorUtil.HueBrush(ColorUtil.HueFor(Extension));

    public long ReceivedBytes { get => _received; set { if (Set(ref _received, value)) Refresh(); } }
    public long TotalBytes { get => _total; set { if (Set(ref _total, value)) Refresh(); } }

    public CoreWebView2DownloadState State
    {
        get => _state;
        set { if (Set(ref _state, value)) { Refresh(); OnPropertyChanged(nameof(IsInProgress)); OnPropertyChanged(nameof(IsDone)); } }
    }

    public bool IsPaused { get => _isPaused; set { if (Set(ref _isPaused, value)) Refresh(); } }
    public bool IsInProgress => _state == CoreWebView2DownloadState.InProgress;
    public bool IsDone => _state == CoreWebView2DownloadState.Completed;
    public double Progress => _state == CoreWebView2DownloadState.Completed ? 1 : _total > 0 ? (double)_received / _total : 0;
    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool FileExists => File.Exists(Path);

    private void Refresh()
    {
        OnPropertyChanged(nameof(Progress));
        Status = _state switch
        {
            CoreWebView2DownloadState.Completed => $"{Format.Bytes(_total > 0 ? _total : _received)} · done",
            CoreWebView2DownloadState.Interrupted => "interrupted",
            _ when _isPaused => $"paused · {(int)(Progress * 100)}%",
            _ when _total > 0 => $"{Format.Bytes(_total)} · {(int)(Progress * 100)}%",
            _ => Format.Bytes(_received)
        };
    }
}

public static class Format
{
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb < 10 ? $"{mb:0.0} MB" : $"{mb:0} MB";
        return $"{mb / 1024.0:0.0} GB";
    }
}
