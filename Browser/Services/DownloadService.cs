using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Browser.Models;
using Microsoft.Web.WebView2.Core;

namespace Browser.Services;

public sealed class DownloadService
{
    public ObservableCollection<DownloadItem> Items { get; } = new();

    public event Action<DownloadItem>? Started;

    public int ActiveCount => Items.Count(i => i.IsInProgress);

    public bool HasActiveFor(BrowserTab tab) => Items.Any(i => i.IsInProgress && i.SourceTab == tab);

    public void Handle(CoreWebView2DownloadStartingEventArgs e, BrowserTab? tab)
    {
        // Свой список загрузок вместо встроенной всплывашки Edge.
        e.Handled = true;
        var op = e.DownloadOperation;
        var item = new DownloadItem
        {
            Operation = op,
            SourceTab = tab,
            Path = e.ResultFilePath,
            Url = op.Uri,
            TotalBytes = (long)(op.TotalBytesToReceive ?? 0)
        };

        op.BytesReceivedChanged += (_, _) => item.ReceivedBytes = op.BytesReceived;
        op.StateChanged += (_, _) =>
        {
            item.State = op.State;
            if (op.State != CoreWebView2DownloadState.InProgress)
            {
                item.IsPaused = false;
                item.Path = op.ResultFilePath;
                item.ReceivedBytes = op.BytesReceived;
                item.TotalBytes = (long)(op.TotalBytesToReceive ?? (ulong)op.BytesReceived);
                item.Operation = null;
            }
        };

        Items.Insert(0, item);
        Started?.Invoke(item);
    }

    public static void Open(DownloadItem item)
    {
        if (!File.Exists(item.Path)) return;
        try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); } catch { }
    }

    public static void ShowInFolder(DownloadItem item)
    {
        try
        {
            if (File.Exists(item.Path)) Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
            else if (Directory.Exists(item.Folder)) Process.Start("explorer.exe", $"\"{item.Folder}\"");
        }
        catch { }
    }

    public static void TogglePause(DownloadItem item)
    {
        var op = item.Operation;
        if (op == null) return;
        if (item.IsPaused)
        {
            if (op.CanResume) { op.Resume(); item.IsPaused = false; }
        }
        else
        {
            op.Pause();
            item.IsPaused = true;
        }
    }

    public static void Cancel(DownloadItem item) => item.Operation?.Cancel();

    public void ClearFinished()
    {
        foreach (var item in Items.Where(i => !i.IsInProgress).ToList()) Items.Remove(item);
    }
}
