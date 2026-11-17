using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;
using Yukari.Models.DTO;
using Yukari.Services.Storage;
using Yukari.Services.UI;
using Yukari.ViewModels.Pages;

namespace Yukari.Views.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPageViewModel ViewModel { get; set; }
    private readonly IDataService _dataService;

    public SettingsPage()
    {
        InitializeComponent();

        ViewModel = App.GetService<SettingsPageViewModel>();
        _dataService = App.GetService<IDataService>();
        DataContext = ViewModel;

        var lclService = App.GetService<ILocalizationService>();
        AboutExpander.Description = lclService.GetFormattedString(
            "About/Copyright",
            "2026",
            "TXG0Fk3",
            "GPL-3.0"
        );
        LicenseHLBT.Content = lclService.GetFormattedString("License", "GPL-3.0");
    }


    private void OpenAidokuLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ViewModel.AidokuLogsFolder,
                UseShellExecute = true
            });
        }
        catch
        {
            // The path remains visible and selectable in Settings if Explorer cannot be opened.
        }
    }

    private async void ManageGroups_Click(object sender, RoutedEventArgs e)
    {
        var groups = await _dataService.GetLocalLibraryGroupsAsync();
        var groupPicker = new ComboBox { Header = "Existing group", PlaceholderText = "Create a new group", MinWidth = 280 };
        foreach (var group in groups)
            groupPicker.Items.Add(group);
        groupPicker.DisplayMemberPath = nameof(LocalLibraryGroup.Name);

        var nameBox = new TextBox { Header = "Group name", PlaceholderText = "For example, Read later", MaxLength = 32 };
        var colorPicker = new ColorPicker { IsAlphaEnabled = false, IsColorPreviewVisible = true, Color = Color.FromArgb(255, 108, 117, 200) };
        var errorText = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28)), TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 10, Children = { groupPicker, nameBox, new TextBlock { Text = "Group color" }, colorPicker, errorText } };
        groupPicker.SelectionChanged += (_, _) =>
        {
            if (groupPicker.SelectedItem is LocalLibraryGroup selected)
            {
                nameBox.Text = selected.Name;
                colorPicker.Color = ParseColor(selected.Color);
            }
            else
            {
                nameBox.Text = string.Empty;
                colorPicker.Color = Color.FromArgb(255, 108, 117, 200);
            }
            errorText.Text = string.Empty;
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Manage local library groups",
            Content = content,
            PrimaryButtonText = "Save group",
            SecondaryButtonText = "Delete selected",
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var name = nameBox.Text.Trim();
            if (name.Length == 0 || name.Length > 32)
            {
                errorText.Text = "Enter a name from 1 to 32 characters.";
                args.Cancel = true;
                return;
            }
            var current = groupPicker.SelectedItem as LocalLibraryGroup;
            if (groups.Any(group => group.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && (current is null || group.Id != current.Id)))
            {
                errorText.Text = "A group with that name already exists.";
                args.Cancel = true;
                return;
            }
            try
            {
                var color = ToHexColor(colorPicker.Color);
                if (current is not null)
                    await _dataService.RenameLocalLibraryGroupAsync(current.Name, name, color);
                else
                    await _dataService.CreateLocalLibraryGroupAsync(name, color);
                args.Cancel = true;
                groups = await RefreshGroupPickerAsync(groupPicker);
                groupPicker.SelectedItem = null;
                errorText.Text = "Saved.";
            }
            catch (Exception ex)
            {
                errorText.Text = ex.Message;
                args.Cancel = true;
            }
        };
        dialog.SecondaryButtonClick += async (_, args) =>
        {
            if (groupPicker.SelectedItem is not LocalLibraryGroup selected)
            {
                errorText.Text = "Choose a group to delete.";
                args.Cancel = true;
                return;
            }
            try
            {
                await _dataService.RemoveLocalLibraryGroupAsync(selected.Name);
                args.Cancel = true;
                groups = await RefreshGroupPickerAsync(groupPicker);
                groupPicker.SelectedItem = null;
                errorText.Text = "Deleted. Titles in that group are now ungrouped.";
            }
            catch (Exception ex)
            {
                errorText.Text = ex.Message;
                args.Cancel = true;
            }
        };
        await dialog.ShowAsync();
    }

    private async Task<System.Collections.Generic.IReadOnlyList<LocalLibraryGroup>> RefreshGroupPickerAsync(ComboBox groupPicker)
    {
        var groups = await _dataService.GetLocalLibraryGroupsAsync();
        groupPicker.Items.Clear();
        foreach (var group in groups)
            groupPicker.Items.Add(group);
        return groups;
    }

    private static Color ParseColor(string value)
    {
        try
        {
            return Color.FromArgb(255, Convert.ToByte(value[1..3], 16), Convert.ToByte(value[3..5], 16), Convert.ToByte(value[5..7], 16));
        }
        catch
        {
            return Color.FromArgb(255, 108, 117, 200);
        }
    }

    private static string ToHexColor(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        await ViewModel.OnNavigatedFromAsync();
    }
}
