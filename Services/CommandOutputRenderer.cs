using System.Text;

namespace Ripple.Services;

/// <summary>
/// Terminal renderer specialized for AI-facing command output capture.
///
/// Unlike <see cref="VtLiteState"/>, which models a fixed viewport and
/// scrolls older content out of the screen, this renderer keeps the
/// full set of logical lines a command produced — scroll-out is
/// preserved so build logs that emit hundreds of lines come back
/// intact. There is intentionally no column-wrap limit either: lines
/// longer than any nominal terminal width stay as one logical line in
/// the output, which is what an LLM consumer actually wants.
///
/// Why a separate renderer (and not extending <see cref="VtLiteState"/>):
///   - VtLiteState is on the cursor-tracking hot path (DSR replies on
///     Unix). Adding scrollback + alt-screen accounting there would
///     complicate semantics every existing caller depends on.
///   - The output renderer's model — unbounded rows, cell-indexed
///     cursor that knows the difference between visible and SGR bytes,
///     alt-screen-as-placeholder — serves a fundamentally different
///     consumer (one-shot extraction at command finalize), so
///     conflating the two would force compromises on both sides.
///
/// Cell-based storage is what makes this renderer correct for cursor
/// positioning AND SGR preservation at the same time. The legacy
/// <c>StripAnsi</c> stored SGR bytes inline in a per-row StringBuilder,
/// which is fine for append-only writes but corrupts as soon as a
/// later write at column N overwrites SGR bytes that happen to occupy
/// that index. Each cell here carries one visible char plus an
/// optional SGR string emitted immediately before it on render — col
/// addresses cells, never raw bytes.
///
/// Escape-sequence handling (matches VtLiteState's parsing for the
/// medium subset, but with this renderer's row/col semantics):
///   - Bare <c>\r</c> moves cursor col → 0 AND clears the row from col 0
///     to end. Real terminals don't clear on CR; we do here because the
///     only reason a shell emits bare CR (without an explicit CSI K)
///     into output is to redraw the line, and a short rewrite after a
///     longer original would otherwise leak the trailing tail of the
///     original into the AI-facing output.
///   - <c>\r\n</c> as a pair is treated as a single newline (no row
///     clear) — that's a CRLF line ending, not a redraw.
///   - <c>\n</c> advances to next row AND resets col to 0. Matches
///     legacy <c>StripAnsi</c> behavior, which has always treated bare
///     LF as logical "next line, fresh column" — closer to what
///     AI consumers expect from test inputs that omit explicit CR
///     than to strict terminal LF semantics.
///   - <c>\b</c> moves cursor back one (does not erase). Subsequent
///     writes overwrite — that's what makes the "Working |\b/\b-..."
///     spinner pattern collapse to its final char.
///   - <c>\t</c> rounds col up to the next multiple of 8.
///   - CSI A/B/C/D/E/F/G/d (CUU/CUD/CUF/CUB/CNL/CPL/CHA/VPA) move
///     cursor; row clamps at 0 below, extends rows above on demand.
///   - CSI H/f (CUP/HVP) absolute positioning.
///   - CSI K (EL) erase-line modes 0/1/2 — applied to the current row.
///   - CSI J (ED) erase-display: mode 0 truncates from cursor to end of
///     output, mode 1 blanks all earlier content, mode 2 clears all and
///     resets cursor to row 0 col 0.
///   - CSI X (ECH) erase N chars at cursor without moving cursor.
///   - CSI P (DCH) / @ (ICH) delete / insert chars.
///   - CSI L / M (IL/DL) insert / delete blank rows at cursor row.
///   - SGR (m) is buffered and attached as a prefix to the next written
///     cell. Sequential SGRs without an intervening write coalesce so
///     <c>\e[31m\e[1m</c> emits once before the next visible char.
///   - OSC sequences are dropped (the OSC parser already extracts OSC
///     633 events upstream; any other OSC noise is stripped here as a
///     safety net).
///   - DEC private modes <c>?1049h/l</c>, <c>?1047h/l</c>, <c>?47h/l</c>
///     toggle the alternate screen. While alt-screen is active all
///     writes go nowhere and the entry is replaced in the final
///     output by a single placeholder line so that vim / less / htop
///     redraw frames don't flood the MCP response.
///
/// Out of scope (ripple's adapters do not emit these):
///   - Character-set selection (G0/G1).
///   - DEC line-drawing.
///   - True scroll regions (DECSTBM): for output rendering the whole
///     row list is the scroll region; partial-region scrolling would
///     only matter inside an alt-screen TUI, which we placeholder anyway.
///   - East-Asian wide-char width: each Cell is one column. CJK output
///     positioned via absolute cursor moves may misalign by the wide-
///     char count. Sequential writes (no cursor positioning) are
///     unaffected.
/// </summary>
internal sealed class CommandOutputRenderer
{
    /// <summary>
    /// Hard cap on the number of logical rows we'll retain. A pathological
    /// command that emits cursor-down / write loops without LFs could
    /// otherwise grow the row list without bound. 100k rows is well
    /// above any real build log; on overflow the oldest rows are
    /// dropped.
    /// </summary>
    public const int MaxRows = 100_000;

