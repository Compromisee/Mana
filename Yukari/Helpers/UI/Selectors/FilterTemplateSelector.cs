using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yukari.ViewModels.Components;

namespace Yukari.Helpers.UI.Selectors;

public partial class FilterTemplateSelector : DataTemplateSelector
{
    public DataTemplate MultipleFilterTemplate { get; set; } = null!;
    public DataTemplate SingleFilterTemplate { get; set; } = null!;
    public DataTemplate TextFilterTemplate { get; set; } = null!;
    public DataTemplate SortFilterTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is FilterViewModel filter)
        {
            if (filter.IsTextInput)
                return TextFilterTemplate;
            if (filter.IsSortFilter)
                return SortFilterTemplate;
            return filter.AllowMultiple ? MultipleFilterTemplate : SingleFilterTemplate;
        }
        return base.SelectTemplateCore(item, container);
    }
}
