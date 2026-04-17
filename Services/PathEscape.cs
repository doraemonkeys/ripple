using System;
using Ripple.Services.Adapters;

namespace Ripple.Services;

/// <summary>
/// Quote context for <see cref="PathEscape.Escape"/>. Declared by an
/// adapter's <c>capabilities.cd_command_quote</c> field. Each value
/// describes how the adapter's <c>cd_command</c> template wraps its
/// <c>{path}</c> placeholder, and therefore which characters need
/// escaping when we substitute a runtime path into that template.
/// </summary>
internal enum QuoteContext
{
    /// <summary>
    /// bash / zsh / sh style single-quoted literal (<c>'path'</c>).
    /// A literal <c>'</c> inside is spelled <c>'\''</c> — close the quote,
    /// emit an escaped quote, reopen the quote. All other bytes are
    /// literal inside <c>'...'</c>, including backslashes.
    /// </summary>
    SingleQuotePosix,

    /// <summary>
    /// pwsh / powershell single-quoted literal (<c>'path'</c>).
    /// A literal <c>'</c> inside is doubled (<c>''</c>). Backslashes
    /// and other bytes are literal.
    /// </summary>
    SingleQuotePwsh,

    /// <summary>
    /// cmd.exe double-quoted string (<c>"path"</c>).
    /// A literal <c>"</c> inside is doubled (<c>""</c>). Backslashes
    /// and other bytes are literal.
    /// </summary>
    DoubleQuoteCmd,

    /// <summary>
    /// C-family double-quoted string literal (Python, JavaScript/Node,
    /// and others). Inside <c>"..."</c> the escape character is
    /// backslash: <c>\</c> is spelled <c>\\</c> and <c>"</c> is
    /// spelled <c>\"</c>. Notably different from shell single-quote
    /// semantics — the <c>'\''</c> trick used for bash doesn't work
    /// here because adjacent string concatenation isn't universal in
    /// C-style languages.
    /// </summary>
    CStyleDoubleQuote,
}

internal static class PathEscape
{
    /// <summary>
    /// Map the YAML-level string value (from <c>capabilities.cd_command_quote</c>)
    /// onto a <see cref="QuoteContext"/>. Returns null for unknown / null
    /// input so callers can treat a missing value as "no cd_command
    /// support on this adapter".
    /// </summary>
    public static QuoteContext? ParseContext(string? yamlValue) => yamlValue switch
    {
        "single_quote_posix" => QuoteContext.SingleQuotePosix,
        "single_quote_pwsh" => QuoteContext.SingleQuotePwsh,
        "double_quote_cmd" => QuoteContext.DoubleQuoteCmd,
        "c_style_double_quote" => QuoteContext.CStyleDoubleQuote,
        _ => null,
    };

    /// <summary>
    /// Escape <paramref name="path"/> for safe substitution into the
    /// <c>{path}</c> placeholder of an adapter's <c>cd_command</c>
    /// template, where the template wraps the placeholder in the quote
    /// style described by <paramref name="ctx"/>. The returned string
    /// does NOT include the surrounding quote characters — those are
    /// part of the template, this only handles the characters that
    /// would otherwise break out of the quoted region.
    /// </summary>
    public static string Escape(string path, QuoteContext ctx) => ctx switch
    {
        QuoteContext.SingleQuotePosix => path.Replace("'", "'\\''"),
        QuoteContext.SingleQuotePwsh => path.Replace("'", "''"),
        QuoteContext.DoubleQuoteCmd => path.Replace("\"", "\"\""),
        // Backslash must be escaped first, otherwise the new backslashes
        // introduced by the `"` → `\"` pass would get doubled on a second
        // backslash sweep.
        QuoteContext.CStyleDoubleQuote => path.Replace("\\", "\\\\").Replace("\"", "\\\""),
        _ => throw new ArgumentOutOfRangeException(nameof(ctx), ctx, null),
    };

    /// <summary>
    /// Render a ready-to-send cd command for <paramref name="adapter"/>
    /// that would move its already-running shell/REPL to
    /// <paramref name="path"/>. Returns null when the adapter does not
    /// declare a <c>cd_command</c> template, which callers treat as
    /// "this adapter does not participate in runtime cwd management".
    /// </summary>
    public static string? RenderCdCommand(Adapter adapter, string path)
    {
        var tpl = adapter.Capabilities.CdCommand;
        var ctx = ParseContext(adapter.Capabilities.CdCommandQuote);
        if (string.IsNullOrEmpty(tpl) || ctx is null) return null;
        return tpl.Replace("{path}", Escape(path, ctx.Value));
    }
}
