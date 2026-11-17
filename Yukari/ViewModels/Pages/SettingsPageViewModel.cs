using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Yukari.Enums;
using Yukari.Helpers;
using Yukari.Messages;
using Yukari.Models;
using Yukari.Models.Settings;
using Yukari.Services.Comics;
using Yukari.Services.Settings;
using Yukari.Services.Sources;
using Yukari.Services.UI;
using Yukari.ViewModels.Components;

namespace Yukari.ViewModels.Pages;

public partial class SettingsPageViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IComicService _comicService;
    private readonly ISourceManager _sourceManager;
    private readonly INotificationService _notificationService;
    private readonly IDialogService _dialogService;
    private readonly IMessenger _messenger;
    private readonly ILocalizationService _localizationService;

    private bool _isInitializing = true;

    public string ManaVersion { get; } = AppInfoHelper.Version;
    public string CoreVersion { get; } = AppInfoHelper.CoreVersionString;

    public ThemeMode[] AvailableThemeModes { get; } = Enum.GetValues<ThemeMode>();
    public UpdateCheckSchedule[] AvailableUpdateCheckSchedules { get; } =
        Enum.GetValues<UpdateCheckSchedule>();
    public ReadingMode[] AvailableReadingModes { get; } = Enum.GetValues<ReadingMode>();
    public ScalingMode[] AvailableScalingModes { get; } = Enum.GetValues<ScalingMode>();

    [ObservableProperty]
    public partial ThemeMode SelectedThemeMode { get; set; }

    [ObservableProperty]
    public partial UpdateCheckSchedule SelectedUpdateCheckSchedule { get; set; }

    [ObservableProperty]
    public partial int AutoComicUpdateRecentCount { get; set; }

    [ObservableProperty]
    public partial bool IsAutoFullscreen { get; set; }

    [ObservableProperty]
    public partial ReadingMode SelectedReadingMode { get; set; }

    [ObservableProperty]
    public partial ScalingMode SelectedScalingMode { get; set; }

    [ObservableProperty]
    public partial bool ShowLocalComicInfoBeforeReading { get; set; }

    [ObservableProperty]
    public partial bool LockLocalLibrary { get; set; }

    [ObservableProperty]
    public partial bool CompactListMode { get; set; }

    [ObservableProperty]
    public partial bool AidokuRequestLoggingEnabled { get; set; }

    public string AidokuLogsFolder => AppDataHelper.GetLogsPath();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedLocalLibraryRoot))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedLocalLibraryRootCommand))]
    public partial string? SelectedLocalLibraryRoot { get; set; }

    public bool HasSelectedLocalLibraryRoot => !string.IsNullOrWhiteSpace(SelectedLocalLibraryRoot);

    public ObservableCollection<string> LocalLibraryRoots { get; } = [];

    [ObservableProperty]
    public partial ComicSourceModel? DefaultComicSource { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyComicSourceEnabled))]
    public partial List<ComicSourceModel> AvailableComicSources { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComicSourcesEmpty))]
    public partial List<ComicSourceItemViewModel> ComicSourceItems { get; set; } = new();

    public bool IsAnyComicSourceEnabled => AvailableComicSources.Count > 0;
    public bool IsComicSourcesEmpty => ComicSourceItems.Count == 0;

    public SettingsPageViewModel(
        ISettingsService settingsService,
        IComicService comicService,
        ISourceManager sourceManager,
        INotificationService notificationService,
        IDialogService dialogService,
        IMessenger messenger,
        ILocalizationService localizationService
    )
    {
        _settingsService = settingsService;
        _comicService = comicService;
        _sourceManager = sourceManager;
        _notificationService = notificationService;
        _dialogService = dialogService;
        _messenger = messenger;
        _localizationService = localizationService;

        _ = LoadSettingsAsync();
    }

    public async Task OnNavigatedFromAsync() => await _settingsService.SaveAsync();

    private async Task LoadSettingsAsync()
    {
        SelectedThemeMode = _settingsService.Current.Theme;
        SelectedUpdateCheckSchedule = _settingsService.Current.AutoComicUpdates;
        AutoComicUpdateRecentCount = _settingsService.Current.AutoComicUpdateRecentCount;
        IsAutoFullscreen = _settingsService.Current.AutoFullscreen;
        SelectedReadingMode = _settingsService.Current.ReadingMode;
        SelectedScalingMode = _settingsService.Current.ScalingMode;
        ShowLocalComicInfoBeforeReading = _settingsService.Current.ShowLocalComicInfoBeforeReading;
        LockLocalLibrary = _settingsService.Current.LockLocalLibrary;
        CompactListMode = _settingsService.Current.CompactListMode;
        AidokuRequestLoggingEnabled = _settingsService.Current.AidokuRequestLoggingEnabled;
        LocalLibraryRoots.Clear();
        foreach (var root in _settingsService.Current.LocalLibraryRoots ?? Array.Empty<string>())
            LocalLibraryRoots.Add(root);
        SelectedLocalLibraryRoot = LocalLibraryRoots.FirstOrDefault();

        await LoadComicSourcesAsync();
        if (AvailableComicSources.Count > 0)
        {
            DefaultComicSource =
                AvailableComicSources.FirstOrDefault(s =>
                    s.Name == _settingsService.Current.DefaultComicSourceName
                ) ?? AvailableComicSources.First();
        }

        _isInitializing = false;
    }

    [RelayCommand]
    private async Task AddLocalLibraryRootAsync()
    {
        var folder = await _dialogService.OpenFolderPickerAsync();
        if (string.IsNullOrWhiteSpace(folder))
            return;
        var roots = (_settingsService.Current.LocalLibraryRoots ?? Array.Empty<string>()).ToList();
        if (roots.Contains(folder, StringComparer.OrdinalIgnoreCase))
            return;
        roots.Add(folder);
        _settingsService.Set(s => s.LocalLibraryRoots, roots.ToArray());
        LocalLibraryRoots.Add(folder);
        SelectedLocalLibraryRoot = folder;
        await _settingsService.SaveAsync();
    }

    public async Task RemoveLocalLibraryRootAsync(string folder)
    {
        var roots = (_settingsService.Current.LocalLibraryRoots ?? Array.Empty<string>())
            .Where(root => !root.Equals(folder, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _settingsService.Set(s => s.LocalLibraryRoots, roots);
        LocalLibraryRoots.Remove(folder);
        if (string.Equals(SelectedLocalLibraryRoot, folder, StringComparison.OrdinalIgnoreCase))
            SelectedLocalLibraryRoot = LocalLibraryRoots.FirstOrDefault();
        await _settingsService.SaveAsync();
    }

    private bool CanRemoveSelectedLocalLibraryRoot() => !string.IsNullOrWhiteSpace(SelectedLocalLibraryRoot);

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedLocalLibraryRoot))]
    private async Task RemoveSelectedLocalLibraryRootAsync()
    {
        if (!string.IsNullOrWhiteSpace(SelectedLocalLibraryRoot))
            await RemoveLocalLibraryRootAsync(SelectedLocalLibraryRoot);
    }

    [RelayCommand]
    private async Task AddComicSourceAsync()
    {
        var pluginPath = await _dialogService.OpenFilePickerAsync([".dll"]);
        if (pluginPath == null)
            return;

        var result = await _sourceManager.UpsertComicSourceAsync(pluginPath);
        if (result.Kind == ResultKind.PendingRestart)
        {
            _notificationService.ShowWarning(
                _localizationService.GetString("WarningUpdatingSourceInUseRestartRequest")
            );
            return;
        }
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        await LoadComicSourcesAsync();

        DefaultComicSource =
            AvailableComicSources.FirstOrDefault(s =>
                s.Name == _settingsService.Current.DefaultComicSourceName
            ) ?? AvailableComicSources.First();

        _notificationService.ShowSuccess(
            _localizationService.GetString("SuccessComicSourceAddedUpdated")
        );
        _messenger.Send(new ComicSourcesUpdatedMessage());
    }

    [RelayCommand]
    private async Task RemoveComicSourceAsync(ComicSourceItemViewModel comicSourceItem)
    {
        var result = await _sourceManager.RemoveComicSourceAsync(comicSourceItem.ComicSource.Name);

        if (result.Kind == ResultKind.PendingRestart)
        {
            _notificationService.ShowWarning(
                _localizationService.GetString("WarningRemovingSourceInUseRestartRequest")
            );
            return;
        }
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        await LoadComicSourcesAsync();
        DefaultComicSource =
            AvailableComicSources.FirstOrDefault(s =>
                s.Name == _settingsService.Current.DefaultComicSourceName
            ) ?? AvailableComicSources.FirstOrDefault();

        _messenger.Send(new ComicSourcesUpdatedMessage());
    }

    [RelayCommand]
    private async Task OnComicSourceIsEnabledChanged((string Name, bool IsEnabled) args)
    {
        var result = await _sourceManager.UpdateComicSourceIsEnabledAsync(
            args.Name,
            args.IsEnabled
        );
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        AvailableComicSources = ComicSourceItems
            .Where(cs => cs.IsEnabled)
            .Select(cs => cs.ComicSource)
            .ToList();

        DefaultComicSource =
            AvailableComicSources.FirstOrDefault(s =>
                s.Name == _settingsService.Current.DefaultComicSourceName
            ) ?? AvailableComicSources.FirstOrDefault();

        _messenger.Send(new ComicSourcesUpdatedMessage());
    }

    [RelayCommand]
    private async Task CleanUpStorageAsync()
    {
        var result = await _comicService.CleanupUnfavoriteComicsDataAsync();
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        _notificationService.ShowSuccess(_localizationService.GetString("SuccessStorageCleanedUp"));
    }

    private async Task LoadComicSourcesAsync()
    {
        var result = await _sourceManager.GetComicSourcesAsync();
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        var comicSources = result.Value!;

        AvailableComicSources = comicSources.Where(cs => cs.IsEnabled).ToList();
        ComicSourceItems = comicSources
            .Select(cs => new ComicSourceItemViewModel(
                cs,
                RemoveComicSourceCommand,
                ComicSourceIsEnabledChangedCommand
            ))
            .ToList();
    }

    private void ApplySetting<T>(Expression<Func<AppSettings, T>> selector, T value)
    {
        if (_isInitializing)
            return;
        _settingsService.Set(selector, value);
    }

    partial void OnSelectedThemeModeChanged(ThemeMode value) => ApplySetting(s => s.Theme, value);

    partial void OnSelectedUpdateCheckScheduleChanged(UpdateCheckSchedule value) =>
        ApplySetting(s => s.AutoComicUpdates, value);

    partial void OnAutoComicUpdateRecentCountChanged(int value) =>
        ApplySetting(s => s.AutoComicUpdateRecentCount, value);

    partial void OnIsAutoFullscreenChanged(bool value) =>
        ApplySetting(s => s.AutoFullscreen, value);

    partial void OnSelectedReadingModeChanged(ReadingMode value) =>
        ApplySetting(s => s.ReadingMode, value);

    partial void OnSelectedScalingModeChanged(ScalingMode value) =>
        ApplySetting(s => s.ScalingMode, value);

    partial void OnShowLocalComicInfoBeforeReadingChanged(bool value) =>
        ApplySetting(s => s.ShowLocalComicInfoBeforeReading, value);

    partial void OnLockLocalLibraryChanged(bool value) => ApplySetting(s => s.LockLocalLibrary, value);

    partial void OnCompactListModeChanged(bool value) => ApplySetting(s => s.CompactListMode, value);

    partial void OnAidokuRequestLoggingEnabledChanged(bool value) =>
        ApplySetting(s => s.AidokuRequestLoggingEnabled, value);

    partial void OnDefaultComicSourceChanged(ComicSourceModel? value)
    {
        if (value == null)
            return;
        ApplySetting(s => s.DefaultComicSourceName, value?.Name);
    }
}
