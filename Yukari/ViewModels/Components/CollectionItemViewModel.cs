using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yukari.Services.Comics;
using Yukari.Services.UI;

namespace Yukari.ViewModels.Components;

public partial class CollectionItemViewModel : ObservableObject
{
    private static readonly string[] ColorPalette =
    [
        "#6C75C8", "#D46283", "#3C9B88", "#D78B37", "#6D8DCE", "#A56CC1", "#4F9DCE", "#B66B50"
    ];

    private readonly IComicService _comicService;
    private readonly INotificationService _notificationService;
    private readonly int _maxCollectionNameLength;

    public string PersistedName { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCollectionCommand))]
    public partial string TextBoxName { get; set; }

    [ObservableProperty]
    public partial string Color { get; set; }

    public IRelayCommand<string> DeleteCollectionCommand { get; }

    public CollectionItemViewModel(
        IComicService comicService,
        INotificationService notificationService,
        int maxCollectionNameLength,
        string name,
        string color,
        IRelayCommand<string> deleteCommand
    )
    {
        _comicService = comicService;
        _notificationService = notificationService;
        _maxCollectionNameLength = maxCollectionNameLength;
        PersistedName = name;
        TextBoxName = name;
        Color = color;
        DeleteCollectionCommand = deleteCommand;
    }

    private bool CanRenameCollection() =>
        !string.IsNullOrWhiteSpace(TextBoxName)
        && TextBoxName.Length <= _maxCollectionNameLength
        && TextBoxName != PersistedName;

    [RelayCommand(CanExecute = nameof(CanRenameCollection))]
    private async Task RenameCollectionAsync()
    {
        var result = await _comicService.RenameCollectionAsync(PersistedName, TextBoxName);
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        PersistedName = TextBoxName;
        OnPropertyChanged(nameof(PersistedName));
        RenameCollectionCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task CycleColorAsync()
    {
        var index = Array.IndexOf(ColorPalette, Color);
        var nextColor = ColorPalette[(index + 1 + ColorPalette.Length) % ColorPalette.Length];
        var result = await _comicService.SetCollectionColorAsync(PersistedName, nextColor);
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }
        Color = nextColor;
    }
}
