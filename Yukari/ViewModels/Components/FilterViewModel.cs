using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Yukari.Core.Models;
using Yukari.Helpers;

namespace Yukari.ViewModels.Components;

public partial class FilterViewModel : ObservableObject
{
    public string Key { get; }
    public string DisplayName { get; }
    public bool AllowMultiple { get; }
    public bool IsTextInput => AidokuFilterKey.IsTextInput(Key);
    public bool IsSortFilter => AidokuFilterKey.IsSort(Key);
    public bool IsGenreFilter => AidokuFilterKey.IsGenre(Key);

    [ObservableProperty]
    public partial string TextValue { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SortAscending { get; set; } = true;

    public ObservableCollection<ToggleOptionViewModel> Options { get; }

    // Notifying this property in a TwoWay ComboBox binding causes an infinite loop;
    // the ComboBox already has the right item when its setter is called.
    public ToggleOptionViewModel? SelectedOptionIfNotAllowMultiple
    {
        get => Options.FirstOrDefault(o => o.IsSelected);
        set
        {
            if (value is null)
                return;

            foreach (var opt in Options)
                opt.IsSelected = false;

            value.IsSelected = true;
        }
    }

    public FilterViewModel(Filter filter, IReadOnlyList<string>? selectedOptions = null)
    {
        Key = filter.Key;
        DisplayName = filter.DisplayName;
        AllowMultiple = filter.AllowMultiple;
        SortAscending = AidokuFilterKey.DefaultSortAscending(Key);

        var appliedValues = selectedOptions ?? [];
        if (IsTextInput)
            TextValue = appliedValues.FirstOrDefault() ?? string.Empty;
        if (IsSortFilter)
        {
            var direction = appliedValues.FirstOrDefault(value => AidokuFilterKey.TryGetSortDirection(value, out _));
            if (direction is not null && AidokuFilterKey.TryGetSortDirection(direction, out var ascending))
                SortAscending = ascending;
        }

        Options = new ObservableCollection<ToggleOptionViewModel>(
            (filter.Options ?? Enumerable.Empty<FilterOption>()).Select(
                o => new ToggleOptionViewModel(
                    o.Key,
                    appliedValues.Contains(o.Key) || (selectedOptions is null && o.Default),
                    o.DisplayName
                )
            )
        );

        // A genre can be included or excluded, but never both at once.
        if (IsGenreFilter)
        {
            foreach (var option in Options)
            {
                option.PropertyChanged += OnGenreOptionChanged;
            }
        }
    }

    private void OnGenreOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToggleOptionViewModel.IsSelected)
            || sender is not ToggleOptionViewModel changed
            || !changed.IsSelected)
            return;

        var separator = changed.Key.IndexOf(':');
        if (separator < 0)
            return;

        var otherPrefix = changed.Key.StartsWith("include:", System.StringComparison.Ordinal)
            ? "exclude:"
            : changed.Key.StartsWith("exclude:", System.StringComparison.Ordinal)
                ? "include:"
                : null;
        if (otherPrefix is null)
            return;

        var other = Options.FirstOrDefault(o => o.Key == otherPrefix + changed.Key[(separator + 1)..]);
        if (other is not null)
            other.IsSelected = false;
    }
}
