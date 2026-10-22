Everything new since Glimpse 2 is on this page.

## New in Glimpse 3

![Glimpse 3.0 showing a PDF with its page rail, in light theme](https://raw.githubusercontent.com/tekkusai/Glimpse/v3.0.1/docs/screenshots/pdf.png)

**Previews arrive instead of loading.** Every file now opens the same way. When its content is ready within a moment, the window fades in with it already there. When it isn't, the window takes the file's shape right away and shows the best picture of it Glimpse has: page one of a PDF you have opened before, an Office file's own thumbnail, or a quiet outline at the page's exact size. The content then dissolves in over it. The spinner, the dimmed overlay and the zoom-in pop are gone. Arrowing through a folder crossfades from one file to the next with no blank frame in between, and a video waits for its first frame instead of flashing black.

**Faster where you wait.** Opening a file again right after closing it is quicker for nearly every type. On the same machine, Markdown went from 199 to 66 ms, PDFs from 454 to 263 ms, PowerPoint from 536 to 251 ms and video from 361 to 87 ms.

**Lighter on your PC.** In our tests of 3.0.0, previewing 26 files of every kind took about a third less CPU than in 2.0, Office conversion aside, and an open PDF takes about half the CPU and 100 to 300 MB less memory. Glimpse listens to the keyboard only while Explorer, the desktop or a file dialog is in front.

**Updates take care of themselves.** Glimpse now installs updates while you're away from your PC: only after a few minutes without keyboard or mouse input, and never while a preview is open or you're in a full-screen game or presentation. There is no update window to click through. Glimpse checks each update's signature and installs only releases signed by its developer. Settings shows the update status in one line, with Check for updates and Install now, and has a switch to turn automatic installs off.

**One look.** The header, menus, Settings and every page Glimpse draws share one set of colors, type and scrollbars, in dark and light, and an open preview switches with you when you change the theme. In light theme the PDF toolbar is light too. The Open button shows your default app's icon, keeps the same width for every file, and its menu starts with Open with. The line under the file name says what the name can't: the size, the page count or pixel size, and a short time like Today 4:35 PM. F11 and the zoom shortcuts work without clicking into the preview first.

**Fill & Sign you can trust.** Signatures, dates and stamps save exactly where the screen showed them, including on A4 and rotated pages. More PDFs can be signed, including the compact kind Acrobat and Word save, and a PDF that can't be signed says so before you start. Leaving with unsaved work always asks first, whether you press Esc, Space or an arrow, close the preview, unpin it or quit Glimpse. Work you haven't saved also keeps itself: if Glimpse closes for an update, a restart, a sign-out or a crash, the Sign button reads Resume the next time you open that PDF and brings the work back on the page you left. Pages render in a separate process, so a difficult PDF can no longer take Glimpse down.

**Documents.** The page rail no longer stops at the first ten pages of a long PDF. Large PowerPoint decks open on their first slides while the rest converts, then switch to the whole deck at the slide you're on. A deck you've looked at finishes converting after you close it, so the next look shows every slide, and Office files next to the one you're viewing convert in the background. Templates, slide shows, macro-enabled and OpenDocument files all convert now, links in Office documents still work, and spreadsheets take the app theme around their cells. A one-page document fits its window whole.

**Images, models and fonts.** Images zoom around the cursor, double-click to their real size, and show transparency on a checkerboard; Ctrl+Plus, Ctrl+Minus and Ctrl+0 work here too. 3D models render as solid surfaces instead of scattered triangles, and GLB files open as models. Fonts preview as a full specimen in their own face.

**Also new in 3**

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

## Fixed in 3.0.4

3.0.4 fixes places where Glimpse undid something you had just done.

**A page you click in the sidebar stays chosen.** Clicking a page in a PDF's page sidebar could send the highlight back to the page before, with a flash. After each click Glimpse checks that the document moved, and it checked a few milliseconds too early. About one click in four looked like it had failed, so Glimpse reloaded the whole document, which was the flash. The reloaded document carried the page number in its address, and Glimpse took that for you scrolling and moved the highlight back. Now Glimpse waits for the document's answer, a page you choose stays highlighted until you scroll away from it, and the document is reloaded only when it really did not move. In the first half second after a document opens, the viewer can ignore a click; Glimpse now tries again, so such a click can take up to a third of a second longer to arrive.

**Office files that are still converting.** In a long Word or PowerPoint file, a page you chose before it had converted lost its highlight to the page on screen within a third of a second. It now stays chosen until the page arrives. A page among the first ones, chosen while the whole document loads behind them, now stays on screen, where the document used to jump to another page and back. If you zoomed out of the first pages, the whole document no longer puts you back at fit width when it arrives.

**E-books.** Clicking a short chapter at the end of a book, such as an epilogue or About the author, moved the highlight back to the chapter before it, every time. The contents now follow the book only when you scroll it.

**Fill & Sign.** A page clicked in the sidebar just as Sign opened could be overruled, and Sign opened on the page before. Sign now opens on the page you chose.

**Words in pictures.** A large picture opens on a quick first image and sharpens a moment later. Words you had selected in between were cleared when it sharpened, and Ctrl+C then copied the file. The selection now stays.

**Folders.** When a folder finished adding up its sizes, its rows were sorted by size at once, even under the pointer, so a click aimed at one row could open another. With the pointer on the rows, the sizes now fill in where the rows are, and the rows are sorted once the pointer leaves the list.

## New in 3.0.3

3.0.3 previews files Glimpse couldn't show before, and shows large pictures, long text files and long books in full.

**Camera RAW photos.** RAW files open as photos: ARW, CR2, CR3, NEF, RAF, DNG and 18 more formats. When the camera saved its own JPEG of the shot at the size the photo would be shown, Glimpse shows that, and otherwise the picture from the sensor. Either way you see one picture that doesn't change while you look at it, and a portrait shot stands upright. Turning a RAW turns the view only: the file is never changed. On our PC a RAW is on screen in 129 to 290 ms when the camera's JPEG is used, and in about 730 ms when the sensor's picture has to be decoded. RAW photos need Microsoft's Raw Image Extension; Windows 11 reads DNG files on its own. Without it the preview says what is missing and offers the extension from the Microsoft Store, and Settings lists it with the other missing pieces.

**Words in pictures.** Glimpse reads the words in a picture that stays on screen for half a second. Drag across words to select them, double-click a word, or press Ctrl+A, then copy with Ctrl+C. Right-click a picture and choose Copy text to take every word at once. The selection follows the picture as you zoom and pan, and slanted text is selected along its slant. The words are read by the engine Windows ships, on your PC, in the languages installed in Windows. On a word, pressing the mouse button now starts a selection where it used to take hold of a zoomed picture, and a double-click selects the word where it used to zoom. Off the words both work as before. Each picture is read once, in the background, which took 14 to 287 ms of one processor core on our PC. Pictures shown by the browser engine (SVG, AVIF and animated pictures) aren't read yet.

**Files on a phone or inside a zip folder.** Space now previews a file that File Explorer shows on a phone, on a camera connected as a device, or inside a zip folder. Like Windows when you open such a file, Glimpse copies it into a folder of its own first, read-only. On our PC a 25 MB photo came out of a zip folder in under 0.2 seconds, and small files in about 10 ms. The copy is not your file: turning a photo there isn't saved, a PDF doesn't offer Sign, and Show in Explorer and Copy path are left out. Glimpse copies files up to 64 MB and gives a copy 20 seconds. A file it can't copy still opens, and the preview says why. Glimpse keeps the newest four copies and removes them all the next time it starts. Glimpse works in File Explorer, so if another program such as WinRAR opens your zip files, right-click one and choose Open with, then Windows Explorer.

**Large pictures at their own size.** Every picture was decoded to at most 4096 pixels on its long side, so a 24-megapixel photo at 100% was stretched 1.47 times however far you zoomed. Zoomed in past the pixels it opened with, a picture is now decoded again at its own size, up to 128 megapixels, which shows a 10,000 by 9,000 picture whole. It opens exactly as before. Photos (JPEG, HEIC, AVIF, camera RAW and Photoshop files) are smoothed at every zoom. PNG, GIF, WebP, TIFF and icons still show their square pixels when zoomed in. After you zoom in, a picture that large holds up to 488 MB of memory for as long as it is on screen.

**Photoshop files.** A PSD zooms to its own pixels too, and large documents (`.psb`) open, up to 300,000 pixels a side. On our PC a 10,000 by 9,000 file opens in about 260 ms, as before, and is sharp about 270 ms after you zoom in. A Photoshop file that claims more than it holds is refused.

**Long text files.** A text file showed its first 400,000 characters, with a line saying so, and never its end: a 20 MB log showed 2% of itself. Now a long file opens on its first lines and reads more as you scroll. Click in the text, and End goes to the last line as the file is at that moment, so a log that is still being written shows its newest lines. Home goes back to the top, and Page Up and Page Down scroll it. On our PC a 20 MB log is on screen in 37 to 126 ms, where 3.0.2 took 127 to 215 ms, and End takes 37 to 73 ms. A line longer than 32 KB shows its first 2 KB and how much more there is, and the lines after it read as usual. Text that fits is shown as before.

**Long books.** An e-book was cut at 500,000 characters, less than half of a long novel. Books now show up to 8,000,000 characters, more than War and Peace or the complete Shakespeare. Pictures in a book keep their limits of 2 MB each and 24 MB a book.

**Shared folders no longer hold the window.** Before showing a file, Glimpse asked its disk about it six or seven times on the window's own thread. It now asks once. A shared folder gets 100 ms to answer: when it is slower, the preview shows the file's name at once and fills in the rest when the share answers. On our PC, a share that had stopped answering froze the window for 14 seconds; now each file waits 100 ms.

## Fixed in 3.0.2

These fixes come from a second review, of 3.0.1 itself. None of them is new in 3.0.1: every one was in 3.0.0 as well.

**Signing work is kept while its PDF is out of reach.** If you had unsaved signing work on a PDF and its network share or removable drive was not connected the next time Glimpse looked at that file, Glimpse deleted the work. It is now kept until the file is back, and Sign reads Resume again.

**Settings fits the screen.** With saved signatures and the notices for missing pieces showing, Settings could be taller than a laptop screen at 150 % scaling. The bottom of the window, with the version and Install now, was cut off and could not be reached. Settings now stays inside the screen and scrolls when it has to.

**The last slide you choose is the one you get.** While a long PowerPoint or Word file converts, you can choose a slide that has not arrived yet. If you then chose one that was already there, Glimpse still carried you to the first when the conversion finished. It now forgets the earlier choice.

**3D models in map coordinates keep their shape.** OBJ, STL and PLY models saved far from zero, as surveying and mapping tools save them, lost their fine detail in 3.0: a small part could collapse flat. 2.0 showed them correctly, and 3.0.2 does again.

**Workbooks left open keep their sheets.** When Glimpse's document cache was full, its own clean-up could delete the files of a workbook that was still open, and the other sheet tabs stopped opening. Files an open preview is using are no longer cleaned up.

**Also fixed in 3.0.2**

- With Windows animations turned off, the scrollbar of a folder or archive list no longer fades

## Fixed in 3.0.1

These fixes come from a line-by-line review of 3.0. Most of what it found is older than 3.0.

**A previewed web page can no longer contact a server.** In 3.0.0 and every earlier version, pressing Space on a saved web page (`.html`) could let that page contact a server its author chose, through an embedded frame or a connection hint that the blocker never saw. The page's author would learn that the file was opened, when, and from what network address. In 3.0.1 every frame is judged by the same rule as the page, the blocker covers every kind of request, and the built-in browser has no route to any server a page names. The one address it can still reach is Microsoft's configuration service for the browser itself, which it has contacted at every start in every version. A saved page that keeps its frames inline will show without them.

**Office and OpenDocument files, and one gap that is still open.** Before LibreOffice converts a document, Glimpse now removes links that point outside the file from OpenDocument files (`.odt`, `.ods`, `.odp`), as it already did for modern Office files (`.docx`, `.xlsx`, `.pptx`). Old-format Office files (`.doc`, `.xls`, `.ppt`, `.rtf`) cannot be cleaned this way yet. An old-format file that links a picture from a shared folder on another computer can still make LibreOffice reach for that folder when you press Space on the file, and Windows may try to sign in there. Until that is closed, don't preview old-format Office files from someone you don't trust. It is still open in 3.0.4.

**Glimpse no longer takes the keys you type.** In every earlier version, a space typed into File Explorer's search box or address bar on Windows 11 never arrived, because Glimpse took it as a request for a preview. Glimpse now takes no key while a text box has the keyboard. During a rename with a preview open, Esc cancels the rename and a second Esc closes the preview. From Explorer's search box or address bar, Esc closes the preview first. Space still opens a preview after a click on Sort, View or a tab. If you move through Explorer's header with the Tab key, Space now presses the button you are on, where earlier versions opened a preview. Glimpse also stays out of dialogs that are not file pickers, such as message boxes and installers.

**File pickers preview the right file.** In an Open or Save dialog, a row shows the name Windows displays for a file, which is not always the file's real name. Windows hides the extension of known file types unless you turn that off, and always hides it for shortcuts. Every earlier version took the displayed name for the real one. With extensions hidden, Space on `report.docx` could preview nothing, or preview a different file that was really named `report`. Shortcuts previewed nothing, and in a Save dialog Glimpse previewed the name typed in the box, not the selected row. Glimpse now asks Windows which file a row stands for. When two files show the same name and the view has no Type column to tell them apart, it previews neither. A Space pressed in a picker is also no longer lost when the picker refreshes at that instant, and a click on another file can no longer open a preview by itself.

**Space keeps working in Explorer.** In 3.0.0 a program in the background could switch Glimpse off in the Explorer window you were using, and Space did nothing there until you clicked away and back. Glimpse now asks Windows which window is in front and no longer takes another program's word for it.

**Keys answer at once.** The Space and arrow keys used to wait whenever Glimpse was busy drawing a preview. In our own log a key waited as long as an eighth of a second. Keys are now read on their own and never wait for the window. A step through a folder also starts up to 20 ms sooner: 3.0.0 always waited 20 ms before loading the next file, and 3.0.1 starts at once when nothing is still loading.

**Fill & Sign.** Scrolling to a page with the mouse wheel and pressing Sign could open page 1 for signing; it now opens the page you are on. A PDF built to show one page and sign another is refused. The signed file is written to disk before it replaces your original, and a PDF in a folder you can't write to now offers a save dialog. Pressing Sign shows at once that it was taken, and a message that says why something wasn't saved is shown whole at any window size. Pages the viewer shows but 3.0 refused to sign, such as a page turned by an odd angle, now sign. Save and Cancel used to show the same four buttons. Save now offers a signed copy or replacing the original. Closing with unsaved work says so and offers to keep editing, discard, or save a signed copy. Pressing X while that question was already showing did nothing you could see; now the question flashes.

**Rotation tells the truth.** A rotation that could not be saved used to leave the picture turned on screen and unturned on disk, with nothing said. Now the picture turns back and says why. Glimpse also no longer loses the file to its own background work on large photos, which was the usual cause. Rotating a PNG or BMP keeps everything else the file held. If the picture is moved or deleted in the moment its rotation is being saved, the rotated copy used to be deleted with it. It is now kept: it takes the picture's name, or Glimpse tells you what the copy is called. Animated PNG and WebP images play.

**Books and documents.** An EPUB that uses common named characters such as a non-breaking space opened as a plain card; it opens as a book now, and one unreadable chapter no longer hides the rest. When the later pages of a long Office file can't be converted, Glimpse says so. When they arrive while you are reading, you keep your place. LibreOffice is never left running after Glimpse closes.

**Network folders.** A PDF on a shared folder (`\\server\share`) opens in the viewer again; 3.0 showed an error card. Web pages, Markdown and text on a shared folder open at full size, and a web page there shows as a page. A PDF on a drive letter that points at a share no longer holds the window still while the share answers, and pictures there show their pixel size again.

**First run.** The welcome window is centered at every display scale. At 125 % and above it used to open off-center or partly off screen.

**Also fixed in 3.0.1**

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
- In a long text file, the scroll bar shows your place in the part Glimpse has read so far, not in the whole file. End and Home always reach the last and first lines
- Old-format Office files can still make LibreOffice reach a shared folder, as described under 3.0.1

**Installing**

Download **Glimpse-Setup-3.0.4.exe** below and run it, or update from inside Glimpse 2.0. Glimpse 3.0, 3.0.1, 3.0.2 and 3.0.3 install this update on their own two days after release, while you are away from your PC, or at once from Settings with Install now. Existing preferences and your startup choice are preserved.

**Requirements**

- Windows 10 1809 or newer
- WebView2 Runtime for web-backed previews; Setup installs it only when it is missing
- LibreOffice or OpenOffice for full Word, Excel and PowerPoint previews; Setup can add LibreOffice when needed
- Raw Image Extension from the Microsoft Store for camera RAW photos; Windows 11 reads DNG files without it

**SHA-256**

`0EC423D063E8655BFE2E92B319CDF6E8D14A6E1EE9A59849EF89CD21BD06D994`

The installer is not code-signed yet, so Windows may show a SmartScreen warning.
