using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Yukari.Core.Models;
using Yukari.Helpers;
using Yukari.ViewModels.Components;

namespace Yukari.ViewModels.Dialogs;

public partial class FiltersDialogViewModel : ObservableObject
{
    [ObservableProperty]
    public partial List<FilterViewModel> Filters { get; set; }

    public FiltersDialogViewModel(
        IReadOnlyList<Filter> filters,
        IReadOnlyDictionary<string, IReadOnlyList<string>> appliedFilters
    )
    {
        Filters = filters
            .Select(f => new FilterViewModel(f, appliedFilters.GetValueOrDefault(f.Key)))
            .ToList();
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetAppliedFilters()
    {
        var result = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var filter in Filters)
        {
            var values = new List<string>();
            if (filter.IsTextInput)
            {
                if (!string.IsNullOrWhiteSpace(filter.TextValue))
                    values.Add(filter.TextValue.Trim());
            }
            else if (filter.AllowMultiple)
            {
                values.AddRange(filter.Options.Where(option => option.IsSelected).Select(option => option.Key));
            }
            else if (filter.SelectedOptionIfNotAllowMultiple is { } selectedOption)
            {
                values.Add(selectedOption.Key);
            }

            if (filter.IsSortFilter)
                values.Add(filter.SortAscending ? AidokuFilterKey.SortAscendingToken : AidokuFilterKey.SortDescendingToken);

            // Keep an explicit empty entry for Aidoku filters. This distinguishes "clear all"
            // from "use the source's default" (notably for default genre selections).
            if (filter.Key.StartsWith("aidoku-filter:", System.StringComparison.Ordinal)
                || values.Count > 0)
                result[filter.Key] = values;
        }
        return result;
    }
}
