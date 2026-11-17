using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Yukari.Enums;
using Yukari.Helpers;
using Yukari.Models;
using Yukari.Models.Common;
using Yukari.Models.DTO;
using Yukari.Services.Sources;

namespace Yukari.Services.Storage;

internal class DownloadService : IDownloadService
{
    private readonly IDataService _dataService;
    private readonly ISourceService _sourceService;
    private readonly ILogger<DownloadService> _logger;

    private readonly Channel<DownloadItem> _downloadQueue = Channel.CreateUnbounded<DownloadItem>();
    private readonly List<DownloadItem> _downloads = new();
    private double _averageSecondsPerPage = 1.5;
    private int _observedPages;
    private double _averageSecondsPerChapter = 25;
    private int _completedChapters;

    public event Action<IReadOnlyList<DownloadItem>>? DownloadsChanged;

    public DownloadService(
        IDataService dataService,
        ISourceService sourceService,
        ILogger<DownloadService> logger
    )
    {
        _dataService = dataService;
        _sourceService = sourceService;
        _logger = logger;

        _ = ConsumeQueueAsync();
    }

    public DownloadItem EnqueueChapterDownload(
        ContentKey comicKey,
        ContentKey chapterKey,
        string comicTitle,
        string chapterTitle,
        Func<CancellationToken, Task<Result<IReadOnlyList<ChapterPageModel>>>> pageProvider
    )
    {
        var existing = GetDownload(comicKey, chapterKey);
        if (existing != null)
            if (existing.CanRetry)
                _downloads.Remove(existing);
            else
                return existing;

        var item = new DownloadItem(comicKey, chapterKey, comicTitle, chapterTitle, pageProvider);
        _downloads.Add(item);
        _downloadQueue.Writer.TryWrite(item);

        NotifyDownloadsChanged();
        return item;
    }

    public IReadOnlyList<DownloadItem> GetAllDownloads() => _downloads.ToList();

    public DownloadItem? GetDownload(ContentKey comicKey, ContentKey chapterKey) =>
        _downloads.FirstOrDefault(d =>
            d.ComicKey.Equals(comicKey) && d.ChapterKey.Equals(chapterKey)
        );

    public void ClearFinishedDownloads()
    {
        var toRemove = _downloads.Where(d => d.IsFinished).ToList();

        if (toRemove.Count == 0)
            return;

        foreach (var item in toRemove)
            _downloads.Remove(item);

        NotifyDownloadsChanged();
    }

    public async Task DeleteComicDataAndDownloadsAsync(ContentKey comicKey)
    {
        var itemsToDelete = _downloads.Where(d => d.ComicKey.Equals(comicKey)).ToList();
        foreach (var item in itemsToDelete)
        {
            item.Cancel();
            _downloads.Remove(item);
        }

        NotifyDownloadsChanged();
        await _dataService.RemoveAllChapterPagesAsync(comicKey);
        await Task.Run(() => DeleteComicFolder(comicKey));
    }

    public async Task DeleteChapterDownloadAsync(ContentKey comicKey, ContentKey chapterKey)
    {
        var existing = GetDownload(comicKey, chapterKey);
        existing?.Cancel();

        await Task.Run(() => DeleteChapterFolder(comicKey, chapterKey));
        await _dataService.RemoveChapterPagesAsync(comicKey, chapterKey);

        if (existing != null)
        {
            _downloads.Remove(existing);
            NotifyDownloadsChanged();
        }
    }

    public async Task CleanupUnfavoriteComicsAsync(IReadOnlyList<ContentKey> unfavoriteComics)
    {
        foreach (var comicKey in unfavoriteComics)
            await Task.Run(() => DeleteComicFolder(comicKey));
    }

    public async Task<string?> DownloadComicCoverAsync(string? imageUrl, ContentKey comicKey)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return null;

        var destFolder = AppDataHelper.GetComicDataPath(comicKey);
        var ext = GetImageExtension(imageUrl);
        var destFile = Path.Combine(destFolder, "cover" + ext);

        try
        {
            return await DownloadImageAsync(imageUrl, destFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed downloading {Comic} cover", comicKey);
            return null;
        }
    }

    public async Task<string?> DownloadPluginLogoAsync(string? logoUrl, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(logoUrl))
            return null;

        var destFolder = AppDataHelper.GetPluginImagesPath();
        var ext = GetImageExtension(logoUrl);
        var destFile = Path.Combine(destFolder, sourceName + ext);

        try
        {
            return await DownloadImageAsync(logoUrl, destFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed downloading {Source} logo", sourceName);
            return null;
        }
    }

