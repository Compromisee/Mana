using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yukari.Core.Models;
using Yukari.Core.Sources;
using Yukari.Core.Sources.Browser;
using Yukari.Core.Sources.Http;
using Yukari.Exceptions;
using Yukari.Helpers;
using Yukari.Models;
using Yukari.Services.Storage;

namespace Yukari.Services.Sources;

internal class SourceService : ISourceService
{
    private readonly IDataService _dataService;
    private readonly ILogger<SourceService> _logger;

    private readonly ISharedHttpClient _sharedHttpClient;
    private readonly IBrowserService _browserService;
    private readonly AidokuRuntimeService _aidokuRuntime;
    private readonly ConcurrentDictionary<string, byte> _aidokuLoadedSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LanguageModel[]> _aidokuLanguages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, JsonElement> _aidokuImageContexts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _aidokuImageChapterUrls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonElement> _aidokuChapterDetails = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonElement> _aidokuMangaDetails = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Type> _sourceTypeCache = new();
    private readonly ConcurrentDictionary<string, IComicSource> _sourceInstances = new();

    public SourceService(
        IDataService dataService,
        ILogger<SourceService> logger,
        HttpClient httpClient,
        IBrowserService browserService,
        AidokuRuntimeService aidokuRuntime
    )
    {
        _dataService = dataService;
        _logger = logger;
        _sharedHttpClient = new SharedHttpClient(httpClient);
        _browserService = browserService;
        _aidokuRuntime = aidokuRuntime;
    }

    public async Task LoadSourceAsync(ComicSourceModel comicSource)
    {
        if (_sourceInstances.ContainsKey(comicSource.Name) || _aidokuLoadedSources.ContainsKey(comicSource.Name))
            return;

        if (comicSource.IsAidoku)
        {
            try
            {
                var package = await File.ReadAllBytesAsync(comicSource.DllPath);
                var loaded = await _aidokuRuntime.LoadSourceAsync(comicSource.Name, package);
                if (loaded.TryGetProperty("manifest", out var manifest)
                    && manifest.TryGetProperty("info", out var info))
                {
                    var languageKeys = JsonStringArray(info, "languages");
                    _aidokuLanguages[comicSource.Name] = languageKeys.Length > 0
                        ? languageKeys.Select(key => new LanguageModel(key, key)).ToArray()
                        : [new LanguageModel("und", "Unknown")];
                }
                _aidokuLoadedSources.TryAdd(comicSource.Name, 0);
                _logger.LogInformation("Loaded Aidoku .aix source '{SourceName}'", comicSource.Name);
                return;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load Aidoku source {comicSource.Name}", ex);
            }
        }

        try
        {
            _logger.LogInformation(
                "Loading comic source '{SourceName}' instance from {DllPath}",
                comicSource.Name,
                comicSource.DllPath
            );

            if (!_sourceTypeCache.TryGetValue(comicSource.Name, out var type))
            {
                // AssemblyLoadContext.Default is intentionally used instead of a collectible context.
                // Plugins are small and rarely removed during a session — the complexity of a collectible
                // context is not justified. DLL removal is handled by deferred deletion at next startup.
                type = await GetSourceTypeFromAssemblyAsync(comicSource.DllPath);
                _sourceTypeCache[comicSource.Name] = type;
            }

            var instance = (IComicSource)Activator.CreateInstance(type)!;

            if (instance is IRequiresHttpClient requiresHttpClient)
                requiresHttpClient.SetHttpClient(_sharedHttpClient);
            if (instance is IRequiresBrowser requiresBrowser)
                requiresBrowser.SetBrowser(_browserService);

            if (!_sourceInstances.TryAdd(comicSource.Name, instance))
                await instance.DisposeAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load source {comicSource.Name}", ex);
        }
    }

