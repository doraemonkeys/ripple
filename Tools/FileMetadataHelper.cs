using System.Text;

namespace Ripple.Tools;

/// <summary>
/// File metadata detection helper. Ported from PowerShell.MCP
/// (Cmdlets/FileMetadataHelper.cs). Reads only a 64KB header + 4KB tail
/// even for multi-GB files to detect encoding, newline sequence, and
/// trailing-newline behavior.
/// </summary>
internal static class FileMetadataHelper
{
    private const int HeaderBufferSize = 65536;
    private const int TailBufferSize = 4096;

    private static (byte[] HeaderBytes, int HeaderLength, byte[] TailBytes, int TailLength) ReadFilePartially(string filePath, long fileLength)
    {
        byte[] headerBytes = new byte[Math.Min(HeaderBufferSize, fileLength)];
        byte[] tailBytes = Array.Empty<byte>();
        int headerLength = 0;
        int tailLength = 0;

        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
        {
            headerLength = stream.Read(headerBytes, 0, headerBytes.Length);

            if (fileLength > HeaderBufferSize)
            {
                tailBytes = new byte[Math.Min(TailBufferSize, fileLength - HeaderBufferSize)];
                stream.Seek(-tailBytes.Length, SeekOrigin.End);
                tailLength = stream.Read(tailBytes, 0, tailBytes.Length);
            }
            else
            {
                tailBytes = headerBytes;
                tailLength = headerLength;
            }
        }

        return (headerBytes, headerLength, tailBytes, tailLength);
    }

    private static (string NewlineSequence, bool HasTrailingNewline) DetectNewlineFromBytes(
        byte[] headerBytes,
        int headerLength,
        byte[] tailBytes,
        int tailLength,
        Encoding encoding,
        long fileLength)
    {
        if (fileLength == 0)
            return (Environment.NewLine, false);

        string newlineSequence = Environment.NewLine;
        string headerContent = encoding.GetString(headerBytes, 0, headerLength);

        if (headerContent.Contains("\r\n"))
            newlineSequence = "\r\n";
        else if (headerContent.Contains("\n"))
            newlineSequence = "\n";
        else if (headerContent.Contains("\r"))
            newlineSequence = "\r";

        bool hasTrailingNewline = false;
        if (tailLength > 0)
        {
            if (tailLength >= 2 && tailBytes[tailLength - 2] == 0x0D && tailBytes[tailLength - 1] == 0x0A)
                hasTrailingNewline = true;
            else if (tailBytes[tailLength - 1] == 0x0A || tailBytes[tailLength - 1] == 0x0D)
                hasTrailingNewline = true;
        }

        return (newlineSequence, hasTrailingNewline);
    }

    internal static Encoding DetectEncodingFromBytes(byte[] bytes, int length)
    {
        if (length == 0)
            return new UTF8Encoding(false);

        // BOM detection
        if (length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(true);
        if (length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            if (length >= 4 && bytes[2] == 0x00 && bytes[3] == 0x00)
                return Encoding.UTF32;
            return Encoding.Unicode;
        }
        if (length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode;

        // Ude heuristic detection
        try
        {
            var detector = new Ude.CharsetDetector();
            detector.Feed(bytes, 0, length);
            detector.DataEnd();

            if (detector.Charset != null)
            {
                try
                {
                    var detectedEncoding = Encoding.GetEncoding(detector.Charset);
                    // BOM was already excluded above, so keep UTF-8 BOM-less
                    if (detectedEncoding is UTF8Encoding)
                        return new UTF8Encoding(false);
                    return detectedEncoding;
                }
                catch
                {
                    // detected charset name unsupported, fall through
                }
            }
        }
        catch
        {
            // Ude unavailable / failed, fall through
        }

        // Fallback: strict UTF-8 validation
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            utf8.GetString(bytes, 0, length);
            return new UTF8Encoding(false);
        }
        catch
        {
            return Encoding.Default;
        }
    }

    public static FileMetadata DetectFileMetadata(string filePath)
    {
        var fileInfo = new FileInfo(filePath);

        if (fileInfo.Length == 0)
        {
            return new FileMetadata
            {
                Encoding = new UTF8Encoding(false),
                NewlineSequence = Environment.NewLine,
                HasTrailingNewline = false
            };
        }

        var (headerBytes, headerLength, tailBytes, tailLength) = ReadFilePartially(filePath, fileInfo.Length);
        var encoding = DetectEncodingFromBytes(headerBytes, headerLength);
        var (newline, hasTrailing) = DetectNewlineFromBytes(headerBytes, headerLength, tailBytes, tailLength, encoding, fileInfo.Length);

        return new FileMetadata
        {
            Encoding = encoding,
            NewlineSequence = newline,
            HasTrailingNewline = hasTrailing
        };
    }

    public static FileMetadata DetectFileMetadata(string filePath, string? encodingName)
    {
        var fileInfo = new FileInfo(filePath);

        if (fileInfo.Length == 0)
        {
            var defaultEncoding = string.IsNullOrEmpty(encodingName)
                ? new UTF8Encoding(false)
                : EncodingHelper.GetEncoding(filePath, encodingName);

            return new FileMetadata
            {
                Encoding = defaultEncoding,
                NewlineSequence = Environment.NewLine,
                HasTrailingNewline = false
            };
        }

        if (!string.IsNullOrEmpty(encodingName))
        {
            try
            {
                var encoding = EncodingHelper.GetEncoding(filePath, encodingName);
                var (headerBytes, headerLength, tailBytes, tailLength) = ReadFilePartially(filePath, fileInfo.Length);
                var (newline, hasTrailing) = DetectNewlineFromBytes(headerBytes, headerLength, tailBytes, tailLength, encoding, fileInfo.Length);

                return new FileMetadata
                {
                    Encoding = encoding,
                    NewlineSequence = newline,
                    HasTrailingNewline = hasTrailing
                };
            }
            catch
            {
                // fall through to auto-detect
            }
        }

        return DetectFileMetadata(filePath);
    }
}
