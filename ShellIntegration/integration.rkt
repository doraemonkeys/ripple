; splash Racket REPL integration — installs OSC 633 emission into
; current-prompt-read so splash can track command boundaries the same way
; it does for pwsh / bash / zsh / python / node.
;
; Event sequence per command (after the first prompt):
;   > user types an expression → Enter →
;   Racket reads + evals + prints →
;   read-eval-print-loop calls (current-prompt-read) →
;   our wrapper prints "OSC D;0  OSC P;Cwd=...  OSC A  > " →
;   splash's OscParser sees the markers and resolves the command.
;
; No OSC B / OSC C — Racket has no stdlib-level pre-input hook and we
; paper over the missing "command started" marker the same way the python
; adapter does: deterministic input-echo stripping walks the capture head
; and matches bytes against what splash sent to the PTY.
;
; Exit codes: Racket REPL has no per-expression exit code, so OSC D always
; carries 0. Errors print a message and the prompt still comes back, which
; is how splash detects completion.

(define sp-esc (string #\u001B))
(define sp-bel (string #\u0007))

(define (sp-osc code)
  (string-append sp-esc "]633;" code sp-bel))

(define sp-first-prompt #t)
(define sp-last-cwd #f)

(define (splash-prompt-read)
  (unless sp-first-prompt
    (display (sp-osc "D;0")))
  (set! sp-first-prompt #f)
  (let ([cwd (path->string (current-directory))])
    (unless (equal? cwd sp-last-cwd)
      (display (sp-osc (string-append "P;Cwd=" cwd)))
      (set! sp-last-cwd cwd)))
  (display (sp-osc "A"))
  (display "> ")
  (flush-output)
  ((current-read-interaction) 'stdin (current-input-port)))

(current-prompt-read splash-prompt-read)

; Multi-line command delivery helper. splash writes the command body to a
; .splash-exec-*.rkt tempfile and sends _splash_exec_file as a single-line
; REPL call so the whole block is one OSC A-to-A boundary regardless of
; how many top-level forms it contains. Definitions leak into the REPL
; namespace because `load` runs in the top-level namespace.
(define (_splash_exec_file path)
  (dynamic-wind
    void
    (lambda () (load path))
    (lambda ()
      (with-handlers ([exn:fail? (lambda (e) (void))])
        (delete-file path)))))

; Racket's `-i` REPL starts in a fresh dynamic extent that does not
; inherit `current-prompt-read` set at init time (parameters are thread-
; local and the interactive loop runs in its own continuation barrier),
; so we can't just set the parameter and let `-i` take over. Instead we
; call `read-eval-print-loop` explicitly here after wiring up the
; parameter — this adapter starts its REPL from within the integration
; script rather than from the racket binary's built-in REPL path.
(read-eval-print-loop)
