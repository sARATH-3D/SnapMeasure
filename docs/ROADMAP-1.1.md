# SnapMeasure 1.1 – scope

One release, **1.1**, which also includes the 1.0.1 fixes already on `main`
(per-monitor UI sizing, ruler "Set scale" box placement, ruler right-click menu after a drag).
No separate 1.0.1 release page.

Example use case for the release video: **quantity takeoff of a façade elevation**.

## Phase A – markup
- [x] **Arrow heads:** Start and End set separately from the right-click menu on arrows, lines and dimensions:
  none, open, closed / filled, slash, tick, dot, diamond, circle, square.
- [x] **Text markup:** drag over text in the picture to underline, strike out or highlight it.
  With OCR (Phase C), it snaps to the text line.
- [x] **Non-printing note:** a sticky-note icon that opens to show text. It shows in the editor and is saved in `.snapm`,
  but is left out of saved / copied / pinned pictures.
- [x] **Revision cloud:** dragged as a rectangle (a freehand-outline cloud is a possible later addition).
- [x] **Stamps:** Approved / Revise / For information / Rejected, plus your own text, with name and date.

## Phase B – measuring & quantities
- [x] **Perimeter tool:** closed outline that measures length only.
- [x] **Polyline length:** open path through several points, total length (sealant runs, gaskets, copings).
- [x] **Area minus cut-outs:** draw the gross area, then the openings to subtract; shows gross, deductions and net.
- [x] **Labels:** name any measurement or marker series (e.g. "BP – Alu 3 mm, RAL 7016").
- [x] **Counts:** each marker series totals in the panel (e.g. "Numbers: 14").
- [x] **Excel export with labels and counts:** one row per item: name, type, value, unit, perimeter, label.
- [x] **Scale presets:** 1:10 … 1:500 at a chosen dpi, or on a paper size (A0–A4), plus your own saved scales.

## Phase C – built into Windows (nothing to install)
- [x] **Open PDF pages** (Windows' own PDF renderer). The page's real paper size makes "1:50 on A1" exact.
- [x] **OCR:** copy text out of a screenshot or a selected area (Windows OCR).

## Phase D – capture & app
- [x] **Scrolling capture:** scrolls the window under the mouse and stitches the pieces into one long image.
- [x] **Update check:** a button in About that asks GitHub if a newer release exists.
  It runs only when you click it, so there's still no background network access.

## Done when
- Every item has a self-test where possible; all checks pass.
- Tested by hand on the 100 % and 200 % monitors.
- README, Share\README.txt and the About shortcuts are updated, and the release is published as v1.1 on GitHub.
