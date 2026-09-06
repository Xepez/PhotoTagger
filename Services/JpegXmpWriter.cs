using System;
using System.IO;
using System.Text;

namespace PhotoTagger.Services;

public class JpegXmpWriter
{
    private static readonly byte[] XmpIdentifier = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

    private const int App1Marker = 0xE1;
    private const int MaxApp1Length = 65535;

    public void WriteXmp(string sourcePath, string destinationPath, byte[] xmpPacket)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source JPEG not found.", sourcePath);
        }

        if (xmpPacket.Length > 65502)
        {
            throw new InvalidOperationException("The XMP packet is too large for a standard JPEG XMP segment.");
        }

        byte[] xmpSegment = CreateXmpSegment(xmpPacket);

        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        CopyJpegWithXmp(input, output, xmpSegment);
    }

    private static byte[] CreateXmpSegment(byte[] xmpPacket)
    {
        int payloadLength = XmpIdentifier.Length + xmpPacket.Length;

        int lengthField = payloadLength + 2;

        if (lengthField > MaxApp1Length)
        {
            throw new InvalidOperationException("The XMP APP1 segment is too large.");
        }

        using var stream = new MemoryStream();

        stream.WriteByte(0xFF);
        stream.WriteByte((byte)App1Marker);

        stream.WriteByte((byte)(lengthField >> 8));

        stream.WriteByte((byte)(lengthField & 0xFF));

        stream.Write(XmpIdentifier, 0, XmpIdentifier.Length);

        stream.Write(xmpPacket, 0, xmpPacket.Length);

        return stream.ToArray();
    }

    private static void CopyJpegWithXmp(FileStream input, FileStream output, byte[] xmpSegment)
    {
        int first = input.ReadByte();
        int second = input.ReadByte();

        if (first != 0xFF || second != 0xD8)
        {
            throw new InvalidDataException("The file is not a JPEG image.");
        }

        output.WriteByte(0xFF);
        output.WriteByte(0xD8);

        bool xmpWritten = false;

        while (true)
        {
            int markerStart = input.ReadByte();

            if (markerStart == -1)
            {
                throw new InvalidDataException("Unexpected end of JPEG file.");
            }

            if (markerStart != 0xFF)
            {
                throw new InvalidDataException("Invalid JPEG marker.");
            }

            int marker = input.ReadByte();

            while (marker == 0xFF)
            {
                marker = input.ReadByte();
            }

            if (marker == -1)
            {
                throw new InvalidDataException("Unexpected end of JPEG file.");
            }

            // Start of Scan.
            if (marker == 0xDA)
            {
                if (!xmpWritten)
                {
                    output.Write(xmpSegment, 0, xmpSegment.Length);

                    xmpWritten = true;
                }

                output.WriteByte(0xFF);
                output.WriteByte((byte)marker);

                CopyRemaining(input, output);
                return;
            }

            // End of Image.
            if (marker == 0xD9)
            {
                if (!xmpWritten)
                {
                    output.Write(xmpSegment, 0, xmpSegment.Length);

                    xmpWritten = true;
                }

                output.WriteByte(0xFF);
                output.WriteByte((byte)marker);
                return;
            }

            // Standalone markers don't have a length field.
            if (IsStandaloneMarker(marker))
            {
                output.WriteByte(0xFF);
                output.WriteByte((byte)marker);
                continue;
            }

            int lengthHigh = input.ReadByte();
            int lengthLow = input.ReadByte();

            if (lengthHigh == -1 || lengthLow == -1)
            {
                throw new InvalidDataException("Unexpected end of JPEG segment.");
            }

            int segmentLength = (lengthHigh << 8) | lengthLow;

            if (segmentLength < 2)
            {
                throw new InvalidDataException("Invalid JPEG segment length.");
            }

            byte[] segmentData = new byte[segmentLength - 2];

            ReadExactly(input, segmentData);

            bool isExistingXmp = marker == App1Marker && StartsWith(segmentData, XmpIdentifier);

            if (isExistingXmp)
            {
                if (!xmpWritten)
                {
                    output.Write(xmpSegment, 0, xmpSegment.Length);

                    xmpWritten = true;
                }

                // Replace the existing XMP segment.
                continue;
            }

            output.WriteByte(0xFF);
            output.WriteByte((byte)marker);

            output.WriteByte((byte)lengthHigh);
            output.WriteByte((byte)lengthLow);

            output.Write(segmentData, 0, segmentData.Length);
        }
    }

    private static bool IsStandaloneMarker(int marker)
    {
        return marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9);
    }

    private static bool StartsWith(byte[] data, byte[] prefix)
    {
        if (data.Length < prefix.Length)
            return false;

        for (int i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i])
                return false;
        }

        return true;
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);

            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }

    private static void CopyRemaining(Stream input, Stream output)
    {
        byte[] buffer = new byte[81920];

        int read;

        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
        }
    }
}