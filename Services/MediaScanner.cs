using PhotoTagger.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PhotoTagger.Services;

public sealed record ScannedFile(string Path, MediaKind MediaKind, long SizeBytes, long ModifiedUtc, string? FolderPath);

public class MediaScanner
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gif", ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp", ".bmp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".3gp"
    };

    public List<ScannedFile> Scan(string folderPath, bool searchSubdirectories = false)
    {
        // Currently only interested in going one layer deep and displaying folders from the subfolders.
        int maxRecursionDepth = 0;
        if (searchSubdirectories)
            maxRecursionDepth = 1;

        EnumerationOptions options = new()
        {
            RecurseSubdirectories = searchSubdirectories,
            MaxRecursionDepth = maxRecursionDepth,
            IgnoreInaccessible = true
        };
        List<ScannedFile> results = new();

        foreach (FileInfo file in new DirectoryInfo(folderPath).EnumerateFiles("*", options))
        {
            string ext = file.Extension;

            MediaKind mediaKind;
            if (ImageExtensions.Contains(ext)) mediaKind = MediaKind.Image;
            else if (VideoExtensions.Contains(ext)) mediaKind = MediaKind.Video;
            else continue;

            results.Add(new ScannedFile(file.FullName, mediaKind, file.Length, file.LastWriteTimeUtc.Ticks, null));
        }

        foreach (string subfolder in Directory.GetDirectories(folderPath, "*", SearchOption.TopDirectoryOnly))
        {
            results.Add(new ScannedFile(subfolder.TrimEnd('\\', '/'), MediaKind.Folder, 0, Directory.GetLastWriteTimeUtc(subfolder).Ticks, null));

            foreach (FileInfo file in new DirectoryInfo(folderPath).EnumerateFiles("*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    MaxRecursionDepth = 0,
                    IgnoreInaccessible = true
                }))
            {
                string ext = file.Extension;

                MediaKind mediaKind;
                if (ImageExtensions.Contains(ext)) mediaKind = MediaKind.Image;
                else if (VideoExtensions.Contains(ext)) mediaKind = MediaKind.Video;
                else continue;

                results.Add(new ScannedFile(file.FullName, mediaKind, file.Length, file.LastWriteTimeUtc.Ticks, subfolder));
            }
        }

        return results;
    }

    /// <summary>
    /// Cheap content identity: file size + SHA-256 of the first and last 64 KB.
    /// Reading 128 KB per file keeps scans of big video libraries fast, and it is enough to
    /// recognise the same file after it has been moved or renamed. (Unlike a path, it is also
    /// something an Android app could compute to match its own copy of a file.)
    /// </summary>
    public static string? ComputeFingerprint(string path, MediaKind mediaKind)
    {
        const int Chunk = 64 * 1024;
        bool fingerprintComputed = false;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (mediaKind == MediaKind.Folder)
        {
            string[] subFiles = Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly);

            string? firstFingerprint = ComputeFingerprint(subFiles[0], MediaKind.Image);
            string? lastFingerprint = ComputeFingerprint(subFiles[subFiles.Length - 1], MediaKind.Image);

            if (firstFingerprint == null || lastFingerprint == null)
                return null;

            List<string> subFilesFingerprints = new List<string>() { firstFingerprint, lastFingerprint };

            foreach (var fp in subFilesFingerprints.Order())
            {
                sha.AppendData(Encoding.UTF8.GetBytes(fp));
                sha.AppendData(new byte[] { 0 });
                fingerprintComputed = true;
            }
        }
        else
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            sha.AppendData(BitConverter.GetBytes(length));

            var buffer = new byte[Chunk];

            int read = ReadFully(stream, buffer);
            sha.AppendData(buffer, 0, read);

            if (length > Chunk)
            {
                stream.Seek(Math.Max(Chunk, length - Chunk), SeekOrigin.Begin);
                read = ReadFully(stream, buffer);
                sha.AppendData(buffer, 0, read);
            }

            fingerprintComputed = true;
        }

        return fingerprintComputed ? Convert.ToHexString(sha.GetHashAndReset()) : null;
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}