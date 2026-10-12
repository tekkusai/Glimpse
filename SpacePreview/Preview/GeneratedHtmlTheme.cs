using System.Globalization;
using System.Windows.Media;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// The one stylesheet every page Glimpse writes starts from. The tokens come
/// from the app's palettes, so a web page cannot drift from the WPF chrome
/// beside it, and each has one name. Light is written as the base and dark
/// behind prefers-color-scheme, which the preview window sets from the app
/// theme: a page is the same text in either theme, cached once, and an open
/// one turns with the app. docs/DESIGN.md ("Web pages") is the contract.
/// </summary>
internal static class GeneratedHtmlTheme
{
    /// <summary>
    /// Inline code longer than this many characters is marked "long" when a
    /// page is built, and only those chips may wrap.
    /// </summary>
    public const int LongChip = 40;

    /// <summary>
    /// Tokens, the canvas, the in-page reveal, scrollbars, selection and the
    /// font stacks. Every page starts here.
    /// </summary>
    public static readonly string PageCss = BothThemes(PaletteTokens) + """
:root {
  --font-ui: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
  --font-display: "Segoe UI Variable Display", "Segoe UI", system-ui, sans-serif;
  --font-mono: "Cascadia Mono", Consolas, monospace;
}

/* The page dissolves itself in over whatever the host shows beneath it.
   No forward fill: a finished fill would pin opacity and turn the host's
   in-page exit fade into a cut. */
html {
  background: var(--canvas);
  color: var(--text);
  font-family: var(--font-ui);
  animation: glimpse-in 160ms cubic-bezier(.2,0,0,1) backwards;
}
@keyframes glimpse-in { from { opacity: 0; } }
@media (prefers-reduced-motion: reduce) {
  html { animation: none; }
}

::selection { background: var(--selection); }

/* PreviewScrollBar's twin: 11 px, no arrows, a 7 px capsule thumb. The
   standard scrollbar properties must stay unset on these pages, or
   Chromium lets them override these. */
::-webkit-scrollbar { width: 11px; height: 11px; background: transparent; }
::-webkit-scrollbar-button { display: none; width: 0; height: 0; }
::-webkit-scrollbar-track, ::-webkit-scrollbar-corner { background: transparent; }
::-webkit-scrollbar-thumb {
  min-width: 30px;
  min-height: 30px;
  border: 2px solid transparent;
  border-radius: 6px;
  background: var(--thumb) padding-box;
}
::-webkit-scrollbar-thumb:hover { background: var(--thumb-hover) padding-box; }

""";

    /// <summary>Markdown, code and EPUB documents.</summary>
    public static readonly string DocumentCss = DocumentRules + BothThemes(SyntaxTokens);

    private static string BothThemes(Func<ThemePalette, string> tokens) => $$"""
:root {
{{tokens(Theme.Light)}}}
@media (prefers-color-scheme: dark) {
:root {
{{tokens(Theme.Dark)}}}
}

""";

    private static string PaletteTokens(ThemePalette p) => $$"""
  color-scheme: {{(p.IsDark ? "dark" : "light")}};
  --canvas: {{Hex(p.CanvasBg)}};
  --chrome: {{Hex(p.ChromeBg)}};
  --raised: {{Hex(p.RaisedBg)}};
  --raised-border: {{Hex(p.RaisedBorder)}};
  --border: {{Hex(p.Border)}};
  --hover: {{Rgba(p.HoverBg, p.HoverBg.A / 255.0)}};
  --pressed: {{Rgba(p.PressedBg, p.PressedBg.A / 255.0)}};
  --text: {{Hex(p.TextPrimary)}};
  --text-2: {{Hex(p.TextSecondary)}};
  --text-3: {{Hex(p.TextTertiary)}};
  --accent: {{Hex(p.Accent)}};
  --warning: {{Hex(p.Warning)}};
  --thumb: {{Rgba(p.ScrollThumb, 0.6)}};
  --thumb-hover: {{Rgba(p.ScrollThumb, 0.95)}};
  --selection: {{Rgba(p.Accent, 0.55)}};

""";

