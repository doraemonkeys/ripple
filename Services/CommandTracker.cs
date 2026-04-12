using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SplashShell.Services;

/// <summary>
/// Tracks command lifecycle using OSC 633 events.
/// Runs in the console worker process.
///
/// Flow:
///   RegisterCommand() → command written to PTY →
///   OSC C (CommandExecuted) → output captured →
///   OSC D;{exitCode} (CommandFinished) → OSC P;Cwd=... → OSC A (PromptStart) →
///   settle timer (150ms) → resolve with { output, exitCode, cwd }
///
/// On timeout: caller's Task is cancelled, but output capture continues.
/// When the shell eventually completes, the result is cached for WaitForCompletion.
/// </summary>
public class CommandTracker
{
    private const int MaxAiOutputBytes = 1024 * 1024; // 1MB
    private const int PostPrimaryMaxBytes = 64 * 1024; // 64 KB for trailing-output delta

    // Rolling window of everything the PTY has emitted recently, regardless
    // of whether an AI command was in flight or the user typed something
    // themselves. Used by (a) execute_command's timeout response, which
    // returns the tail of this buffer as `partialOutput` so the AI can
    // diagnose stuck commands, and (b) the peek_console MCP tool, which
    // lets the AI inspect a busy console on demand.
    // Small and fixed-size so the token cost of returning it stays bounded.
    private const int RecentOutputCapacity = 4096;

    // Non-SGR ANSI escape sequence pattern.
    // Strips cursor movement, erase, and other control sequences, but preserves
    // SGR (Select Graphic Rendition, ending in 'm') so color information is
    // passed through to the AI for context (e.g. red errors, green success).
    // OSC sequences (window title etc.) are also stripped.
    private static readonly Regex AnsiRegex = new(
        @"\x1b\[[0-9;?]*[a-ln-zA-Z]|\x1b\][^\x07]*\x07|\x1b\][^\x1b]*\x1b\\|\x1b[()][0-9A-B]",
        RegexOptions.Compiled);


    // pwsh prompt pattern: "PS <drive>:\<path>> "
    // ConPTY may emit this glued to the previous line via cursor positioning,
    // so we strip it inline before splitting on \n.
    private static readonly Regex PwshPromptInline = new(
        @"PS [A-Z]:\\[^\n>]*> ?",
        RegexOptions.Compiled);

    private readonly object _lock = new();
    private TaskCompletionSource<CommandResult>? _tcs;
    private CancellationTokenRegistration _timeoutReg;
    private bool _isAiCommand;
    private bool _userCommandBusy;
    private bool _shellReady; // flipped on first PromptStart; gates user-busy tracking
    private string _aiOutput = "";
    private bool _truncated;

    // Circular buffer storing the last RecentOutputCapacity chars of the
    // PTY output stream (OSC-stripped but still with SGR colors/cursor
    // escapes in it — the read side does final cleanup). Written to from
    // FeedOutput unconditionally; snapshotted via GetRecentOutputSnapshot.
    private readonly char[] _recentBuf = new char[RecentOutputCapacity];
    private int _recentPos;     // next write index
    private int _recentLen;     // count of valid chars, capped at capacity
    private int _exitCode;
    private string? _cwd;
    private string _commandSent = "";
    private Stopwatch? _stopwatch;

    // Slice markers: _aiOutput position at OSC C (command about to run) and
    // OSC D (command finished). CleanOutput slices [commandStart..commandEnd)
    // from _aiOutput to produce the result, which cleanly excludes both the
    // AcceptLine finalize rendering that PSReadLine writes between OSC B and
    // OSC C and the prompt text that comes after OSC D / OSC A.
    private int _commandStart = -1;
    private int _commandEnd = -1;

    // Trailing output that arrives AFTER Resolve() has returned the primary
    // CommandResult — e.g. the pwsh prompt repaint or pwsh Format-Table rows
    // that finish streaming after the OSC A marker. The proxy drains this via
    // the drain_post_output pipe message after each successful execute.
    private readonly StringBuilder _postPrimaryOutput = new();

