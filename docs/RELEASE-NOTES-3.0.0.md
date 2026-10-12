# Glimpse 3.0

![Glimpse 3.0 showing a PDF with its page rail, in light theme](https://raw.githubusercontent.com/tekkusai/Glimpse/v3.0.0/docs/screenshots/pdf.png)

**Previews arrive instead of loading.** Every file now opens the same way. When its content is ready within a moment, the window fades in with it already there. When it isn't, the window takes the file's shape right away and shows the best picture of it Glimpse has: page one of a PDF you have opened before, an Office file's own thumbnail, or a quiet outline at the page's exact size. The content then dissolves in over it. The spinner, the dimmed overlay and the zoom-in pop are gone. Arrowing through a folder crossfades from one file to the next with no blank frame in between, and a video waits for its first frame instead of flashing black.

**Faster where you wait.** Opening a file again right after closing it is quicker for nearly every type. On the same machine, Markdown went from 199 to 66 ms, PDFs from 454 to 263 ms, PowerPoint from 536 to 251 ms and video from 361 to 87 ms.

**Lighter on your PC.** In our tests, previewing 26 files of every kind took about a third less CPU than in 2.0, Office conversion aside, and an open PDF takes about half the CPU and 100 to 300 MB less memory. Glimpse listens to the keyboard only while Explorer, the desktop or a file dialog is in front, and no longer wakes the processor while it waits in the tray.

**Updates take care of themselves.** Glimpse now installs updates while you're away from your PC: only after a few minutes without keyboard or mouse input, and never while a preview is open or you're in a full-screen game or presentation. There is no update window to click through. Glimpse checks each update's signature and installs only releases signed by its developer. Settings shows the update status in one line, with Check for updates and Install now, and has a switch to turn automatic installs off.

**One look.** The header, menus, Settings and every page Glimpse draws share one set of colors, type and scrollbars, in dark and light, and an open preview switches with you when you change the theme. In light theme the PDF toolbar is light too. The Open button shows your default app's icon, keeps the same width for every file, and its menu starts with Open with. The line under the file name says what the name can't: the size, the page count or pixel size, and a short time like Today 4:35 PM. F11 and the zoom shortcuts work without clicking into the preview first.

**Fill & Sign you can trust.** Signatures, dates and stamps save exactly where the screen showed them, including on A4 and rotated pages. More PDFs can be signed, including the compact kind Acrobat and Word save, and a PDF that can't be signed says so before you start. Leaving with unsaved work always asks first, whether you press Esc, Space or an arrow, close the preview, unpin it or quit Glimpse. Work you haven't saved also keeps itself: if Glimpse closes for an update, a restart, a sign-out or a crash, the Sign button reads Resume the next time you open that PDF and brings the work back on the page you left. Pages render in a separate process, so a difficult PDF can no longer take Glimpse down.

**Documents.** The page rail no longer stops at the first ten pages of a long PDF. Large PowerPoint decks open on their first slides while the rest converts, then switch to the whole deck at the slide you're on. A deck you've looked at finishes converting after you close it, so the next look shows every slide, and Office files next to the one you're viewing convert in the background. Templates, slide shows, macro-enabled and OpenDocument files all convert now, links in Office documents still work, and spreadsheets take the app theme around their cells. A one-page document fits its window whole.

**Images, models and fonts.** Images zoom around the cursor, double-click to their real size, and show transparency on a checkerboard; Ctrl+Plus, Ctrl+Minus and Ctrl+0 work here too. 3D models render as solid surfaces instead of scattered triangles, and GLB files open as models. Fonts preview as a full specimen in their own face.

**Also in this release**

- A new icon: the Space key, the one key Glimpse asks you to learn, in the taskbar, the tray and the welcome
- Files without a richer view share one card layout, so titles no longer jump between file types
- Archives open as the same card as a folder, with the size of everything inside, compressed tar archives included
- A damaged HEIC photo says it may be damaged instead of sending you to the Microsoft Store
- Run as administrator appears only for files that can run that way
- Audio shows the track's cover and tags; heading links in Markdown work
- Space in a second Explorer window moves the preview there
- A pinned, always-on-top preview steps behind a game that goes full screen in its own window
- Web pages, Markdown, code, text and spreadsheets open at the size of a desktop browser window, so a page shows the layout its author designed
- Launching Glimpse while it is already running opens Settings, which replaces the Hide tray icon option
- Settings lists anything this PC is missing for full previews, with one button to get each, and the signatures you've saved, which you can delete there
- Copy diagnostics in Settings copies the version and recent log lines for a bug report
- Settings are saved safely, and a damaged settings file is set aside instead of lost
- Setup follows Windows' light or dark mode

**Installing**

Download **Glimpse-Setup-3.0.0.exe** below and run it, or update from inside Glimpse 2.0. Existing preferences and your startup choice are preserved.

**Requirements**

- Windows 10 1809 or newer
- WebView2 Runtime for web-backed previews; Setup installs it only when it is missing
- LibreOffice or OpenOffice for full Word, Excel and PowerPoint previews; Setup can add LibreOffice when needed

**SHA-256**

`CA6370B1AF226EF2CB5BECA29EA93FE6F73B628CF125AD6CD4AC4411DC42CC87`

The installer is not code-signed yet, so Windows may show a SmartScreen warning.
