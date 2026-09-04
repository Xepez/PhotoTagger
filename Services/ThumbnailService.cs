using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace PhotoTagger.Services;

public class ThumbnailService
{
    private readonly string _cacheDirectory;

    public ThumbnailService()
    {
        _cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhotoTagger",
                "Thumbnails"
            );

        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<BitmapImage?> GetThumbnailAsync(string filePath)
    {
        try
        {
            string cacheFile = GetCacheFilePath(filePath);

            if (!File.Exists(cacheFile))
            {
                await CreateThumbnailAsync(filePath, cacheFile);
            }

            return await LoadBitmapAsync(cacheFile);
        }
        catch
        {
            return null;
        }
    }

    private string GetCacheFilePath(string filePath)
    {
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(filePath))
            );

        return Path.Combine(_cacheDirectory, $"{hash}.jpg");
    }

    private async Task CreateThumbnailAsync(string sourcePath, string destinationPath)
    {
        using var sourceFile = File.OpenRead(sourcePath);

        var decoder = await BitmapDecoder.CreateAsync(sourceFile.AsRandomAccessStream());

        // Decode a reasonably small version of the image.
        var transform = new BitmapTransform
        {
            ScaledWidth = 400,
            ScaledHeight = 400
        };

        var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage
            );

        using var outputFile = File.Create(destinationPath);

        var encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.JpegEncoderId,
            outputFile.AsRandomAccessStream());

        encoder.SetSoftwareBitmap(softwareBitmap);

        await encoder.FlushAsync();
    }


    private async Task<BitmapImage> LoadBitmapAsync(string filePath)
    {
        using var stream = File.OpenRead(filePath);

        var bitmap = new BitmapImage();

        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());

        return bitmap;
    }
}