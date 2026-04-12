using SplashShell.Services;

namespace SplashShell.Tests;

/// <summary>
/// Unit tests for CommandTracker.Busy semantics — in particular, that user-initiated
/// commands (OSCs fired without a preceding RegisterCommand) are also reflected in
/// Busy so get_status reports "busy" and the proxy switches to a different console.
/// </summary>
public class CommandTrackerTests
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

        Console.WriteLine("=== CommandTracker Tests ===");

        static OscParser.OscEvent Evt(OscParser.OscEventType t, int exit = 0, string? cwd = null)
            => new(t, exit, cwd);

        // Test 1: fresh tracker is idle.
        {
            var t = new CommandTracker();
            Assert(!t.Busy, "initial Busy is false");
        }

        // Helper: simulate shell startup (initial OSC B then first OSC A) so
        // subsequent OSC B/C events are recognised as user commands. The
        // tracker gates user-busy tracking until the shell has reached its
        // first prompt, otherwise the startup OSC B would leave new consoles
        // looking busy.
        static void PrimeShellReady(CommandTracker t)
        {
            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.CommandInputStart));
            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.PromptStart));
        }

        // Test 2: pwsh user-command pattern — OSC B on Enter, OSC A after prompt.
        {
            var t = new CommandTracker();
            PrimeShellReady(t);
            Assert(!t.Busy, "pwsh: idle after startup B → A");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandInputStart));
            Assert(t.Busy, "pwsh: Busy true after OSC B (Enter)");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            Assert(t.Busy, "pwsh: still Busy after OSC C (fires from prompt fn after cmd)");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            Assert(t.Busy, "pwsh: still Busy after OSC D");

            t.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: "C:\\Users"));
            Assert(t.Busy, "pwsh: still Busy after OSC P");

            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
            Assert(!t.Busy, "pwsh: Busy cleared by OSC A");
        }

        // Test 3: bash/zsh user-command pattern — OSC C on preexec, OSC D+A on precmd.
        {
            var t = new CommandTracker();
            PrimeShellReady(t);
            Assert(!t.Busy, "bash: idle after startup B → A");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            Assert(t.Busy, "bash: Busy true after OSC C (preexec)");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            Assert(t.Busy, "bash: still Busy after OSC D");

            t.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: "/home/user"));
            Assert(t.Busy, "bash: still Busy after OSC P");

            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
            Assert(!t.Busy, "bash: Busy cleared by OSC A");
        }

        // Test 4: startup OSC B must NOT mark busy before the shell is ready.
        // Before the first OSC A there is no established baseline, and the
        // initial OSC B from integration scripts would otherwise race against
        // the first execute_command from the proxy.
        {
            var t = new CommandTracker();
            t.HandleEvent(Evt(OscParser.OscEventType.CommandInputStart));
            Assert(!t.Busy, "startup: initial OSC B is ignored (shell not ready)");
            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
            Assert(!t.Busy, "startup: still idle after first OSC A");
            t.HandleEvent(Evt(OscParser.OscEventType.CommandInputStart));
            Assert(t.Busy, "startup: OSC B after first prompt does set busy");
        }

        // Test 5: LastKnownCwd is updated even for user-command OSC P.
        {
            var t = new CommandTracker();
            Assert(t.LastKnownCwd == null, "LastKnownCwd starts null");

            PrimeShellReady(t);
            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            t.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: "/tmp"));
            Assert(t.LastKnownCwd == "/tmp", "LastKnownCwd updated from user OSC P");

            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
            Assert(t.LastKnownCwd == "/tmp", "LastKnownCwd retained after prompt");
        }

        // Test 6: AI command wins over user state — RegisterCommand while idle, then events
        // still reflect Busy via _isAiCommand until the AI command resolves.
        {
            var t = new CommandTracker();
            var task = t.RegisterCommand("Get-Date", timeoutMs: 30_000);
            Assert(t.Busy, "AI cmd: Busy true after RegisterCommand");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandInputStart));
            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            Assert(t.Busy, "AI cmd: still Busy before prompt resolves");
        }

        // Test 7: RegisterCommand refuses when another AI command is in flight.
        {
            var t = new CommandTracker();
            _ = t.RegisterCommand("first", timeoutMs: 30_000);
            bool threw = false;
            try { _ = t.RegisterCommand("second", timeoutMs: 30_000); }
            catch (InvalidOperationException) { threw = true; }
            Assert(threw, "AI cmd: second RegisterCommand throws");
        }

        // Test 8: a late first-prompt OSC A that arrives AFTER RegisterCommand
        // but BEFORE the command's own OSC C / OSC D boundary must NOT resolve
        // the pending task. Reproduces the "slow pwsh startup + timeout
        // fallback" case where the pre-command buffer (reason banner etc.)
        // would otherwise be returned as the command's output.
        {
            var t = new CommandTracker();
            var task = t.RegisterCommand("Get-Location", timeoutMs: 30_000);

            // Simulate the bytes that arrived between worker-ready and the
            // real first prompt: the Write-Host reason text + the first
            // prompt's OSC sequence arriving AFTER RegisterCommand has
            // already fired. OSC B from integration.ps1's init-time write,
            // then the delayed first prompt's OSC D/P/A.
            t.FeedOutput("Reason: test\n\n");
            t.HandleEvent(Evt(OscParser.OscEventType.CommandInputStart));
            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            t.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: "C:\\Users\\foo"));
            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));

            Assert(!task.IsCompleted, "late first-prompt OSC A does NOT resolve before OSC C");
            Assert(t.Busy, "AI cmd: still Busy after stray first prompt");

            // Now simulate the real command actually running, which will
            // deliver OSC C + output + OSC D + OSC A in proper order.
            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            t.FeedOutput("C:\\MyProj\\splashshell\n");
            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            t.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: "C:\\MyProj\\splashshell"));
            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));

            Assert(task.IsCompleted, "real OSC C/D/A resolves the task");
            var result = task.Result;
            Assert(!result.Output.Contains("Reason"), "reason banner is NOT in captured output");
            Assert(result.Output.Contains("C:\\MyProj\\splashshell"), "real command output is captured");
            Assert(result.Cwd == "C:\\MyProj\\splashshell", "post-command cwd is reported");
        }

        // Test 9: recent-output ring buffer captures output from AI commands,
        // user commands, and pre-prompt boot noise alike — regardless of
        // whether an AI command is registered. peek_console / timeout
        // partialOutput depend on this "always on" behaviour.
        {
            var t = new CommandTracker();
            Assert(t.GetRecentOutputSnapshot() == "", "recent: empty on fresh tracker");

            // Pre-prompt boot bytes — no AI command active yet.
            t.FeedOutput("boot banner\n");
            Assert(t.GetRecentOutputSnapshot().Contains("boot banner"), "recent: pre-prompt output captured");

            PrimeShellReady(t);
            t.FeedOutput("user typed output\n");
            Assert(t.GetRecentOutputSnapshot().Contains("user typed output"), "recent: user output captured");

            // AI command: real command output flows through too.
            _ = t.RegisterCommand("Get-Date", timeoutMs: 30_000);
            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            t.FeedOutput("ai command output\n");
            var snapshot = t.GetRecentOutputSnapshot();
            Assert(snapshot.Contains("ai command output"), "recent: AI output captured");
        }

        // Test 10: ring buffer wraps correctly when output exceeds capacity.
        // A 4 KB write followed by a marker tail should show only the
        // marker plus the tail of the prior content, never the head.
        {
            var t = new CommandTracker();
            var big = new string('x', 5000);
            t.FeedOutput(big);
            t.FeedOutput("<<TAIL>>");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap.EndsWith("<<TAIL>>"), "recent: tail marker is at the end after wrap");
            Assert(snap.Length <= 4096, "recent: snapshot bounded by ring capacity");
            Assert(!snap.Contains("<<HEAD>>"), "recent: head is not present");
        }

        // Test 11: GetRecentOutputSnapshot strips ANSI but preserves CRLF
        // normalisation (matches AI command output cleaning).
        {
            var t = new CommandTracker();
            t.FeedOutput("line1\r\n\x1b[2Kline2\r\n");
            var snap = t.GetRecentOutputSnapshot();
            Assert(!snap.Contains('\r'), "recent: CR stripped");
            Assert(!snap.Contains('\x1b'), "recent: ANSI stripped");
            Assert(snap.Contains("line1\nline2"), "recent: LF-joined content preserved");
        }

        // Test 12: CR-overwrite collapse — PSReadLine's typing animation
        // and progress bars use bare \r to redraw in place. The snapshot
        // must show only the final state of each line, not the historical
        // concatenation of every intermediate redraw.
        {
            var t = new CommandTracker();
            // Simulate a progress bar updating in place, then a real line.
            t.FeedOutput("Progress 10%\rProgress 50%\rProgress 100%\ndone\n");
            var snap = t.GetRecentOutputSnapshot();
            Assert(!snap.Contains("Progress 10%"), "recent: earlier progress state dropped");
            Assert(!snap.Contains("Progress 50%"), "recent: intermediate progress state dropped");
            Assert(snap.Contains("Progress 100%"), "recent: final progress state kept");
            Assert(snap.Contains("done"), "recent: following line kept");
        }

        // Test 13: CRLF is preserved — CR collapse must not eat the prior
        // line when it ends with CRLF (regression guard).
        {
            var t = new CommandTracker();
            t.FeedOutput("first\r\nsecond\r\n");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap.Contains("first"), "recent: CRLF prior line preserved");
            Assert(snap.Contains("second"), "recent: CRLF second line preserved");
        }

        // Test 14: PSReadLine-style in-place redraw using CSI cursor
        // positioning rather than \r. CHA (\e[G) rewinds to column 1,
        // EL (\e[K) erases to end of line. The final state should
        // collapse to just the last rewrite, not the accumulated history.
        {
            var t = new CommandTracker();
            t.FeedOutput("PS> abc\x1b[G\x1b[KPS> abcdef\x1b[G\x1b[KPS> final");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "PS> final", $"recent: CSI redraws collapse to final — got: {snap}");
        }

        // Test 15: CUP (\e[<row>;<col>H) positioning is collapsed into
        // column-only movement so in-place row rewrites still work.
        {
            var t = new CommandTracker();
            t.FeedOutput("first line\x1b[1;1Hoverwritten");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "overwritten", $"recent: CUP to col 1 overwrites — got: {snap}");
        }

        // Test 16: SGR sequences are dropped cleanly (no leftover bytes).
        {
            var t = new CommandTracker();
            t.FeedOutput("\x1b[31mred\x1b[0m text");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "red text", $"recent: SGR dropped — got: {snap}");
        }

        // Test 17: OSC sequences terminated by BEL or ST are dropped.
        // Use \a (0x07) instead of \x07 — \x hex escapes are greedy and
        // would gobble the following 'a' as part of the hex literal.
        {
            var t = new CommandTracker();
            t.FeedOutput("before\x1b]0;window title\aafter");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "beforeafter", $"recent: OSC BEL dropped — got: {snap}");
        }

        // Test 18: cursor back (\e[D) and forward (\e[C) adjust the
        // write position on the current line.
        {
            var t = new CommandTracker();
            t.FeedOutput("abcdef\x1b[3DXY");
            // start: "abcdef" col=6, \e[3D → col=3, write "X" at 3 → "abcXef" col=4, write "Y" at 4 → "abcXYf" col=5
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "abcXYf", $"recent: cursor back then write — got: {snap}");
        }

        // Test 19: multi-row CUP addresses absolute rows. A new row's
        // contents must NOT bleed into a previous row. Regression for
        // the ConPTY "stale tail" bug where CUP(2,1) to start a new
        // command line was collapsed onto row 1 and left trailing
        // chars from the previous command.
        {
            var t = new CommandTracker();
            t.FeedOutput("long first row content\x1b[2;1Hshort");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "long first row content\nshort",
                $"recent: CUP(2,1) writes to row 2, row 1 preserved — got: {snap}");
        }

        // Test 20: CUP to an existing row overwrites from col 1
        // without touching unrelated cells past the new content.
        // Uses \r\n (CRLF) matching real PTY output — bare \n
        // only moves down without resetting col per VT spec.
        {
            var t = new CommandTracker();
            t.FeedOutput("aaaaaaaaaaaa\r\nrow2\x1b[1;1Hbbb\x1b[K");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "bbb\nrow2",
                $"recent: CUP(1,1) + shorter write + EL clears tail — got: {snap}");
        }

        // Test 21: CUU (cursor up) moves the cursor to the previous
        // row so subsequent writes land on that row.
        {
            var t = new CommandTracker();
            t.FeedOutput("row1\r\nrow2\r\nrow3\x1b[2AX");
            // 3 rows written via CRLF. After "row3", Col=4.
            // CUU 2 → Row goes from 2 to 0, Col stays at 4.
            // Write 'X' at row 0 col 4 → "row1X".
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "row1X\nrow2\nrow3",
                $"recent: CUU then write — got: {snap}");
        }

        // Test: \e[<params>t (DEC window manipulation) is treated as a
        // full-screen refresh trigger. ConPTY emits this right before
        // repainting the entire viewport, so VtLite should drop the
        // current grid and start fresh on the refresh content.
        {
            var t = new CommandTracker();
            // Old stale content, then the window-manip refresh trigger,
            // then a fresh screen write.
            t.FeedOutput("stale junk line\x1b[8;25;105tfresh content\nsecond line");
            var snap = t.GetRecentOutputSnapshot();
            Assert(!snap.Contains("stale junk"),
                $"recent: \\e[<...>t clears stale pre-refresh content — got: {snap}");
            Assert(snap.Contains("fresh content"),
                $"recent: post-refresh content is captured — got: {snap}");
            Assert(snap.Contains("second line"),
                $"recent: post-refresh multi-row content — got: {snap}");
        }

        // Test 22: the recent-output ring is cleared on (a) the FIRST
        // OSC A (PromptStart) — drops pre-shell boot noise and any
        // prior-session residue on a reused standby console — and (b)
        // every OSC C (CommandExecuted) — drops PSReadLine typing
        // noise and inline prediction artifacts that are rendered via
        // terminal-absolute coordinates we can't faithfully reflow.
        // After OSC C, the ring accumulates only the actual command
        // output, giving peek_console a clean "what is the current
        // command doing right now?" view.
        {
            var t = new CommandTracker();

            // Stale residue from a prior shell session (reused standby).
            t.FeedOutput("stale output from a previous session\n");
            Assert(t.GetRecentOutputSnapshot().Contains("stale output"),
                "recent: stale residue present before first PromptStart");

            // First OSC A = shell is ready. Ring gets wiped.
            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.PromptStart));
            Assert(t.GetRecentOutputSnapshot() == "",
                "recent: first OSC A clears the ring");

            // User starts typing — PSReadLine rendering noise flows in.
            t.FeedOutput("PS > \x1b[?25l\x1b[93mS\x1b[97m\x1b[2met-Location prediction\x1b[?25h");
            Assert(t.GetRecentOutputSnapshot().Length > 0,
                "recent: typing noise is in the ring pre-OSC-C");

            // OSC C fires when the command actually starts running.
            // Ring must clear to drop the typing noise.
            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.CommandExecuted));
            Assert(t.GetRecentOutputSnapshot() == "",
                "recent: OSC C clears PSReadLine typing noise");

            // Command output accumulates into the fresh ring.
            t.FeedOutput("actual command output\n");
            Assert(t.GetRecentOutputSnapshot().Contains("actual command output"),
                "recent: post-OSC-C output captured cleanly");

            // Command finishes, next prompt fires. The ring is NOT
            // cleared here — the current command's output stays
            // visible until the NEXT command starts.
            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.CommandFinished, ExitCode: 0));
            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.PromptStart));
            var afterPrompt = t.GetRecentOutputSnapshot();
            Assert(afterPrompt.Contains("actual command output"),
                $"recent: OSC A after first cycle preserves output — got: {afterPrompt}");
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }
}
