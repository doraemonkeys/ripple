using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace Ripple.Tools;

/// <summary>
/// File operation tools — compatible with Claude Code's built-in tools.
/// Single-pass streaming for large files, binary detection, shared read access.
/// Encoding detection (BOM + Ude heuristic) and newline preservation ported
/// from PowerShell.MCP so edits round-trip Shift-JIS / CRLF files intact.
/// </summary>
[McpServerToolType]
public class FileTools
{
    private const int BinaryCheckBytes = 8192;
    private const int MaxLineLength = 10000;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".hg", ".svn", "__pycache__",
        "dist", "build", ".next", ".nuxt", "coverage",
        ".tox", ".venv", "venv", ".mypy_cache", ".pytest_cache",
        "target", "bin", "obj",
    };

    [McpServerTool]
    [Description("Read a file with line numbers. Supports offset/limit for large files. Auto-detects encoding (UTF-8/16/32 BOM, Shift-JIS, EUC-JP, GBK, Big5, windows-125x, etc.).")]
    public static async Task<string> ReadFile(
        [Description("Absolute path to the file")] string path,
        [Description("Line number to start from (0-based)")] int offset = 0,
        [Description("Maximum number of lines to read")] int limit = 2000,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return $"Error: File not found: {path}";
        if (Directory.Exists(path)) return $"Error: Path is a directory: {path}";
        if (IsBinaryFile(path)) return $"Error: Binary file, cannot display: {path}";

        var lines = new List<string>();
        int lineNum = 0, totalLines = 0;
        var encoding = EncodingHelper.DetectEncoding(path);

        using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true,
            new FileStreamOptions { Access = FileAccess.Read, Share = FileShare.ReadWrite });

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            totalLines++;
            if (lineNum >= offset && lines.Count < limit)
            {
                var display = line.Length > MaxLineLength ? line[..MaxLineLength] + "..." : line;
                lines.Add($"{lineNum + 1,4}: {display}");
            }
            lineNum++;
        }

        var output = string.Join('\n', lines);
        if (totalLines > offset + limit)
            output += $"\n\n[Showing lines {offset + 1}-{offset + lines.Count} of {totalLines}]";

        return output;
    }

    [McpServerTool]
    [Description("Write content to a file. Creates the file if it does not exist, overwrites if it does. Creates parent directories as needed. When overwriting, preserves the original file's encoding and newline sequence (CRLF/LF/CR) by default. Specify `encoding` only when converting between encodings (e.g., Shift-JIS → UTF-8).")]
    public static Task<string> WriteFile(
        [Description("Absolute path to the file")] string path,
        [Description("Content to write")] string content,
        [Description("Optional encoding override for conversion. Usually leave unset to auto-preserve. Accepts: utf-8, utf-8-bom, utf-16, utf-16be, utf-32, shift_jis/sjis/cp932, euc-jp, iso-2022-jp, big5, gb2312/gbk/gb18030, euc-kr, windows-125x, iso-8859-x, ascii.")] string? encoding = null,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        Encoding enc;
        string output;
        if (File.Exists(path))
        {
            var meta = FileMetadataHelper.DetectFileMetadata(path);
            enc = string.IsNullOrEmpty(encoding)
                ? meta.Encoding
                : EncodingHelper.GetEncoding(path, encoding);
            // Preserve the file's existing newline sequence on overwrite,
            // even under encoding conversion. Changing both in one step
            // is ambiguous — convert first, adjust line endings after.
            output = NormalizeNewlines(content, meta.NewlineSequence);
        }
        else
        {
            enc = string.IsNullOrEmpty(encoding)
                ? Utf8NoBom
                : EncodingHelper.GetEncoding(path, encoding);
            output = content;
        }

        File.WriteAllText(path, output, enc);
        var lines = content.Count(c => c == '\n') + 1;
        return Task.FromResult($"Written {lines} lines to {path}");
    }

    [McpServerTool]
    [Description("Edit a file by replacing an exact string with a new string. By default old_string must be unique. Use replace_all to replace all occurrences. Preserves the file's original encoding and newline sequence.")]
    public static Task<string> EditFile(
        [Description("Absolute path to the file")] string path,
        [Description("Exact string to find and replace")] string old_string,
        [Description("Replacement string")] string new_string,
        [Description("Replace all occurrences (default: false, requires unique match)")] bool replace_all = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return Task.FromResult($"Error: File not found: {path}");

        var meta = FileMetadataHelper.DetectFileMetadata(path);

        // Read with detected encoding, then normalize to LF for matching.
        // This lets old_string/new_string use \n even when the file is CRLF —
        // we convert everything back to the file's original newline on write.
        var rawContent = File.ReadAllText(path, meta.Encoding);
        var content = ToLf(rawContent, meta.NewlineSequence);
        var oldNorm = ToLf(old_string, meta.NewlineSequence);
        var newNorm = ToLf(new_string, meta.NewlineSequence);

        var firstIdx = content.IndexOf(oldNorm, StringComparison.Ordinal);
        if (firstIdx == -1) return Task.FromResult("Error: old_string not found in file.");

        string resultLf;
        int replacedCount;
        if (replace_all)
        {
            var sb = new StringBuilder();
            int lastEnd = 0, idx = firstIdx, count = 0;
            while (idx != -1)
            {
                sb.Append(content, lastEnd, idx - lastEnd);
                sb.Append(newNorm);
                lastEnd = idx + oldNorm.Length;
                count++;
                idx = content.IndexOf(oldNorm, lastEnd, StringComparison.Ordinal);
            }
            sb.Append(content, lastEnd, content.Length - lastEnd);
            resultLf = sb.ToString();
            replacedCount = count;
        }
        else
        {
            var secondIdx = content.IndexOf(oldNorm, firstIdx + 1, StringComparison.Ordinal);
            if (secondIdx != -1)
            {
                int count = 0;
                int idx = -1;
                while ((idx = content.IndexOf(oldNorm, idx + 1, StringComparison.Ordinal)) != -1) count++;
                return Task.FromResult($"Error: old_string found {count} times. It must be unique. Add more context or use replace_all.");
            }
            resultLf = string.Concat(content.AsSpan(0, firstIdx), newNorm, content.AsSpan(firstIdx + oldNorm.Length));
            replacedCount = 1;
        }

        var finalContent = FromLf(resultLf, meta.NewlineSequence);
        File.WriteAllText(path, finalContent, meta.Encoding);
        return Task.FromResult($"Replaced {replacedCount} occurrence{(replacedCount > 1 ? "s" : "")} in {path}");
    }

    [McpServerTool]
    [Description("Search file contents using a regular expression. Returns matching lines with file paths and line numbers.")]
    public static async Task<string> SearchFiles(
        [Description("Regular expression pattern to search for")] string pattern,
        [Description("Directory or file to search in (default: current directory)")] string? path = null,
        [Description("Glob pattern to filter files (e.g., \"*.js\", \"*.ts\")")] string? glob = null,
        [Description("Maximum number of matching lines to return")] int max_results = 50,
        CancellationToken cancellationToken = default)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var basePath = path ?? Directory.GetCurrentDirectory();
        var results = new List<string>();

        if (File.Exists(basePath))
        {
            await SearchInFileAsync(basePath, regex, results, max_results, cancellationToken);
        }
        else if (Directory.Exists(basePath))
        {
            await WalkAndSearchAsync(basePath, regex, results, max_results, glob, cancellationToken);
        }
        else
        {
            return $"Error: Path not found: {basePath}";
        }

        if (results.Count == 0) return "No matches found.";
        var output = string.Join('\n', results);
        if (results.Count >= max_results) output += $"\n\n[Results limited to {max_results}]";
        return output;
    }

    [McpServerTool]
    [Description("Find files by glob pattern. Returns matching file paths.")]
    public static Task<string> FindFiles(
        [Description("Glob pattern (e.g., \"*.js\", \"src/**/*.ts\")")] string pattern,
        [Description("Base directory to search in (default: current directory)")] string? path = null,
        [Description("Maximum number of files to return")] int max_results = 200,
        CancellationToken cancellationToken = default)
    {
        var dir = path ?? Directory.GetCurrentDirectory();
        var results = new List<string>();
        FindFilesRecursive(dir, pattern, results, max_results);

        if (results.Count == 0) return Task.FromResult("No files found.");
        return Task.FromResult(string.Join('\n', results));
    }

    // --- Helpers ---

    private static async Task SearchInFileAsync(string filePath, Regex regex, List<string> results, int maxResults, CancellationToken ct)
    {
        if (IsBinaryFile(filePath)) return;

        var encoding = EncodingHelper.DetectEncoding(filePath);
        using var reader = new StreamReader(filePath, encoding, true,
            new FileStreamOptions { Access = FileAccess.Read, Share = FileShare.ReadWrite });

        int lineNum = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            lineNum++;
            if (results.Count >= maxResults) return;
            if (regex.IsMatch(line))
            {
                var display = line.Length > MaxLineLength ? line[..MaxLineLength] + "..." : line;
                results.Add($"{filePath}:{lineNum}: {display}");
            }
        }
    }

    private static async Task WalkAndSearchAsync(string dir, Regex regex, List<string> results, int maxResults, string? globPattern, CancellationToken ct)
    {
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(dir); }
        catch { return; }

        foreach (var entry in entries)
        {
            if (results.Count >= maxResults) return;
            ct.ThrowIfCancellationRequested();

            if (Directory.Exists(entry))
            {
                var name = Path.GetFileName(entry);
                if (SkipDirs.Contains(name)) continue;
                await WalkAndSearchAsync(entry, regex, results, maxResults, globPattern, ct);
            }
            else if (File.Exists(entry))
            {
                if (globPattern != null && !MatchGlob(Path.GetFileName(entry), globPattern)) continue;
                await SearchInFileAsync(entry, regex, results, maxResults, ct);
            }
        }
    }

    private static void FindFilesRecursive(string dir, string pattern, List<string> results, int maxResults)
    {
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(dir); }
        catch { return; }

        foreach (var entry in entries)
        {
            if (results.Count >= maxResults) return;

            if (Directory.Exists(entry))
            {
                var name = Path.GetFileName(entry);
                if (SkipDirs.Contains(name)) continue;
                FindFilesRecursive(entry, pattern, results, maxResults);
            }
            else if (File.Exists(entry))
            {
                var name = Path.GetFileName(entry);
                if (MatchGlob(name, pattern) || MatchGlob(entry, pattern))
                    results.Add(entry);
            }
        }
    }

    // Normalize any incoming newlines (\r\n, \r, \n) to the target sequence.
    // Used by WriteFile on overwrite so the file keeps its original line endings.
    private static string NormalizeNewlines(string content, string target)
    {
        var lf = content.Replace("\r\n", "\n").Replace("\r", "\n");
        return target == "\n" ? lf : lf.Replace("\n", target);
    }

    // ToLf/FromLf: round-trip through \n for matching in EditFile,
    // then re-emit with the file's original newline sequence on write.
    //
    // ToLf always normalises every newline flavour (\r\n + orphan \r) to
    // \n regardless of the file's own line ending — the AI-supplied
    // old_string / new_string can arrive with whatever newlines the
    // client happened to send (Windows clipboard CRLF, VS Code LF, etc.),
    // and pre-2026-04-18 this branched on the file's newline so an AI
    // editing a LF file with CRLF in old_string silently missed. Normal
    // files (pure LF / pure CRLF / pure CR) are unchanged by the double-
    // replace; only mixed-newline files see any difference, and for those
    // "match in LF-space" is still the correct behaviour.
    private static string ToLf(string s, string originalNewline)
        => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string FromLf(string s, string originalNewline) => originalNewline switch
    {
        "\r\n" => s.Replace("\n", "\r\n"),
        "\r" => s.Replace("\n", "\r"),
        _ => s,
    };

    private static bool IsBinaryFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[BinaryCheckBytes];
            int read = fs.Read(buf, 0, buf.Length);
            // UTF-16/32 text legitimately contains 0x00 bytes for ASCII chars,
            // so a BOM presence takes precedence over the null-byte heuristic.
            if (read >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF)))
                return false;
            if (read >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF)
                return false;
            for (int i = 0; i < read; i++)
                if (buf[i] == 0) return true;
            return false;
        }
        catch { return false; }
    }

    private static bool MatchGlob(string str, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", @"[^/\\]*")
            .Replace(@"\?", ".") + "$";
        return Regex.IsMatch(str, regexPattern, RegexOptions.IgnoreCase);
    }
}
