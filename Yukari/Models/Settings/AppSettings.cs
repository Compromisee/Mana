using System;
using System.Collections.Generic;
using System.Text.Json;
using Yukari.Enums;

namespace Yukari.Models.Settings;

public class AppSettings
{
    public WindowState MainWindowState { get; set; } = new();
    public bool NavigationPaneIsOpen { get; set; } = true;
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public FavoritesSortBy FavoritesSortBy { get; set; } = FavoritesSortBy.Alphabetical;
    public SortDirection FavoritesSortDirection { get; set; } = SortDirection.Ascending;

    public bool ReversedChaptersOrder { get; set; } = false;
    public bool HideDuplicateChapters { get; set; } = false;

    public UpdateCheckSchedule AutoComicUpdates { get; set; } = UpdateCheckSchedule.Never;
    public int AutoComicUpdateRecentCount { get; set; } = 0;
    public DateTime? LastAutoComicUpdate { get; set; }

    public bool AutoFullscreen { get; set; } = false;
    public ReadingMode ReadingMode { get; set; } = ReadingMode.RightToLeft;
    public ScalingMode ScalingMode { get; set; } = ScalingMode.FitScreen;
    public double ReaderBrightness { get; set; } = 0;
    public double ReaderContrast { get; set; } = 0;

    public string? DefaultComicSourceName { get; set; }

    // Local library additions; paths stay on-device and are never synchronized.
    public string[] LocalLibraryRoots { get; set; } = Array.Empty<string>();
    public bool ShowLocalComicInfoBeforeReading { get; set; }
    public bool LockLocalLibrary { get; set; }
    public bool CompactListMode { get; set; }
    public string? LocalLibraryPinHash { get; set; }

    public bool AidokuRequestLoggingEnabled { get; set; } = true;

    public Dictionary<string, Dictionary<string, JsonElement>> AidokuSourceSettings { get; set; } = new();

    public string[] AidokuSourceRepositoryUrls { get; set; } =
    [
        "https://amqx.github.io/sources/index.min.json",
        "https://aidoku-community.github.io/sources/index.min.json",
        "https://kunihir0.github.io/aidoku-sources/index.min.json",
        "https://poppingmangosources.github.io/popmango-aidoku-sources/index.min.json",
        "https://smexhy.github.io/yomu-aidoku-sources/index.min.json"
    ];
}
