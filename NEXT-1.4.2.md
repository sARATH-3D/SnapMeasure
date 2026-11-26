# SnapMeasure 1.4.2: requested changes (noted 2026-11-26)

Status: **built locally 2026-11-26.** Version 1.4.2.0. Local commits on `release-1.4.2` (`0e0fa7e`, `677c084`, `523a2c8`, then the third cloud review fixes below); 255/255 self-checks pass. Release files prepared in `Share` (MSIX not installed, WACK not run). Nothing pushed, tagged or uploaded.

Implementation choices made while building:
- Stamp moved to **Shift+Z** (Z = zoom to area). Fill tool = **Shift+F**. Find = **Ctrl+F**. Symbol marker = **Shift+M**.
- Ctrl+Minus only removes room added by Ctrl+Plus / Extend canvas (tracked per side, kept through undo, rotate and crop).
- Item 10 is done for **dimensions**: Text = On the line / Above / Below (stored in Align).
- Origin coordinates: double-click the origin (or right-click a point → Origin coordinates…); saved in .snapm as originX / originY.
- Fill makes a polygon (with holes) under the annotations; it stops at annotation lines and colour edges in the picture, closing gaps of about 4 px.
- Remove background was built, then taken out before release (2026-11-27).
- Symbol picker: Ω button in the text bar, and Insert symbol on a text's right-click menu.
- Shortcut hints were taken out of menu labels (Save as, Extend canvas, pin menu) and moved into tooltips.