    /// <summary>
    /// Hard cap on the visible col coordinate the cursor can occupy. Bare
    /// CSI C / CSI G with absurd parameters (\e[2147483647C) must not
    /// allocate gigabytes of padding inside <see cref="WriteChar"/>.
    /// Chosen well above any practical terminal width.
    /// </summary>
    public const int MaxCol = 100_000;

    /// <summary>
    /// Placeholder line emitted in place of an alt-screen session
    /// (vim, less, htop, etc.). Keeping it short and recognisable so
    /// LLM consumers can tell something interactive happened without
    /// wading through redraw frames.
    /// </summary>
    public const string AltScreenPlaceholder = "[interactive screen session]";

    private readonly List<Row> _rows = new() { new Row() };
    private int _row;
    private int _col;
    private int _savedRow;
    private int _savedCol;

    // Pending SGR bytes that will attach to the next written cell.
    // Coalesces consecutive SGRs with no visible write between them
    // (e.g. \e[31m\e[1m) into one prefix string.
    private string? _pendingSgr;

    private bool _altActive;
    private int _altEntryRow;
    private int _altEntryCol;
    private bool _altEverEntered;

    /// <summary>
    /// True if at least one alt-screen session was observed during this
    /// render. Exposed for diagnostics; the placeholder line is already
    /// inserted into the output stream during processing.
    /// </summary>
    public bool VisitedAltScreen => _altEverEntered;

