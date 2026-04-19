using Ripple.Services;

namespace Ripple.Tests;

public static class RegexPromptDetectorTests
{
    public static void Run()
    {
        var pass = 0;
        var fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        Console.WriteLine("=== RegexPromptDetector Tests ===");

        // Python-style primary prompt: >>> at line start (or buffer start).
        // The trailing space is part of the visible prompt but optional in
        // the regex to tolerate pasted input.
        const string pythonPrompt = @"(^|\n)>>> ";

        // Fresh chunk with a single prompt at start.
        {
            var d = new RegexPromptDetector(pythonPrompt);
            var matches = d.Scan(">>> ");
            Assert(matches.Count == 1 && matches[0].Start == 0 && matches[0].End == 4,
                $"prompt at chunk start: Start=0, End=4 (got {FormatMatches(matches)})");
        }

        // Prompt after output. Start should be RIGHT AFTER the leading \n
        // — the visible-prompt boundary the worker uses for synthetic
        // CommandFinished + PromptStart placement.
        {
            var d = new RegexPromptDetector(pythonPrompt);
            var chunk = "hello\n>>> ";
            var matches = d.Scan(chunk);
            Assert(matches.Count == 1 && matches[0].Start == 6 && matches[0].End == chunk.Length,
                $"prompt after output: Start=6 (after \\n), End=chunk.Length (got {FormatMatches(matches)})");
        }

        // Two prompts in one chunk (rare but possible on fast commands).
        {
            var d = new RegexPromptDetector(pythonPrompt);
            var chunk = ">>> 1\n>>> ";
            var matches = d.Scan(chunk);
            Assert(matches.Count == 2,
                $"two prompts in one chunk (got {matches.Count})");
        }

        // No prompt: nothing emitted.
        {
            var d = new RegexPromptDetector(pythonPrompt);
            var matches = d.Scan("just some output with no prompt\n");
            Assert(matches.Count == 0,
                $"no prompt, no events (got {matches.Count})");
        }

        // Prompt split across chunk boundary — the \n arrives in chunk 1,
        // the >>> in chunk 2. The detector must carry the \n in its buffer
        // so the match anchors correctly.
        {
            var d = new RegexPromptDetector(pythonPrompt);
            var off1 = d.Scan("some output\n");
            Assert(off1.Count == 0, "partial chunk (trailing newline only) no match");
            var off2 = d.Scan(">>> ");
            Assert(off2.Count == 1,
                $"boundary-spanning prompt matches on the second chunk (got {off2.Count})");
        }

        // Prompt split mid-sequence: "\n>>" in chunk 1, "> " in chunk 2.
        {
            var d = new RegexPromptDetector(pythonPrompt);
            var off1 = d.Scan("hello\n>>");
            Assert(off1.Count == 0, "partial '>>' alone no match");
            var off2 = d.Scan("> ");
            Assert(off2.Count == 1,
                $"'>>> ' split across two chunks matches (got {off2.Count})");
        }

        // Already-reported prompt is not re-reported on the next scan.
        {
            var d = new RegexPromptDetector(pythonPrompt);
            var off1 = d.Scan("line\n>>> ");
            Assert(off1.Count == 1, "first scan reports the prompt once");
            var off2 = d.Scan("next line\n");
            Assert(off2.Count == 0, "second scan does NOT re-report the same prompt");
        }

        // Output containing ">>>" that isn't at line start must not match.
        {
            var d = new RegexPromptDetector(pythonPrompt);
            var matches = d.Scan("look: >>> that is mid-line");
            Assert(matches.Count == 0,
                $"'>>>' mid-line does NOT match (got {matches.Count})");
        }

        // irb-style prompt with a numeric counter — proves the detector
        // is not Python-specific and handles regex patterns other than
        // a fixed literal.
        {
            const string irbPrompt = @"(^|\n)irb\(main\):\d+:\d+> ";
            var d = new RegexPromptDetector(irbPrompt);
            var matches = d.Scan("irb(main):001:0> ");
            Assert(matches.Count == 1,
                $"irb prompt regex fires on match (got {matches.Count})");
            var matches2 = d.Scan("=> 2\nirb(main):002:0> ");
            Assert(matches2.Count == 1,
                $"irb counter increments, still matches (got {matches2.Count})");
        }

        // --- CSI-aware scenarios (fsi-style ConPTY rendering) ---
        // These exercise the CSI strip + offset-translation path. The
        // adapter author writes `^> $` against the visible text; the
        // detector silently strips cursor positioning and color escapes
        // before matching, then translates the match position back to
        // the original-coordinate buffer the caller passes in.

        // CSI noise around the prompt token (one chunk).
        {
            var d = new RegexPromptDetector(@"^> $");
            var chunk = "\x1b[?25l\x1b[m it: int = 2\x1b[12;1H> \x1b[?25h";
            var matches = d.Scan(chunk);
            Assert(matches.Count == 1,
                $"CSI: prompt with cursor positioning + show-cursor matches (got {matches.Count})");
            // End should land at chunk.Length (past trailing \x1b[?25h).
            // Start should land right after the cursor-positioning CSI
            // that the strip path substituted for a synthesized newline.
            if (matches.Count == 1)
            {
                Assert(matches[0].End == chunk.Length,
                    $"CSI: End is past trailing CSI (got {matches[0].End}, expected {chunk.Length})");
                Assert(matches[0].Start < matches[0].End,
                    $"CSI: Start ({matches[0].Start}) precedes End ({matches[0].End})");
            }
        }

        // CSI noise WITHOUT a `> ` token must not match `^> $`.
        {
            var d = new RegexPromptDetector(@"^> $");
            var chunk = "\x1b[?25l\x1b[m it: int = 2\x1b[12;1H\x1b[?25h";  // no `> `
            var matches = d.Scan(chunk);
            Assert(matches.Count == 0,
                $"CSI: chunk without `> ` does NOT match (got {matches.Count})");
        }

        // Prompt arriving as a separate chunk after the CSI tail of the
        // previous one. Buffer carry-over keeps the trailing CSI bytes
        // so the next scan re-strips with the full context.
        {
            var d = new RegexPromptDetector(@"^> $");
            // First chunk: result text + cursor positioning + show-cursor,
            // but no `> `.
            var off1 = d.Scan("\x1b[?25l it: int = 2\x1b[12;1H\x1b[?25h");
            Assert(off1.Count == 0,
                $"CSI: first chunk without `> ` does not match (got {off1.Count})");
            // Second chunk: just the `> ` (with a leading color reset).
            var off2 = d.Scan("\x1b[m> ");
            Assert(off2.Count == 1,
                $"CSI: `> ` arriving in next chunk matches via buffer (got {off2.Count})");
        }

        // Trailing `\n` after the prompt (some shells emit a fresh line
        // before the prompt). Must still match `^> $` because $ in
        // Multiline mode matches before \n.
        {
            var d = new RegexPromptDetector(@"^> $");
            var offsets = d.Scan("\x1b[m> \n");
            Assert(offsets.Count == 1,
                $"CSI: prompt followed by newline matches (got {offsets.Count})");
        }

        // Color escapes interleaved with the prompt characters
        // themselves (defensive: some REPLs colorize the prompt).
        {
            var d = new RegexPromptDetector(@"^> $");
            var chunk = "\x1b[38;5;15m> \x1b[m";
            var offsets = d.Scan(chunk);
            Assert(offsets.Count == 1,
                $"CSI: colorized `> ` strips to plain `> ` and matches (got {offsets.Count})");
        }

        // Pattern with a literal non-ASCII anchor character (not a CSI
        // case but verifies the strip pass doesn't mangle plain text).
        {
            var d = new RegexPromptDetector(@"λ> $");
            var offsets = d.Scan("\x1b[mλ> \n");
            Assert(offsets.Count == 1,
                $"CSI: lambda prompt stripped of CSI matches (got {offsets.Count})");
        }

        // Cursor Forward (CUF) substitution — `\x1b[1C` is visually a
        // single space (assuming no pre-existing content in that cell).
        // fsi's error-recovery prompt uses this instead of a literal
        // space: `\r\n>\x1b[1C`. Substituting CUF with spaces keeps the
        // stripped text visually faithful to what the user sees on
        // screen and lets `^> $` match both normal and error cases.
        {
            var d = new RegexPromptDetector(@"^> $");
            // fsi's error-recovery shape: newline, `>`, CUF(1).
            var offsets = d.Scan("\r\n>\x1b[1C");
            Assert(offsets.Count == 1,
                $"CSI: `>` + CUF(1) matches `^> $` via CUF→space substitution (got {offsets.Count})");
        }

        // CUF with explicit parameter (3 columns).
        {
            var d = new RegexPromptDetector(@"^>   $");
            var offsets = d.Scan("\r\n>\x1b[3C");
            Assert(offsets.Count == 1,
                $"CSI: CUF(3) substitutes 3 spaces (got {offsets.Count})");
        }

        // Malformed CUF parameter falls back to default of 1 column.
        {
            var d = new RegexPromptDetector(@"^> $");
            var offsets = d.Scan("\r\n>\x1b[C");   // empty param → default 1
            Assert(offsets.Count == 1,
                $"CSI: empty CUF param defaults to 1 column (got {offsets.Count})");
        }

        // OSC title set (`ESC ] 0 ; <title> BEL`) interleaved with a prompt.
        // This is the jdb adapter's exact failure mode pre-fix: ConPTY
        // emits `\x1b]0;<path-to-jdb.exe>\x07` right after the banner, and
        // the previous version of the stripper (CSI-only) left it intact,
        // pushing the subsequent `> ` prompt off column 1 and preventing
        // `^> $` from matching. The OSC branch in StripCsiWithMap now
        // drops these sequences entirely so the prompt regex anchors
        // correctly regardless of window-title noise from the host.
        {
            var d = new RegexPromptDetector(@"^> $");
            var chunk = "\x1b]0;C:\\Program Files\\Microsoft\\jdk\\bin\\jdb.exe\x07> ";
            var offsets = d.Scan(chunk);
            Assert(offsets.Count == 1,
                $"OSC: title-setter (`ESC ] 0 ; ... BEL`) stripped, `^> $` matches (got {offsets.Count})");
        }

        // OSC with ST (`ESC \`) terminator — the strict ECMA-48 form.
        // Less common than BEL in practice but a valid terminator the
        // stripper must also handle to avoid leaving OSC payloads in the
        // matching text.
        {
            var d = new RegexPromptDetector(@"^> $");
            var chunk = "\x1b]0;title\x1b\\> ";
            var offsets = d.Scan(chunk);
            Assert(offsets.Count == 1,
                $"OSC: ST terminator (`ESC \\\\`) form also stripped (got {offsets.Count})");
        }

        // OSC followed by a CSI cursor-show, then prompt — real jdb chunk
        // layout as captured during root-cause investigation (2026-04-16).
        // The stripper must handle both escape families in sequence; if
        // the OSC branch ever gets the loop state wrong, the trailing CSI
        // pass is where it would surface as a leaked `\x1b[?25h` in the
        // stripped text.
        {
            var d = new RegexPromptDetector(@"^> $");
            var chunk = "\x1b]0;jdb.exe\x07\x1b[?25h> ";
            var offsets = d.Scan(chunk);
            Assert(offsets.Count == 1,
                $"OSC + CSI sequence: both stripped cleanly (got {offsets.Count})");
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }

    private static string FormatMatches(System.Collections.Generic.List<RegexPromptDetector.PromptMatch> matches)
    {
        return "[" + string.Join(", ", matches.ConvertAll(m => $"({m.Start},{m.End})")) + "]";
    }
}
