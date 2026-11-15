using System.Text.RegularExpressions;

namespace Agent42.Backend;

/// <summary>
/// The app's look for framed artifacts. An html or svg artifact runs on an opaque origin (the CSP sandbox), so it
/// cannot load <c>/styles.css</c>. <c>/frame</c> therefore injects this stylesheet: the app's theme tokens for the
/// palette and theme the pane asks for (<c>?palette=</c>, <c>?theme=light|dark</c>), plus element defaults copied from the chat's prose.
///
/// <para>Every element rule sits in <c>:where()</c> (zero specificity) and the block goes FIRST in the head, so any
/// CSS the artifact writes wins. The tokens (<c>--bg</c>, <c>--ink</c>, <c>--amber</c> …) are the contract the
/// <c>create_artifact</c> description hands the model. Their values live in <c>wwwroot/shared/themes.css</c>; <c>ArtifactThemePalettes</c> mirrors them and <c>ThemePaletteTests</c> guards the parity.</para>
/// </summary>
public static partial class ArtifactTheme
{
    // The app's own font stylesheet; the artifact CSP allows exactly these two Google Fonts hosts (ADR 0084).
    private const string FontsLink =
        "<link rel=\"stylesheet\" href=\"https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;500;600"
        + "&family=Newsreader:ital,opsz,wght@0,6..72,400;0,6..72,500;0,6..72,600;1,6..72,400&display=swap\">";

    private const string SharedTokens =
        "--mono:\"IBM Plex Mono\",ui-monospace,SFMono-Regular,Menlo,monospace;"
        + "--serif:\"Newsreader\",Georgia,\"Times New Roman\",serif;--radius:10px;";

    private const string ElementDefaults =
        ":where(html){background:var(--bg);color:var(--ink);font-family:var(--serif);font-size:16px;line-height:1.6;"
        + "accent-color:var(--amber)}"
        + ":where(body){margin:16px}"
        + ":where(h1,h2,h3,h4){font-weight:600;line-height:1.25;margin:1.2em 0 .5em}"
        + ":where(a){color:var(--amber);text-decoration-color:var(--amber-deep);text-underline-offset:3px}"
        + ":where(code,kbd,samp,pre){font-family:var(--mono);font-size:.85em}"
        + ":where(:not(pre)>code){background:var(--bg-inset);border:1px solid var(--line-soft);border-radius:4px;"
        + "padding:.1em .35em;color:var(--amber)}"
        + ":where(pre){background:var(--bg-inset);border:1px solid var(--line);border-radius:8px;padding:12px 14px;"
        + "overflow-x:auto}"
        + ":where(blockquote){margin:1em 0;padding:2px 0 2px 16px;border-left:2px solid var(--ink-faint);"
        + "color:var(--ink-dim);font-style:italic}"
        + ":where(hr){border:0;border-top:1px dashed var(--line)}"
        + ":where(table){border-collapse:collapse}"
        + ":where(th,td){border:1px solid var(--line);padding:6px 10px;text-align:left}"
        + ":where(th){background:var(--bg-raised);font-family:var(--mono);font-size:.8em;font-weight:500;"
        + "color:var(--ink-dim)}"
        + ":where(button,input,select,textarea){font-family:var(--mono);font-size:13px;color:var(--ink);"
        + "background:var(--bg-inset);border:1px solid var(--line);border-radius:6px;padding:6px 10px}"
        + ":where(button){background:var(--bg-raised);cursor:pointer}"
        + ":where(button:hover,button:focus-visible){border-color:var(--amber);color:var(--amber)}"
        + ":where(input:focus,select:focus,textarea:focus){outline:none;border-color:var(--amber)}"
        + ":where(input[type=checkbox],input[type=radio],input[type=range],input[type=color]){padding:0}"
        + ":where(svg){color:var(--ink)}"
        + "::selection{background:var(--amber-glow)}";

    /// <summary>
    /// The style block for one palette and theme; anything but <c>"light"</c> is the app's dark default and an
    /// unknown (or absent) palette is amber.
    /// </summary>
    public static string Block(string? theme, string? palette = null)
        => FontsLink + "<style data-agent42-theme>:root{"
            + TokensFor(palette, theme) + SharedTokens + "}" + ElementDefaults + "</style>";

    /// <summary>
    /// Puts the block first inside the artifact's head: after <c>&lt;head&gt;</c>, else after <c>&lt;html&gt;</c>,
    /// else after the doctype, else at the very start. Never BEFORE a doctype, which would drop the page into
    /// quirks mode.
    /// </summary>
    public static string Apply(string html, string? theme, string? palette = null)
    {
        var block = Block(theme, palette);
        var anchor = HeadTag().Match(html);
        if (!anchor.Success)
        {
            anchor = HtmlTag().Match(html);
        }

        if (!anchor.Success)
        {
            anchor = Doctype().Match(html);
        }

        var at = anchor.Success ? anchor.Index + anchor.Length : 0;
        return html.Insert(at, block);
    }

    [GeneratedRegex(@"<head(?:\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadTag();

    [GeneratedRegex(@"<html(?:\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^\s*<!doctype[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Doctype();
}
