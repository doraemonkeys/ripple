// splash Node.js REPL integration — installs OSC 633 emission into the
// repl.start() prompt rendering so splash can track command boundaries
// the same way it does for pwsh / bash / python.
//
// Node has no `-i script.js` flag (unlike Python's `python -i script.py`),
// so the launch convention is just `node integration.js`. This file calls
// repl.start() to begin the interactive REPL and then customises it.
//
// OSC bytes are written out-of-band via process.stdout.write *after*
// the original displayPrompt has positioned the cursor for input.
// Putting them inside the prompt string (via setPrompt) miscomputes
// the visible cursor on Windows: Node's readline strips OSC escapes
// when computing prompt width, but ConPTY advances its tracked
// cursor for every byte it doesn't recognise as a terminal command,
// so readline-vs-ConPTY desync after the first prompt and every
// keystroke lands at the wrong column. Writing the bytes AFTER orig
// keeps the prompt string a clean "> " for readline's width math
// and emits the OSC sequence as a zero-width side channel that
// splash's OscParser captures cleanly.
//
// Multi-line command bodies arrive via splash's tempfile delivery as
// `await _splash_exec_file("...")`. The helper is async + top-level
// await so const/let/class declarations evaluated through the REPL's
// own server.eval() leak into the REPL context the way they would for
// directly typed input.

const repl = require("repl");
const fs = require("fs");

const ESC = "\x1b";
const BEL = "\x07";
const osc = (code) => `${ESC}]633;${code}${BEL}`;

let _splashFirstPrompt = true;
let _splashLastCwd = null;

function _splashOscPrefix() {
    const parts = [];
    if (!_splashFirstPrompt) {
        // A command (or the integration loader) just finished. Node's
        // REPL doesn't surface a per-expression exit code, so emit D;0
        // unconditionally — same convention Python uses.
        parts.push(osc("D;0"));
    }
    _splashFirstPrompt = false;

    let cwd;
    try { cwd = process.cwd(); } catch { cwd = null; }
    if (cwd && cwd !== _splashLastCwd) {
        parts.push(osc("P;Cwd=" + cwd));
        _splashLastCwd = cwd;
    }

    parts.push(osc("A"));
    return parts.join("");
}

const VISIBLE_PROMPT = "> ";

const server = repl.start({
    prompt: VISIBLE_PROMPT,
    useColors: true,
    terminal: true,
    ignoreUndefined: false,
});

// Fire the very first PromptStart event manually so splash's worker
// observes it and flips _ready. The constructor's first displayPrompt
// already ran with the plain prompt above (before this line executed),
// and we can't intercept it without monkey-patching the prototype, so
// just emit the OSC bytes here. They're zero-width — they don't
// disturb the cursor that readline just positioned at column 2.
process.stdout.write(_splashOscPrefix());

const _splashOriginalDisplayPrompt = server.displayPrompt.bind(server);
server.displayPrompt = function (preserveCursor) {
    const result = _splashOriginalDisplayPrompt(preserveCursor);
    // Emit OSC bytes AFTER the original has rendered the prompt and
    // positioned the cursor. Zero-width side channel — see the header
    // comment for the rationale.
    process.stdout.write(_splashOscPrefix());
    return result;
};

// Multi-line command delivery. splash writes the body to a tempfile and
// sends `await _splash_exec_file("path")` as a single line. We re-eval
// the body through the REPL's own server.eval, which handles top-level
// const/let/class scoping the same way directly typed input does. The
// finally block deletes the tempfile so interrupted commands don't
// leak files into TEMP.
//
// The helper is attached to server.context (the REPL's vm context) and
// NOT to the Node `global` — they're different objects. REPL user code
// resolves identifiers against server.context, so a plain `global.foo`
// assignment from this file would be invisible at the prompt.
server.context._splash_exec_file = function (p) {
    return new Promise((resolve, reject) => {
        let body;
        try {
            body = fs.readFileSync(p, "utf8");
        } catch (err) {
            try { fs.unlinkSync(p); } catch {}
            return reject(err);
        }
        server.eval(body, server.context, p, (err, result) => {
            try { fs.unlinkSync(p); } catch {}
            if (err) reject(err);
            else resolve(result);
        });
    });
};

// Self-delete the integration tempfile. Node has already read this file
// into memory and compiled it by the time this line executes, so the
// unlink is safe on Windows (and a no-op if the file is already gone).
try { fs.unlinkSync(__filename); } catch {}