    public async Task UnloadSourceAsync(string sourceName)
    {
        if (_sourceInstances.TryRemove(sourceName, out var source))
            await source.DisposeAsync();

        if (_aidokuLoadedSources.TryRemove(sourceName, out _))
        {
            _aidokuLanguages.TryRemove(sourceName, out _);
            try { await _aidokuRuntime.InvokeAsync("dispose", new { sourceKey = sourceName }); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not dispose Aidoku source {SourceName}", sourceName); }
        }
        _sourceTypeCache.TryRemove(sourceName, out _);
        var sourcePrefix = sourceName + "\u001f";
        RemoveAidokuEntries(_aidokuMangaDetails, sourcePrefix);
        RemoveAidokuEntries(_aidokuChapterDetails, sourcePrefix);
        RemoveAidokuEntries(_aidokuImageContexts, sourcePrefix);
        RemoveAidokuEntries(_aidokuImageChapterUrls, sourcePrefix);
    }

    private static void RemoveAidokuEntries<T>(ConcurrentDictionary<string, T> entries, string keyPrefix)
    {
        foreach (var key in entries.Keys)
            if (key.StartsWith(keyPrefix, StringComparison.Ordinal))
                entries.TryRemove(key, out _);
    }

    public async Task<IReadOnlyList<Filter>> GetFiltersAsync(
        string sourceName,
        CancellationToken ct = default
    )
    {
        await EnsureLoadedAsync(sourceName, ct);
        if (_aidokuLoadedSources.ContainsKey(sourceName))
        {
            var raw = await _aidokuRuntime.InvokeAsync("filters", new { sourceKey = sourceName }, ct);
            return MapAidokuFilters(raw);
        }
        return GetLoadedSource(sourceName).Filters;
    }

    public async Task<IReadOnlyList<ComicModel>> SearchComicsAsync(
        string sourceName,
        string query,
        IReadOnlyDictionary<string, IReadOnlyList<string>> filters,
        int page = 1,
        CancellationToken ct = default
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);

        await EnsureLoadedAsync(sourceName, ct);
        if (_aidokuLoadedSources.ContainsKey(sourceName))
        {
            var aidokuFilters = await BuildAidokuFilterValuesAsync(sourceName, filters, ct);
            var result = await _aidokuRuntime.InvokeAsync("search", new { sourceKey = sourceName, query, page, filters = aidokuFilters }, ct);
            return ReadAidokuEntries(result).Select(x => MapAidokuComic(x, sourceName)).ToList();
        }
        var comics = await GetLoadedSource(sourceName).SearchAsync(query, filters, page, ct);
        return comics.Select(c => MapToModel(c, sourceName)).ToList();
    }

    public async Task<IReadOnlyList<ComicModel>> GetTrendingComicsAsync(
        string sourceName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> filters,
        int page = 1,
        CancellationToken ct = default
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);

