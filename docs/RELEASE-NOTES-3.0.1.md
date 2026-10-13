# Glimpse 3.0.1

This release fixes what a line-by-line review of 3.0 found. Most of it is older than 3.0, and some of it concerns what a previewed file can do without your say, so please update.

**A previewed web page can no longer contact a server.** In 3.0.0 and every earlier version, pressing Space on a saved web page (`.html`) could let that page contact a server its author chose, through an embedded frame or a connection hint that the blocker never saw. The page's author would learn that the file was opened, when, and from what network address. In 3.0.1 every frame is judged by the same rule as the page, the blocker covers every kind of request, and the built-in browser has no route to any server a page names. The one address it can still reach is Microsoft's configuration service for the browser itself, which it has contacted at every start in every version. A saved page that keeps its frames inline will show without them.

**Office and OpenDocument files, and one gap that is still open.** Before LibreOffice converts a document, Glimpse now removes links that point outside the file from OpenDocument files (`.odt`, `.ods`, `.odp`), as it already did for modern Office files (`.docx`, `.xlsx`, `.pptx`). Old-format Office files (`.doc`, `.xls`, `.ppt`, `.rtf`) cannot be cleaned this way yet. An old-format file that links a picture from a shared folder on another computer can still make LibreOffice reach for that folder when you press Space on the file, and Windows may try to sign in there. Until that is closed, don't preview old-format Office files from someone you don't trust. Closing it for every format is the next piece of work.

**Glimpse no longer takes the keys you type.** In every earlier version, a space typed into File Explorer's search box or address bar on Windows 11 never arrived, because Glimpse took it as a request for a preview. Glimpse now takes no key while a text box has the keyboard. During a rename with a preview open, Esc cancels the rename and a second Esc closes the preview. From Explorer's search box or address bar, Esc closes the preview first. Space still opens a preview after a click on Sort, View or a tab. If you move through Explorer's header with the Tab key, Space now presses the button you are on, where earlier versions opened a preview. Glimpse also stays out of dialogs that are not file pickers, such as message boxes and installers.

**File pickers preview the right file.** In an Open or Save dialog, a row shows the name Windows displays for a file, which is not always the file's real name. Windows hides the extension of known file types unless you turn that off, and always hides it for shortcuts. Every earlier version took the displayed name for the real one. With extensions hidden, Space on `report.docx` could preview nothing, or preview a different file that was really named `report`. Shortcuts previewed nothing, and in a Save dialog Glimpse previewed the name typed in the box, not the selected row. Glimpse now asks Windows which file a row stands for. When two files show the same name and the view has no Type column to tell them apart, it previews neither. A Space pressed in a picker is also no longer lost when the picker refreshes at that instant, and a click on another file can no longer open a preview by itself.

**Space keeps working in Explorer.** In 3.0.0 a program in the background could switch Glimpse off in the Explorer window you were using, and Space did nothing there until you clicked away and back. Glimpse now asks Windows which window is in front and no longer takes another program's word for it.

**Keys answer at once.** The Space and arrow keys used to wait whenever Glimpse was busy drawing a preview. In our own log a key waited as long as an eighth of a second. Keys are now read on their own and never wait for the window. A step through a folder also starts up to 20 ms sooner: 3.0.0 always waited 20 ms before loading the next file, and 3.0.1 starts at once when nothing is still loading.

**Fill & Sign.** Scrolling to a page with the mouse wheel and pressing Sign could open page 1 for signing; it now opens the page you are on. A PDF built to show one page and sign another is refused. The signed file is written to disk before it replaces your original, and a PDF in a folder you can't write to now offers a save dialog. Pressing Sign shows at once that it was taken, and a message that says why something wasn't saved is shown whole at any window size. Pages the viewer shows but 3.0 refused to sign, such as a page turned by an odd angle, now sign. Save and Cancel used to show the same four buttons. Save now offers a signed copy or replacing the original. Closing with unsaved work says so and offers to keep editing, discard, or save a signed copy. Pressing X while that question was already showing did nothing you could see; now the question flashes.

**Rotation tells the truth.** A rotation that could not be saved used to leave the picture turned on screen and unturned on disk, with nothing said. Now the picture turns back and says why. Glimpse also no longer loses the file to its own background work on large photos, which was the usual cause. Rotating a PNG or BMP keeps everything else the file held. If the picture is moved or deleted in the moment its rotation is being saved, the rotated copy used to be deleted with it. It is now kept: it takes the picture's name, or Glimpse tells you what the copy is called. Animated PNG and WebP images play.

**Books and documents.** An EPUB that uses common named characters such as a non-breaking space opened as a plain card; it opens as a book now, and one unreadable chapter no longer hides the rest. When the later pages of a long Office file can't be converted, Glimpse says so. When they arrive while you are reading, you keep your place. LibreOffice is never left running after Glimpse closes.

**Network folders.** A PDF on a shared folder (`\\server\share`) opens in the viewer again; 3.0 showed an error card. Web pages, Markdown and text on a shared folder open at full size, and a web page there shows as a page. A PDF on a drive letter that points at a share no longer holds the window still while the share answers, and pictures there show their pixel size again.

**First run.** The welcome window is centered at every display scale. At 125 % and above it used to open off-center or partly off screen.

**Also in this release**

- A folder's rows appear with its card, and a large folder shows its real size instead of "At least"
- App packages show their description again
- Model files, old Illustrator files and unreadable STL files show their facts on the card again, as in 2.0
- 3D model files built to exhaust memory are refused: a 60-byte file could ask for 19 GB
- A graphics driver reset no longer replaces a pinned preview with an error
- The text-only view of a workbook says which sheets, rows and columns it left out, and a workbook built to exhaust memory can no longer do so there
- Sheet tabs show their names when LibreOffice runs in a language other than English
- An Illustrator file whose title contains a bracket shows the whole title, as in 2.0
- An archive that holds two files whose names differ only by upper and lower case shows both
- An app package with a very tall or very wide logo no longer has it enlarged in memory
- Copy diagnostics no longer includes the names of files you previewed
- A release whose signature arrives late is picked up at the next check instead of being skipped for good
- Setup closes every leftover Glimpse process before it replaces files, and leaves a copy running from another folder alone
- The window can no longer be dragged smaller than its minimum size
- With Windows animations turned off, nothing in Glimpse fades

**Known in this release**

- Rotation is view-only for a few uncommon kinds of PNG and BMP that could only be saved by converting the file to another format
- A chapter of an EPUB that is written as plain HTML is skipped, with a line saying part of the book is missing
- In a file picker shown as a list or as icons, a file and a shortcut that display the same name preview neither. Details view tells them apart
- In a file picker, a file that is outlined but not selected takes two presses of Space: the first selects it
- File dialogs of the oldest style, which a few older programs still use, are not previewed

**Installing**

Download **Glimpse-Setup-3.0.1.exe** below and run it. Glimpse 3.0 installs this update on its own two days after release, while you are away from your PC, or at once from Settings with Install now. Existing preferences and your startup choice are preserved.

**Requirements**

- Windows 10 1809 or newer
- WebView2 Runtime for web-backed previews; Setup installs it only when it is missing
- LibreOffice or OpenOffice for full Word, Excel and PowerPoint previews; Setup can add LibreOffice when needed

**SHA-256**

`BD898B8DBA2CBF01D23DD5CBB6C04888236354716A307B59B36B71A203D6C86B`

The installer is not code-signed yet, so Windows may show a SmartScreen warning.