    /// <summary>
    /// Feed a chunk of cleaned output (OSC 633 already extracted upstream).
    /// </summary>
    public void Feed(ReadOnlySpan<char> text)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n') { LineFeed(); i++; }
            else if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    // CRLF — treat as a single newline (no row clear).
                    LineFeed();
                    i += 2;
                }
                else
                {
                    // Bare CR — cursor to col 0, then clear the row so a
                    // shorter rewrite doesn't leak the prior tail into
                    // the cleaned output. This is intentionally lossier
                    // than strict terminal CR semantics — see class
                    // doc comment.
                    _col = 0;
                    EraseLine(2);
                    i++;
                }
            }
            else if (c == '\b') { if (_col > 0) _col--; i++; }
            else if (c == '\t') { _col = Math.Min(((_col / 8) + 1) * 8, MaxCol); i++; }
            else if (c == '\x1b') { i = ParseEscape(text, i); }
            else if (c >= ' ') { WriteChar(c); i++; }
            else { i++; /* drop other C0 */ }
        }
    }

    public void Feed(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Feed(text.AsSpan());
    }

    /// <summary>
    /// Render the accumulated rows as a single string. Trailing blank
    /// rows are dropped; per-row trailing space-only cells are trimmed
    /// (a terminal pads blanks to its right margin during cursor moves;
    /// none of that padding is meaningful in a transcript). pwsh
    /// continuation-prompt rows ("&gt;&gt;", "&gt;&gt; ...") are filtered
    /// out — they are line-editor artifacts of multi-line input, not
    /// command output.
    /// </summary>
    public string Render()
    {
        int last = _rows.Count - 1;
        while (last >= 0 && IsBlank(_rows[last])) last--;
        if (last < 0) return "";

        var sb = new StringBuilder();
        bool firstEmitted = false;
        for (int r = 0; r <= last; r++)
        {
            var line = RenderRow(_rows[r]);
            if (line == ">>" || line.StartsWith(">> ")) continue;
            if (firstEmitted) sb.Append('\n');
            sb.Append(line);
            firstEmitted = true;
        }
        return sb.ToString();
    }

    // ---- internals ----

    private struct Cell
    {
        public char Ch;
        // SGR bytes (entire ESC[<params>m sequence) to emit immediately
        // before this cell on render. Null when no SGR change applies
        // here.
        public string? SgrPrefix;
    }

    private sealed class Row
    {
        public List<Cell> Cells { get; } = new();
        // Trailing SGR sequences that arrived after the last visible
        // write on this row. Emitted at the end of the row text on
        // render so a "before\x1b[31mred\x1b[0m" with the trailing
        // reset still emits the reset.
        public string? TrailingSgr;
    }

    private static bool IsBlank(Row r)
    {
        foreach (var cell in r.Cells)
            if (cell.Ch != ' ') return false;
        return r.TrailingSgr == null;
    }

    private static string RenderRow(Row r)
    {
        // Find last non-blank cell so we don't emit trailing space
        // padding (which only exists because cursor moves opened a
        // gap). SGR-only "blank" cells stay if they have a SgrPrefix
        // — losing those would silently drop color resets at end of
        // line.
        int last = r.Cells.Count - 1;
        while (last >= 0 && r.Cells[last].Ch == ' ' && r.Cells[last].SgrPrefix == null)
            last--;
        if (last < 0 && r.TrailingSgr == null) return "";

        var sb = new StringBuilder();
        for (int i = 0; i <= last; i++)
        {
            var cell = r.Cells[i];
            if (cell.SgrPrefix != null) sb.Append(cell.SgrPrefix);
            sb.Append(cell.Ch);
        }
        if (r.TrailingSgr != null) sb.Append(r.TrailingSgr);
        return sb.ToString();
    }

    private void EnsureRow(int targetRow)
    {
        if (targetRow < 0) { _row = 0; return; }
        if (targetRow >= MaxRows) targetRow = MaxRows - 1;
        while (_rows.Count <= targetRow) _rows.Add(new Row());
        _row = targetRow;
    }

    private void LineFeed()
    {
        // Flush pending SGR to the current row's trailing slot before we
        // leave it — otherwise a "text\e[0m\n" loses the reset.
        FlushPendingSgrAsTrailing();

        if (_row + 1 >= MaxRows)
        {
            // Hit the row cap — drop the oldest row to make room. Recent
            // output is more useful than the head when the cap kicks in.
            _rows.RemoveAt(0);
            _row = MaxRows - 1;
        }
        else
        {
            _row++;
        }
        EnsureRow(_row);
        _col = 0; // legacy semantics: bare LF behaves as CR+LF.
    }

    private void WriteChar(char c)
    {
        if (_altActive) return;
        if (_col >= MaxCol) return; // pathological cursor position; drop the write
        EnsureRow(_row);
        var cells = _rows[_row].Cells;

        // Pad with blank cells up to _col (only happens when a cursor
        // move opened a gap past the row's existing tail).
        while (cells.Count < _col) cells.Add(new Cell { Ch = ' ' });

        var prefix = _pendingSgr;
        _pendingSgr = null;

        if (_col < cells.Count)
        {
            // Overwrite an existing cell. If a new SGR prefix arrived
            // before this write, it replaces whatever the cell carried;
            // otherwise we KEEP the existing prefix so SGR set by a
            // prior pass through this row (e.g. coloured progress bar
            // re-redraw) still shows.
            var existing = cells[_col];
            cells[_col] = new Cell
            {
                Ch = c,
                SgrPrefix = prefix ?? existing.SgrPrefix
            };
        }
        else
        {
            cells.Add(new Cell { Ch = c, SgrPrefix = prefix });
        }
        _col++;
    }

    private void RecordSgr(string sgrSequence)
    {
        if (_altActive) return;
        // Coalesce consecutive SGRs with no intervening write so
        // \e[31m\e[1m emits as one prefix on the next char.
        _pendingSgr = _pendingSgr is null
            ? sgrSequence
            : _pendingSgr + sgrSequence;
    }

    private void FlushPendingSgrAsTrailing()
    {
        if (_altActive || _pendingSgr is null) return;
        EnsureRow(_row);
        var row = _rows[_row];
        row.TrailingSgr = row.TrailingSgr is null
            ? _pendingSgr
            : row.TrailingSgr + _pendingSgr;
        _pendingSgr = null;
    }

    private void EraseLine(int mode)
    {
        if (_altActive) return;
        EnsureRow(_row);
        var row = _rows[_row];
        var cells = row.Cells;
        if (mode == 0)
        {
            // cursor → end
            if (_col < cells.Count) cells.RemoveRange(_col, cells.Count - _col);
            row.TrailingSgr = null;
        }
        else if (mode == 1)
        {
            // start → cursor
            int end = Math.Min(_col + 1, cells.Count);
            for (int i = 0; i < end; i++) cells[i] = new Cell { Ch = ' ' };
        }
        else if (mode == 2)
        {
            cells.Clear();
            row.TrailingSgr = null;
        }
    }

    private void EraseDisplay(int mode)
    {
        if (_altActive) return;
        if (mode == 0)
        {
            EraseLine(0);
            if (_row + 1 < _rows.Count)
                _rows.RemoveRange(_row + 1, _rows.Count - (_row + 1));
        }
        else if (mode == 1)
        {
            for (int r = 0; r < _row; r++)
            {
                _rows[r].Cells.Clear();
                _rows[r].TrailingSgr = null;
            }
            EraseLine(1);
        }
        else if (mode == 2)
        {
            _rows.Clear();
            _rows.Add(new Row());
            _row = 0;
            _col = 0;
            _pendingSgr = null;
        }
    }

    private void EraseChars(int n)
    {
        if (_altActive) return;
        EnsureRow(_row);
        var cells = _rows[_row].Cells;
        n = Math.Max(1, n);
        int end = Math.Min(_col + n, cells.Count);
        for (int i = _col; i < end; i++) cells[i] = new Cell { Ch = ' ' };
    }

    private void DeleteChars(int n)
    {
        if (_altActive) return;
        EnsureRow(_row);
        var cells = _rows[_row].Cells;
        n = Math.Max(1, n);
        if (_col >= cells.Count) return;
        int actual = Math.Min(n, cells.Count - _col);
        cells.RemoveRange(_col, actual);
    }

    private void InsertChars(int n)
    {
        if (_altActive) return;
        EnsureRow(_row);
        var cells = _rows[_row].Cells;
        n = Math.Max(1, n);
        while (cells.Count < _col) cells.Add(new Cell { Ch = ' ' });
        for (int i = 0; i < n; i++) cells.Insert(_col, new Cell { Ch = ' ' });
    }

    private void InsertLines(int n)
    {
        if (_altActive) return;
        n = Math.Max(1, n);
        EnsureRow(_row);
        for (int i = 0; i < n && _rows.Count < MaxRows; i++)
            _rows.Insert(_row, new Row());
    }

    private void DeleteLines(int n)
    {
        if (_altActive) return;
        n = Math.Max(1, n);
        EnsureRow(_row);
        int actual = Math.Min(n, _rows.Count - _row);
        if (actual > 0) _rows.RemoveRange(_row, actual);
        if (_rows.Count == 0) _rows.Add(new Row());
        if (_row >= _rows.Count) _row = _rows.Count - 1;
    }

    private void EnterAlt()
    {
        if (_altActive) return;
        _altActive = true;
        _altEverEntered = true;
        _altEntryRow = _row;
        _altEntryCol = _col;
    }

    private void ExitAlt()
    {
        if (!_altActive) return;
        _altActive = false;
        // Restore cursor to the entry point and emit a placeholder line
        // marking where the interactive session was. Anything drawn
        // into the alt buffer is dropped — vim's redraw frames stay
        // out of the MCP response.
        _row = _altEntryRow;
        _col = _altEntryCol;
        EnsureRow(_row);

        // Insert the placeholder on a fresh row so it doesn't get glued
        // to whatever was on the entry row (typically the prompt line
        // before vim cleared the screen).
        if (_rows[_row].Cells.Count > 0 || _col > 0)
        {
            LineFeed();
        }
        var row = _rows[_row];
        row.Cells.Clear();
        row.TrailingSgr = null;
        foreach (var ch in AltScreenPlaceholder)
            row.Cells.Add(new Cell { Ch = ch });
        _col = AltScreenPlaceholder.Length;

        // Leave a fresh row after the placeholder so subsequent command
        // output (the next prompt, post-vim shell echo) doesn't get
        // appended to the placeholder line.
        LineFeed();
    }

    private int ParseEscape(ReadOnlySpan<char> input, int start)
    {
        // Caller guarantees input[start] == 0x1b.
        int i = start + 1;
        if (i >= input.Length) return input.Length; // dangling ESC at end — drop

        char next = input[i];
        if (next == '[')
        {
            int paramStart = i + 1;
            int j = paramStart;
            while (j < input.Length && input[j] >= 0x30 && input[j] <= 0x3f) j++;
            var paramsSpan = j > paramStart ? input.Slice(paramStart, j - paramStart) : ReadOnlySpan<char>.Empty;
            while (j < input.Length && input[j] >= 0x20 && input[j] <= 0x2f) j++;
            if (j >= input.Length) return input.Length; // unterminated CSI — drop
            char final = input[j];
            ApplyCsi(paramsSpan, final, input, start, j);
            return j + 1;
        }
        if (next == ']')
        {
            // OSC — drop to BEL or ST. The OSC parser upstream already
            // strips OSC 633; this is a safety net for OSC 0/1/2 (title)
            // or anything else.
            int j = i + 1;
            while (j < input.Length)
            {
                if (input[j] == '\x07') { j++; break; }
                if (input[j] == '\x1b' && j + 1 < input.Length && input[j + 1] == '\\') { j += 2; break; }
                j++;
            }
            return j;
        }
        if (next == '(' || next == ')')
        {
            return Math.Min(i + 2, input.Length);
        }
        if (next == '7') { _savedRow = _row; _savedCol = _col; return i + 1; }
        if (next == '8') { _row = _savedRow; _col = _savedCol; EnsureRow(_row); return i + 1; }
        if (next == '=' || next == '>') return i + 1;
        return i + 1;
    }

    private void ApplyCsi(ReadOnlySpan<char> paramsSpan, char final, ReadOnlySpan<char> fullInput, int seqStart, int finalIdx)
    {
        if (paramsSpan.Length > 0 && paramsSpan[0] == '?')
        {
            var modeSpan = paramsSpan.Slice(1);
            if (modeSpan.SequenceEqual("1049".AsSpan())
                || modeSpan.SequenceEqual("1047".AsSpan())
                || modeSpan.SequenceEqual("47".AsSpan()))
            {
                if (final == 'h') EnterAlt();
                else if (final == 'l') ExitAlt();
            }
            return;
        }

        switch (final)
        {
            case 'A': _row = Math.Max(0, _row - Math.Max(1, GetParam(paramsSpan, 0, 1))); EnsureRow(_row); break;
            case 'B': EnsureRow(_row + Math.Max(1, GetParam(paramsSpan, 0, 1))); break;
            case 'C': _col = Math.Min(MaxCol, _col + Math.Max(1, GetParam(paramsSpan, 0, 1))); break;
            case 'D': _col = Math.Max(0, _col - Math.Max(1, GetParam(paramsSpan, 0, 1))); break;
            case 'E':
                EnsureRow(_row + Math.Max(1, GetParam(paramsSpan, 0, 1)));
                _col = 0;
                break;
            case 'F':
                _row = Math.Max(0, _row - Math.Max(1, GetParam(paramsSpan, 0, 1)));
                EnsureRow(_row);
                _col = 0;
                break;
            case 'G': _col = Math.Clamp(GetParam(paramsSpan, 0, 1) - 1, 0, MaxCol); break;
            case 'H':
            case 'f':
                {
                    int wantRow = Math.Max(0, GetParam(paramsSpan, 0, 1) - 1);
                    int newCol = Math.Clamp(GetParam(paramsSpan, 1, 1) - 1, 0, MaxCol);
                    // In the main buffer, absolute cursor positioning to
                    // a row BEFORE our current row is almost always
                    // ConPTY screen-mirror redraw noise (Git Bash on
                    // Windows emits \x1b[H + space + \x1b[N;1H around
                    // every prompt), and following it would corrupt
                    // earlier scrollback content. Multi-row progress
                    // redraws use CSI A (relative up), not CSI H. So
                    // clamp wantRow upward to the current row when in
                    // the main buffer. Inside alt-screen the placeholder
                    // already swallowed everything, but writes are
                    // suppressed there anyway so the clamp is moot —
                    // keep it permissive for alt-screen so any
                    // hypothetical post-exit logic still works.
                    int newRow = _altActive ? wantRow : Math.Max(_row, wantRow);
                    EnsureRow(newRow);
                    _col = newCol;
                }
                break;
            case 'd':
                {
                    int wantRow = Math.Max(0, GetParam(paramsSpan, 0, 1) - 1);
                    int newRow = _altActive ? wantRow : Math.Max(_row, wantRow);
                    EnsureRow(newRow);
                }
                break;
            case 'K': EraseLine(GetParam(paramsSpan, 0, 0)); break;
            case 'J': EraseDisplay(GetParam(paramsSpan, 0, 0)); break;
            case 'X': EraseChars(GetParam(paramsSpan, 0, 1)); break;
            case 'P': DeleteChars(GetParam(paramsSpan, 0, 1)); break;
            case '@': InsertChars(GetParam(paramsSpan, 0, 1)); break;
            case 'L': InsertLines(GetParam(paramsSpan, 0, 1)); break;
            case 'M': DeleteLines(GetParam(paramsSpan, 0, 1)); break;
            case 's': _savedRow = _row; _savedCol = _col; break;
            case 'u': _row = _savedRow; _col = _savedCol; EnsureRow(_row); break;
            case 'm':
                RecordSgr(fullInput.Slice(seqStart, finalIdx - seqStart + 1).ToString());
                break;
            // CSI t / r / n / c — window manip, scroll region, DSR, DA: no-op.
            // Unknown finals: drop silently.
        }
    }

    private static int GetParam(ReadOnlySpan<char> paramsSpan, int idx, int def)
    {
        int count = 0;
        int start = 0;
        for (int k = 0; k <= paramsSpan.Length; k++)
        {
            if (k == paramsSpan.Length || paramsSpan[k] == ';')
            {
                if (count == idx)
                {
                    int len = k - start;
                    if (len == 0) return def;
                    return int.TryParse(paramsSpan.Slice(start, len), out var n) ? n : def;
                }
                count++;
                start = k + 1;
            }
        }
        return def;
    }
}
