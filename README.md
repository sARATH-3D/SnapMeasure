# SnapMeasure 1.2

Portable screenshot and **quantity takeoff** tool: **dimensions, areas with cut-outs, path lengths, perimeters, angles, counts,
drawing scales**, mark-up (clouds, stamps, text markup, notes), **PDF pages**, **OCR** and **scrolling capture**,
plus an **on-screen ruler and protractor** that float over any app.
One file, `SnapMeasure.exe` (~300 KB). No installer, no admin rights. It runs on any Windows 10/11 PC
(it uses the .NET Framework 4.x, the PDF reader and the text recognition that are built into Windows).
**Download:** take `SnapMeasure.exe` (or the portable zip) from the [Releases](../../releases) page, or build it yourself (see below).
Free and open source under the [MIT licence](LICENSE). Made by **sARATH**.

**What's new in 1.2:**
* **Settings window** with three tabs: **Capture** (what PrtSc does – open the editor, only copy, only save, or copy + save;
  mouse pointer; camera sound; notifications; magnifier; delay), **Output** and **About** (version, GitHub, check for updates, shortcuts).
* **Coordinates** (`I`): the first click sets the origin 0,0; every point then shows X / Y with **Y up, like CAD**.
  Drag the origin (or right-click a point → *Make this point the origin*) and every value updates. X and Y go to Excel.
* **Line styles** for all outlines: solid, dashed, dotted, dash-dot, long dash and **cloud** (scalloped rectangles, ellipses, polygons).
* **Revision cloud callouts:** a **revision triangle** with a number, or a **text box**, on a leader. Drag the yellow handle to move it.
* **Stamps:** type straight onto the stamp (no pop-up) and more stamp types (Draft, Final, For comment, For construction,
  Not approved, Checked, As built, Confidential, Void …).
* **Text:** drag a **text box** to set its width (the text wraps), side handles to resize it, an optional **frame** in the line
  colour and width, and **Esc keeps your text**.
* **Length / perimeter** is open by default; tick **Closed** for a perimeter.
* **Freehand** can have arrowheads (any of the 10 line ends).
* **Highlight** now includes **Pixelate**, and every highlight can be a rectangle, ellipse, triangle, diamond or rounded box.
* **Status bar guide:** tells you the next step for the tool you have (step 1, step 2 …) and what you can do with the selection.
* **Closing:** clear buttons – *Save…*, *Close without saving*, *Keep editing*.
* **Tool bar** grouped under headings: Measure, Count, Mark-up, Text, Tools.

**What came in 1.1:**
* **Quantities:** area **minus cut-outs** (gross / cut-outs / net), **path length** (sealant runs, gaskets), **perimeter**,
  **counts** per marker series, and **labels** on every measurement. *Copy measurements* puts all of it into Excel.
