using MetadataExtractor;
using MetadataExtractor.Formats.Xmp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XmpCore;
using XmpCore.Options;

namespace PhotoTagger.Services;

public class XmpService
{
    private const string DcNamespace =
        "http://purl.org/dc/elements/1.1/";

    public List<string> ReadKeywords(string filePath)
    {
        var keywords = new List<string>();

        try
        {
            var directories =
                ImageMetadataReader.ReadMetadata(filePath);

            var xmpDirectory =
                directories
                    .OfType<XmpDirectory>()
                    .FirstOrDefault();

            if (xmpDirectory == null)
                return keywords;

            var xmpMeta = xmpDirectory.XmpMeta;

            if (xmpMeta == null)
                return keywords;

            int count =
                xmpMeta.CountArrayItems(
                    DcNamespace,
                    "subject");

            for (int i = 1; i <= count; i++)
            {
                var property =
                    xmpMeta.GetArrayItem(
                        DcNamespace,
                        "subject",
                        i);

                if (property == null)
                    continue;

                string? keyword = property.Value;

                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    keywords.Add(keyword);
                }
            }
        }
        catch
        {
            // No readable XMP metadata.
        }

        return keywords;
    }

    public byte[] CreateXmpWithKeyword(
        string filePath,
        string keyword)
    {
        IXmpMeta xmpMeta = ReadXmp(filePath);

        xmpMeta.AppendArrayItem(
            DcNamespace,
            "subject",
            null,
            keyword,
            null);

        return XmpMetaFactory.SerializeToBuffer(
            xmpMeta,
            new SerializeOptions());
    }

    private IXmpMeta ReadXmp(string filePath)
    {
        try
        {
            var directories =
                ImageMetadataReader.ReadMetadata(filePath);

            var xmpDirectory =
                directories
                    .OfType<XmpDirectory>()
                    .FirstOrDefault();

            if (xmpDirectory?.XmpMeta != null)
                return xmpDirectory.XmpMeta;
        }
        catch
        {
            // Fall through and create new XMP.
        }

        return XmpMetaFactory.Create();
    }

    public bool TestWriteKeyword(
        string filePath,
        string keyword)
    {
        string tempPath =
            filePath + ".phototagger-test.jpg";

        try
        {
            byte[] xmpPacket =
                CreateXmpWithKeyword(
                    filePath,
                    keyword);

            var writer = new JpegXmpWriter();

            writer.WriteXmp(
                filePath,
                tempPath,
                xmpPacket);

            var keywords =
                ReadKeywords(tempPath);

            return keywords.Any(
                existing =>
                    string.Equals(
                        existing,
                        keyword,
                        StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Ignore cleanup failures.
            }
        }
    }
}