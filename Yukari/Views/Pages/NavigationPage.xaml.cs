using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Yukari.Messages;
using Yukari.Services.Sources;
using Yukari.Services.UI;
using Yukari.ViewModels.Pages;

namespace Yukari.Views.Pages;

public sealed partial class NavigationPage : Page
{
    private WebView2? _aidokuRuntimeWebView;
    private WebView2? _aidokuImageBrowserWebView;
    public NavigationPageViewModel ViewModel { get; set; }

    public NavigationPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<NavigationPageViewModel>();
        DataContext = ViewModel;

#if DEBUG || DEBUG_UNPACKAGED
        DebugBadge.Visibility = Visibility.Visible;
#endif

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.GetService<INavigationService>().Initialize(ContentFrame);

        if (ContentFrame.Content == null)
            ViewModel.NavigateCommand.Execute(new NavigateMessage(typeof(FavoritesPage), null));

        try
        {
            _aidokuRuntimeWebView ??= new WebView2
            {
                Width = 2,
                Height = 2,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Opacity = 0,
                IsHitTestVisible = false
            };
            if (AidokuRuntimeHost.Children.Count == 0)
                AidokuRuntimeHost.Children.Add(_aidokuRuntimeWebView);
            _aidokuImageBrowserWebView ??= new WebView2
            {
                Width = 2,
                Height = 2,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Opacity = 0,
                IsHitTestVisible = false
            };
            if (AidokuRuntimeHost.Children.Count < 2)
                AidokuRuntimeHost.Children.Add(_aidokuImageBrowserWebView);
            await App.GetService<AidokuRuntimeService>().InitializeAsync(
                _aidokuRuntimeWebView,
                _aidokuImageBrowserWebView
            );
        }
        catch (Exception ex)
        {
            App.GetService<ILogger<NavigationPage>>().LogError(ex, "Aidoku runtime failed to initialize");
        }
    }

    private void NavigationViewControl_ItemInvoked(
        NavigationView sender,
        NavigationViewItemInvokedEventArgs args
    )
    {
        var tag = args.IsSettingsInvoked
            ? "Yukari.Views.Pages.SettingsPage"
            : args.InvokedItemContainer?.Tag?.ToString();
        if (!string.IsNullOrEmpty(tag) && Type.GetType(tag) is { } pageType)
            ViewModel.NavigateCommand.Execute(new NavigateMessage(pageType, null));
    }

    private void NavigationViewControl_BackRequested(
        NavigationView sender,
        NavigationViewBackRequestedEventArgs args
    ) => ViewModel.BackCommand.Execute(null);

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        if (ContentFrame.SourcePageType == typeof(SettingsPage))
        {
            NavigationViewControl.SelectedItem = NavigationViewControl.SettingsItem;
            return;
        }

        var selectedMenuItem = NavigationViewControl
            .MenuItems.OfType<NavigationViewItem>()
            .Concat(NavigationViewControl.FooterMenuItems.OfType<NavigationViewItem>())
            .FirstOrDefault(item => item.Tag.ToString() == ContentFrame.SourcePageType.FullName);

        if (selectedMenuItem != null)
            NavigationViewControl.SelectedItem = selectedMenuItem;
    }

    private void NavigationViewControl_DisplayModeChanged(
        NavigationView sender,
        NavigationViewDisplayModeChangedEventArgs args
    ) =>
        AppTitleBar.Margin =
            args.DisplayMode == NavigationViewDisplayMode.Minimal
                ? new Thickness { Left = 96 }
                : new Thickness { Left = 48 };

    private void AutoSuggestBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args
    )
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
            ViewModel.SearchTextChangedCommand.Execute(null);
    }
}