* **Drawing scales:** 1:50 on an A1 sheet, 1:100 at 300 dpi, the exact size of a PDF page, or scales you save yourself.
* **Open PDF pages** directly (Windows' own PDF reader) – the paper size makes the scale exact.
* **Mark-up:** 10 line ends for arrows, lines and dimensions (right-click), **revision cloud**, **stamps**
  (Approved / Revise / For information / Rejected / custom, with date), **text markup** (underline, strike out, highlight –
  snaps to the words), and **notes** that are shown in the editor but never printed.
* **OCR:** copy text out of a picture or straight off the screen (`Ctrl+Shift+6`).
* **Scrolling capture** of long pages (`Ctrl+Shift+5`).
* **Check for updates** in the About window (only when you click it).
* Fixes from 1.0.1: right-sized icons, text and menus on every monitor (mixed 100 % / 200 % screens);
  the ruler's *Set scale* box opens beside the ruler, and its right-click menu also works straight after a drag.

**Highlights:**
* Measuring: dimensions, areas with cut-outs, path length, perimeter, 3-point angle, counts, scale calibration and presets,
  rulers & guides, Excel export with labels.
* Annotating: a style bar above the canvas, arrows and lines with 10 end styles, text, speech bubbles, clouds, stamps,
  text markup, notes, markers, highlights, pixelate, and pasted pictures.
* Helpers: adjustable crop, a blank canvas, a pin you can resize and crop, and an on-screen ruler (calibratable) and protractor.
* **Ctrl+S** saves the picture **and** an editable `.snapm` project, named like Greenshot after the app you captured
  (e.g. `2026-11-18 11_34_09-Autodesk AutoCAD LT 2026 - [plan.dwg].png`).
  The folder, file name pattern, format and quality are under **Settings**.
* Export formats: **PNG, JPG, BMP, GIF, TIFF, PDF, ICO**, plus `.snapm` (stays editable).

## Run it
Double-click `SnapMeasure.exe`. It sits in the system tray (bottom-right).
Settings are saved in `SnapMeasure.ini` **next to the exe**, so you can carry it on a USB stick.

| Capture | Keys |
|---|---|
| Region, or click a window | `PrtSc` or `Ctrl+Shift+1` (or left-click the tray icon) |
| Active window | `Alt+PrtSc` or `Ctrl+Shift+2` |
| Screen under mouse | `Ctrl+PrtSc` or `Ctrl+Shift+3` |
| Last region again | `Shift+PrtSc` or `Ctrl+Shift+4` |
| **Scrolling capture** (long page): pick the area, SnapMeasure scrolls it and joins the pieces; `Esc` stops | `Ctrl+Shift+5` |
| **Capture text (OCR)**: pick an area, its text goes to the clipboard | `Ctrl+Shift+6` |

**PDF drawings:** *Open…* (editor or tray) also opens PDFs. Pick the page and the sharpness (100–300 dpi);
SnapMeasure remembers the page's real paper size, so *Drawing scale → PDF page (exact)* gives the right scale straight away.

If Windows Snipping Tool owns `PrtSc`, SnapMeasure says so at start-up. Use the `Ctrl+Shift` keys instead,
or turn off *Settings → Accessibility → Keyboard → "Use the Print screen key to open screen capture"*.

## On-screen ruler & protractor (work over any app: PDF, CAD, browser …)
Open them from the tray menu or the **Ruler** / **Protractor** buttons in the editor's top bar.
* **Calibrate the ruler to real units:** put the red circle on one end of a known dimension (e.g. a 160 mm CAD dimension)
  and drag the **red marker** to the other end. Then right-click → *Calibrate: red marker = real length…* and type the length.
  The ticks and the readout switch to mm, cm, m, in or ft, and the scale is remembered.
  Right-click also has *Set scale (1 px = …)* and *Reset to pixels*.
* **Ruler:** drag it to move it. Drag the **green knob** to rotate it about the red pivot; `Shift` gives 15° steps.
  The mouse wheel turns it 1° at a time (`Ctrl` = 0.1°). Drag the far end to change the length.
  It always shows its angle and its length in px. Right-click for 0° / 90°, *Set angle…* and preset lengths.
* **Protractor:** drag the **blue** and **red** knobs onto the two edges you want to measure.
  It shows the angle between them plus the inside and other-side angles. Drag anywhere else to move it; the mouse wheel changes its size.
* The arrow keys nudge either tool and `Esc` closes it. In the tray menu, *Close rulers & protractors* closes them all.

## Editor tools
| Tool | Key | What it does |
|---|---|---|
| Dimension | `D` | CAD-style dimension: solid arrowheads that grow with the line width, end ticks, and the value **in the middle of the line**. Drag the yellow handle to offset it. Double-click to add a prefix. |
| Area | `M` | Click corners → area + perimeter label (A1, A2 …) |
| Cut-out | `X` | Click the corners of an opening **inside an area** → it is subtracted: the label shows gross, cut-outs and **net** area |
| Path length | `W` | Click points along a run (sealant, gasket, coping) → total length (L1, L2 …) |
| Perimeter | `Q` | Closed outline → perimeter only (P1, P2 …) |
| Angle | `G` | **3 points:** end of the first arm → vertex → end of the second arm. Shows the angle (∠1, ∠2 …); also listed under Measurements. |
| Calibrate | `K` | Draw over a known length, type its real size → sets the scale factor |
| Number marker | `N` | 1, 2, 3 … renumbers itself when one is deleted – **the count is listed** (Σ1 = 14 nr) |
| Letter marker | `Shift+N` | A, B, C … is **its own series**, separate from the numbers, also counted |
| Polygon | `P` | Highlight with any number of edges, with fill colour and opacity |
| Highlight | `H` | Text marker, spotlight, grayscale outside, magnify ×2, **pixelate** (`B`) – as a rectangle, ellipse, triangle, diamond or rounded box |
| Text markup | `Y` | Drag over a line of text in the picture: **underline, strike out or highlight**. Snaps to the words (Windows text recognition). |
| Rectangle / Ellipse | `R` `E` | Fill and line colour, each with its own opacity. The centre mark snaps to points. |
| Revision cloud | `U` | Drag a cloud around a change; the line width sets the size of the scallops. **Callout:** none, revision triangle (number) or text box, on a leader |
| Line / Arrow / Freehand | `L` `A` `F` | **10 line ends** (none, filled / open / hollow arrow, slash, tick, dot, circle, diamond, square) for the start and the end |
| Text | `T` | Click and type, or **drag a box** (the text wraps; side handles change the width). Bold, italic, alignment and an optional **frame** on the style bar. `Esc` keeps the text. |
| Speech bubble | `S` | Drag or click, then type. Bold / italic / alignment as for Text. Drag the yellow handle to point the tail. |
| Stamp | `Z` | Click to place APPROVED, REVISE, FOR INFORMATION, REJECTED, DRAFT, FINAL, FOR COMMENT, FOR CONSTRUCTION, NOT APPROVED, CHECKED, AS BUILT, CONFIDENTIAL, VOID or your own words, with the date. **Double-click and type straight onto it.** |
| Note | `O` | A sticky note shown in the editor only – **left out of saved, copied and pinned pictures** (listed in the Excel export) |
| Copy text (OCR) | `Shift+T` | Drag over text to copy it; a click copies all the text. The **Copy text** button copies the whole picture. |
| Pixelate | `B` | Hide sensitive information |
| Crop | `C` | **Adjustable crop** (see below) |

**Line ends:** right-click a line, arrow or dimension → *Start head* / *End head* (with pictures), or *Swap start / end heads*.
The same choices are on the style bar.

**Labels and counts:** double-click a measurement to label it (e.g. *Spandrel L01*), or a marker to say what its series counts
(e.g. *Blind panel*). Double-clicking a row in the list on the right does the same.
*Copy measurements (for Excel)* gives one row per item: Name, Type, Value, Unit, Perimeter, Gross, Cut-outs, Label –
including the marker counts and the notes.

**Markers:** *Start at* (style bar) continues a series. Changing the colour, size or shadow of one marker
changes **every marker in that series**.

**Style bar** (right above the canvas): line colour, opacity and width; fill colour and opacity; text size; B / I / alignment;
start / end line ends; highlight, text-markup and stamp type; marker *Start at*; shadow; and a quick palette.
In the palette, click a colour for the line and right-click it for the fill.
The **Scale panel** button (or `F4`) shows or hides the scale & measurements panel on the right.

**Colour keys:**
* `Alt+L` = pick the line colour, `Alt+F` = pick the fill colour.
* `Alt+1`…`Alt+9` = line colour from the palette (red, orange, yellow, green, blue, purple, black, white, grey).
* `Alt+Shift+1`…`9` = the same colours for the fill. `Alt+Shift+0` = no fill.

**Pictures:** `Ctrl+V` pastes a copied picture (or a copied image file) onto the canvas. You can also drag image files onto the editor.
Drag it to move it, drag a corner to scale it (proportions are kept; `Shift` = free), or use the green knob to rotate it.
*Fill opacity* sets how see-through the picture is.

**Blank canvas:** the **Blank** button or the tray menu opens a new canvas: screen size, Full HD, A4 or custom,
on white, grid paper, transparent or black.

## Selecting, copy / paste and draw order
* Click to select. **Shift+click** or **drag a box** selects several shapes; `Ctrl+A` selects all.
* **Right-click** gives Cut, Copy, Paste here, Duplicate, Delete, Bring to front, Bring forward, Send backward, Send to back,
  Select all, Remove crop and Clear guides.
* `Ctrl+C` / `Ctrl+X` / `Ctrl+V` copy, cut and paste **shapes**, even into another editor window. `Ctrl+D` duplicates.
  With nothing selected, `Ctrl+C` copies the whole image.
* Draw order: `Ctrl+]` brings a shape forward, `Ctrl+[` sends it backward. Add `Shift` for front / back.
* Drag the **green knob** to rotate, the 8 white handles to resize, or the **centre mark** to move by the centre.

## Crop (adjustable)
Press `C` and drag the area to keep. Drag the handles or the frame to adjust it, and press `Enter` when done.
Anything outside the crop is hidden and is not saved in the image. Pick Crop again at any time to change it.
To undo it, press `Ctrl+Z`; to remove it, press `Delete` in Crop mode or right-click → *Remove crop*.
Border, drop shadow and torn edge apply to the cropped picture.

## Rulers & guides
Rulers run along the top and left. They read in your scale units (px, mm, m …), and a red mark follows the mouse.
**Drag from a ruler** to place a guide; points snap to guides.
Drag a guide to move it, or drag it back onto the ruler to delete it. `Ctrl+R` or the **Rulers** button shows or hides the rulers.

## Image menus and view
* **Rotate:** right / left 90°, mirror horizontal / vertical. Annotations, the crop and measurements follow.
* **Effects:** border, drop shadow, torn edge, grayscale, invert (`Ctrl+Z` to undo).
* `Home` (or `0`, or double-click the middle mouse button) fits and centres the image. `Ctrl+wheel` zooms; `Space`+drag pans.

**Scale:** set the unit and *1 pixel = …* in the right panel, calibrate from a known length (`K`), or use
**Drawing scale…**: 1:N with the picture's resolution (e.g. 1:100 at 300 dpi), the sheet it shows (e.g. 1:50 on A1 landscape),
or the exact size of a PDF page. Scales you use often can be saved by name and picked again.
The **Measurements & counts** list → *Copy measurements* pastes straight into Excel.

**Save:** `Ctrl+S` saves to *Pictures\SnapMeasure* (or the folder you choose in **Settings**). `Ctrl+Shift+S` saves as PNG, JPG, BMP, GIF, TIFF, PDF or ICO,
or as **.snapm**, which stays fully editable and keeps the crop and guides.

**Pin** (`Ctrl+P`) keeps a copy of the image on top of other windows. You can:
* drag its edges or corners to resize it (it keeps its proportions);
* press `C` (or right-click → *Crop*) and drag the part to keep; right-click → *Undo crop* brings the rest back;
* set the size from 25 % to 200 %, change the opacity, and copy it.

## Security
* Project files and clipboard data are treated as untrusted:
  * no DTDs or external entities are read (XXE-safe);
  * damaged or unknown shapes are skipped;
  * numbers are range-checked;
  * images over 30 000 px or 250 MP are refused.
* DLLs are loaded only from System32, never from the exe's folder (DLL planting).
  The build uses ASLR (high-entropy), DEP and an asInvoker manifest, so no admin rights are needed.
* No telemetry and no installer. The only time SnapMeasure goes online is when you click *Check for updates* in the About window
  (it asks GitHub for the latest release number). Nothing is written outside the exe folder or the chosen save folder.
* PDF pages and text recognition use the components built into Windows 10/11; nothing is downloaded.

## Greenshot weak spots and what SnapMeasure does instead
| Greenshot | SnapMeasure |
|---|---|
| No measuring: no dimensions, area, angle or scale | Dimension, area/perimeter, 3-point angle, calibration, units, Excel export |
| No rulers, guides or protractor | Editor rulers with guides, plus a floating on-screen ruler and protractor |
| No polygon highlight | Polygon with any number of edges, editable vertices |
| Crop is final | Adjustable crop that can be removed later |
| Reported scaling bugs with mixed-DPI / multiple monitors | Per-monitor-DPI aware: captures in true physical pixels |
| Annotations flattened once exported | `.snapm` project keeps everything editable |
| Scrolling capture unreliable | Scrolls the window, lines the pieces up even when the app scrolls by fractions of a pixel, and keeps sticky headers once |
| No OCR built in | Copy text from pictures or the screen with Windows' own text recognition |

Not included (yet): upload plugins.

## Build from source
Run `build.cmd`. It uses `csc.exe` from `C:\Windows\Microsoft.NET\Framework64\v4.0.30319` and the Windows metadata in
`C:\Windows\System32\WinMetadata`, so nothing needs installing.
`SnapMeasure.exe --selftest <folder>` runs the 108 built-in checks.

## Licence
MIT – see [LICENSE](LICENSE). You can use, change and share SnapMeasure freely; keep the copyright notice.
