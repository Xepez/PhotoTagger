using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace PhotoTagger.Models;

public enum MediaKind
{
    Image = 0,
    Video = 1,
    Folder = 2
}

/// <summary>Anything shown as a thumbnail card, so one loader can serve both grids.</summary>
public interface IThumbnailItem
{
    string? ThumbnailSourcePath { get; }
    MediaKind ThumbnailKind { get; }
    BitmapImage? Thumbnail { get; set; }
    bool IsLoadingThumbnail { get; set; }
}

/// <summary>One photo or video in the library (a row in the media table).</summary>
public partial class MediaItem(int index, long id, string filePath, MediaKind kind, string folderThumbnailPath, int imageHeight, int imageWidth) : INotifyPropertyChanged, IThumbnailItem
{
    public int Index { get; } = index;
    public long Id { get; } = id;
    public string FilePath { get; } = filePath;
    public MediaKind Kind { get; } = kind;
    public string FolderThumbnailPath { get; } = folderThumbnailPath;
    public int ImageHeight { get; } = imageHeight;
    public int ImageWidth { get; } = imageWidth;

    public string FileName => Path.GetFileName(FilePath);
    public bool IsVideo => Kind == MediaKind.Video;
    public bool IsFolder => Kind == MediaKind.Folder;
    public Visibility VideoBadgeVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FolderBadgeVisibility => IsFolder ? Visibility.Visible : Visibility.Collapsed;

    string? IThumbnailItem.ThumbnailSourcePath => Kind == MediaKind.Folder ? FolderThumbnailPath : FilePath;
    MediaKind IThumbnailItem.ThumbnailKind => Kind;

    // Used by the thumbnail loader so one item isn't loaded twice at once.
    public bool IsLoadingThumbnail { get; set; }

    private BitmapImage? _thumbnail;
    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (_thumbnail == value) return;
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>A tag and how many items (or folders) use it. Shown in the filter sidebar.</summary>
public sealed record TagInfo(long Id, string Name, int Count)
{
    public string Label => $"{Name} ({Count})";
}

/// <summary>A tag and how many items (or folders) use it. Shown in the filter sidebar.</summary>
public sealed record FolderInfo(long Id, string Label);

/// <summary>
/// Everything the filter sidebar can ask for. All parts are combined with AND.
/// For folders, Text matches anywhere in the folder path and Kind is ignored.
/// </summary>
public sealed class MediaFilter
{
    public string? Text { get; init; }
    public MediaKind? MediaKind { get; init; }                 // null = photos and videos
    public bool? UntaggedOnly { get; init; }               // when true, Tags is ignored
    public bool? MatchAllTags { get; init; } = true;       // true = AND, false = OR
    public IReadOnlyList<string>? Tags { get; init; } = Array.Empty<string>();
    public string? Path { get; init; }
    public IReadOnlyList<string>? FolderPath { get; init; } = Array.Empty<string>();
    public bool? IsFolder { get; init; } = null;
}