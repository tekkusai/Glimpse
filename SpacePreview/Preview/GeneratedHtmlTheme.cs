namespace SpacePreview.Preview;

internal static class GeneratedHtmlTheme
{
    public const string Css = """
/*
 * Glimpse generated-content theme.
 * One file serves Markdown previews, standalone code previews, and EPUB base styling.
 * The shell template stamps data-theme="dark" or data-theme="light" on <html>.
 * Color values are keyed to Utilities/Theme.cs; if a token changes there, change it here.
 */

:root {
  color-scheme: light;

  /* Surface tokens (Theme.cs Light) */
  --bg: #FFFFFF;              /* ContentBg */
  --text: #1D1E22;            /* TextPrimary */
  --text-2: #5C6270;          /* TextSecondary */
  --text-3: #878D99;          /* TextTertiary */
  --border: #D8DADF;          /* Border */
  --accent: #3B72E8;          /* Accent */
  --thumb: #B4B8C2;           /* ScrollThumb */

  /* Code surfaces */
  --code-bg: #F4F5F8;
  --code-border: #E4E6EB;
  --code-text: #23252B;
  --inline-code-bg: #EFF0F4;
  --kbd-bg: #F5F5F8;          /* BadgeBg */
  --kbd-border: #D9DBE2;      /* BadgeBorder */
  --ln: #B3B8C2;

  /* Code tokens, light */
  --tk-comment: #9AA0AB;
  --tk-keyword: #2F5FD0;
  --tk-string: #A9622F;
  --tk-number: #3E7D52;
  --tk-function: #6E42BF;
  --tk-type: #1D7A74;
  --tk-constant: #205FB8;
  --tk-property: #4A5B82;
  --tk-tag: #2F5FD0;
  --tk-attribute: #8A6A1F;
  --tk-operator: #5C6270;
  --tk-punct: #878D99;
  --tk-invalid: #BE4B43;
}

:root[data-theme="dark"] {
  color-scheme: dark;

  /* Surface tokens (Theme.cs Dark) */
  --bg: #18191D;              /* ContentBg */
  --text: #F4F4F5;            /* TextPrimary */
  --text-2: #A9ADB8;          /* TextSecondary */
  --text-3: #8F95A0;          /* TextTertiary */
  --border: #343741;          /* Border */
  --accent: #7DA2F7;          /* Accent, lifted for dark-surface contrast */
  --thumb: #5C616E;           /* ScrollThumb */

  /* Code surfaces */
  --code-bg: #121316;         /* DeepBg: blocks recede below the page */
  --code-border: #23252C;
  --code-text: #DFE2E8;
  --inline-code-bg: #26282F;  /* BadgeBg */
  --kbd-bg: #26282F;          /* BadgeBg */
  --kbd-border: #4B4E58;      /* BadgeBorder */
  --ln: #565B66;

  /* Code tokens, dark */
  --tk-comment: #7E8490;
  --tk-keyword: #7FA3F0;
  --tk-string: #D7A97C;
  --tk-number: #9FCBAB;
  --tk-function: #BBA7EE;
  --tk-type: #6FC4BE;
  --tk-constant: #86BFF5;
  --tk-property: #9BB2D8;
  --tk-tag: #7FA3F0;
  --tk-attribute: #D3BC8A;
  --tk-operator: #A9ADB8;
  --tk-punct: #8F95A0;
  --tk-invalid: #E2807C;
}

/* ---------- Page ---------- */

* { box-sizing: border-box; }

html {
  background: var(--bg);
  scrollbar-width: thin;
  scrollbar-color: var(--thumb) transparent;
}

body {
  margin: 0;
  background: var(--bg);
  color: var(--text);
  font-family: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
  font-size: 16px;
  line-height: 1.65;
  text-rendering: optimizeLegibility;
  -webkit-font-smoothing: antialiased;
  overflow-wrap: break-word;
}

/* ---------- Markdown document ---------- */

.md-root {
  max-width: 44rem;
  margin: 0 auto;
  padding: 44px clamp(20px, 6vw, 48px) 72px;
}

.md-root > :first-child { margin-top: 0; }
.md-root > :last-child { margin-bottom: 0; }

/* Chapter anchors (EPUB rail): land with a breath of space above */
.md-root section { scroll-margin-top: 10px; }

h1, h2, h3, h4, h5, h6 {
  font-family: "Segoe UI Variable Display", "Segoe UI", system-ui, sans-serif;
  font-weight: 600;
  line-height: 1.3;
  margin: 1.7em 0 0.55em;
  color: var(--text);
}

h1 {
  font-size: 1.75em;
  font-weight: 650;
  letter-spacing: -0.012em;
  padding-bottom: 0.35em;
  border-bottom: 1px solid var(--border);
}

h2 {
  font-size: 1.4em;
  letter-spacing: -0.008em;
  padding-bottom: 0.3em;
  border-bottom: 1px solid var(--border);
}

h3 { font-size: 1.17em; }
h4 { font-size: 1em; }
h5 { font-size: 0.92em; }
h6 { font-size: 0.85em; color: var(--text-2); }

p { margin: 0 0 1.05em; }

a {
  color: var(--accent);
  text-decoration: none;
}

a:hover { text-decoration: underline; }

strong { font-weight: 650; }

hr {
  border: 0;
  border-top: 1px solid var(--border);
  margin: 2.2em 0;
}

ul, ol {
  margin: 0 0 1.05em;
  padding-left: 1.7em;
}

li { margin: 0.22em 0; }

li > ul, li > ol { margin-bottom: 0; }

/* GFM task lists */
li.task-list-item { list-style: none; margin-left: -1.45em; }
li.task-list-item > input[type="checkbox"] {
  margin: 0 0.55em 0.14em 0;
  vertical-align: middle;
  accent-color: var(--accent);
}

blockquote {
  margin: 0 0 1.05em;
  padding: 0.1em 0 0.1em 1.05em;
  border-left: 3px solid var(--border);
  color: var(--text-2);
}

blockquote > :last-child { margin-bottom: 0; }

img {
  max-width: 100%;
  height: auto;
}

/* Book covers (EPUB): natural proportions, bounded height, quiet frame */
img.cover {
  max-height: 420px;
  width: auto;
  border: 1px solid var(--border);
}

/* Tables: GitHub-calm, self-scrolling when wide */
table {
  display: block;
  max-width: 100%;
  overflow-x: auto;
  border-collapse: collapse;
  margin: 0 0 1.15em;
  font-size: 0.94em;
}

th, td {
  padding: 0.45em 0.85em;
  border-bottom: 1px solid var(--border);
  text-align: left;
  vertical-align: top;
}

th {
  font-weight: 600;
  color: var(--text);
}

td { color: var(--text); }

kbd {
  font-family: "Cascadia Mono", Consolas, monospace;
  font-size: 0.8em;
  padding: 0.14em 0.45em 0.2em;
  background: var(--kbd-bg);
  border: 1px solid var(--kbd-border);
  border-bottom-width: 2px;
  border-radius: 5px;
  color: var(--text-2);
}

/* ---------- Code ---------- */

code, pre, samp {
  font-family: "Cascadia Mono", Consolas, monospace;
  font-variant-ligatures: none;
}

/* Inline code in text */
:not(pre) > code {
  font-size: 0.86em;
  padding: 0.14em 0.4em;
  background: var(--inline-code-bg);
  border-radius: 4px;
  color: var(--code-text);
}

/* Fenced blocks in Markdown */
pre.block {
  margin: 0 0 1.15em;
  padding: 14px 16px;
  background: var(--code-bg);
  border: 1px solid var(--code-border);
  border-radius: 8px;
  overflow-x: auto;
  scrollbar-width: thin;
  scrollbar-color: var(--thumb) transparent;
}

pre.block > code {
  display: block;
  font-size: 0.8125rem;
  line-height: 1.6;
  color: var(--code-text);
  background: none;
  padding: 0;
}

/* ---------- Standalone code document ---------- */

.code-doc {
  display: flex;
  align-items: flex-start;
  padding: 18px 0 48px;
  font-size: 0.8125rem;
  line-height: 1.6;
}

.code-doc pre {
  margin: 0;
  font-size: inherit;
  line-height: inherit;
}

.code-doc .gutter {
  flex: none;
  padding: 0 14px 0 18px;
  text-align: right;
  color: var(--ln);
  user-select: none;
  -webkit-user-select: none;
}

.code-doc .src {
  flex: 1;
  min-width: 0;
  padding-right: 24px;
  overflow-x: auto;
  color: var(--code-text);
  scrollbar-width: thin;
  scrollbar-color: var(--thumb) transparent;
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
}
