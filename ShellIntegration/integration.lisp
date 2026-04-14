;;;; splash Clozure CL REPL integration — installs OSC 633 emission
;;;; into ccl::print-listener-prompt so splash can track command
;;;; boundaries the same way it does for pwsh / bash / zsh / python /
;;;; node / racket.
;;;;
;;;; Event sequence per command (after the first prompt):
;;;;   ? user types an expression → Enter →
;;;;   CCL reads + evals + prints →
;;;;   the listener loop calls (print-listener-prompt stream) →
;;;;   our wrapper prints "OSC D;0  OSC P;Cwd=...  OSC A  ? " →
;;;;   splash's OscParser sees the markers and resolves the command.
;;;;
;;;; CCL's default prompt format is "~[?~:;~:*~d >~] " — `?` for the
;;;; top-level, `1 > ` / `2 > ` / ... for nested debugger break loops.
;;;; The format directive reads ccl::*break-level* from the listener
;;;; loop's dynamic environment, which is exactly the integer the
;;;; modes runtime needs for level_capture (SCHEMA §9 nested modes).
;;;;
;;;; No OSC B / OSC C — CCL has no stdlib pre-input hook. Input echo
;;;; is stripped deterministically the same way Python and Node do.

(in-package :cl-user)

(defparameter *splash-first-prompt* t)
(defparameter *splash-last-cwd* nil)

(defun splash-cwd ()
  (handler-case (namestring *default-pathname-defaults*)
    (error () nil)))

(defun splash-print-listener-prompt (stream &optional force)
  (declare (ignore force))
  (let ((esc (code-char #x1B))
        (bel (code-char #x07)))
    ;; D;0 between commands. CCL has no per-expression exit code,
    ;; same as Python and Racket.
    (unless *splash-first-prompt*
      (format stream "~C]633;D;0~C" esc bel))
    (setf *splash-first-prompt* nil)
    ;; P;Cwd when the working directory changed since the last prompt.
    (let ((cwd (splash-cwd)))
      (when (and cwd (not (equal cwd *splash-last-cwd*)))
        (format stream "~C]633;P;Cwd=~A~C" esc cwd bel)
        (setf *splash-last-cwd* cwd)))
    ;; A;prompt-start.
    (format stream "~C]633;A~C" esc bel)
    ;; The actual CCL prompt: ? at top-level, "N > " inside the Nth
    ;; break loop. Reads ccl::*break-level* from the listener loop's
    ;; dynamic environment, same as the default print-listener-prompt
    ;; would. The mode regex in ccl.yaml matches the resulting prompt
    ;; literally to detect debugger entry / nesting depth.
    (format stream "~[?~:;~:*~d >~] " ccl::*break-level*)
    (force-output stream)))

;; CCL hard-locks redefinition of internal symbols ("kernel" functions)
;; by default, even via (setf (symbol-function ...)). Bind the warn
;; variable to NIL so the locked-function check downgrades to a
;; permitted redefine. Without this, loading the integration script
;; bombs with "The function CCL::PRINT-LISTENER-PROMPT is predefined
;; in Clozure CL." and the worker's pipe never comes up.
(setq ccl:*warn-if-redefine-kernel* nil)
(setf (symbol-function 'ccl::print-listener-prompt)
      #'splash-print-listener-prompt)

;;; Multi-line command delivery helper. splash writes the command body
;;; to a .splash-exec-*.lisp tempfile and sends a single-line
;;; (splash-exec-file "...") call so the whole block is one OSC A-to-A
;;; boundary regardless of how many top-level forms it contains.
;;; Definitions leak into CL-USER because LOAD uses the calling
;;; package's namespace.
(defun splash-exec-file (path)
  (unwind-protect
       (load path)
    (handler-case (delete-file path)
      (error () nil))))

;;; Self-delete the integration tempfile so a long-running splash
;;; process doesn't leave stale files in TEMP. Gated on filename
;;; matching ".splash-integration-" so accidentally running this file
;;; from a developer checkout (e.g. probing CCL with the canonical
;;; source) does NOT wipe the source out from under the repository.
;;; CCL has already read and loaded this file by the time this form
;;; runs, so deleting it mid-load is safe in the worker case.
(handler-case
    (let ((p *load-pathname*))
      (when (and p
                 (search ".splash-integration-" (namestring p)))
        (delete-file p)))
  (error () nil))