        await EnsureLoadedAsync(sourceName, ct);
        if (_aidokuLoadedSources.ContainsKey(sourceName))
        {
            var listings = await _aidokuRuntime.InvokeAsync("listings", new { sourceKey = sourceName }, ct);
            JsonElement result;
            if (listings.ValueKind == JsonValueKind.Array && listings.GetArrayLength() > 0)
                result = await _aidokuRuntime.InvokeAsync("listing", new { sourceKey = sourceName, listing = listings[0], page }, ct);
            else
            {
                var aidokuFilters = await BuildAidokuFilterValuesAsync(sourceName, filters, ct);
                result = await _aidokuRuntime.InvokeAsync("search", new { sourceKey = sourceName, query = (string?)null, page, filters = aidokuFilters }, ct);
            }
            return ReadAidokuEntries(result).Select(x => MapAidokuComic(x, sourceName)).ToList();
        }
        var comics = await GetLoadedSource(sourceName).GetTrendingAsync(filters, page, ct);
        return comics.Select(c => MapToModel(c, sourceName)).ToList();
    }

    public async Task<ComicModel?> GetComicDetailsAsync(
        string sourceName,
        string comicId,
        CancellationToken ct = default
    )
    {
        await EnsureLoadedAsync(sourceName, ct);
        if (_aidokuLoadedSources.ContainsKey(sourceName))
        {
            var manga = GetAidokuMangaArgument(sourceName, comicId);
            var raw = await _aidokuRuntime.InvokeAsync("details", new { sourceKey = sourceName, manga }, ct);
            if (raw.ValueKind == JsonValueKind.Object)
                _aidokuMangaDetails[AidokuMangaKey(sourceName, comicId)] = raw.Clone();
            return raw.ValueKind == JsonValueKind.Null ? null : MapAidokuComic(raw, sourceName);
        }
        var comic = await GetLoadedSource(sourceName).GetDetailsAsync(comicId, ct);
        if (comic == null)
            return null;

        return MapToModel(comic, sourceName);
    }

    public async Task<IReadOnlyList<ChapterModel>> GetAllChaptersAsync(
        string sourceName,
        string comicId,
        string language,
        CancellationToken ct = default
    )
    {
        await EnsureLoadedAsync(sourceName, ct);
        if (_aidokuLoadedSources.ContainsKey(sourceName))
        {
            var manga = await EnsureAidokuMangaArgumentAsync(sourceName, comicId, ct);
            var raw = await _aidokuRuntime.InvokeAsync("chapters", new { sourceKey = sourceName, manga }, ct);
            if (raw.ValueKind != JsonValueKind.Array)
                return [];

            var rawChapters = raw.EnumerateArray().ToArray();
            var aidokuChapters = rawChapters.Select(x => MapAidokuChapter(x, sourceName, language)).ToList();
            foreach (var item in rawChapters)
            {
                var id = JsonString(item, "key") ?? JsonString(item, "id");
                if (!string.IsNullOrWhiteSpace(id))
                    _aidokuChapterDetails[AidokuChapterKey(sourceName, comicId, id)] = item.Clone();
            }
            return aidokuChapters;
        }
        var chapters = await GetLoadedSource(sourceName).GetAllChaptersAsync(comicId, language, ct);
        return chapters
            .Select(c => new ChapterModel
            {
                Id = c.Id,
                Source = sourceName,
                Title = c.Title,
                Number = c.Number,
                Volume = c.Volume,
                Language = c.Language,
                Groups = c.Groups,
                LastUpdate = c.LastUpdate,
                Pages = c.Pages,
            })
            .ToList();
    }

    public async Task<IReadOnlyList<ChapterPageModel>> GetChapterPagesAsync(
        string sourceName,
        string comicId,
        string chapterId,
        CancellationToken ct = default,
        string? chapterUrl = null
    )
    {
        await EnsureLoadedAsync(sourceName, ct);
        if (_aidokuLoadedSources.ContainsKey(sourceName))
        {
            var manga = await EnsureAidokuMangaArgumentAsync(sourceName, comicId, ct);
            var chapterDetailsKey = AidokuChapterKey(sourceName, comicId, chapterId);
            _aidokuChapterDetails.TryGetValue(chapterDetailsKey, out var chapterDetails);

            // Older app databases did not save Aidoku chapter metadata. Reload the
            // source chapter list once so page providers receive its original URL and
            // number/title fields, not just the internal key.
            if (chapterDetails.ValueKind != JsonValueKind.Object)
            {
                var chapterList = await _aidokuRuntime.InvokeAsync("chapters", new
                {
                    sourceKey = sourceName,
                    manga
                }, ct);
                if (chapterList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in chapterList.EnumerateArray())
                    {
                        var itemId = JsonString(item, "key") ?? JsonString(item, "id");
                        if (string.IsNullOrWhiteSpace(itemId))
                            continue;
                        var itemKey = AidokuChapterKey(sourceName, comicId, itemId);
                        _aidokuChapterDetails[itemKey] = item.Clone();
                        if (string.Equals(itemId, chapterId, StringComparison.Ordinal))
                            chapterDetails = item.Clone();
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(chapterUrl))
                chapterUrl = JsonString(chapterDetails, "url");

            var chapterArguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (chapterDetails.ValueKind == JsonValueKind.Object)
                foreach (var property in chapterDetails.EnumerateObject())
                    chapterArguments[property.Name] = property.Value.Clone();
            chapterArguments["key"] = chapterId;
            chapterArguments["id"] = chapterId;
            chapterArguments["mangaId"] = comicId;
            if (!string.IsNullOrWhiteSpace(chapterUrl))
                chapterArguments["url"] = chapterUrl;

            var raw = await _aidokuRuntime.InvokeAsync("pages", new
            {
                sourceKey = sourceName,
                manga,
                chapter = chapterArguments
            }, ct);
            var result = new List<ChapterPageModel>();
            if (raw.ValueKind == JsonValueKind.Array)
            {
                foreach (var page in raw.EnumerateArray())
                {
                    var url = JsonString(page, "url");
                    if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(JsonString(page, "base64")))
                        url = "data:application/octet-stream;base64," + JsonString(page, "base64");
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out _)
                        && Uri.TryCreate(chapterUrl, UriKind.Absolute, out var chapterUri)
                        && Uri.TryCreate(chapterUri, url, out var resolvedPageUri))
                        url = resolvedPageUri.ToString();
                    if (page.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object)
                        _aidokuImageContexts[sourceName + "\u001f" + url] = context.Clone();
                    if (Uri.TryCreate(chapterUrl, UriKind.Absolute, out var sourceChapterUri)
                        && (string.Equals(sourceChapterUri.Host, "madaradex.org", StringComparison.OrdinalIgnoreCase)
                            || sourceChapterUri.Host.EndsWith(".madaradex.org", StringComparison.OrdinalIgnoreCase)))
                        _aidokuImageChapterUrls[sourceName + "\u001f" + url] = chapterUrl!;
                    result.Add(new ChapterPageModel
                    {
                        Number = JsonInt(page, "index") ?? result.Count,
                        ImageUrl = ImageUriHelper.EncodeSourceImage(sourceName, url)
                    });
                }
            }
            return result;
        }
        var pages = await GetLoadedSource(sourceName).GetChapterPagesAsync(comicId, chapterId, ct);
        return pages
            .Select(p => new ChapterPageModel
            {
                Number = p.Number,
                ImageUrl = ImageUriHelper.EncodeSourceImage(sourceName, p.ImageUrl),
            })
            .ToList();
    }

    public async Task<byte[]?> GetImageBytesAsync(
        string sourceName,
        string imageUrl,
        CancellationToken ct = default
    )
    {
        await EnsureLoadedAsync(sourceName, ct);
        if (_aidokuLoadedSources.ContainsKey(sourceName))
        {
            if (imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = imageUrl.IndexOf(',');
                if (comma >= 0 && imageUrl[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    return Convert.FromBase64String(imageUrl[(comma + 1)..]);
            }
            var imageKey = sourceName + "\u001f" + imageUrl;
            _aidokuImageContexts.TryGetValue(imageKey, out var context);
            _aidokuImageChapterUrls.TryGetValue(imageKey, out var chapterUrl);
            var raw = await _aidokuRuntime.InvokeAsync("image", new
            {
                sourceKey = sourceName,
                url = imageUrl,
                chapterUrl,
                context = context.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : context
            }, ct);
            var base64 = JsonString(raw, "bytesBase64");
            return string.IsNullOrWhiteSpace(base64) ? null : Convert.FromBase64String(base64);
        }
        return await GetLoadedSource(sourceName).GetImageBytesAsync(imageUrl, ct);
    }

    public async Task<ComicSourceModel> GetComicSourceModelFromAssemblyAsync(
        string dllPath,
        bool metadataOnly = false
    )
    {
        var type = await GetSourceTypeFromAssemblyAsync(dllPath, metadataOnly);
        var comicSourceMetadata =
            type.GetCustomAttribute<ComicSourceMetadataAttribute>()
            ?? throw new InvalidOperationException(
                $"{type.Name} is missing [ComicSourceMetadata] attribute."
            );

        if (!_sourceTypeCache.ContainsKey(comicSourceMetadata.Name) && !metadataOnly)
            _sourceTypeCache[comicSourceMetadata.Name] = type;

        _logger.LogInformation(
            "Plugin loaded from '{DllPath}' — Name: '{Name}', Version: {Version}",
            dllPath,
            comicSourceMetadata.Name,
            comicSourceMetadata.Version
        );
        return new ComicSourceModel
        {
            Name = comicSourceMetadata.Name,
            Version = comicSourceMetadata.Version,
            ReleasesPage = comicSourceMetadata.ReleasesPage,
            LogoUrl = TryEncodeImageUrl(comicSourceMetadata.Name, comicSourceMetadata.LogoUrl),
            Description = comicSourceMetadata.Description,
            DllPath = dllPath,
            IsEnabled = true,
        };
    }

    private string? TryEncodeImageUrl(string sourceName, string? imageUrl) =>
        imageUrl != null ? ImageUriHelper.EncodeSourceImage(sourceName, imageUrl) : null;

    private ComicModel MapToModel(Comic coreComic, string sourceName) =>
        new()
        {
            Id = coreComic.Id,
            Source = sourceName,
            ComicUrl = coreComic.ComicUrl,
            Title = coreComic.Title,
            Author = coreComic.Author,
            Description = coreComic.Description,
            Status = coreComic.Status,
            Tags = coreComic.Tags,
            Year = coreComic.Year,
            CoverImageUrl = TryEncodeImageUrl(sourceName, coreComic.CoverImageUrl),
            Langs = CreateLanguageModelArray(
                GetLoadedSource(sourceName).Languages,
                coreComic.Langs
            ),
        };

    private LanguageModel[] CreateLanguageModelArray(
        IReadOnlyDictionary<string, string> sourceLangs,
        string[] languageKeys
    )
    {
        return languageKeys
                ?.Select(key => new LanguageModel(
                    key,
                    sourceLangs.TryGetValue(key, out var displayName) ? displayName : key
                ))
                .ToArray()
            ?? [];
    }

    private static IReadOnlyList<JsonElement> ReadAidokuEntries(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return Array.Empty<JsonElement>();
        return entries.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private ComicModel MapAidokuComic(JsonElement manga, string sourceName)
    {
        var key = JsonString(manga, "key");
        var sourceId = JsonString(manga, "id");
        var id = key ?? sourceId ?? string.Empty;
        if (manga.ValueKind == JsonValueKind.Object)
        {
            if (!string.IsNullOrWhiteSpace(key))
                _aidokuMangaDetails[AidokuMangaKey(sourceName, key)] = manga.Clone();
            if (!string.IsNullOrWhiteSpace(sourceId))
                _aidokuMangaDetails[AidokuMangaKey(sourceName, sourceId)] = manga.Clone();
        }
        var authors = JsonStringArray(manga, "authors");
        var status = JsonInt(manga, "status") switch
        {
            1 => ComicStatus.Ongoing,
            2 => ComicStatus.Completed,
            3 => ComicStatus.Cancelled,
            4 => ComicStatus.Hiatus,
            _ => ComicStatus.Unknown
        };
        var cover = JsonString(manga, "cover");
        return new ComicModel
        {
            Id = id,
            Source = sourceName,
            ComicUrl = JsonString(manga, "url"),
            Title = JsonString(manga, "title") ?? id,
            Author = authors.Length == 0 ? null : string.Join(", ", authors),
            Description = JsonString(manga, "description"),
            Status = status,
            Tags = JsonStringArray(manga, "tags"),
            CoverImageUrl = string.IsNullOrWhiteSpace(cover) ? null : ImageUriHelper.EncodeSourceImage(sourceName, cover),
            Langs = _aidokuLanguages.TryGetValue(sourceName, out var languages)
                ? languages.ToArray()
                : Array.Empty<LanguageModel>()
        };
    }

    private static ChapterModel MapAidokuChapter(JsonElement chapter, string sourceName, string requestedLanguage)
    {
        var id = JsonString(chapter, "key") ?? JsonString(chapter, "id") ?? string.Empty;
        var number = JsonDouble(chapter, "chapterNumber");
        var volume = JsonDouble(chapter, "volumeNumber");
        DateOnly? lastUpdate = null;
        if (JsonDouble(chapter, "dateUploaded") is double timestamp)
        {
            try
            {
                var seconds = timestamp > 100_000_000_000 ? timestamp / 1000d : timestamp;
                lastUpdate = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime);
            }
            catch (ArgumentOutOfRangeException) { }
        }
        var scanlator = JsonString(chapter, "scanlator");
        return new ChapterModel
        {
            Id = id,
            Source = sourceName,
            Title = JsonString(chapter, "title"),
            Url = JsonString(chapter, "url"),
            Number = number?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            Volume = volume?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            // Aidoku often omits Chapter.lang for language-specific source packages. Mana's
            // chapter cache is keyed by the selected comic language, so preserve that key.
            Language = string.IsNullOrWhiteSpace(requestedLanguage)
                ? JsonString(chapter, "lang")
                : requestedLanguage,
            Groups = string.IsNullOrWhiteSpace(scanlator) ? Array.Empty<string>() : [scanlator],
            LastUpdate = lastUpdate
        };
    }

    private static IReadOnlyList<Filter> MapAidokuFilters(JsonElement rawFilters)
    {
        var result = new List<Filter>();
        if (rawFilters.ValueKind != JsonValueKind.Array)
            return result;

        AddFilters(rawFilters, result, Array.Empty<string>());
        return result;

        static void AddFilters(JsonElement array, List<Filter> output, IReadOnlyList<string> groupPath)
        {
            foreach (var item in array.EnumerateArray())
            {
                var type = JsonInt(item, "type") ?? -1;
                var name = JsonString(item, "name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (type == 5)
                {
                    if (item.TryGetProperty("filters", out var children) && children.ValueKind == JsonValueKind.Array)
                        AddFilters(children, output, groupPath.Append(name).ToArray());
                    continue;
                }

                if (type is not (0 or 1 or 2 or 3 or 4 or 6 or 7))
                    continue;

                var defaultAscending = true;
                var defaultIndex = JsonInt(item, "default");
                if (type == 3 && item.TryGetProperty("default", out var sortDefault))
                {
                    defaultIndex = JsonInt(sortDefault, "index") ?? 0;
                    if (sortDefault.ValueKind == JsonValueKind.Object
                        && sortDefault.TryGetProperty("ascending", out var ascending)
                        && ascending.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        defaultAscending = ascending.GetBoolean();
                }

                var key = AidokuFilterKey.Create(type, name, groupPath, defaultAscending);
                var displayName = groupPath.Count == 0 ? name : $"{string.Join(" / ", groupPath)} / {name}";
                if (type is 0 or 1 or 7)
                {
                    output.Add(new Filter(key, displayName, Array.Empty<FilterOption>(), false));
                    continue;
                }

                var labels = item.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
                    ? options.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").ToArray()
                    : Array.Empty<string>();
                var ids = item.TryGetProperty("ids", out var idsJson) && idsJson.ValueKind == JsonValueKind.Array
                    ? idsJson.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").ToArray()
                    : Array.Empty<string>();
                var optionModels = new List<FilterOption>();

                if (type == 4)
                {
                    var isOnByDefault = JsonBool(item, "default");
                    optionModels.Add(new FilterOption("true", "Enabled", isOnByDefault));
                    optionModels.Add(new FilterOption("false", "Disabled", !isOnByDefault));
                }
                else if (type == 6)
                {
                    var defaultStates = new Dictionary<int, int>();
                    if (item.TryGetProperty("default", out var genres) && genres.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var genre in genres.EnumerateArray())
                            if (JsonInt(genre, "index") is int index)
                                defaultStates[index] = JsonInt(genre, "state") ?? 0;
                    }
                    for (var i = 0; i < labels.Length; i++)
                    {
                        var id = i < ids.Length && !string.IsNullOrWhiteSpace(ids[i]) ? ids[i] : labels[i];
                        defaultStates.TryGetValue(i, out var state);
                        optionModels.Add(new FilterOption($"include:{id}", $"Include: {labels[i]}", state > 0));
                        optionModels.Add(new FilterOption($"exclude:{id}", $"Exclude: {labels[i]}", state < 0));
                    }
                }
                else
                {
                    for (var i = 0; i < labels.Length; i++)
                    {
                        var id = i < ids.Length && !string.IsNullOrWhiteSpace(ids[i]) ? ids[i] : labels[i];
                        optionModels.Add(new FilterOption(id, labels[i], i == defaultIndex));
                    }
                }

                output.Add(new Filter(key, displayName, optionModels, type == 6));
            }
        }
    }

    private async Task<object[]> BuildAidokuFilterValuesAsync(
        string sourceName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> selected,
        CancellationToken ct
    )
    {
        var rawFilters = await _aidokuRuntime.InvokeAsync("filters", new { sourceKey = sourceName }, ct);
        if (rawFilters.ValueKind != JsonValueKind.Array)
            return Array.Empty<object>();

        return BuildValues(rawFilters, selected, Array.Empty<string>());

        static object[] BuildValues(
            JsonElement array,
            IReadOnlyDictionary<string, IReadOnlyList<string>> selectedValues,
            IReadOnlyList<string> groupPath
        )
        {
            var output = new List<object>();
            foreach (var filter in array.EnumerateArray())
            {
                var type = JsonInt(filter, "type") ?? -1;
                var name = JsonString(filter, "name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (type == 5)
                {
                    if (filter.TryGetProperty("filters", out var nested) && nested.ValueKind == JsonValueKind.Array)
                        output.Add(new { type, name, filters = BuildValues(nested, selectedValues, groupPath.Append(name).ToArray()) });
                    continue;
                }
                if (type is not (0 or 1 or 2 or 3 or 4 or 6 or 7))
                    continue;

                var defaultAscending = true;
                if (type == 3 && filter.TryGetProperty("default", out var sortDefault)
                    && sortDefault.ValueKind == JsonValueKind.Object
                    && sortDefault.TryGetProperty("ascending", out var ascending)
                    && ascending.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    defaultAscending = ascending.GetBoolean();
                var key = AidokuFilterKey.Create(type, name, groupPath, defaultAscending);
                selectedValues.TryGetValue(key, out var selectedItems);

                var options = filter.TryGetProperty("options", out var optionJson) && optionJson.ValueKind == JsonValueKind.Array
                    ? optionJson.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").ToArray()
                    : Array.Empty<string>();
                var ids = filter.TryGetProperty("ids", out var idsJson) && idsJson.ValueKind == JsonValueKind.Array
                    ? idsJson.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").ToArray()
                    : Array.Empty<string>();

                if (type is 0 or 1 or 7)
                {
                    var text = selectedItems?.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(text))
                        output.Add(new { type, name, value = text });
                }
                else if (type == 2)
                {
                    if (selectedItems is { Count: 0 })
                        continue;
                    var value = selectedItems?.FirstOrDefault();
                    if (selectedItems is null && JsonInt(filter, "default") is int defaultIndex && defaultIndex >= 0 && defaultIndex < options.Length)
                        value = defaultIndex < ids.Length ? ids[defaultIndex] : options[defaultIndex];
                    output.Add(new { type, name, value = value ?? "" });
                }
                else if (type == 3)
                {
                    var directionToken = selectedItems?.FirstOrDefault(value => AidokuFilterKey.TryGetSortDirection(value, out _));
                    var sortValues = selectedItems?.Where(value => !AidokuFilterKey.TryGetSortDirection(value, out _)).ToArray() ?? Array.Empty<string>();
                    var index = 0;
                    if (sortValues.FirstOrDefault() is string chosen)
                    {
                        var idIndex = Array.IndexOf(ids, chosen);
                        var optionIndex = Array.IndexOf(options, chosen);
                        if (idIndex >= 0) index = idIndex;
                        else if (optionIndex >= 0) index = optionIndex;
                        else if (int.TryParse(chosen, out var parsed)) index = parsed;
                    }
                    else if (filter.TryGetProperty("default", out var defaultSort))
                        index = JsonInt(defaultSort, "index") ?? 0;
                    var ascendingValue = directionToken is not null
                        && AidokuFilterKey.TryGetSortDirection(directionToken, out var selectedAscending)
                            ? selectedAscending
                            : defaultAscending;
                    output.Add(new { type, name, value = new { index, ascending = ascendingValue } });
                }
                else if (type == 4)
                {
                    if (selectedItems is { Count: 0 })
                        continue;
                    var value = selectedItems?.FirstOrDefault() is string check && bool.TryParse(check, out var parsed)
                        ? parsed
                        : JsonBool(filter, "default");
                    output.Add(new { type, name, value });
                }
                else
                {
                    var included = new List<string>();
                    var excluded = new List<string>();
                    if (selectedItems is not null)
                    {
                        foreach (var selectedItem in selectedItems)
                        {
                            if (selectedItem.StartsWith("include:", StringComparison.Ordinal))
                                included.Add(selectedItem[8..]);
                            else if (selectedItem.StartsWith("exclude:", StringComparison.Ordinal))
                                excluded.Add(selectedItem[8..]);
                        }
                    }
                    else if (filter.TryGetProperty("default", out var defaults) && defaults.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in defaults.EnumerateArray())
                        {
                            var state = JsonInt(item, "state") ?? 0;
                            var genreIndex = JsonInt(item, "index") ?? -1;
                            if (state == 0 || genreIndex < 0 || genreIndex >= options.Length)
                                continue;
                            var id = genreIndex < ids.Length ? ids[genreIndex] : options[genreIndex];
                            if (state > 0) included.Add(id);
                            else excluded.Add(id);
                        }
                    }
                    output.Add(new { type, name, value = new { included, excluded } });
                }
            }
            return output.ToArray();
        }
    }

    private static string AidokuMangaKey(string sourceName, string mangaId) =>
        sourceName + "\u001f" + mangaId;

    private object GetAidokuMangaArgument(string sourceName, string mangaId)
    {
        if (!_aidokuMangaDetails.TryGetValue(AidokuMangaKey(sourceName, mangaId), out var manga)
            || manga.ValueKind != JsonValueKind.Object)
            return new { key = mangaId, id = mangaId };

        var argument = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in manga.EnumerateObject())
            argument[property.Name] = property.Value.Clone();
        if (string.IsNullOrWhiteSpace(JsonString(manga, "key")))
            argument["key"] = mangaId;
        if (string.IsNullOrWhiteSpace(JsonString(manga, "id")))
            argument["id"] = mangaId;
        return argument;
    }

    private async Task<object> EnsureAidokuMangaArgumentAsync(
        string sourceName,
        string mangaId,
        CancellationToken ct
    )
    {
        if (!_aidokuMangaDetails.ContainsKey(AidokuMangaKey(sourceName, mangaId)))
        {
            try
            {
                var raw = await _aidokuRuntime.InvokeAsync("details", new
                {
                    sourceKey = sourceName,
                    manga = new { key = mangaId, id = mangaId }
                }, ct);
                if (raw.ValueKind == JsonValueKind.Object)
                {
                    _aidokuMangaDetails[AidokuMangaKey(sourceName, mangaId)] = raw.Clone();
                    MapAidokuComic(raw, sourceName);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not hydrate Aidoku manga metadata for {Source}/{MangaId}", sourceName, mangaId);
            }
        }
        return GetAidokuMangaArgument(sourceName, mangaId);
    }

    private static string AidokuChapterKey(string sourceName, string comicId, string chapterId) =>
        sourceName + "\u001f" + comicId + "\u001f" + chapterId;

    private static string? JsonString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? JsonInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number) ? number
        : element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out number) ? number
        : null;

    private static double? JsonDouble(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;

    private static bool JsonBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string[] JsonStringArray(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToArray()
            : Array.Empty<string>();

    private async Task EnsureLoadedAsync(string sourceName, CancellationToken ct = default)
    {
        if (_sourceInstances.ContainsKey(sourceName) || _aidokuLoadedSources.ContainsKey(sourceName))
            return;

        var comicSourceModel =
            await _dataService.GetComicSourceDetailsAsync(sourceName, ct)
            ?? throw new InvalidOperationException(
                $"The source '{sourceName}' is not registered in the database."
            );

        if (!comicSourceModel.IsEnabled)
            throw new ComicSourceDisabledException(sourceName);

        await LoadSourceAsync(comicSourceModel);
    }

    private IComicSource GetLoadedSource(string sourceName)
    {
        if (!_sourceInstances.TryGetValue(sourceName, out var source))
            throw new InvalidOperationException($"Source '{sourceName}' is not loaded.");
        return source;
    }

    private async Task<Type> GetSourceTypeFromAssemblyAsync(
        string pluginPath,
        bool collectibleContext = false
    )
    {
        if (!File.Exists(pluginPath))
            throw new FileNotFoundException();

        var context = collectibleContext
            ? new AssemblyLoadContext("CollectibleContext", isCollectible: true)
            : AssemblyLoadContext.Default;

        try
        {
            return await Task.Run(() =>
            {
                Assembly pluginAssembly = context.LoadFromAssemblyPath(pluginPath);
                var comicSourceType =
                    pluginAssembly
                        .GetTypes()
                        .FirstOrDefault(t =>
                            typeof(IComicSource).IsAssignableFrom(t)
                            && !t.IsInterface
                            && !t.IsAbstract
                        )
                    ?? throw new InvalidOperationException(
                        $"{pluginPath} does not implement IComicSource."
                    );

                if (!collectibleContext)
                    ValidateCoreCompatibility(Path.GetFileName(pluginPath), pluginAssembly);

                return comicSourceType;
            });
        }
        catch (Exception ex) when (ex is ReflectionTypeLoadException or TypeLoadException)
        {
            throw new PluginVersionMismatchException(Path.GetFileName(pluginPath));
        }
    }

    private void ValidateCoreCompatibility(string pluginPath, Assembly pluginAssembly)
    {
        var coreReference = pluginAssembly
            .GetReferencedAssemblies()
            .FirstOrDefault(a => a.Name == "Yukari.Core");

        if (coreReference == null)
            return;

        var requiredVersion = coreReference.Version;
        if (requiredVersion > AppInfoHelper.CoreVersion)
            throw new PluginVersionMismatchException(pluginPath);

        if (requiredVersion < AppInfoHelper.CoreVersion)
        {
            _logger.LogWarning(
                "Plugin '{pluginPath}' targets an older Core version ({RequiredCore}) and may lack features.",
                pluginPath,
                requiredVersion?.ToString(3)
            );
        }
    }
}