    // Cached result from timed-out commands
    private CommandResult? _cachedResult;

    // Last known cwd from any prompt (AI command or user command)
    private string? _lastKnownCwd;
    public string? LastKnownCwd { get { lock (_lock) return _lastKnownCwd; } }

    public bool Busy => _isAiCommand || _userCommandBusy;
    public bool HasCachedOutput => _cachedResult != null;

    /// <summary>
    /// Text of the AI command currently executing, or null when idle / the
    /// active command is user-initiated (we don't know what the human typed).
    /// </summary>
    public string? RunningCommand
    {
        get { lock (_lock) return _isAiCommand ? _commandSent : null; }
    }

    /// <summary>
    /// Elapsed seconds since the current AI command was registered, or null
    /// when no AI command is tracked.
    /// </summary>
    public double? RunningElapsedSeconds
    {
        get { lock (_lock) return _isAiCommand ? _stopwatch?.Elapsed.TotalSeconds : null; }
    }

    public record CommandResult(string Output, int ExitCode, string? Cwd, string? Command, string Duration);

    /// <summary>
    /// Register an AI-initiated command. Returns a Task that completes
    /// when the shell signals command completion via OSC markers.
    /// </summary>
    public Task<CommandResult> RegisterCommand(string commandText, int timeoutMs = 170_000)
    {
        // Minimum 1 second to avoid CancellationTokenSource(0) race conditions
        timeoutMs = Math.Max(timeoutMs, 1000);

        lock (_lock)
        {
            if (_isAiCommand)
                throw new InvalidOperationException("Another command is already executing.");

            _tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _isAiCommand = true;
            _aiOutput = "";
            _truncated = false;
            _exitCode = 0;
            _cwd = null;
            _commandSent = commandText;
            _commandStart = -1;
            _commandEnd = -1;
            _cachedResult = null;
            _postPrimaryOutput.Clear();
            _stopwatch = Stopwatch.StartNew();

            // Setup timeout
            var timeoutCts = new CancellationTokenSource(timeoutMs);
            _timeoutReg = timeoutCts.Token.Register(() =>
            {
                lock (_lock)
                {
                    if (_tcs != null && !_tcs.Task.IsCompleted)
                    {
                        var tcs = _tcs;
                        _tcs = null; // Detach — output capture continues
                        tcs.TrySetException(new TimeoutException($"Command timed out after {timeoutMs}ms"));
                    }
                }
            });

            return _tcs.Task;
        }
    }

    /// <summary>
    /// Fail any in-flight RegisterCommand with a "shell exited" error so the
    /// HandleExecuteAsync call blocked on it unwinds promptly. Called from
    /// the worker's read loop when the child shell process goes away.
    /// </summary>
    public void AbortPending()
    {
        lock (_lock)
        {
            if (_tcs != null && !_tcs.Task.IsCompleted)
            {
                var tcs = _tcs;
                _tcs = null;
                _timeoutReg.Dispose();
                Cleanup();
                tcs.TrySetException(new InvalidOperationException("Shell process exited before the command completed."));
            }
        }
    }