    private const string DocumentRules = """
* { box-sizing: border-box; }

body {
  margin: 0;
  font-size: 15px;
  line-height: 1.6;
  overflow-wrap: break-word;
}

/* ---------- Markdown and EPUB documents ---------- */

.md-root {
  max-width: 720px;
  margin: 0 auto;
  padding: 40px clamp(24px, 6vw, 48px) 64px;
}

.md-root > :first-child { margin-top: 0; }
.md-root > :last-child { margin-bottom: 0; }

/* Anchor landings (headings, EPUB chapters) keep a breath of space above. */
.md-root section, .md-root [id] { scroll-margin-top: 16px; }

h1, h2, h3, h4, h5, h6 {
  font-weight: 600;
  line-height: 1.3;
  margin: 32px 0 12px;
  color: var(--text);
}

h1, h2 { font-family: var(--font-display); }

h1 {
  font-size: 28px;
  letter-spacing: -0.01em;
  padding-bottom: 8px;
  border-bottom: 1px solid var(--border);
}

h2 {
  font-size: 21px;
  padding-bottom: 6px;
  border-bottom: 1px solid var(--border);
}

h3 { font-size: 17px; }
h4 { font-size: 15px; }
h5, h6 { font-size: 13px; }
h6 { color: var(--text-2); }

p { margin: 0 0 16px; }

a { color: var(--accent); text-decoration: none; }
a:hover { text-decoration: underline; }

strong { font-weight: 600; }

hr {
  border: 0;
  border-top: 1px solid var(--border);
  margin: 32px 0;
}

ul, ol { margin: 0 0 16px; padding-left: 24px; }
li { margin: 4px 0; }
li > ul, li > ol, li > p { margin-bottom: 0; }

/* GFM task lists */
li.task-list-item { list-style: none; margin-left: -20px; }
li.task-list-item > input[type="checkbox"] {
  margin: 0 8px 0 0;
  vertical-align: -2px;
  accent-color: var(--accent);
}

blockquote {
  margin: 0 0 16px;
  padding: 0 0 0 16px;
  border-left: 3px solid var(--raised-border);
  color: var(--text-2);
}

blockquote > :last-child { margin-bottom: 0; }

img { max-width: 100%; height: auto; }

/* Book covers (EPUB): natural proportions, bounded height, quiet frame */
img.cover {
  max-height: 420px;
  width: auto;
  border: 1px solid var(--border);
  border-radius: 8px;
}

/* Tables: hairline rows, self-scrolling when wide */
table {
  display: block;
  max-width: 100%;
  width: max-content;
  overflow-x: auto;
  border-collapse: collapse;
  margin: 0 0 16px;
  font-size: 14px;
  line-height: 1.5;
}

th, td {
  padding: 6px 16px 6px 0;
  border-bottom: 1px solid var(--border);
  text-align: left;
  vertical-align: top;
}

th {
  font-weight: 600;
  border-bottom-color: var(--raised-border);
}

kbd {
  font-family: var(--font-mono);
  font-size: 0.8em;
  padding: 1px 6px 2px;
  background: var(--raised);
  border: 1px solid var(--raised-border);
  border-bottom-width: 2px;
  border-radius: 6px;
  color: var(--text-2);
}

.notice { font-size: 13px; color: var(--text-3); }

/* ---------- Code ---------- */

code, pre, samp {
  font-family: var(--font-mono);
  font-variant-ligatures: none;
}

/* Inline code in prose stays one piece: a break inside a command reads as
   two chips and misquotes it. Only a chip over LongChip characters
   (code.long) wraps, and stays a chip on each line. */
:not(pre) > code {
  font-size: 0.87em;
  padding: 1px 6px;
  background: var(--raised);
  border: 1px solid var(--raised-border);
  border-radius: 6px;
  white-space: nowrap;
  -webkit-box-decoration-break: clone;
  box-decoration-break: clone;
}

:not(pre) > code.long { white-space: normal; }

/* Fenced blocks in Markdown */
pre.block {
  margin: 0 0 16px;
  padding: 12px 16px;
  background: var(--raised);
  border: 1px solid var(--raised-border);
  border-radius: 8px;
  overflow-x: auto;
}

pre.block > code {
  display: block;
  font-size: 13px;
  line-height: 1.6;
}

/* ---------- Standalone code document ---------- */

/* The root scrolls both ways, so the horizontal bar sits at the window's
   edge, and the gutter stays put while long lines slide under it. */
.code-doc {
  display: flex;
  align-items: flex-start;
  width: max-content;
  min-width: 100%;
  padding: 16px 0 48px;
  font-size: 13px;
  line-height: 1.6;
}

.code-doc pre {
  margin: 0;
  font-size: inherit;
  line-height: inherit;
}

.code-doc .gutter {
  position: sticky;
  left: 0;
  flex: none;
  padding: 0 16px 0 20px;
  text-align: right;
  color: var(--text-3);
  background: var(--canvas);
  user-select: none;
}

.code-doc .src {
  flex: 1;
  padding-right: 24px;
}

/* ---------- Syntax tokens ---------- */

.tk-comment   { color: var(--tk-comment); }
.tk-keyword   { color: var(--tk-keyword); }
.tk-string    { color: var(--tk-string); }
.tk-number    { color: var(--tk-number); }
.tk-function  { color: var(--tk-function); }
.tk-type      { color: var(--tk-type); }
.tk-constant  { color: var(--tk-constant); }
.tk-property  { color: var(--tk-property); }
.tk-tag       { color: var(--tk-tag); }
.tk-attribute { color: var(--tk-attribute); }
.tk-operator  { color: var(--tk-operator); }
.tk-punct     { color: var(--tk-punct); }
.tk-invalid   { color: var(--tk-invalid); }

""";

    // Syntax hues are the file's content, not chrome; the neutral roles
    // (comments, operators, punctuation) still come from the palette.
    private static string SyntaxTokens(ThemePalette p) => p.IsDark
        ? $$"""
  --tk-comment: {{Hex(p.TextTertiary)}};
  --tk-keyword: #7FA3F0;
  --tk-string: #D7A97C;
  --tk-number: #9FCBAB;
  --tk-function: #BBA7EE;
  --tk-type: #6FC4BE;
  --tk-constant: #86BFF5;
  --tk-property: #9BB2D8;
  --tk-tag: #7FA3F0;
  --tk-attribute: #D3BC8A;
  --tk-operator: {{Hex(p.TextSecondary)}};
  --tk-punct: {{Hex(p.TextTertiary)}};
  --tk-invalid: #E2807C;

"""
        : $$"""
  --tk-comment: {{Hex(p.TextTertiary)}};
  --tk-keyword: #2F5FD0;
  --tk-string: #A9622F;
  --tk-number: #3E7D52;
  --tk-function: #6E42BF;
  --tk-type: #1D7A74;
  --tk-constant: #205FB8;
  --tk-property: #4A5B82;
  --tk-tag: #2F5FD0;
  --tk-attribute: #8A6A1F;
  --tk-operator: {{Hex(p.TextSecondary)}};
  --tk-punct: {{Hex(p.TextTertiary)}};
  --tk-invalid: #BE4B43;

""";

    internal static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    internal static string Rgba(Color c, double alpha) =>
        $"rgba({c.R},{c.G},{c.B},{alpha.ToString("0.###", CultureInfo.InvariantCulture)})";
}
