using System.Text;
using Ripple.Tools;

namespace Ripple.Tests;

/// <summary>
/// Tests for FileTools encoding detection + newline preservation. Covers the
/// PowerShell.MCP port (EncodingHelper + FileMetadataHelper) and the Shift-JIS /
/// UTF-16 / CRLF round-trip scenarios that used to silently corrupt edits.
/// </summary>
public static class FileToolsTests
{
    public static void Run()
    {
        Console.WriteLine("=== FileTools Tests ===");
        var pass = 0;
        var fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        // Ensure the legacy codepages provider is live — normal MCP startup
        // registers it in Main, but --test mode reaches us before that path
        // on some arg orderings. Registering twice is a no-op.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var tmpRoot = Path.Combine(Path.GetTempPath(), $"ripple-filetools-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpRoot);

        try
        {
            // --- Encoding detection ---

            {
                // Pure ASCII would be detected as ASCII (codepage 20127) by Ude;
                // include non-ASCII so detection resolves to UTF-8 without BOM.
                var p = Path.Combine(tmpRoot, "utf8-plain.txt");
                File.WriteAllBytes(p, Encoding.UTF8.GetBytes("hello — τέλος — 終わり\n"));
                var enc = EncodingHelper.DetectEncoding(p);
                Assert(enc is UTF8Encoding u && u.GetPreamble().Length == 0,
                    "detect UTF-8 (no BOM) on non-ASCII content");
            }

            {
                var p = Path.Combine(tmpRoot, "utf8-bom.txt");
                var enc = new UTF8Encoding(true);
                File.WriteAllBytes(p, enc.GetPreamble().Concat(enc.GetBytes("hello")).ToArray());
                var detected = EncodingHelper.DetectEncoding(p);
                Assert(detected is UTF8Encoding u && u.GetPreamble().Length == 3,
                    "detect UTF-8 BOM");
            }

            {
                var p = Path.Combine(tmpRoot, "utf16-le.txt");
                var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                File.WriteAllBytes(p, enc.GetPreamble().Concat(enc.GetBytes("hello")).ToArray());
                var detected = EncodingHelper.DetectEncoding(p);
                Assert(detected.CodePage == Encoding.Unicode.CodePage, "detect UTF-16 LE BOM");
            }

            {
                var p = Path.Combine(tmpRoot, "utf16-be.txt");
                var enc = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
                File.WriteAllBytes(p, enc.GetPreamble().Concat(enc.GetBytes("hello")).ToArray());
                var detected = EncodingHelper.DetectEncoding(p);
                Assert(detected.CodePage == Encoding.BigEndianUnicode.CodePage, "detect UTF-16 BE BOM");
            }

            {
                var p = Path.Combine(tmpRoot, "sjis.txt");
                var sjis = Encoding.GetEncoding("shift_jis");
                File.WriteAllBytes(p, sjis.GetBytes("先頭行\r\n日本語のテスト\r\n末尾行\r\n"));
                var detected = EncodingHelper.DetectEncoding(p);
                Assert(detected.CodePage == sjis.CodePage, "detect Shift-JIS via Ude heuristic");
            }

            {
                var p = Path.Combine(tmpRoot, "eucjp.txt");
                var euc = Encoding.GetEncoding("euc-jp");
                File.WriteAllBytes(p, euc.GetBytes("日本語のサンプルをもう少し長めに書いてみる\n"));
                var detected = EncodingHelper.DetectEncoding(p);
                Assert(detected.CodePage == euc.CodePage, "detect EUC-JP via Ude heuristic");
            }

            // --- Newline + trailing newline detection ---

            {
                var p = Path.Combine(tmpRoot, "crlf.txt");
                File.WriteAllText(p, "a\r\nb\r\nc\r\n", new UTF8Encoding(false));
                var m = FileMetadataHelper.DetectFileMetadata(p);
                Assert(m.NewlineSequence == "\r\n" && m.HasTrailingNewline, "detect CRLF + trailing newline");
            }

            {
                var p = Path.Combine(tmpRoot, "lf-no-trailing.txt");
                File.WriteAllText(p, "a\nb\nc", new UTF8Encoding(false));
                var m = FileMetadataHelper.DetectFileMetadata(p);
                Assert(m.NewlineSequence == "\n" && !m.HasTrailingNewline, "detect LF with no trailing newline");
            }

            {
                var p = Path.Combine(tmpRoot, "cr-only.txt");
                File.WriteAllText(p, "a\rb\rc\r", new UTF8Encoding(false));
                var m = FileMetadataHelper.DetectFileMetadata(p);
                Assert(m.NewlineSequence == "\r" && m.HasTrailingNewline, "detect bare CR newline");
            }

            // --- EditFile round-trip: Shift-JIS CRLF with LF old_string ---

            {
                var p = Path.Combine(tmpRoot, "sjis-crlf-edit.txt");
                var sjis = Encoding.GetEncoding("shift_jis");
                File.WriteAllBytes(p, sjis.GetBytes("先頭行\r\n日本語のテスト\r\n末尾行\r\n"));

                // old_string uses \n even though file is CRLF
                var result = FileTools.EditFile(p, "日本語のテスト", "NIHONGO").GetAwaiter().GetResult();
                Assert(result.StartsWith("Replaced 1"), $"EditFile on SJIS CRLF succeeds ({result})");

                var after = File.ReadAllBytes(p);
                var decoded = sjis.GetString(after);
                Assert(decoded == "先頭行\r\nNIHONGO\r\n末尾行\r\n",
                    "SJIS CRLF round-trip: encoding + CRLF preserved, content replaced");
                Assert(!(after.Length >= 3 && after[0] == 0xEF && after[1] == 0xBB && after[2] == 0xBF),
                    "SJIS CRLF round-trip: no stray UTF-8 BOM introduced");
                int crlfCount = 0;
                for (int i = 0; i < after.Length - 1; i++)
                    if (after[i] == 0x0D && after[i + 1] == 0x0A) crlfCount++;
                Assert(crlfCount == 3, $"SJIS CRLF round-trip: 3 CRLF kept (got {crlfCount})");
            }

            // --- EditFile with old_string spanning a newline ---

            {
                var p = Path.Combine(tmpRoot, "multiline-edit.txt");
                File.WriteAllText(p, "line1\r\nline2\r\nline3\r\n", new UTF8Encoding(false));
                // Pass old_string with \n; file has \r\n. Must still match.
                var result = FileTools.EditFile(p, "line1\nline2", "LINE1\nLINE2").GetAwaiter().GetResult();
                Assert(result.StartsWith("Replaced 1"), $"multi-line edit with LF old_string ({result})");
                var after = File.ReadAllText(p, new UTF8Encoding(false));
                Assert(after == "LINE1\r\nLINE2\r\nline3\r\n", $"multi-line edit preserves CRLF (got [{after.Replace("\r", "\\r").Replace("\n", "\\n")}])");
            }

            // --- WriteFile overwrite preserves encoding + newline ---

            {
                var p = Path.Combine(tmpRoot, "overwrite.txt");
                var sjis = Encoding.GetEncoding("shift_jis");
                // Ude needs enough content to reliably detect SJIS; use a longer sample.
                File.WriteAllBytes(p, sjis.GetBytes("初期のサンプル内容をある程度長く書く\r\nもう一行追加しておく\r\n"));

                // Claude passes LF content; WriteFile must re-emit as SJIS + CRLF
                FileTools.WriteFile(p, "新しい内容\n2行目\n").GetAwaiter().GetResult();

                var after = File.ReadAllBytes(p);
                var decoded = sjis.GetString(after);
                Assert(decoded == "新しい内容\r\n2行目\r\n",
                    $"WriteFile overwrite: SJIS + CRLF preserved from existing file (got [{decoded.Replace("\r", "\\r").Replace("\n", "\\n")}])");
            }

            // --- WriteFile on new file uses UTF-8 no BOM ---

            {
                var p = Path.Combine(tmpRoot, "new-file.txt");
                FileTools.WriteFile(p, "hello\nworld\n").GetAwaiter().GetResult();
                var bytes = File.ReadAllBytes(p);
                Assert(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF),
                    "WriteFile on new file has no UTF-8 BOM");
                Assert(Encoding.UTF8.GetString(bytes) == "hello\nworld\n",
                    "WriteFile on new file keeps Claude's newline choice (LF)");
            }

            // --- WriteFile with explicit encoding: convert existing SJIS → UTF-8 ---

            {
                var p = Path.Combine(tmpRoot, "convert-to-utf8.txt");
                var sjis = Encoding.GetEncoding("shift_jis");
                File.WriteAllBytes(p, sjis.GetBytes("変換元のサンプル内容を十分な長さで書いておく\r\n二行目も書いておく\r\n"));

                FileTools.WriteFile(p, "変換後\n", encoding: "utf-8").GetAwaiter().GetResult();

                var after = File.ReadAllBytes(p);
                Assert(!(after.Length >= 3 && after[0] == 0xEF && after[1] == 0xBB && after[2] == 0xBF),
                    "SJIS → UTF-8 conversion: no BOM");
                Assert(Encoding.UTF8.GetString(after) == "変換後\r\n",
                    $"SJIS → UTF-8 conversion: content correct, CRLF preserved from original");
            }

            // --- WriteFile with explicit encoding: new file as UTF-16 LE BOM ---

            {
                var p = Path.Combine(tmpRoot, "new-utf16.txt");
                FileTools.WriteFile(p, "hello\nworld\n", encoding: "utf-16").GetAwaiter().GetResult();
                var after = File.ReadAllBytes(p);
                Assert(after.Length >= 2 && after[0] == 0xFF && after[1] == 0xFE,
                    "new file with encoding=utf-16: LE BOM present");
                var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                Assert(enc.GetString(after, 2, after.Length - 2) == "hello\nworld\n",
                    "new file with encoding=utf-16: content correct");
            }

            // --- WriteFile with explicit encoding: convert UTF-8 → SJIS ---

            {
                var p = Path.Combine(tmpRoot, "convert-to-sjis.txt");
                File.WriteAllText(p, "元データ\n", new UTF8Encoding(false));

                FileTools.WriteFile(p, "SJISに変換\n", encoding: "sjis").GetAwaiter().GetResult();

                var after = File.ReadAllBytes(p);
                var sjis = Encoding.GetEncoding("shift_jis");
                Assert(sjis.GetString(after) == "SJISに変換\n",
                    $"UTF-8 → SJIS conversion: content correct, LF preserved");
            }

            // --- ReadFile on UTF-16 file (regression: was flagged as binary) ---

            {
                var p = Path.Combine(tmpRoot, "utf16-read.txt");
                var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                File.WriteAllBytes(p, enc.GetPreamble().Concat(enc.GetBytes("alpha\nbeta\ngamma\n")).ToArray());
                var output = FileTools.ReadFile(p).GetAwaiter().GetResult();
                Assert(!output.StartsWith("Error:"), $"UTF-16 BOM file not flagged as binary ({output.Split('\n')[0]})");
                Assert(output.Contains("alpha") && output.Contains("beta") && output.Contains("gamma"),
                    "UTF-16 ReadFile decodes content");
            }

            // --- EditFile on UTF-16 preserves BOM ---

            {
                var p = Path.Combine(tmpRoot, "utf16-edit.txt");
                var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                File.WriteAllBytes(p, enc.GetPreamble().Concat(enc.GetBytes("alpha\nbeta\ngamma\n")).ToArray());
                FileTools.EditFile(p, "beta", "BETA").GetAwaiter().GetResult();
                var after = File.ReadAllBytes(p);
                Assert(after.Length >= 2 && after[0] == 0xFF && after[1] == 0xFE, "UTF-16 LE BOM preserved after edit");
                Assert(enc.GetString(after, 2, after.Length - 2) == "alpha\nBETA\ngamma\n",
                    "UTF-16 edit content correct");
            }

            // --- EditFile: non-existent old_string returns error ---

            {
                var p = Path.Combine(tmpRoot, "notfound.txt");
                File.WriteAllText(p, "some content", new UTF8Encoding(false));
                var result = FileTools.EditFile(p, "missing", "X").GetAwaiter().GetResult();
                Assert(result.StartsWith("Error: old_string not found"), $"missing old_string → error ({result})");
            }

            // --- EditFile: non-unique old_string without replace_all returns error ---

            {
                var p = Path.Combine(tmpRoot, "duplicate.txt");
                File.WriteAllText(p, "a\na\na\n", new UTF8Encoding(false));
                var result = FileTools.EditFile(p, "a", "b").GetAwaiter().GetResult();
                Assert(result.Contains("found 3 times"), $"duplicate old_string → count error ({result})");
            }

            // --- EditFile: replace_all replaces every occurrence ---

            {
                var p = Path.Combine(tmpRoot, "replace-all.txt");
                File.WriteAllText(p, "a\na\na\n", new UTF8Encoding(false));
                var result = FileTools.EditFile(p, "a", "b", replace_all: true).GetAwaiter().GetResult();
                Assert(result.StartsWith("Replaced 3"), $"replace_all count ({result})");
                var after = File.ReadAllText(p);
                Assert(after == "b\nb\nb\n", $"replace_all content ({after.Replace("\n", "\\n")})");
            }
        }
        finally
        {
            try { Directory.Delete(tmpRoot, recursive: true); } catch { }
        }

        Console.WriteLine($"  Total: {pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }
}
