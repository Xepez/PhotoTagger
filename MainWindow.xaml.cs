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
        private readonly XmpService _xmpService = new();


        public MainWindow()
        {
            InitializeComponent();
        }
        
        // Tag Button
        private void AddTagButton_Click(object sender, RoutedEventArgs e)
        {
            if (PhotoList.SelectedItem is not Photo)
                return;

            string tag = TagInput.Text.Trim();

            if (string.IsNullOrWhiteSpace(tag))
                return;

            var currentTags =
                PreviewTags.Text == "No tags"
                    ? new List<string>()
                    : PreviewTags.Text
                        .Split(
                            ',',
                            StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .ToList();

            if (!currentTags.Contains(
                    tag,
                    StringComparer.OrdinalIgnoreCase))
            {
                currentTags.Add(tag);
            }

            PreviewTags.Text =
                string.Join(", ", currentTags);

            TagInput.Text = string.Empty;
        }

        // Test Tag Button
        private async void TestWriteButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("****TEST****");
            System.Diagnostics.Debug.WriteLine(XmpService.GetPropertyOptionsInfo());
            System.Diagnostics.Debug.WriteLine("****END TEST****");

            if (PhotoList.SelectedItem is not Photo photo)
            {
                PreviewTags.Text = "Select a photo first.";
                return;
            }

            string tag = TagInput.Text.Trim();

            if (string.IsNullOrWhiteSpace(tag))
            {
                PreviewTags.Text = "Enter a test tag first.";
                return;
            }

            if (!string.Equals(Path.GetExtension(photo.FilePath), ".jpg", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetExtension(photo.FilePath), ".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                PreviewTags.Text = "Test Write currently supports JPEG files only.";
                return;
            }

            string tempPath = photo.FilePath + ".phototagger-test.jpg";

            try
            {
                bool success = _xmpService.TestWriteKeyword(photo.FilePath, tag);
                if (success)
                {
                    PreviewTags.Text = $"TEST SUCCESS: \"{tag}\" was written and read back.";

                    // Confirm that the temporary file was removed.
                    bool tempStillExists = File.Exists(tempPath);
                    if (tempStillExists)
                    {
                        PreviewTags.Text += " WARNING: temporary file still exists.";
                    }
                    else
                    {
                        PreviewTags.Text += " Temporary file was removed.";
                    }
                }
                else
                {
                    PreviewTags.Text = "TEST FAILED: the tag could not be verified.";
                }
            }
            catch (Exception ex)
            {
                PreviewTags.Text = $"TEST ERROR: {ex.Message}";
            }
        }

        // Thumbnail Queue
        private async Task QueueThumbnailsAsync(IEnumerable<Photo> photos)
        {
            var tasks = photos.Select(LoadThumbnailWithLimitAsync);
            await Task.WhenAll(tasks);
        }

        private async Task LoadThumbnailWithLimitAsync(Photo photo)
        {
            await _thumbnailSemaphore.WaitAsync();

            try
            {
                photo.Thumbnail = await _thumbnailService.GetThumbnailAsync(photo.FilePath);
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

            var keywords = _xmpService.ReadKeywords(photo.FilePath);

            PreviewTags.Text = keywords.Count > 0 ? string.Join(", ", keywords) : "No tags";
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
