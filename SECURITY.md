# Security policy

## Supported versions

Security fixes go into the current release line. If you think you've found a security problem, please test against the latest release before reporting it when that's practical.

## Reporting a vulnerability

Please don't open a public issue for a suspected vulnerability. Use GitHub's private vulnerability reporting for this repository, or email `contact@glsswrks.gg`.

A useful report includes the Glimpse version or commit, your Windows version, the file type involved, clear reproduction steps, and any relevant logs. A small purpose-built test file is much easier and safer to work with than an original private document.

Logs redact local paths by default, but they can still contain details about the preview session. Please read them before attaching them. Don't send private files, credentials, or personal information.

We'll acknowledge the report, investigate it privately, and coordinate disclosure when a fix is ready. If the issue turns out not to be security-sensitive, we may suggest moving the discussion to a normal GitHub issue.

## Security boundaries

Glimpse installs a low-level keyboard hook so it can recognize preview shortcuts on supported Windows selection surfaces. It also parses and renders untrusted local files, including PDFs, archives, media, Office exports, and HTML.

Local HTML previews run with scripts disabled and remote network requests blocked. Glimpse does not upload previewed files. An elevated application is outside the reach of a normally running Glimpse process; Glimpse reports that mismatch rather than trying to cross the Windows integrity boundary.