    /// <summary>
    /// Feed an OSC event from the parser. The caller must pass events in
    /// source order, interleaved with matching FeedOutput calls, so that
    /// _aiOutput.Length at event-dispatch time is the offset at which the
    /// event fired in the original byte stream.
    /// </summary>
    public void HandleEvent(OscParser.OscEvent evt)
    {
        lock (_lock)
        {
            // Always track cwd, even outside AI commands (for user manual cd)
            if (evt.Type == OscParser.OscEventType.Cwd)
                _lastKnownCwd = evt.Cwd;

            // OSC C (CommandExecuted) is the cleanest boundary to reset the
            // recent-output ring for peek_console / timeout partialOutput.
            // Everything before OSC C is PSReadLine typing noise — per-
            // keystroke re-rendering, inline history prediction, cursor
            // dancing via absolute CUP — which no amount of VT-lite
            // interpretation can sanitise perfectly because PSReadLine
            // uses terminal-absolute coordinates that don't line up with
            // our ring's start. Clearing on OSC C gives peek a clean
            // "everything since the current command started running"
            // view, which is exactly the question execute_command's
            // timeout asks ("what is this stuck command doing right now?").
            // The command text itself is still reported via the
            // runningCommand metadata field, so peek callers never lose
            // context about what's running.
            if (evt.Type == OscParser.OscEventType.CommandExecuted)
                ResetRecentBuffer();

            // Mark the shell as "ready" on the first PromptStart. Until then,
            // ignore user-command busy transitions — the initial OSC B that
            // integration scripts emit at startup (and the subsequent prompt
            // setup) would otherwise leave the new console looking busy and
            // cause HandleExecuteAsync to reject the first incoming command.
            // The FIRST PromptStart is also when we clear the recent-output
            // ring: anything before it is pre-shell boot noise (or prior-
            // session residue on a reused standby console) that isn't part
            // of what the user would see on a fresh terminal.
            if (evt.Type == OscParser.OscEventType.PromptStart && !_shellReady)
                ResetRecentBuffer();
            if (evt.Type == OscParser.OscEventType.PromptStart)
                _shellReady = true;

            // When no AI command is active, track whether the human user is
            // mid-command in the terminal. OSC B / OSC C both mean "a command
            // is about to start / is starting" (pwsh fires B on Enter, bash
            // and zsh fire C from preexec); OSC A means the shell is back at
            // a prompt. This lets get_status report "busy" for user commands
            // too, so execute_command won't shove an AI command into a PTY
            // that the human is actively using.
            if (!_isAiCommand)
            {
                if (!_shellReady) return;

                switch (evt.Type)
                {
                    case OscParser.OscEventType.CommandInputStart:
                    case OscParser.OscEventType.CommandExecuted:
                        _userCommandBusy = true;
                        break;
                    case OscParser.OscEventType.PromptStart:
                        _userCommandBusy = false;
                        break;
                }
                return;
            }

            switch (evt.Type)
            {
                case OscParser.OscEventType.CommandExecuted:
                    // OSC C: PreCommandLookupAction has fired, everything
                    // preceding this point in _aiOutput is AcceptLine finalize
                    // noise. Record the position so CleanOutput knows where
                    // the real command output begins.
                    _commandStart = _aiOutput.Length;
                    break;

                case OscParser.OscEventType.CommandFinished:
                    // OSC D: command is done, the prompt function is about to
                    // print the prompt. Snapshot the position here so the
                    // prompt text is excluded from the result.
                    _exitCode = evt.ExitCode;
                    _commandEnd = _aiOutput.Length;
                    break;

                case OscParser.OscEventType.Cwd:
                    _cwd = evt.Cwd;
                    break;

                case OscParser.OscEventType.PromptStart:
                    // Only treat OSC A as the end of an AI command when we
                    // actually saw a command cycle — both OSC C (command
                    // started) and OSC D (command finished) must have
                    // fired since RegisterCommand. A bare OSC A without
                    // that framing means the shell just printed a prompt
                    // unrelated to our AI command — most commonly the
                    // very first prompt after pwsh startup, which can
                    // arrive AFTER RegisterCommand if the shell was slow
                    // to initialize (Defender first-scan, Import-Module
                    // PSReadLine, sourcing the banner prefix, etc) and
                    // WaitForReady's timeout fell through. Resolving here
                    // would hand the AI the reason banner / PSReadLine
                    // prediction rendering as "command output" and leave
                    // the real command unanswered. Ignore this OSC A and
                    // wait for the real one.
                    if (_commandStart >= 0 && _commandEnd >= 0)
                        Resolve();
                    break;
            }
        }
    }