    public async Task<byte[]?> GetImageBytesAsync(string encodedUrl)
    {
        try
        {
            if (!ImageUriHelper.TryDecodeSourceImage(encodedUrl, out var sourceName, out var url))
                return null;
            return await _sourceService.GetImageBytesAsync(sourceName, url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed getting bytes for {encodedUrl}", encodedUrl);
            return null;
        }
    }

    private async Task ConsumeQueueAsync()
    {
        try
        {
            await foreach (var item in _downloadQueue.Reader.ReadAllAsync())
            {
                if (item.Status == DownloadStatus.Cancelled)
                {
                    NotifyDownloadsChanged();
                    continue;
                }

                await ProcessDownloadAsync(item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Download queue consumer terminated unexpectedly");
        }
    }

    private async Task ProcessDownloadAsync(DownloadItem item)
    {
        item.Status = DownloadStatus.Downloading;
        item.QueuePosition = 0;
        item.EstimatedTimeRemaining = null;
        NotifyDownloadsChanged();

        var downloadTimer = Stopwatch.StartNew();
        var destFolder = AppDataHelper.GetComicChapterDataPath(item.ComicKey, item.ChapterKey.Id);
        try
        {
            var pagesResult = await item.PageProvider(item.Cts.Token);
            if (!pagesResult.IsSuccess)
            {
                if (pagesResult.IsCancelled)
                    throw new OperationCanceledException();

                item.Status = DownloadStatus.Failed;
                _logger.LogWarning(
                    "Failed to get pages for chapter {ChapterKey}: {Error}",
                    item.ChapterKey,
                    pagesResult.Error
                );
                NotifyDownloadsChanged();
                return;
            }

            var pages = pagesResult.Value!;
            item.TotalPages = pages.Count;
            item.CompletedPages = 0;
            item.Progress = pages.Count == 0 ? 1 : 0;
            for (var index = 0; index < pages.Count; index++)
            {
                item.Cts.Token.ThrowIfCancellationRequested();
                var page = pages[index];
                var ext = GetImageExtension(page.ImageUrl);
                var destFile = Path.Combine(destFolder, $"{page.Number}{ext}");
                var pageTimer = Stopwatch.StartNew();
                await DownloadImageAsync(page.ImageUrl, destFile, item.Cts.Token);
                pageTimer.Stop();
                page.ImageUrl = destFile;

                item.CompletedPages = index + 1;
                item.Progress = (double)item.CompletedPages / Math.Max(1, pages.Count);
                _averageSecondsPerPage = ((_averageSecondsPerPage * _observedPages) + Math.Max(0.05, pageTimer.Elapsed.TotalSeconds)) / ++_observedPages;
                item.EstimatedTimeRemaining = TimeSpan.FromSeconds(
                    _averageSecondsPerPage * (pages.Count - item.CompletedPages)
                );
            }

            await _dataService.UpsertChapterPagesAsync(item.ComicKey, item.ChapterKey, pages);
            downloadTimer.Stop();
            _averageSecondsPerChapter = ((_averageSecondsPerChapter * _completedChapters) + Math.Max(1, downloadTimer.Elapsed.TotalSeconds)) / ++_completedChapters;
            item.Progress = 1;
            item.Status = DownloadStatus.Completed;
            item.EstimatedTimeRemaining = TimeSpan.Zero;
            _logger.LogInformation("Download completed for chapter {ChapterKey}", item.ChapterKey);
            NotifyDownloadsChanged();
        }
        catch (OperationCanceledException)
        {
            item.Status = DownloadStatus.Cancelled;
            item.EstimatedTimeRemaining = null;
            await Task.Run(() => DeleteChapterFolder(item.ComicKey, item.ChapterKey));
            _logger.LogInformation("Download cancelled for chapter {ChapterKey}", item.ChapterKey);
            NotifyDownloadsChanged();
        }
        catch (Exception ex)
        {
            item.Status = DownloadStatus.Failed;
            item.EstimatedTimeRemaining = null;
            await Task.Run(() => DeleteChapterFolder(item.ComicKey, item.ChapterKey));
            _logger.LogError(ex, "Download failed for chapter {ChapterKey}", item.ChapterKey);
            NotifyDownloadsChanged();
        }
    }

    private async Task<string> DownloadImageAsync(
        string encodedUrl,
        string destFile,
        CancellationToken ct = default
    )
    {
        if (!ImageUriHelper.TryDecodeSourceImage(encodedUrl, out var sourceName, out var url))
            throw new InvalidOperationException("Invalid Encoded Url");

        byte[]? imageBytes =
            await _sourceService.GetImageBytesAsync(sourceName, url, ct)
            ?? throw new InvalidOperationException("Failed to download image.");

        await File.WriteAllBytesAsync(destFile, imageBytes, ct);
        return destFile;
    }

    private void DeleteComicFolder(ContentKey comicKey)
    {
        try
        {
            AppDataHelper.DeleteComicDataPath(comicKey);
        }
        catch
        {
            _logger.LogError("Failed to delete data for comic {ComicKey}", comicKey);
        }
    }

    private void DeleteChapterFolder(ContentKey comicKey, ContentKey chapterKey)
    {
        try
        {
            AppDataHelper.DeleteComicChapterDataPath(comicKey, chapterKey.Id);
        }
        catch
        {
            _logger.LogError(
                "Failed to delete data for chapter {ChapterKey} of comic {ComicKey}",
                chapterKey,
                comicKey
            );
        }
    }

    private static string GetImageExtension(string url)
    {
        var ext = ImageUriHelper.GetFileExtension(url);
        if (string.IsNullOrWhiteSpace(ext))
            ext = ".jpg";
        return ext;
    }

    private void NotifyDownloadsChanged()
    {
        var queuePosition = 0;
        foreach (var item in _downloads.Where(download => download.Status == DownloadStatus.Queued))
        {
            item.QueuePosition = ++queuePosition;
            item.EstimatedTimeRemaining = TimeSpan.FromSeconds(_averageSecondsPerChapter * queuePosition);
        }
        DownloadsChanged?.Invoke(_downloads.ToList());
    }
}
