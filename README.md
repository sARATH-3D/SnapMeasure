# SnapMeasure 1.0

Portable screenshot tool with **dimensions, area, angles, scale, polygon highlighting, rulers & guides, rotation, effects and speech bubbles**,
plus an **on-screen ruler and protractor** that float over any app.
One file, `SnapMeasure.exe` (~230 KB). No installer, no admin rights. It runs on any Windows 10/11 PC
(it uses the .NET Framework 4.x that's built into Windows).
**Download:** take `SnapMeasure.exe` (or the portable zip) from the [Releases](../../releases) page, or build it yourself (see below).
Free and open source under the [MIT licence](LICENSE). Made by **sARATH**.

**Highlights:**
* Measuring: dimensions, area / perimeter, 3-point angle, scale calibration, rulers & guides, Excel export.
* Annotating: a style bar above the canvas, arrows (heads at the start / end / both), text, speech bubbles,
  markers, highlights, pixelate, and pasted pictures.
* Helpers: adjustable crop, a blank canvas, a pin you can resize and crop, and an on-screen ruler (calibratable) and protractor.
* **Ctrl+S** saves the picture **and** an editable `.snapm` project. The folder, file name pattern, format and quality are under **Settings**.

## Run it
Double-click `SnapMeasure.exe`. It sits in the system tray (bottom-right).
Settings are saved in `SnapMeasure.ini` **next to the exe**, so you can carry it on a USB stick.

| Capture | Keys |
|---|---|
| Region, or click a window | `PrtSc` or `Ctrl+Shift+1` (or left-click the tray icon) |
| Active window | `Alt+PrtSc` or `Ctrl+Shift+2` |
| Screen under mouse | `Ctrl+PrtSc` or `Ctrl+Shift+3` |
| Last region again | `Shift+PrtSc` or `Ctrl+Shift+4` |

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
| Angle | `G` | **3 points:** end of the first arm → vertex → end of the second arm. Shows the angle (∠1, ∠2 …); also listed under Measurements. |
| Calibrate | `K` | Draw over a known length, type its real size → sets the scale factor |
| Polygon | `P` | Highlight with any number of edges, with fill colour and opacity |
| Highlight | `H` | Text marker, spotlight, grayscale outside, magnify ×2 |
| Rectangle / Ellipse | `R` `E` | Fill and line colour, each with its own opacity. The centre mark snaps to points. |
| Line / Arrow / Freehand | `L` `A` `F` | Arrowheads keep a good shape at any line width |
| Text | `T` | Type straight onto the image. **Bold, italic, left / centre / right** buttons are in the right-hand panel. |
| Speech bubble | `S` | Drag or click, then type. Bold / italic / alignment as for Text. Drag the yellow handle to point the tail. |
| Number marker | `N` | 1, 2, 3 … renumbers itself when one is deleted |
| Letter marker | `Shift+N` | A, B, C … is **its own series**, separate from the numbers |
| Pixelate | `B` | Hide sensitive information |
| Crop | `C` | **Adjustable crop** (see below) |

**Markers:** *Start at* (right panel) continues a series. Changing the colour, size or shadow of one marker
changes **every marker in that series**.

**Style bar** (right above the canvas): line colour, opacity and width; fill colour and opacity; text size; B / I / alignment;
arrowheads (none / end / start / both); highlight mode; marker *Start at*; shadow; and a quick palette.
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

**Scale:** set the unit and *1 pixel = …* in the right panel. The **Measurements** list (dimensions, areas, angles) →
*Copy measurements* pastes straight into Excel.

**Save:** `Ctrl+S` quick-saves a PNG to *Pictures\SnapMeasure*. `Ctrl+Shift+S` saves as PNG, JPG or BMP,
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
* There's no network access, telemetry or installer, and nothing is written outside the exe folder or the chosen save folder.

## Greenshot weak spots and what SnapMeasure does instead
| Greenshot | SnapMeasure |
|---|---|
| No measuring: no dimensions, area, angle or scale | Dimension, area/perimeter, 3-point angle, calibration, units, Excel export |
| No rulers, guides or protractor | Editor rulers with guides, plus a floating on-screen ruler and protractor |
| No polygon highlight | Polygon with any number of edges, editable vertices |
| Crop is final | Adjustable crop that can be removed later |
| Reported scaling bugs with mixed-DPI / multiple monitors | Per-monitor-DPI aware: captures in true physical pixels |
| Annotations flattened once exported | `.snapm` project keeps everything editable |

Not included (yet): scrolling capture, OCR, upload plugins.

## Build from source
Run `build.cmd`. It uses `csc.exe` from `C:\Windows\Microsoft.NET\Framework64\v4.0.30319`, so nothing needs installing.
`SnapMeasure.exe --selftest <folder>` runs the 62 built-in checks.

## Licence
MIT – see [LICENSE](LICENSE). You can use, change and share SnapMeasure freely; keep the copyright notice.
