using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Yukari.Helpers;
using Yukari.Services.Storage;

namespace Yukari.Services.UI;

internal class ImageCacheService : IImageCacheService
{
    private const int MaxCacheEntries = 64;

    private readonly IDownloadService _downloadService;
    private readonly ILogger<ImageCacheService> _logger;
    private static readonly HttpClient RemoteImageClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const int MaxRemoteImageBytes = 8 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, ImageSource> _cache = new();
    private readonly ConcurrentDictionary<string, Task> _loadTasks = new();
    private readonly ConcurrentQueue<string> _insertionQueue = new();

    public ImageCacheService(IDownloadService downloadService, ILogger<ImageCacheService> logger)
    {
        _downloadService = downloadService;
        _logger = logger;
    }

    public ImageSource GetImageSource(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null!;

        if (_cache.TryGetValue(url, out var cached))
            return cached;

        ImageSource imageSource = IsSvg(url) ? new SvgImageSource() : new BitmapImage();
        AddToCache(url, imageSource);

        _loadTasks.GetOrAdd(url, u => LoadAsync(u, imageSource));

        return imageSource;
    }

    public bool TryGetCached(string? url, out ImageSource? source)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            source = null;
            return false;
        }
        return _cache.TryGetValue(url, out source);
    }

    public async Task<(int Width, int Height)?> TryGetDimensionsAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var imageSource = GetImageSource(url);

        if (_loadTasks.TryGetValue(url, out var loadTask))
        {
            try
            {
                await loadTask;
            }
            catch
            {
                return null;
            }
        }

        return imageSource is BitmapImage { PixelWidth: > 0 } bmp
            ? (bmp.PixelWidth, bmp.PixelHeight)
            : null;
    }

    public async Task<string?> SaveImageAsync(string? url, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(filePath))
            return null;

        byte[]? bytes = await GetImageBytesAsync(url);
        if (bytes == null)
            return null;

        await File.WriteAllBytesAsync(filePath, bytes);
        return filePath;
    }

    public async Task<byte[]?> GetImageBytesAsync(string url)
    {
        if (IsZipEntry(url))
            return await ReadZipEntryBytesAsync(url);
        if (IsLocalPath(url))
            return await ReadLocalFileBytesAsync(url);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return await ReadRemoteImageBytesAsync(uri);
        return await _downloadService.GetImageBytesAsync(url);
    }

    private static async Task<byte[]?> ReadRemoteImageBytesAsync(Uri uri)
    {
        try
        {
            using var response = await RemoteImageClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode
                || (response.Content.Headers.ContentLength is long length && length > MaxRemoteImageBytes))
                return null;

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return null;

            await using var input = await response.Content.ReadAsStreamAsync();
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                if (output.Length + read > MaxRemoteImageBytes)
                    return null;
                output.Write(buffer, 0, read);
            }
            return output.Length == 0 ? null : output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private void AddToCache(string key, ImageSource imageSource)
    {
        _cache[key] = imageSource;
        _insertionQueue.Enqueue(key);

        while (
            _insertionQueue.Count > MaxCacheEntries && _insertionQueue.TryDequeue(out var oldest)
        )
        {
            _cache.TryRemove(oldest, out _);
            _loadTasks.TryRemove(oldest, out _);
        }
    }

    private async Task LoadAsync(string url, ImageSource imageSource)
    {
        try
        {
            byte[]? bytes = await GetImageBytesAsync(url);
            await ApplyBytesToImageSourceAsync(url, imageSource, bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode or load image {ImageUrl}", url);
            _cache.TryRemove(url, out _);
            _loadTasks.TryRemove(url, out _);
            MarkImageSourceAsFailed(imageSource);
        }
    }

    private static async Task<byte[]?> ReadLocalFileBytesAsync(string path)
    {
        try
        {
            return await File.ReadAllBytesAsync(path);
        }
        catch
        {
            return null;
        }
    }

    private static Task<byte[]?> ReadZipEntryBytesAsync(string url) =>
        Task.Run(() =>
        {
            try
            {
                ImageUriHelper.TryDecodeZipEntry(url, out var zipPath, out var entryName);
                using var archive = ZipFile.OpenRead(zipPath);
                var entry = archive.GetEntry(entryName);
                if (entry == null)
                    return null;

                using var entryStream = entry.Open();
                using var memStream = new MemoryStream();
                entryStream.CopyTo(memStream);
                return memStream.ToArray();
            }
            catch
            {
                return null;
            }
        });

    private async Task ApplyBytesToImageSourceAsync(
        string url,
        ImageSource imageSource,
        byte[]? bytes
    )
    {
        if (bytes == null)
        {
            // The purpose of assigning a value that refers to a file that does not exist is to trigger the ImageSource error event.
            MarkImageSourceAsFailed(imageSource);

            _cache.TryRemove(url, out _);
            _loadTasks.TryRemove(url, out _);
            return;
        }

        using var stream = new MemoryStream(bytes);
        var randomAccessStream = stream.AsRandomAccessStream();

        switch (imageSource)
        {
            case BitmapImage bitmap:
                await bitmap.SetSourceAsync(randomAccessStream);
                break;
            case SvgImageSource svg:
                await svg.SetSourceAsync(randomAccessStream);
                break;
            default:
                _cache.TryRemove(url, out _);
                _loadTasks.TryRemove(url, out _);
                break;
        }
    }

    private static void MarkImageSourceAsFailed(ImageSource imageSource)
    {
        // Force ImageFailed so the reader's retry/error UI is used consistently.
        var errorUri = new Uri("ms-appx:///Assets/___nonexistent___");
        if (imageSource is BitmapImage bitmap)
            bitmap.UriSource = errorUri;
        else if (imageSource is SvgImageSource svg)
            svg.UriSource = errorUri;
    }

    private static bool IsSvg(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsZipEntry(string url) => url.StartsWith(ImageUriHelper.ZipScheme);

    private static bool IsLocalPath(string path) =>
        Path.IsPathRooted(path) && !path.StartsWith("http", StringComparison.OrdinalIgnoreCase);
}