    /// <summary>
    /// Feed cleaned output from the PTY (OSC stripped). During an AI command
    /// the text is appended to _aiOutput for later OSC C/D slicing; outside
    /// an AI command it goes to the post-primary drain buffer for the
    /// proxy. In BOTH modes the text is also mirrored into _recentBuf, a
    /// small rolling window that peek_console and execute timeout responses
    /// return as "what's on screen right now" context.
    /// </summary>
    public void FeedOutput(string text)
    {
        lock (_lock)
        {
            AppendRecent(text);

            if (_isAiCommand)
            {
                if (_aiOutput.Length < MaxAiOutputBytes)
                {
                    _aiOutput += text;
                    if (_aiOutput.Length > MaxAiOutputBytes)
                    {
                        _aiOutput = _aiOutput[..MaxAiOutputBytes];
                        _truncated = true;
                    }
                }
                return;
            }

            // No AI command active: this is either pre-first-prompt shell boot
            // noise, a user-typed command's output, or trailing bytes arriving
            // after a primary Resolve() has returned. Capture into the
            // post-primary buffer — the proxy drains it via drain_post_output.
            // Bounded at PostPrimaryMaxBytes so a runaway shell can't grow the
            // buffer forever if the proxy never drains.
            var remaining = PostPrimaryMaxBytes - _postPrimaryOutput.Length;
            if (remaining <= 0) return;
            if (text.Length <= remaining) _postPrimaryOutput.Append(text);
            else _postPrimaryOutput.Append(text, 0, remaining);
        }
    }

    private void ResetRecentBuffer()
    {
        _recentPos = 0;
        _recentLen = 0;
    }

    /// <summary>
    /// Public hook to drop everything currently sitting in the
    /// recent-output ring. Called by the ConsoleWorker when a new
    /// proxy claims this console, so peek_console / timeout
    /// partialOutput don't start out leaking bytes from whatever
    /// the previous owner was running.
    /// </summary>
    public void ClearRecentOutput()
    {
        lock (_lock) ResetRecentBuffer();
    }

    /// <summary>
    /// Diagnostic: return the raw bytes currently in the recent-output
    /// ring, in the order they were received, without any ANSI or
    /// VT processing. Used to debug VtLite interpretation issues
    /// (when peek_console shows content that isn't in the byte
    /// stream).
    /// </summary>
    public string GetRawRecentBytes()
    {
        lock (_lock)
        {
            if (_recentLen == 0) return "";
            if (_recentLen < RecentOutputCapacity)
                return new string(_recentBuf, 0, _recentLen);

            var tmp = new char[RecentOutputCapacity];
            var firstPart = RecentOutputCapacity - _recentPos;
            Array.Copy(_recentBuf, _recentPos, tmp, 0, firstPart);
            Array.Copy(_recentBuf, 0, tmp, firstPart, _recentPos);
            return new string(tmp);
        }
    }

    private void AppendRecent(string text)
    {
        if (text.Length == 0) return;

        // If this single write is larger than the ring, only the tail of
        // it can ever survive — fast-path by copying the last N chars
        // straight into buf[0..N] and setting pos/len appropriately.
        if (text.Length >= RecentOutputCapacity)
        {
            text.AsSpan(text.Length - RecentOutputCapacity).CopyTo(_recentBuf);
            _recentPos = 0;
            _recentLen = RecentOutputCapacity;
            return;
        }

        foreach (var ch in text)
        {
            _recentBuf[_recentPos] = ch;
            _recentPos = (_recentPos + 1) % RecentOutputCapacity;
            if (_recentLen < RecentOutputCapacity) _recentLen++;
        }
    }

    /// <summary>
    /// Snapshot the rolling recent-output window as a string, processed
    /// through a VT-lite interpreter so in-place redraws from PSReadLine,
    /// progress bars, and cursor-positioning escape sequences collapse to
    /// their final state. Both peek_console and execute timeout responses
    /// use this so the AI sees what the console actually displays, not
    /// the concatenated history of every intermediate redraw.
    /// </summary>
    public string GetRecentOutputSnapshot()
    {
        string raw;
        lock (_lock)
        {
            if (_recentLen == 0) return "";
            if (_recentLen < RecentOutputCapacity)
            {
                raw = new string(_recentBuf, 0, _recentLen);
            }
            else
            {
                // Wrapped: valid data starts at _recentPos and wraps around.
                var tmp = new char[RecentOutputCapacity];
                var firstPart = RecentOutputCapacity - _recentPos;
                Array.Copy(_recentBuf, _recentPos, tmp, 0, firstPart);
                Array.Copy(_recentBuf, 0, tmp, firstPart, _recentPos);
                raw = new string(tmp);
            }
        }
        return VtLite(raw);
    }

