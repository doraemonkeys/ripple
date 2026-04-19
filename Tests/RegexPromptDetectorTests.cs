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

        // ---- Prompt SGR-decoration backup ----

        // Non-reset SGR immediately before the visible prompt is
        // prompt decoration — back the Start past it.
        {
            var d = new RegexPromptDetector(@"DB<\d+>");
            var chunk = "result\x1b[4mDB<2>";  // \e[4m directly before DB
            var matches = d.Scan(chunk);
            // Match "DB<2>" at original 10 (after "result" + 4-byte SGR).
            // Back past the 4-byte \e[4m → Start = 6 (the \e position).
            Assert(matches.Count == 1,
                $"prompt-decoration SGR (non-reset): prompt matched (got {matches.Count})");
            if (matches.Count == 1)
            {
                Assert(matches[0].Start == 6,
                    $"prompt-decoration SGR: Start backed past non-reset SGR (got {matches[0].Start}, expected 6)");
            }
        }

        // SGR separated from prompt by whitespace — back-up does NOT
        // cross whitespace. Crossing would require adapter-specific
        // knowledge of prompt formatting (is the SGR for the prior
        // output or for the upcoming prompt?), which we can't infer
        // from bytes alone. Accept the cosmetic residue.
        {
            var d = new RegexPromptDetector(@"DB<\d+>");
            var chunk = "result\x1b[4m\r\n  DB<2>";
            var matches = d.Scan(chunk);
            Assert(matches.Count == 1,
                $"SGR + CRLF + prompt: prompt matched (got {matches.Count})");
            if (matches.Count == 1)
            {
                // Start sits at 'D' (after the +1 \n bump). Back-up
                // sees whitespace at start-1 and halts — the
                // separated SGR stays attributed to the prior content.
                Assert(matches[0].Start == 14,
                    $"SGR + whitespace: Start unchanged (got {matches[0].Start}, expected 14)");
            }
        }

        // Reset SGR right before the prompt is end-of-prior-output —
        // do NOT back past it.
        {
            var d = new RegexPromptDetector(@">>>");
            var chunk = "output\x1b[0m>>>";
            var matches = d.Scan(chunk);
            // Match ">>>" at original 10 (after "output" + "\e[0m").
            // Reset SGR is NOT decoration → Start stays at 10.
            Assert(matches.Count == 1,
                $"reset SGR before prompt: prompt matched (got {matches.Count})");
            if (matches.Count == 1)
            {
                Assert(matches[0].Start == 10,
                    $"reset SGR before prompt: Start NOT backed past reset (got {matches[0].Start}, expected 10)");
            }
        }

        // Multiple non-reset SGRs back-to-back — back past all of them.
        {
            var d = new RegexPromptDetector(@">");
            var chunk = "x\x1b[1m\x1b[31m>";  // x + \e[1m (4) + \e[31m (5) + > = 11 chars
            var matches = d.Scan(chunk);
            // Match at ">" at index 10. Back past "\e[31m" (start at
            // 9-3=...) actually let's count: positions 0='x', 1-4='\e[1m',
            // 5-9='\e[31m', 10='>'. Back past \e[31m (5 bytes) → start=5.
            // Back past \e[1m (4 bytes) → start=1.
            Assert(matches.Count == 1,
                $"multiple decoration SGRs: prompt matched (got {matches.Count})");
            if (matches.Count == 1)
            {
                Assert(matches[0].Start == 1,
                    $"multiple decoration SGRs: Start backed past all (got {matches[0].Start}, expected 1)");
            }
        }

        // Compound reset (\e[0;1;31m = reset + set) is still a reset:
        // do NOT back past it.
        {
            var d = new RegexPromptDetector(@">");
            var chunk = "x\x1b[0;1;31m>";  // 11 chars; > at index 10
            var matches = d.Scan(chunk);
            Assert(matches.Count == 1,
                $"compound-reset SGR: prompt matched (got {matches.Count})");
            if (matches.Count == 1)
            {
                Assert(matches[0].Start == 10,
                    $"compound-reset SGR: Start NOT backed past reset (got {matches[0].Start}, expected 10)");
            }
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }

    private static string FormatMatches(System.Collections.Generic.List<RegexPromptDetector.PromptMatch> matches)
    {
        return "[" + string.Join(", ", matches.ConvertAll(m => $"({m.Start},{m.End})")) + "]";
    }
}
