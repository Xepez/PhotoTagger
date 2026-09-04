using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoTagger.Models;
using PhotoTagger.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PhotoTagger
{
    public sealed partial class MainWindow : Window
    {
        // Models
        private readonly PhotoScanner _photoScanner = new();

        // Services
        private readonly ThumbnailService _thumbnailService = new();
        private readonly SemaphoreSlim _thumbnailSemaphore = new(6);
        private readonly PreviewService _previewService = new();


        public MainWindow()
        {
            InitializeComponent();
        }

        // Thumbnail Queue
        private async Task QueueThumbnailsAsync(
            IEnumerable<Photo> photos)
        {
            var tasks = photos.Select(
                LoadThumbnailWithLimitAsync);

            await Task.WhenAll(tasks);
        }

        private async Task LoadThumbnailWithLimitAsync(
            Photo photo)
        {
            await _thumbnailSemaphore.WaitAsync();

            try
            {
                photo.Thumbnail =
                    await _thumbnailService.GetThumbnailAsync(
                        photo.FilePath);
            }
            finally
            {
                _thumbnailSemaphore.Release();
            }
        }

        // Expand Thumbnail
        private async void PhotoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PhotoList.SelectedItem is not Photo photo)
            {
                PreviewImage.Source = null;
                PreviewFileName.Text = string.Empty;
                PreviewFilePath.Text = string.Empty;

                return;
            }

            PreviewFileName.Text = photo.FileName;
            PreviewFilePath.Text = photo.FilePath;

            PreviewImage.Source = null;

            var preview = await _previewService.LoadPreviewAsync(photo.FilePath);

            PreviewImage.Source = preview;
        }

        // Load images from folders
        private async void SelectFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();

            picker.FileTypeFilter.Add("*");

            var windowHandle = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, windowHandle);

            StorageFolder? folder = await picker.PickSingleFolderAsync();

            if (folder == null)
                return;

            FolderText.Text = folder.Path;

            var photos = _photoScanner.Scan(folder.Path);

            PhotoCountText.Text = $"{photos.Count} photos found";
            PhotoList.ItemsSource = photos;

            _ = QueueThumbnailsAsync(photos);
        }
    }
}
