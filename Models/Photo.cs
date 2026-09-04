using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace PhotoTagger.Models;

public class Photo : INotifyPropertyChanged
{
    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath);

    private BitmapImage? _thumbnail;

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;

        set
        {
            if (_thumbnail == value)
                return;

            _thumbnail = value;

            OnPropertyChanged();
        }
    }

    public Photo(string filePath)
    {
        FilePath = filePath;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}