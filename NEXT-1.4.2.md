# SnapMeasure 1.4.2: requested changes (noted 2026-11-26)

Status: **built locally 2026-11-26, not committed.** All 16 items are in the source; version 1.4.2.0; 204/204 self-checks pass. Not yet checked by eye in the running editor (the screen was locked). Nothing pushed, tagged or uploaded.

Implementation choices made while building:
- Stamp moved to **Shift+Z** (Z = zoom to area). Fill tool = **Shift+F**. Find = **Ctrl+F**. Symbol marker = **Shift+M**.
- Ctrl+Minus only removes room added by Ctrl+Plus / Extend canvas (tracked per side, kept through undo, rotate and crop).
- Item 10 is done for **dimensions**: Text = On the line / Above / Below (stored in Align).
- Origin coordinates: double-click the origin (or right-click a point → Origin coordinates…); saved in .snapm as originX / originY.
- Fill makes a polygon (with holes) under the annotations; it stops at annotation lines and colour edges in the picture, closing gaps of about 4 px.
- Remove background (Adjust menu) clears the plain colour joined to the picture's edges.
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
13. **Remove background**, similar to the one in Paint. Make the picture background transparent, keeping the subject.
16. **Highlighter outline.** Set the highlighter's outline to 0 by default. In the editor, show a thin magenta 0.5 line around it so the user can find it. This line must never appear in a printed, copied or saved picture.

## About box wording (see screenshot)
14. **Rename "Support SnapMeasure"** to **"☕ Buy me a coffee — keep SnapMeasure free"**. *Approved by the user 2026-11-26.*
15. **Replace "28 captures so far – thank you for using SnapMeasure!"** with a plain **"Captures: 28"** in smaller grey text. *Approved by the user 2026-11-26.*

## When work starts
- Use the 1.4.1 release process: bump AssemblyVersion, run the self-tests, build the MSIX, write release notes and update the Store "What's new" text.
- Ask before any push, tag, release or Store upload.
