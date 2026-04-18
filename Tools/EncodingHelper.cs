using System.Text;

namespace Ripple.Tools;

/// <summary>
/// File metadata captured during detection. Used by FileTools so writes
/// preserve the source file's encoding, newline sequence, and trailing-newline
/// behavior instead of silently coercing everything to UTF-8 + LF.
/// </summary>
internal sealed class FileMetadata
{
    public required Encoding Encoding { get; set; }
    public required string NewlineSequence { get; set; }
    public required bool HasTrailingNewline { get; set; }
}

/// <summary>
/// Encoding detection and management helper for text file operations.
/// Ported from PowerShell.MCP (Cmdlets/EncodingHelper.cs).
/// Only reads first 64KB of a file for detection.
/// </summary>
internal static class EncodingHelper
{
    private const int DetectionBufferSize = 65536; // 64KB

    public static Encoding DetectEncoding(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
            return new UTF8Encoding(false);

        int bufferSize = (int)Math.Min(DetectionBufferSize, fileInfo.Length);
        byte[] bytes = new byte[bufferSize];
        int bytesRead;

        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
        {
            bytesRead = stream.Read(bytes, 0, bufferSize);
        }

        return FileMetadataHelper.DetectEncodingFromBytes(bytes, bytesRead);
    }

    /// <summary>
    /// Canonical encoding alias resolver. Returns null if the name is unrecognized.
    /// </summary>
    private static Encoding? GetEncodingByName(string encodingName)
    {
        try
        {
            return encodingName.ToLowerInvariant() switch
            {
                // UTF encodings
                "utf-8" or "utf8" or "utf8nobom" or "utf-8nobom" or "utf-8-nobom" or "utf8-nobom" => new UTF8Encoding(false),
                "utf-8-bom" or "utf8-bom" or "utf8bom" or "utf8-sig" or "utf-8-sig" => new UTF8Encoding(true),
                "utf-16" or "utf16" or "utf-16le" or "utf16le" or "unicode" or "utf16bom" or "utf-16bom" or "utf16lebom" or "utf-16lebom" => Encoding.Unicode,
                "utf-16be" or "utf16be" or "utf16bebom" or "utf-16bebom" => Encoding.BigEndianUnicode,
                "utf-32" or "utf32" or "utf-32le" or "utf32le" or "utf32bom" or "utf-32bom" or "utf32lebom" or "utf-32lebom" => Encoding.UTF32,
                "utf-32be" or "utf32be" or "utf32bebom" or "utf-32bebom" => new UTF32Encoding(true, true),

                // Japanese
                "shift_jis" or "shift-jis" or "shiftjis" or "sjis" or "cp932" => Encoding.GetEncoding("shift_jis"),
                "euc-jp" or "euc_jp" or "eucjp" => Encoding.GetEncoding("euc-jp"),
                "iso-2022-jp" or "iso2022jp" or "iso2022-jp" or "jis" => Encoding.GetEncoding("iso-2022-jp"),

                // Chinese
                "big-5" or "big5hkscs" or "cp950" => Encoding.GetEncoding("big5"),
                "gb2312" or "gbk" or "gb18030" or "cp936" => Encoding.GetEncoding("gb2312"),

                // Korean
                "euckr" or "cp949" => Encoding.GetEncoding("euc-kr"),

                // Windows codepages
                "874" => Encoding.GetEncoding("windows-874"),
                "1250" => Encoding.GetEncoding("windows-1250"),
                "1251" => Encoding.GetEncoding("windows-1251"),
                "1252" => Encoding.GetEncoding("windows-1252"),
                "1253" => Encoding.GetEncoding("windows-1253"),
                "1254" => Encoding.GetEncoding("windows-1254"),
                "1255" => Encoding.GetEncoding("windows-1255"),
                "1256" => Encoding.GetEncoding("windows-1256"),
                "1257" => Encoding.GetEncoding("windows-1257"),
                "1258" => Encoding.GetEncoding("windows-1258"),
                "cp874" => Encoding.GetEncoding("windows-874"),
                "cp1250" => Encoding.GetEncoding("windows-1250"),
                "cp1251" => Encoding.GetEncoding("windows-1251"),
                "cp1254" => Encoding.GetEncoding("windows-1254"),

                // ISO-8859
                "latin-1" or "iso88591" or "iso_8859_1" => Encoding.GetEncoding("iso-8859-1"),
                "latin-2" or "iso88592" or "iso_8859_2" => Encoding.GetEncoding("iso-8859-2"),
                "iso88595" or "iso_8859_5" => Encoding.GetEncoding("iso-8859-5"),
                "iso88596" => Encoding.GetEncoding("iso-8859-6"),
                "iso88599" => Encoding.GetEncoding("iso-8859-9"),
                "latin-9" or "iso885915" => Encoding.GetEncoding("iso-8859-15"),

                "koi8u" => Encoding.GetEncoding("koi8-u"),
                "tis620" => Encoding.GetEncoding("tis-620"),
                "ascii" => Encoding.ASCII,

                _ => Encoding.GetEncoding(encodingName)
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get encoding from explicit name, or auto-detect from file if name is null/empty/unknown.
    /// </summary>
    public static Encoding GetEncoding(string filePath, string? encodingName)
    {
        if (!string.IsNullOrEmpty(encodingName))
        {
            var resolved = GetEncodingByName(encodingName);
            if (resolved != null)
                return resolved;
        }
        return DetectEncoding(filePath);
    }
}
