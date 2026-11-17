using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Yukari.Enums;
using Yukari.Models.Common;
using Yukari.Models.DTO;

namespace Yukari.Models;

public partial class DownloadItem : ObservableObject
{
    public ContentKey ComicKey { get; }
    public ContentKey ChapterKey { get; }
    public string ComicTitle { get; }
    public string ChapterTitle { get; }

    public Func<
        CancellationToken,
        Task<Result<IReadOnlyList<ChapterPageModel>>>
    > PageProvider { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel), nameof(CanRetry), nameof(IsFinished), nameof(FormattedETA))]
    public partial DownloadStatus Status { get; set; } = DownloadStatus.Queued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedProgress))]
    public partial double Progress { get; set; } = 0.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedProgress))]
    public partial int CompletedPages { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedProgress))]
    public partial int TotalPages { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedETA))]
    public partial int QueuePosition { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedETA))]
    public partial TimeSpan? EstimatedTimeRemaining { get; set; }

    public string FormattedProgress => TotalPages > 0
        ? $"{Progress:P0} · {CompletedPages}/{TotalPages} pages"
        : $"{Progress:P0}";

    public string FormattedETA => Status switch
    {
        DownloadStatus.Queued => EstimatedTimeRemaining is { } wait
            ? $"Queued #{QueuePosition} · starts in {FormatDuration(wait)}"
            : QueuePosition > 0 ? $"Queued #{QueuePosition}" : "Queued",
        DownloadStatus.Downloading => EstimatedTimeRemaining is { } eta
            ? $"ETA {FormatDuration(eta)}"
            : "Preparing download…",
        DownloadStatus.Completed => "Complete",
        DownloadStatus.Failed => "Failed",
        DownloadStatus.Cancelled => "Cancelled",
        _ => string.Empty
    };

    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}h {value.Minutes}m"
        : value.TotalMinutes >= 1 ? $"{value.Minutes}m {value.Seconds}s" : $"{Math.Max(1, value.Seconds)}s";

    public bool CanCancel => Status is DownloadStatus.Queued or DownloadStatus.Downloading;
    public bool CanRetry => Status is DownloadStatus.Failed or DownloadStatus.Cancelled;
    public bool IsFinished =>
        Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled;

    internal CancellationTokenSource Cts { get; } = new();

    public DownloadItem(
        ContentKey comicKey,
        ContentKey chapterKey,
        string comicTitle,
        string chapterTitle,
        Func<CancellationToken, Task<Result<IReadOnlyList<ChapterPageModel>>>> pageProvider
    )
    {
        ComicKey = comicKey;
        ChapterKey = chapterKey;
        ChapterTitle = chapterTitle;
        ComicTitle = comicTitle;
        PageProvider = pageProvider;
    }

    public void Cancel()
    {
        if (!CanCancel)
            return;
        Cts.Cancel();
        Status = DownloadStatus.Cancelled;
    }
}
