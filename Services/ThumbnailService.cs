using Microsoft.UI.Xaml.Media.Imaging;
using PhotoTagger.Models;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace PhotoTagger.Services;

/// <summary>
/// Asks Windows for the same thumbnail Explorer shows. That covers photos AND videos,
/// respects EXIF rotation, keeps the aspect ratio, and Windows caches the results itself,
/// so the old hand-rolled disk cache (and its squashing/rotation bugs) is no longer needed.
/// You can delete %LOCALAPPDATA%\PhotoTagger\Thumbnails.
/// </summary>
public class ThumbnailService
{
    public async Task<BitmapImage?> GetThumbnailAsync(string filePath, MediaKind mediaKind)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);

            ThumbnailMode mode = mediaKind == MediaKind.Video
                ? ThumbnailMode.VideosView
                : ThumbnailMode.PicturesView;

            using StorageItemThumbnail thumbnail =
                await file.GetThumbnailAsync(mode, 320, ThumbnailOptions.UseCurrentScale);

            if (thumbnail == null || thumbnail.Size == 0)
                return null;

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            return bitmap;
        }
        catch (Exception ex)
        {
            // Missing file, unsupported codec (e.g. HEIC without the HEIF extension), etc.
            Debug.WriteLine($"Thumbnail failed for {filePath}: {ex.Message}");
            return null;
        }
    }
}