Round 2 (user's on-screen feedback, 2026-11-26), built locally, 208/208 self-checks pass:
- Symbol icon: Ω centred by its ink. Symbol picker inserts at the caret (no longer clears typed text); up to 12 characters.
- Symbol marker with more than 2 characters becomes a pill (circle for 1–2).
- Fill: text and small marks in the picture no longer block it; reaches right up to and under lines (no white gap, like Paint); works outside the picture inside a closed outline (open areas are refused); ellipses traced finer.
- Fill colour: palette clicks and the screen colour picker colour the fill for the Fill tool and its fills (they have no line); a selected fill is styled as Fill, not Polygon.
- Text box: Top / Middle / Bottom (saved as valign).
- Find: toolbar button, ⋮ options (Case sensitive, Whole words, Ignore accents, Include labels and values / tool names / picture text), Replace and Replace all (one undo step).

Design pass (full UI check of every window in light and dark, 2026-11-26), built locally, 208/208 self-checks pass:
- Find and replace: dark mode; "Find" / "Replace with" labels; Aa (case) and ab| (whole words) switch buttons, the rest under ⋮; Replace buttons off when nothing can be replaced; "Replaced 1 – no more matches" instead of "Nothing found"; Find button moved next to Undo/Redo.
- About: a long capture key gets its own line (the columns ran together).
- Settings → General: one shared note under Language and Appearance ("Same as Windows" instead of "Automatic"); Output: the example file name ends in "…", and the whole JPEG quality row greys out for PNG; roomier tab names.
- Same names for the same things: "Calibrate from a known length" and "Drawing scales" dialogs; "Copy all text" in the Copy menu; Extend canvas says "Add space around the picture" and "Background".
- Symbol dialog: wider box for 12 characters, "or pick a symbol:". Save-changes question without the old "?" icon.
- Tick boxes and option buttons drawn at the text's size when the text is enlarged (they stayed 13 px).

Cloud review fixes (ultrareview, 2026-11-26), built locally, 214/214 self-checks pass:
- Dimension value position has its own setting (it moved with text alignment).
- Freehand closes onto its own start when released; turned boxes and ellipses bound a fill and take freehand ends.
- Fill on a filled area changes its colour instead of hiding a second fill under it.
- Remove background judges the cropped picture, and changes nothing when there is nothing to clear.
- Origin coordinates: "12.5" is never read as 125 on decimal-comma Windows.
- Find: finishes the open text box before replacing; the result list follows undo and edits.
- Ctrl+Minus: never empties a cropped picture; the added room is saved in .snapm.
- No self-check, try by hand: Find / Replace (with a text box open, and after Ctrl+Z) and freehand closing onto its start.

Second cloud review (2026-11-26), 216/216 self-checks pass: 8 of its 10 findings were already fixed above. Also fixed:
- The Focus mode list no longer turns selected number markers into symbol markers; files saved that way open as numbers.
- Arcs, radius arcs and angle arms bound a fill.
- Korean texts for the new features (and Appearance/Light/Dark, missing since 1.4.0). Written by Claude: have a native speaker check before the Store release.

Speed fix (benchmark, 2026-11-26): a very large outline took 13.7 s and 1.7 GB to fill. Fill now stays within the picture for clicks on it, and refuses areas reaching too far beyond it. Find waits 200 ms for changes to settle before searching again. 218/218 self-checks pass. Still slow: filling on an A1 page at 300 dpi takes about 6 s; a wait cursor shows meanwhile.

Find window made simpler (user's feedback, 2026-11-26): Ctrl+F opens Find, Ctrl+H opens it with Replace unfolded; Match case / Whole words / Picture text ticks on one line; < > with "2 of 3"; rarely used choices under More options; list and Replace shown only when needed.

Tool search and Fill icon (user's feedback, 2026-11-26): a search box in the top bar (Ctrl+K) picks tools and commands; Find is for the drawing's text only ("Include tool names" removed; its button moved to Adjust → Find and replace). Fill icon recoloured to the draw group's red. 225/225 self-checks pass; typing into the box with the real keyboard not yet tried.

Screenshot round (user's feedback, 2026-11-26), 232/232 self-checks pass. These replace the older Fill notes above.
- Fill is bounded by annotations only (planned: options for a THERM .bmp import later). A closed shape nothing touches is filled with its own outline; large areas are worked at a smaller scale.
- A fill with holes or more than 12 corners shows no corner handles; corners snap to the lines they came from.
- Fill icon redrawn as a tipped bucket with a drop. Alignment: six toggle buttons (top / middle / bottom for text boxes, left / centre / right) instead of two lists.
- Find list in sections: Texts, Measurements & counts, Picture text; type names no longer match; symbol markers read like number markers.
- Scale panel: Ceil / Floor on their own "Rounding" row; the unit after "1 pixel =" is no longer cut off. Tick boxes at 200% fixed. Search box centred. Dark mode: pressed toggle buttons are readable.
- Open: which message blocks the header (item 2); Remove background: keep, replace or drop (item 9).

Third screenshot round (2026-11-26/27), 240/240 self-checks pass:
- Fill is now worked out from the lines' geometry (no pixel grid): loose ends are ignored, so no jagged notches; cut-outs stay open and fill on their own; clouds are filled to their scallops.
- An unfilled revision cloud is picked on its outline, so it and its fill can be selected and deleted separately.
- A selected fill is headed "Fill (selected)". Search box: soft pill with a magnifier. Colour picker button works with nothing selected.
- A selected fill's bar offers no Line or Closed. Fill timed with up to 300 revision clouds (under 1.5 s).
- Right-click, tested: Polygon, open polygon, Area, Length, Perimeter, Focus perimeter and Cut-out end at the last clicked point; Angle, Arc and Radius finish on their third click, and a right-click after two cancels them. Asked the user for the steps that show the reported problem.
- Open, suggestions only: picked colour for Extend canvas; slimmer text bar; Find list layout; Fill icon (three candidates); the header "message"; Remove background.

Fourth round (user's choices, 2026-11-27), 243/243 self-checks pass:
- Find has two tabs, Texts (picture words included, marked "Picture:") and Measurements & counts; an empty tab is left for the other one when only that has matches.
- Fill icon: choice B, an all-red outline bucket.
- The "message that blocks the header" was the capture screen's hint: now one short line (translated), gone after 3 s or when the pointer comes near. Arrow keys and Enter are described in the README instead.
- Open, suggestions only: picked colour for Extend canvas; slimmer text bar; Remove background.

Fifth round (user's screenshots, 2026-11-27), 246/246 self-checks pass:
- Replace with a picture-word row chosen changes nothing and says so.
- Ctrl+Plus / Extend canvas use the colour last picked with the pipette ("Picked colour" in the dialog).
- Colour picker icon redrawn as a dropper: round bulb, collar, hollow tube.
- Find window laid out to one left and right edge; the count sits inside the find box.
- Ctrl+Shift+T is a default key for Capture text (it takes "reopen closed tab" from browsers while SnapMeasure runs).

Dark mode: the selected row of the Texts and measurements lists was written in black; it now has light text on blue (checked in off-screen pictures).

Also fixed (247/247): the Find window shrinks back after a wide title; a colour from *More colours* is used by Ctrl+Plus too.

Fixed after the on-screen test (250/250): Find stays on its tab after Replace and Replace all; a window opened light keeps its light search box after Appearance is switched to Dark.

Third cloud review (2026-11-27), 255/255 self-checks pass:
- Remove background taken out (the user's decision).
- The Origin box offers its values without thousands separators or rounding, and accepts "1250, 300".
- Replace changes only text you typed and never leaves a symbol marker empty or longer than 12 characters.
- Only areas the Fill tool made count as fills; a borderless polygon you drew stays a polygon.
- A picture whose text can't be read is not read again on every keystroke.
- Excel copy shows symbol marker counts as whole numbers; freehand strokes are lighter on busy sheets.
- Kept: Ctrl+Shift+T as the default key for Capture text (asked for by the user).
- Opened polygons no longer show a hidden default arrowhead (255/255).

Notes in *italics* are suggestions or findings from the current code, not decisions.

## Shortcuts
1. **Ctrl+I = Invert colours.** *Ctrl+I is free in the editor today. Plain `I` stays the Coordinates tool. Inside a text box Ctrl+I should still mean italic.*
2. **Ctrl+G = Grayscale.** *Ctrl+G is free. Plain `G` stays the Angle tool.*
8. **`Z` = Zoom to area.** *Today `Z` picks the Stamp tool and Shift+Z zooms to an area (Canvas.cs:2458). Stamp needs a new key: suggest `Shift+S`, or swap the two so that Shift+Z = Stamp.*
5. **Hide shortcut hints in menus and labels.** Show a shortcut only in the tooltip when the mouse hovers over a tool or menu item. Example: the menu text "Extend canvas… (Ctrl+Plus / Ctrl+Minus = 10 px)" becomes just "Extend canvas…".
6. **Bug: Ctrl+Minus crops the picture for good.** Shrinking the canvas cuts the image, and Ctrl+Plus doesn't bring the cut part back. *Fix: Ctrl+Minus should only remove space that was added before, and stop at the original picture edge. Or keep the full picture and only change the visible frame, so Ctrl+Plus restores it. Undo must also restore it.* (Editor.cs:2977–2980, StepCanvas)

## Tools and annotation
3. **Symbol picker for text editing.** A button or menu with engineering symbols: Ø ° ± × ÷ ≈ ≠ ≤ ≥ √ ∑ Δ π µ ² ³ ½ ¼ ¾ ‰ ∠ ⊥ ∥ ⌀ ℄ m² m³ ←→↑↓.
4. **New manual marker.** The user types any single character or symbol, and it's shown inside the marker circle. This complements the existing number and letter markers, which count themselves. *Suggested key: `Shift+M`.*
   - **Icon (approved 2026-11-26):** a circle with **Ω** inside, in the same purple family as the Number ① and Letter Ⓐ icons. Number is solid purple and Letter is outlined; Symbol gets a **light purple tint** fill: fill #CECBF6, outline #7F77DD, character #3C3489. Teal was rejected because it didn't blend with the other two.
   - **Default style for new Symbol markers on the picture** is the same tint: light purple fill, purple outline, dark purple character.
   - **Name: "Symbol".** It sits beside "Number" and "Letter", which also name what they place. Avoid "Manual", which describes behaviour rather than content. Use "Symbol" in the toolbar and tool list, and "Symbol marker" in the status bar and Excel export type.
   - **Tooltip:** "Symbol marker (Shift+M) — type any character, like Ø, ±, ★ or a fixed 3. It doesn't count up." Per item 5, the shortcut appears only in the tooltip.
   - **Use it to repeat existing markers.** People will also use the manual marker to repeat a number or letter that an existing marker already has, for example a second "3" or "B" placed by hand.
   - **Match properties must work across all three marker types:**
     - Copy colour, size, fill, border and font between number, letter and manual markers in either direction.
     - Only the style is copied, never the marker's character or count.
     - A manual "3" should be able to look exactly like the counted ③ next to it.
7. **User-defined origin for the Coordinates tool.** Let the user set the coordinates of the first click, which today is always 0,0. For example, type X = 1250, Y = 300 to match the drawing grid.
9. **Find tool.** Search text in annotations (labels, text, notes, stamps), tool names and, if the user turns it on, OCR text in the picture. Results highlight and zoom to the match. *Suggested key: Ctrl+F.*
10. **Text position on shapes.** Today the text can't be placed in the middle of a shape or below its border. Add positions: above, on the border line, middle (centred), and below.
11. **Freehand tool snapping.** The freehand pen doesn't snap to existing annotations. Its start and end points should snap to nearby shape points and lines, as other tools do. This also lets it close shapes for the fill tool (item 12).
12. **Fill tool.** Fill a closed area with colour. The area can be bounded by annotations, shapes or freehand sketches, like a paint bucket. *It needs a gap tolerance so small gaps in hand-drawn lines still fill.*
13. **Remove background** (dropped on 2026-11-27, not in 1.4.2), similar to the one in Paint. Make the picture background transparent, keeping the subject.
16. **Highlighter outline.** Set the highlighter's outline to 0 by default. In the editor, show a thin magenta 0.5 line around it so the user can find it. This line must never appear in a printed, copied or saved picture.

## About box wording (see screenshot)
14. **Rename "Support SnapMeasure"** to **"☕ Buy me a coffee — keep SnapMeasure free"**. *Approved by the user 2026-11-26.*
15. **Replace "28 captures so far – thank you for using SnapMeasure!"** with a plain **"Captures: 28"** in smaller grey text. *Approved by the user 2026-11-26.*

## When work starts
- Use the 1.4.1 release process: bump AssemblyVersion, run the self-tests, build the MSIX, write release notes and update the Store "What's new" text.
- Ask before any push, tag, release or Store upload.
