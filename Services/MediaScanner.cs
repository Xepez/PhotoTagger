using PhotoTagger.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace PhotoTagger.Services;

public sealed record ScannedFile(string Path, MediaKind Kind, long SizeBytes, long ModifiedUtcTicks);

/// <summary>Replaces PhotoScanner. Finds photos AND videos and never throws on unreadable folders.</summary>
public class MediaScanner
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp", ".gif", ".bmp"
    };

    // Whether a given video plays in the preview depends on the codecs installed in Windows.
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".3gp"
    };

    public List<ScannedFile> Scan(string folderPath)
    {
        // Skips folders we aren't allowed to read instead of throwing.
        // Default options also skip hidden/system entries (e.g. $RECYCLE.BIN).
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };

        var results = new List<ScannedFile>();

        foreach (var file in new DirectoryInfo(folderPath).EnumerateFiles("*", options))
        {
            string ext = file.Extension;

            MediaKind kind;
            if (ImageExtensions.Contains(ext)) kind = MediaKind.Image;
            else if (VideoExtensions.Contains(ext)) kind = MediaKind.Video;
            else continue;

            results.Add(new ScannedFile(file.FullName, kind, file.Length, file.LastWriteTimeUtc.Ticks));
        }

        return results;
    }

    /// <summary>
    /// Cheap content identity: file size + SHA-256 of the first and last 64 KB.
    /// Reading 128 KB per file keeps scans of big video libraries fast, and it is enough to
    /// recognise the same file after it has been moved or renamed. (Unlike a path, it is also
    /// something an Android app could compute to match its own copy of a file.)
    /// </summary>
    public static string ComputeFingerprint(string path)
    {
        const int Chunk = 64 * 1024;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

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

        return Convert.ToHexString(sha.GetHashAndReset());
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