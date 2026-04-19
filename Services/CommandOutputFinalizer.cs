using System.Text;

namespace Ripple.Services;

/// <summary>
/// Slice-reader-driven cleaner that turns a raw
/// <see cref="CommandOutputCapture"/> window into the string the MCP
/// client sees. Moved out of the old <c>CommandTracker.CleanOutput</c>
/// to:
///   - operate on the capture's bounded slice reader instead of one
///     unbounded "_aiOutput" string, so arbitrarily large command
///     output never materializes as a single allocation here.
///   - run in the worker's finalize-once path, so inline
///     <c>execute_command</c> and deferred
///     <c>wait_for_completion</c> always go through the same code.
///
/// Not responsible for: truncation (that's
/// <see cref="OutputTruncationHelper"/>), echo stripping (that's
/// <see cref="EchoStripper"/>), or shell-specific post-prompt
/// settling (the worker handles that before calling
/// <see cref="Clean"/>).
/// </summary>
internal static class CommandOutputFinalizer
{
    /// <summary>
    /// Read the command window out of <paramref name="capture"/>, strip
    /// ANSI escapes (except SGR), normalize CRLF to LF, and drop pwsh
    /// continuation-prompt lines (">>", ">> ..."). The result is the
    /// cleaned finalized string that the worker hands to
    /// <see cref="OutputTruncationHelper"/> — at or under
    /// <see cref="CommandOutputCapture.MaxInlineSliceChars"/> the slice
    /// is read as one <c>string</c>, otherwise it streams through the
    /// capture's <see cref="System.IO.TextReader"/> so large captures
    /// never force a single big allocation here.
    /// </summary>
    public static string Clean(
        CommandOutputCapture capture,
        long commandStart,
        long commandEnd)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (commandEnd < commandStart) commandEnd = commandStart;
        long length = commandEnd - commandStart;
        if (length <= 0) return "";

        string raw;
        if (length <= CommandOutputCapture.MaxInlineSliceChars)
        {
            raw = capture.ReadSlice(commandStart, length);
        }
        else
        {
            // Stream the slice in page-sized chunks. The StringBuilder
            // still holds the whole cleaned result, but that's
            // intentional — this is the pre-truncation path; the
            // OutputTruncationHelper will immediately spill if the
            // cleaned stream is over threshold, so callers shouldn't
            // rely on us staying under MaxInlineSliceChars here.
            //
            // No initial capacity hint: `length` is the caller-provided
            // upper bound and only gets clamped inside OpenSliceReader,
            // so a mismatched commandEnd against a tiny capture would
            // otherwise pre-allocate up to int.MaxValue chars (~4 GB)
            // before the reader reports the real smaller size. Letting
            // the StringBuilder grow on demand costs a few reallocs on
            // the large-slice path and nothing at all when the slice is
            // bogus.
            using var reader = capture.OpenSliceReader(commandStart, length);
            var sb = new StringBuilder();
            var buf = new char[8 * 1024];
            int n;
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                sb.Append(buf, 0, n);
            raw = sb.ToString();
        }

