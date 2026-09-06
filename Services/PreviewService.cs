using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;

namespace PhotoTagger.Services;

public class PreviewService
{
    public async Task<BitmapImage?> LoadPreviewAsync(
        string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);

            var bitmap = new BitmapImage();

            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());

            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}