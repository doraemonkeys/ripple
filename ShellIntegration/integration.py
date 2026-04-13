# splash Python REPL integration — installs OSC 633 emission into sys.ps1
# so splash can track command boundaries the same way it does for pwsh / bash.
#
# Event sequence per command (after the first prompt):
#   >>> user types something → Enter →
#   Python reads + compiles + execs the statement →
#   output is written to stdout →
#   Python re-renders the prompt by calling str(sys.ps1) →
#   our __str__ returns "OSC D;0  OSC P;Cwd=...  OSC A  >>> " →
#   splash's OscParser sees the markers and resolves the command.
#
# No OSC B / OSC C — Python has no stdlib-level pre-input hook, and we
# paper over the missing "command started" marker by having the worker
# call SkipCommandStartMarker (same trick used for cmd.exe which has the
# same limitation). Input echo is stripped deterministically from the
# captured window using the byte-match strategy.
#
# Exit codes: Python REPL has no per-expression exit code, so OSC D
# always carries 0. Exceptions print a traceback to stderr but the
# prompt still comes back, which is how splash detects completion.

import sys
import os

_SP_ESC = "\x1b"
_SP_BEL = "\x07"

def _sp_osc(code):
    return "{esc}]633;{code}{bel}".format(esc=_SP_ESC, code=code, bel=_SP_BEL)


_sp_last_cwd = None
_sp_first_prompt = True


class _SplashPS1:
    """sys.ps1 replacement — Python re-evaluates str(sys.ps1) on every
    prompt render when the object isn't a plain str, so __str__ becomes
    our per-prompt hook. See sys.ps1 docs:
    "If a non-string object is assigned to either variable, its str()
     is re-evaluated each time the interpreter prepares to read a new
     interactive command."
    """

    def __str__(self):
        global _sp_last_cwd, _sp_first_prompt
        parts = []
        if not _sp_first_prompt:
            # A command just finished. Python doesn't surface a useful
            # per-expression exit code here, so emit D;0 unconditionally.
            parts.append(_sp_osc("D;0"))
        _sp_first_prompt = False

        try:
            cwd = os.getcwd()
        except OSError:
            cwd = None
        if cwd is not None and cwd != _sp_last_cwd:
            parts.append(_sp_osc("P;Cwd=" + cwd))
            _sp_last_cwd = cwd

        parts.append(_sp_osc("A"))
        parts.append(">>> ")
        return "".join(parts)


class _SplashPS2:
    """Continuation prompt ('... '). No markers — continuation lines are
    part of a multi-line input, not command boundaries."""

    def __str__(self):
        return "... "


sys.ps1 = _SplashPS1()
sys.ps2 = _SplashPS2()


def _splash_exec_file(path):
    """Exec a splash-written tempfile in the REPL's __main__ scope, then
    delete it. Used by splash's multi-line command delivery path:
    instead of typing a def/class/if block into the REPL (which the
    parser-based REPL would need a trailing blank line to close), splash
    writes the whole block to a tempfile and sends
    _splash_exec_file(r"...") as a single-line call. Definitions leak
    into the REPL namespace because we pass globals() as the exec
    namespace — same as if the user had typed them directly.
    """
    import os as _os
    try:
        with open(path, "rb") as f:
            src = f.read()
        code = compile(src, path, "exec")
        exec(code, globals())
    finally:
        try:
            _os.unlink(path)
        except Exception:
            pass


# Self-delete the tempfile so a long-running splash process doesn't
# leave stale integration files scattered across TEMP. On Windows,
# Python has already closed the file by the time this line runs (the
# script is read into memory and compiled before execution begins).
try:
    os.unlink(__file__)
except Exception:
    pass