    /// <summary>
    /// Light VT-100 / ECMA-48 interpreter for the recent-output snapshot.
    /// We don't implement a full terminal emulator — just enough to turn
    /// cursor-positioning and line-redraw escape sequences into a
    /// collapsed final-state text block, so peek_console shows something
    /// close to what a human sees on screen.
    ///
    /// Multi-row screen model: a grow-on-demand list of rows plus a
    /// (row, col) cursor. CUP / HVP address absolute rows (row 1-based
    /// from the top of what we've seen), CUU/CUD move vertically, \n
    /// advances to the next row, \r resets col to 0. This handles
    /// ConPTY's absolute-row redraw pattern where a new command's input
    /// line is painted on its own row via CUP, which the previous
    /// single-row model collapsed onto the prior command's row and left
    /// stale tail bytes in place.
    /// </summary>
    private static string VtLite(string input)
    {
        var state = new VtState();
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (c == '\n') { state.LineFeed(); i++; }
            else if (c == '\r') { state.CarriageReturn(); i++; }
            else if (c == '\b') { state.Backspace(); i++; }
            else if (c == '\t') { state.Tab(); i++; }
            else if (c == '\x1b') { i = ParseEscape(input, i, state); }
            else if (c >= ' ') { state.WriteChar(c); i++; }
            else { i++; /* drop other C0 */ }
        }
        return state.Render();
    }

    /// <summary>
    /// Virtual screen state for VtLite. Rows grow on demand; there is no
    /// fixed viewport height, so CUP addresses into rows[0..] directly.
    /// MaxRow tracks the highest row index written so the final render
    /// can trim trailing empty rows.
    /// </summary>
    private sealed class VtState
    {
        public readonly List<StringBuilder> Rows = new() { new StringBuilder() };
        public int Row;
        public int Col;
        public int MaxRow;

        public void EnsureRow(int r)
        {
            while (Rows.Count <= r) Rows.Add(new StringBuilder());
            if (r > MaxRow) MaxRow = r;
        }

        public void WriteChar(char c)
        {
            EnsureRow(Row);
            var row = Rows[Row];
            if (Col < row.Length) row[Col] = c;
            else
            {
                while (row.Length < Col) row.Append(' ');
                row.Append(c);
            }
            Col++;
        }

        public void LineFeed()
        {
            Row++;
            Col = 0;
            EnsureRow(Row);
        }

        public void CarriageReturn() { Col = 0; }
        public void Backspace() { if (Col > 0) Col--; }
        public void Tab() { Col = ((Col / 8) + 1) * 8; }

        public void CursorUp(int n) { Row = Math.Max(0, Row - Math.Max(1, n)); }
        public void CursorDown(int n) { Row += Math.Max(1, n); EnsureRow(Row); }
        public void CursorForward(int n) { Col += Math.Max(1, n); }
        public void CursorBack(int n) { Col = Math.Max(0, Col - Math.Max(1, n)); }
        public void CursorCol(int c1) { Col = Math.Max(0, c1 - 1); }

        public void CursorPos(int r1, int c1)
        {
            Row = Math.Max(0, r1 - 1);
            Col = Math.Max(0, c1 - 1);
            EnsureRow(Row);
        }

        public void EraseLine(int mode)
        {
            EnsureRow(Row);
            var row = Rows[Row];
            if (mode == 0)
            {
                if (Col < row.Length) row.Length = Col;
            }
            else if (mode == 1)
            {
                int end = Math.Min(Col + 1, row.Length);
                for (int k = 0; k < end; k++) row[k] = ' ';
            }
            else if (mode == 2)
            {
                row.Clear();
            }
        }

        public void EraseDisplay(int mode)
        {
            if (mode == 0)
            {
                EnsureRow(Row);
                var row = Rows[Row];
                if (Col < row.Length) row.Length = Col;
                for (int r = Row + 1; r < Rows.Count; r++) Rows[r].Clear();
            }
            else if (mode == 1)
            {
                for (int r = 0; r < Row && r < Rows.Count; r++) Rows[r].Clear();
                EnsureRow(Row);
                var row = Rows[Row];
                int end = Math.Min(Col + 1, row.Length);
                for (int k = 0; k < end; k++) row[k] = ' ';
            }
            else if (mode == 2)
            {
                foreach (var r in Rows) r.Clear();
                Row = 0;
                Col = 0;
                MaxRow = 0;
            }
        }

        public string Render()
        {
            int endRow = MaxRow;
            while (endRow > 0 && Rows[endRow].Length == 0) endRow--;
            var sb = new StringBuilder();
            for (int r = 0; r <= endRow; r++)
            {
                if (r > 0) sb.Append('\n');
                sb.Append(Rows[r].ToString().TrimEnd());
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Parse an ESC sequence starting at position <paramref name="start"/>
    /// (where input[start] == 0x1b), mutate the VT state, and return the
    /// index of the byte immediately after the sequence.
    /// </summary>
    private static int ParseEscape(string input, int start, VtState state)
    {
        int i = start + 1;
        if (i >= input.Length) return i;

        char next = input[i];
        if (next == '[')
        {
            // CSI — \e[<param bytes><intermediate><final>
            int paramStart = i + 1;
            int j = paramStart;
            while (j < input.Length && input[j] >= 0x30 && input[j] <= 0x3f) j++;
            var paramsStr = j > paramStart ? input.Substring(paramStart, j - paramStart) : "";
            while (j < input.Length && input[j] >= 0x20 && input[j] <= 0x2f) j++;
            if (j >= input.Length) return input.Length; // incomplete — drop rest
            char final = input[j];
            ApplyCsi(paramsStr, final, state);
            return j + 1;
        }
        if (next == ']')
        {
            // OSC — \e]...(BEL or ST). Drop entire sequence.
            int j = i + 1;
            while (j < input.Length)
            {
                if (input[j] == '\x07') { j++; break; }
                if (input[j] == '\x1b' && j + 1 < input.Length && input[j + 1] == '\\')
                { j += 2; break; }
                j++;
            }
            return j;
        }
        if (next == '(' || next == ')')
        {
            // Character set selection — \e(<char>
            return Math.Min(i + 2, input.Length);
        }
        // Single-char ESC — skip it and the follower.
        return Math.Min(i + 1, input.Length);
    }

    private static void ApplyCsi(string paramsStr, char final, VtState state)
    {
        // Private-mode sequences (DEC — \e[?25h etc.) — ignore entirely.
        if (paramsStr.Length > 0 && paramsStr[0] == '?') return;

        // Parse semicolon-separated numeric params. Empty == default.
        string[] parts;
        if (paramsStr.Length == 0) parts = Array.Empty<string>();
        else parts = paramsStr.Split(';');

        int Param(int idx, int def)
        {
            if (idx >= parts.Length) return def;
            if (string.IsNullOrEmpty(parts[idx])) return def;
            if (int.TryParse(parts[idx], out var n)) return n;
            return def;
        }

        switch (final)
        {
            case 'A': state.CursorUp(Param(0, 1)); break;         // CUU
            case 'B': state.CursorDown(Param(0, 1)); break;       // CUD
            case 'C': state.CursorForward(Param(0, 1)); break;    // CUF
            case 'D': state.CursorBack(Param(0, 1)); break;       // CUB
            case 'E': state.CursorDown(Param(0, 1)); state.Col = 0; break; // CNL
            case 'F': state.CursorUp(Param(0, 1)); state.Col = 0; break;   // CPL
            case 'G': state.CursorCol(Param(0, 1)); break;        // CHA
            case 'H':                                              // CUP
            case 'f':                                              // HVP
                state.CursorPos(Param(0, 1), Param(1, 1));
                break;
            case 'K': state.EraseLine(Param(0, 0)); break;        // EL
            case 'J': state.EraseDisplay(Param(0, 0)); break;     // ED
            case 'd': state.Row = Math.Max(0, Param(0, 1) - 1); state.EnsureRow(state.Row); break; // VPA
            case 't':
                // DEC / xterm window manipulation — e.g. \e[8;<h>;<w>t to
                // resize the text area. ConPTY emits this as the prelude
                // to a full-screen refresh: after the `t` sequence it
                // repaints the entire viewport starting from \e[H. Treat
                // it as a full clear so our grid stays in sync with
                // ConPTY's viewport and doesn't carry stale content
                // (PSReadLine prediction artifacts, prior command
                // fragments) forward from before the refresh.
                state.EraseDisplay(2);
                break;
            case 'm': // SGR — colors/attrs, no-op
            case 's': // save cursor
            case 'u': // restore cursor
            case 'r': // scroll region
            case 'n': // device status report
            case 'c': // device attributes
                break;
            default:
                // Unknown final byte — drop silently.
                break;
        }
    }

    /// <summary>
    /// Consume cached output from a timed-out command that has since completed.
    /// </summary>
    public CommandResult? ConsumeCachedOutput()
    {
        lock (_lock)
        {
            var result = _cachedResult;
            _cachedResult = null;
            return result;
        }
    }

    /// <summary>
    /// Discard whatever is in the post-primary buffer without waiting. Used
    /// by shells (pwsh/powershell) where nothing useful ever arrives after
    /// OSC PromptStart — the trailing bytes are just the prompt repaint and
    /// PSReadLine prediction animation, which would otherwise leak into the
    /// next command's delta capture.
    /// </summary>
    public void ClearPostPrimary()
    {
        lock (_lock) _postPrimaryOutput.Clear();
    }

    /// <summary>
    /// Wait for the post-primary output buffer to stabilise (no growth for
    /// stableMs), then drain it and return the cleaned delta. Called from
    /// the worker's drain_post_output pipe handler after the primary execute
    /// response has been delivered to the proxy.
    /// </summary>
    public async Task<string> WaitAndDrainPostOutputAsync(int stableMs, int maxMs, CancellationToken ct)
    {
        stableMs = Math.Max(1, stableMs);
        maxMs = Math.Max(stableMs, maxMs);
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(maxMs);

        int lastLen;
        lock (_lock) lastLen = _postPrimaryOutput.Length;
        var lastChange = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            var pollMs = Math.Clamp(Math.Min(30, stableMs / 2), 5, remaining);
            try { await Task.Delay(pollMs, ct); }
            catch (OperationCanceledException) { break; }

            int currentLen;
            lock (_lock) currentLen = _postPrimaryOutput.Length;

            if (currentLen != lastLen)
            {
                lastLen = currentLen;
                lastChange = DateTime.UtcNow;
                continue;
            }

            if ((DateTime.UtcNow - lastChange).TotalMilliseconds >= stableMs)
                break;
        }

        string raw;
        lock (_lock)
        {
            raw = _postPrimaryOutput.ToString();
            _postPrimaryOutput.Clear();
        }
        return CleanDelta(raw);
    }

    /// <summary>
    /// Clean a trailing-output delta — strip ANSI, drop trailing prompt and
    /// blank lines, normalise CRLF. Unlike CleanOutput this does NOT strip
    /// AcceptLine noise (no command echo in the delta) and does NOT trim
    /// leading blanks (they might be meaningful Format-Table spacing).
    /// </summary>
    private static string CleanDelta(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var output = StripAnsi(raw);
        var lines = output.Split('\n');
        var cleaned = new List<string>(lines.Length);
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimEnd();
            if (trimmed == ">>" || trimmed.StartsWith(">> ")) continue;
            cleaned.Add(line);
        }

        while (cleaned.Count > 0)
        {
            var last = cleaned[^1].Trim();
            if (string.IsNullOrEmpty(last) ||
                last is "$" or "#" or "%" or ">>" ||
                IsShellPrompt(last))
            {
                cleaned.RemoveAt(cleaned.Count - 1);
            }
            else break;
        }

        return string.Join('\n', cleaned).TrimEnd();
    }

    private void Resolve()
    {
        lock (_lock)
        {
            var output = CleanOutput();
            var duration = _stopwatch?.Elapsed.TotalSeconds.ToString("F1") ?? "0.0";
            var result = new CommandResult(output, _exitCode, _cwd, _commandSent, duration);

            if (_tcs != null && !_tcs.Task.IsCompleted)
            {
                var tcs = _tcs;
                _timeoutReg.Dispose();
                Cleanup();
                tcs.TrySetResult(result);
            }
            else
            {
                // Timed out earlier — cache result for wait_for_completion
                _cachedResult = result;
                Cleanup();
            }
        }
    }

    /// <summary>
    /// Slice the command-output window out of _aiOutput and clean it up.
    /// The window is [_commandStart, _commandEnd), filled in by OSC C and
    /// OSC D. If OSC C never fired (parse error, OSC markers misconfigured)
    /// we fall back to the whole buffer. If OSC D never fired but OSC A did
    /// (unusual), we take everything up to _aiOutput.Length.
    /// </summary>
    private string CleanOutput()
    {
        var start = _commandStart >= 0 ? _commandStart : 0;
        var end = _commandEnd >= 0 ? _commandEnd : _aiOutput.Length;
        if (end < start) end = start;
        if (end > _aiOutput.Length) end = _aiOutput.Length;

        var raw = _aiOutput.Substring(start, end - start);

        var output = StripAnsi(raw);
        var lines = output.Split('\n');
        var cleaned = new List<string>();
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimEnd();
            // pwsh continuation prompt lines from multi-line input aren't
            // command output and look jarring in the result.
            if (trimmed == ">>" || trimmed.StartsWith(">> ")) continue;
            cleaned.Add(line);
        }

        var result = string.Join('\n', cleaned).Trim();
        if (_truncated)
            result += "\n\n[Output truncated at 1MB]";
        return result;
    }

    /// <summary>
    /// Test hook — force-seed the ring buffer. Only used by
    /// CommandTrackerTests to verify GetRecentOutputSnapshot without
    /// having to plumb a full PTY.
    /// </summary>
    internal void SeedRecentForTests(string text)
    {
        lock (_lock) AppendRecent(text);
    }

    /// <summary>
    /// Detect shell prompt lines. Used as fallback when OSC markers are unavailable.
    /// Checks common formats + generic trailing prompt characters.
    /// </summary>
    private static bool IsShellPrompt(string line)
    {
        // pwsh: "PS <path>>"
        if (line.StartsWith("PS ") && line.EndsWith(">")) return true;
        // cmd: "<drive>:\...>"
        if (line.Length >= 2 && line[1] == ':' && line.EndsWith(">")) return true;
        // bash/zsh/fish: ends with $, #, %, >, ❯, λ
        if (line.EndsWith('$') || line.EndsWith('#') || line.EndsWith('%') ||
            line.EndsWith('>') || line.EndsWith('❯') || line.EndsWith('λ'))
            return true;
        return false;
    }

    private void Cleanup()
    {
        _tcs = null;
        _isAiCommand = false;
        _aiOutput = "";
        _commandSent = "";
        _stopwatch = null;
        _commandStart = -1;
        _commandEnd = -1;
        // _recentBuf survives deliberately — it's a rolling window
        // spanning command boundaries so peek_console can still show
        // what was on screen just before/during the next call.
    }

    private static string StripAnsi(string text)
    {
        text = AnsiRegex.Replace(text, "");  // strip non-SGR sequences, keep colors
        text = text.Replace("\r\n", "\n");   // CRLF → LF
        text = text.Replace("\r", "");       // remove any remaining standalone CR
        return text;
    }

}
