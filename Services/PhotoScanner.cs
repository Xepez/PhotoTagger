using PhotoTagger.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PhotoTagger.Services;

public class PhotoScanner
{
    // Look into webp?
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".heic",
            ".heif",
            ".webp"
        };

    public List<Photo> Scan(string folderPath)
    {
        var files = Directory.EnumerateFiles(
                folderPath,
                "*.*",
                SearchOption.AllDirectories
            );

        return files
            .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)))
            .Select(file => new Photo(file))
            .ToList();
    }
}