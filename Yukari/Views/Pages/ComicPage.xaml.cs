using System;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Streams;
using Yukari.Models.DTO;
using Yukari.Services.UI;
using Yukari.ViewModels.Pages;

namespace Yukari.Views.Pages;

public sealed partial class ComicPage : Page
{
    private long _backdropRequest;
    public ComicPageViewModel ViewModel { get; set; }

    public ComicPage()
    {
        InitializeComponent();

        ViewModel = App.GetService<ComicPageViewModel>();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is ContentKey ComicKey)
        {
            await ViewModel.InitializeAsync(ComicKey);
            await LoadHeaderBackdropAsync(ViewModel.Comic?.CoverImageUrl);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Interlocked.Increment(ref _backdropRequest);
        ViewModel.OnNavigatedFrom();
    }

    private async Task LoadHeaderBackdropAsync(string? imageUrl)
    {
        var request = Interlocked.Increment(ref _backdropRequest);
        if (string.IsNullOrWhiteSpace(imageUrl))
            return;

        try
        {
            var imageCache = App.GetService<IImageCacheService>();
            var bytes = await imageCache.GetImageBytesAsync(imageUrl);
            if (bytes is null || request != Interlocked.Read(ref _backdropRequest))
                return;

            using var memory = new MemoryStream(bytes);
            using var input = memory.AsRandomAccessStream();
            using var bitmap = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), input);
            var scale = Math.Min(1f, Math.Min(1600f / bitmap.SizeInPixels.Width, 900f / bitmap.SizeInPixels.Height));
            var width = bitmap.SizeInPixels.Width * scale;
            var height = bitmap.SizeInPixels.Height * scale;
            using var blurred = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
            using (var drawingSession = blurred.CreateDrawingSession())
            {
                drawingSession.Transform = Matrix3x2.CreateScale(scale);
                drawingSession.DrawImage(new GaussianBlurEffect
                {
                    Source = bitmap,
                    BlurAmount = 28,
                    Optimization = EffectOptimization.Quality
                }, Vector2.Zero);
            }

            using var output = new InMemoryRandomAccessStream();
            await blurred.SaveAsync(output, CanvasBitmapFileFormat.Png);
            output.Seek(0);
            var source = new BitmapImage();
            await source.SetSourceAsync(output);
            if (request == Interlocked.Read(ref _backdropRequest))
                HeaderBackdrop.Source = source;
        }
        catch
        {
            // Keep the ordinary cover image as a readable fallback if a source image cannot be blurred.
        }
    }
}