        return CleanString(raw);
    }

    /// <summary>
    /// Clean a finalized command-window string. Separated from
    /// <see cref="Clean"/> so callers (echo stripping) that already
    /// hold a string don't pay a capture round-trip.
    /// </summary>
    public static string CleanString(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var stripped = StripAnsi(raw);
        var lines = stripped.Split('\n');
        var cleaned = new List<string>(lines.Length);
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimEnd();
            // pwsh continuation prompt lines from multi-line input aren't
            // command output and look jarring in the result.
            if (trimmed == ">>" || trimmed.StartsWith(">> ")) continue;
            cleaned.Add(line);
        }

        return string.Join('\n', cleaned).Trim();
    }

    /// <summary>
    /// Strip non-SGR ANSI sequences and collapse progress-bar / spinner
    /// redraws so the AI-facing MCP output shows the final state of each
    /// line instead of every intermediate frame. Bare CR overwrites the
    /// current line (dotnet-build style "\r[10%]\r[20%]..." spinners);
    /// CSI cursor-up (A) rewinds the committed line list (msbuild's
    /// "\x1b[NA\x1b[K..." multi-row status block); CSI erase-in-line
    /// (K) / erase-in-display (J) clear the current line. SGR ('m') is
    /// kept verbatim for color. OSC / character-set / keypad sequences
    /// are dropped.
    ///
    /// The visible console mirror (`MirrorToVisible`) keeps receiving
    /// raw PTY bytes, so the human sees a live progress bar the way the
    /// shell intended. Only the MCP-response `output` passes through
    /// this collapse.
    /// </summary>
    private static string StripAnsi(string text)
    {
        // Lines-of-StringBuilder + cursor row model. Each row holds
        // arbitrary-length content (no column grid — output lines aren't
        // capped to a viewport width). cursor-up / cursor-down move the
        // row index; bare CR / EL / ED clear the current row in place;
        // LF advances row without touching the new row's existing
        // content (that's the right terminal semantics — LF is a
        // cursor move, not an overwrite).
        var lines = new List<StringBuilder> { new StringBuilder() };
        int row = 0;

        void EnsureRow()
        {
            while (lines.Count <= row) lines.Add(new StringBuilder());
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    // CRLF — a real newline. Move down a row, leave the
                    // new row's content alone.
                    row++;
                    EnsureRow();
                    i += 2;
                }
                else
                {
                    // Bare CR — rewind to column 0 in place; subsequent
                    // writes overwrite this row.
                    lines[row].Clear();
                    i++;
                }
            }
            else if (c == '\n')
            {
                row++;
                EnsureRow();
                i++;
            }
            else if (c == '\b')
            {
                // Backspace spinner — drop the last char on the row.
                if (lines[row].Length > 0) lines[row].Length--;
                i++;
            }
            else if (c == '\x1b')
            {
                if (i + 1 >= text.Length) { i++; continue; }
                char next = text[i + 1];
                if (next == '[')
                {
                    int paramStart = i + 2;
                    int j = paramStart;
                    while (j < text.Length && text[j] >= 0x30 && text[j] <= 0x3f) j++;
                    string paramsStr = j > paramStart ? text.Substring(paramStart, j - paramStart) : "";
                    while (j < text.Length && text[j] >= 0x20 && text[j] <= 0x2f) j++;
                    if (j >= text.Length) { i = text.Length; continue; }
                    char final = text[j];
                    int n = 1;
                    if (paramsStr.Length > 0 && int.TryParse(paramsStr, out var parsed) && parsed > 0) n = parsed;

                    if (final == 'm')
                    {
                        // SGR — keep verbatim so colors survive.
                        lines[row].Append(text, i, j - i + 1);
                    }
                    else if (final == 'A')
                    {
                        // Cursor up N — clamp at row 0.
                        row = Math.Max(0, row - n);
                    }
                    else if (final == 'B')
                    {
                        // Cursor down N — extend the row list as needed.
                        row += n;
                        EnsureRow();
                    }
                    else if (final == 'K' || final == 'J')
                    {
                        // Erase in line / display — clear the current
                        // row. EL/ED mode params (0/1/2) all collapse to
                        // "forget the current progress frame" in our
                        // AI-facing collapse model.
                        lines[row].Clear();
                    }
                    // Other CSI finals (CUF/CUB/CUP/HVP/SU/SD/DSR/DA/etc.)
                    // are dropped silently — they don't contribute to
                    // what the AI needs to see.
                    i = j + 1;
                }
                else if (next == ']')
                {
                    // OSC — drop non-greedily to first BEL or ESC\.
                    int j = i + 2;
                    while (j < text.Length)
                    {
                        if (text[j] == '\x07') { j++; break; }
                        if (text[j] == '\x1b' && j + 1 < text.Length && text[j + 1] == '\\') { j += 2; break; }
                        j++;
                    }
                    i = j;
                }
                else if (next == '(' || next == ')')
                {
                    // Character-set designation \e(<byte> / \e)<byte>.
                    i = Math.Min(i + 3, text.Length);
                }
                else if (next == '=' || next == '>')
                {
                    // DECKPAM / DECKPNM — drop.
                    i += 2;
                }
                else
                {
                    // Other single-char ESC sequences — drop follower too.
                    i += 2;
                }
            }
            else
            {
                lines[row].Append(c);
                i++;
            }
        }

        // Trim trailing empty rows (left over from a final LF or
        // cursor-down-without-write) so the join below doesn't tack
        // a stray newline on the result.
        int last = lines.Count - 1;
        while (last >= 0 && lines[last].Length == 0) last--;
        if (last < 0) return "";

        var sb = new StringBuilder();
        for (int k = 0; k <= last; k++)
        {
            if (k > 0) sb.Append('\n');
            sb.Append(lines[k]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Deterministic echo stripping for adapters that declare
/// <c>input_echo_strategy: deterministic_byte_match</c> (cmd, python,
/// any REPL without a stdlib pre-input hook that can emit OSC C). The
/// adapter's PTY stream interleaves the worker's PTY-write bytes with
/// the command's real output; there is no marker separating the two,
/// so the finalizer strips exactly the payload bytes the worker wrote
/// (minus the Enter keystroke) from the head of the cleaned window.
///
/// Moved out of <c>ConsoleWorker.StripCmdInputEcho</c> unchanged in
/// behaviour — the matching rules (skip CR/LF injected by ConPTY's
/// soft-wrap, fail closed when bytes diverge) are preserved verbatim.
/// </summary>
internal static class EchoStripper
{
    /// <summary>
    /// Strip the PTY payload (with line-ending removed) off the head
    /// of <paramref name="output"/>. Returns the original string
    /// unchanged when the head does not match the expected echo,
    /// matching the old tracker behaviour of failing open rather
    /// than silently mangling output.
    /// </summary>
    public static string Strip(string output, string ptyPayload, string lineEnding)
    {
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(ptyPayload))
            return output;

        var sentInput = ptyPayload;
        if (!string.IsNullOrEmpty(lineEnding) && sentInput.EndsWith(lineEnding))
            sentInput = sentInput[..^lineEnding.Length];

        if (sentInput.Length == 0) return output;

        int oi = 0;
        int ci = 0;
        while (ci < sentInput.Length && oi < output.Length)
        {
            var oc = output[oi];
            // ConPTY wraps long input echo at terminal width by injecting
            // CR/LF into the output stream — those bytes were never in the
            // typed command, so skip them while continuing to match.
            if (oc is '\r' or '\n')
            {
                oi++;
                continue;
            }

            if (oc != sentInput[ci])
                return output;

            oi++;
            ci++;
        }

        if (ci < sentInput.Length)
            return output;

        while (oi < output.Length && output[oi] is '\r' or '\n')
            oi++;

        return output[oi..];
    }
}
