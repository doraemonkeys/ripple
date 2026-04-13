# splash shell integration for bash
# Injects OSC 633 escape sequences for command lifecycle tracking.
# Sourced automatically by splash console worker.

# Guard against double-sourcing
if [[ "$__SPLASH_INJECTED" == "1" ]]; then
    return
fi
__SPLASH_INJECTED=1

# Save original PROMPT_COMMAND
__sp_original_prompt_command="$PROMPT_COMMAND"

# Emit OSC 633 sequence: \e]633;{code}[;{data}]\a
__sp_osc() {
    printf '\e]633;%s\a' "$1"
}

# Precmd: runs before each prompt via PROMPT_COMMAND. Emits OSC 633 D
# (CommandFinished with exit code), P (cwd), A (PromptStart) so the
# proxy tracker can close out the command just resolved. OSC D fires
# unconditionally — even if no command ran this cycle (empty Enter,
# Ctrl+C at idle prompt), the tracker's _commandStart gate will keep
# stray emissions from spuriously resolving.
__sp_precmd() {
    local exit_code=$?

    __sp_osc "D;$exit_code"
    __sp_osc "P;Cwd=$(pwd)"
    __sp_osc "A"

    if [[ -n "$__sp_original_prompt_command" ]]; then
        eval "$__sp_original_prompt_command"
    fi
}

# PS0 is expanded and printed by bash AFTER reading the full command
# line and BEFORE it starts executing any part of it — even for
# subshells, pipelines, compound commands, or multi-statement lines.
# That's exactly when we want OSC 633 C to fire: once per submission,
# in the parent shell, so the proxy tracker's _commandStart marks the
# boundary between input echo and real command output.
#
# Using PS0 replaces the old DEBUG-trap / preexec approach, which had
# two subtle failure modes:
#
#   1. DEBUG didn't fire for compound commands in the parent (only for
#      simple commands inside subshells, and only with `set -T`), so
#      `(echo foo)` left the parent's state un-flipped and resolve
#      never fired.
#   2. Even with `set -T`, inner `__sp_osc` calls inside PROMPT_COMMAND
#      themselves fired DEBUG and caused spurious OSC C emissions mid-
#      precmd, scrambling the output slice.
#
# PS0 is a single expansion-time hook with no recursion risk and no
# subshell visibility issues.
PS0=$'\e]633;C\a'

PROMPT_COMMAND="__sp_precmd"

# Initial marker — emitted once at integration load so the proxy tracker
# can distinguish "shell still starting up" from "shell ready for the
# first user command". The initial OSC A from __sp_precmd's first prompt
# cycle flips the tracker's _shellReady flag.
__sp_osc "B"
