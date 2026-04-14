using Splash.Services;
using Splash.Services.Adapters;

namespace Splash.Tests;

public static class BalancedParensCounterTests
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

        Console.WriteLine("=== BalancedParensCounter Tests ===");

        // Lisp-family spec mirroring what racket.yaml declares. Every test
        // runs against this spec unless it explicitly builds its own.
        var lispSpec = new BalancedParensSpec
        {
            Open = ["(", "[", "{"],
            Close = [")", "]", "}"],
            StringDelims = ["\""],
            Escape = "\\",
            LineComment = ";",
            BlockComment = ["#|", "|#"],
            CharLiteralPrefix = "#\\",
            DatumCommentPrefix = "#;",
        };

        // --- baseline: balanced expressions are complete ---

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(+ 1 2)").IsComplete,
            "simple balanced expression is complete");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "").IsComplete,
            "empty input is trivially complete");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(define (f x) (+ x 1))").IsComplete,
            "nested balanced expression is complete");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "[1 2 3]").IsComplete,
            "square brackets balanced");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(let ([x 1] [y 2]) (+ x y))").IsComplete,
            "mixed paren + square brackets balanced");

        // --- baseline: unbalanced expressions are incomplete ---

        {
            var r = BalancedParensCounter.Evaluate(lispSpec, "(+ 1");
            Assert(!r.IsComplete && r.Depth == 1,
                $"open paren without close: incomplete, depth=1 (got IsComplete={r.IsComplete}, depth={r.Depth})");
        }

        {
            var r = BalancedParensCounter.Evaluate(lispSpec, "(define (f x");
            Assert(!r.IsComplete && r.Depth == 2,
                $"two open parens without close: incomplete, depth=2 (got depth={r.Depth})");
        }

        {
            var r = BalancedParensCounter.Evaluate(lispSpec, "(+ 1 2))");
            Assert(!r.IsComplete && r.Diagnostic != null && r.Diagnostic.Contains("unexpected"),
                $"extra closing paren is incomplete with diagnostic (got {r.Diagnostic})");
        }

        // --- strings with embedded brackets don't count toward depth ---

        Assert(BalancedParensCounter.Evaluate(lispSpec, "\"(((\"").IsComplete,
            "string literal with embedded opens doesn't break counting");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(display \"(\")").IsComplete,
            "paren inside string inside call balanced");

        Assert(!BalancedParensCounter.Evaluate(lispSpec, "\"unterminated").IsComplete,
            "unterminated string literal is incomplete");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(display \"say \\\"hi\\\"\")").IsComplete,
            "escaped quote inside string balanced");

        // --- comments are ignored ---

        Assert(BalancedParensCounter.Evaluate(lispSpec, "; (((\n(+ 1 2)").IsComplete,
            "line comment with fake opens doesn't affect depth");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "#| nested |# (+ 1 2)").IsComplete,
            "block comment with fake opens doesn't affect depth");

        Assert(!BalancedParensCounter.Evaluate(lispSpec, "#| open without close").IsComplete,
            "unterminated block comment is incomplete");

        // --- reader macros: char literals of parens (§18 Q1 extension) ---

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(char->integer #\\()").IsComplete,
            "char literal #\\( does NOT count as open paren");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(list #\\( #\\) #\\[)").IsComplete,
            "multiple char literals of bracket chars don't affect depth");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(char->integer #\\space)").IsComplete,
            "named char literal #\\space consumed as atom run");

        {
            // Without the char-literal extension, this would be detected as
            // "3 open parens + 0 close = depth 3". The fix must ensure the
            // second and third parens inside char literals are skipped.
            var r = BalancedParensCounter.Evaluate(lispSpec, "(list #\\( #\\( #\\()");
            Assert(r.IsComplete,
                $"three char-literal opens in a balanced list are complete (got depth={r.Depth})");
        }

        // --- reader macros: datum comments (§18 Q1 extension) ---

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(+ 1 #;999 2)").IsComplete,
            "datum comment on atom is complete");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(+ 1 #;(nested list here) 2)").IsComplete,
            "datum comment on list is complete");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(+ 1 #;\"skipped string\" 2)").IsComplete,
            "datum comment on string is complete");

        {
            // Nested datum comments: #;#;(a)(b) skips two following datums.
            var r = BalancedParensCounter.Evaluate(lispSpec, "(list 1 #;#;(a) (b) 2)");
            Assert(r.IsComplete,
                $"nested datum comments skip two datums (got IsComplete={r.IsComplete}, depth={r.Depth})");
        }

        // --- mixed: strings with char-literal-looking content ---

        Assert(BalancedParensCounter.Evaluate(lispSpec, "\"#\\(\"").IsComplete,
            "#\\( inside a string stays part of the string, not a char literal");

        // --- defaults: spec with no fields uses sensible defaults ---

        var defaultSpec = new BalancedParensSpec();
        Assert(BalancedParensCounter.Evaluate(defaultSpec, "(+ 1 2)").IsComplete,
            "defaults: ()/[]/{} balanced");
        Assert(!BalancedParensCounter.Evaluate(defaultSpec, "(+ 1").IsComplete,
            "defaults: unbalanced detected");

        Console.WriteLine($"\n{pass} passed, {fail} failed");
    }
}
