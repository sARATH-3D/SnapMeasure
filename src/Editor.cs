using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace SnapMeasure
{
    /// <summary>ToolStrip that acts on the first click even when its window is not active
    /// (a plain ToolStrip swallows that click just to activate the window).</summary>
    public class ClickStrip : ToolStrip
    {
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // WM_MOUSEACTIVATE: turn MA_ACTIVATEANDEAT (2) into MA_ACTIVATE (1)
            if (m.Msg == 0x21 && m.Result == (IntPtr)2) m.Result = (IntPtr)1;
        }
    }

    public class EditorForm : Form
    {
        readonly Canvas cv;
        readonly float k;
        ToolStrip bar, toolBar;
        ToolStripButton bUndo, bRedo;
        ToolStripLabel lZoomBar;
        StatusStrip status;
        ToolStripStatusLabel lPos, lZoom, lScale, lHint;
        readonly Dictionary<Tool, ToolStripButton> toolButtons = new Dictionary<Tool, ToolStripButton>();
        Label lStyleFor, lSO, lFO;
        Button bStroke, bFill;
        NumericUpDown nWidth, nFont, nDec;
        TrackBar tSO, tFO;
        CheckBox cShadow, cKeepScale;
        ComboBox cWhere;
        ComboBox cUnit, cHlMode, cHeadS, cHeadE, cStamp, cMarkup, cDash, cForm, cCallout, cShow;
        CheckBox cClosed, cFrame;
        TextBox tUpp, tMarkStart;
        TrackBar tStr;
        Label lStr;
        Panel rowStrength;
        Panel rowHl, rowMarkStart, rowTextStyle, gLine, gFill, gFont, gHeads, gShadow, props, rowStamp, rowMarkup, gDash, rowForm, rowCallout, rowClosed, rowFrame, rowShow;
        public double? PaperMmPerPx;     // set when the picture is a PDF page: exact paper size per pixel
        FlowLayoutPanel styleBar;
        CheckBox cBold, cItalic, cUnder, cStrike, cAlignL, cAlignC, cAlignR;
        ToolStripMenuItem bRulers;
        ToolStripButton bPanel;
        ToolStripStatusLabel lVersion;
        readonly ToolTip tip = new ToolTip();
        ListView lv, lvText;
        bool textListEditing;
        bool loading, dirty;
        string lastSaved;
        DateTime lastStyleUndo = DateTime.MinValue;

        // status bar: one short, plain sentence per tool (what to do first)
        static readonly Dictionary<Tool, string> Hints = new Dictionary<Tool, string>
        {
            { Tool.Select, "Click a shape to select it. Drag a box to select several." },
            { Tool.Rect, "Rectangle: drag from corner to corner. Shift = square." },
            { Tool.Ellipse, "Ellipse: drag from corner to corner. Shift = circle." },
            { Tool.Line, "Line: drag from start to end. Shift = 15° steps." },
            { Tool.Arrow, "Arrow: drag from start to end. Shift = 15° steps." },
            { Tool.Freehand, "Freehand: press and draw." },
            { Tool.Marker, "Highlighter: drag over the text. Shift = straight." },
            { Tool.Text, "Text: click and type, or drag a box." },
            { Tool.Polygon, "Polygon: click the corners, double-click to finish." },
            { Tool.PolyDraw, "Polyline: click the points, double-click to finish." },
            { Tool.Dimension, "Dimension: drag from one point to the other." },
            { Tool.Area, "Area: click the corners, double-click to finish." },
            { Tool.Calibrate, "Calibrate: draw over a known length, then type its size." },
            { Tool.Counter, "Numbers: click each item to count it." },
            { Tool.Letter, "Letters: click each item to count it." },
            { Tool.Angle, "Angle: click arm, corner, arm." },
            { Tool.Arc, "Arc: click start, a point on the arc, end." },
            { Tool.Radius, "Radius: click 3 points on the curve." },
            { Tool.Pixelate, "Pixelate: drag over what to hide." },
            { Tool.Crop, "Crop: drag the part to keep, then press Enter." },
            { Tool.Highlight, "Focus: drag over the area to bring out." },
            { Tool.Bubble, "Speech bubble: drag or click, then type." },
            { Tool.Polyline, "Length: click along the run, double-click to finish." },
            { Tool.Perimeter, "Length: click along the run, double-click to finish." },
            { Tool.Coord, "Coordinates: click the origin first, then the points." },
            { Tool.Cutout, "Cut-out: click the corners of the opening inside an area." },
            { Tool.Cloud, "Cloud: drag around the change." },
            { Tool.Stamp, "Stamp: click where it goes." },
            { Tool.Note, "Note: click where it goes (never printed)." },
            { Tool.Markup, "Text markup: drag over a line of text." },
            { Tool.Ocr, "Copy text: drag over the text (a click = all)." },
        };

        public EditorForm(Bitmap img, string title)
        {
            // size the UI for the monitor it opens on (e.g. 200 % laptop screen next to a 100 % desktop monitor)
            k = Native.ScaleAt(Cursor.Position);
            Text = "SnapMeasure – " + title;
            captureTitle = title;
            Icon = AppIcon.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = Native.UiFont("Segoe UI", 9f, k);
            KeyPreview = true;
            StartPosition = FormStartPosition.Manual;
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min(S(1300), (int)(wa.Width * 0.92)), Math.Min(S(860), (int)(wa.Height * 0.92)));
            if (Settings.GetBool("MatchCaptureSize", false) && img != null)
            {
                // Settings → Capture → "Match capture size": the window fits the picture at 100 % (within the screen)
                int chromeW = S(52) + (Settings.GetBool("ShowPanel", true) ? S(262) : 0) + S(20) + S(60);
                int chromeH = S(32) + S(36) + S(26) + S(20) + S(40) + S(60);
                Size = new Size(Math.Max(S(780), Math.Min(img.Width + chromeW, (int)(wa.Width * 0.95))),
                                Math.Max(S(520), Math.Min(img.Height + chromeH, (int)(wa.Height * 0.95))));
            }
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);
            // cascade: step down-right from any editor already open there, so windows don't stack exactly
            List<Point> others = Native.EditorWindowCorners(IntPtr.Zero);   // this form has no window yet
            for (int i = 0; i < 20; i++)
            {
                bool taken = false;
                foreach (Point o in others)
                    if (Math.Abs(o.X - Left) < S(8) && Math.Abs(o.Y - Top) < S(8)) { taken = true; break; }
                if (!taken) break;
                int nx = Left + S(32), ny = Top + S(32);
                if (nx + Width > wa.Right || ny + Height > wa.Bottom) { nx = wa.X + S(16) + i * S(8); ny = wa.Y + S(16); }
                Location = new Point(nx, ny);
            }
            MinimumSize = new Size(S(780), S(520));
            AllowDrop = true;

            cv = new Canvas();
            cv.Dock = DockStyle.Fill;
            if (img.PixelFormat == PixelFormat.Format32bppArgb) cv.Image = img;
            else
            {
                cv.Image = Effects.To32(img);
                img.Dispose();                // the editor owns the picture it is given: free the 24-bit original
                img = cv.Image;
            }
            cv.Dpi = k;
            if (Settings.GetBool("KeepScale", false))
            {
                cv.Scale.Unit = Settings.Get("Unit", "px");
                cv.Scale.Upp = Settings.GetDouble("Upp", 1.0);
            }
            cv.Scale.Decimals = Settings.GetInt("Decimals", 2);
            cv.ShowRulers = Settings.GetBool("ShowRulers", true);
            cv.AskText = (t, init) => Dialogs.Ask(this, t, init, false);
            cv.AskNote = (t, init) => Dialogs.Ask(this, t, init, true);
            cv.AskCalibration = px => Dialogs.Calibrate(this, px, cv.Scale);
            cv.Message = Flash;
            cv.OcrArea = r => CopyText(r);
            cv.FindWords = r =>
            {
                if (!TextRecognition.Available) return null;
                return TextRecognition.Read(cv.Image, r).Boxes;
            };

            BuildTopBar();
            BuildToolBar();
            BuildStyleBar();
            BuildProps();
            BuildStatus();

            Controls.Add(cv);
            Controls.Add(props);
            Controls.Add(toolBar);
            Controls.Add(styleBar);
            Controls.Add(bar);
            Controls.Add(status);

            cv.DocChanged += (s, e) => { dirty = true; RefreshMeasurements(); UpdateUndo(); LoadMarker(); UpdateGuide(); };
            cv.SelectionChanged += (s, e) => { LoadStyle(); SyncListSelection(); UpdateGuide(); };
            cv.StateChanged += (s, e) => UpdateGuide();
            cv.ToolChanged += (s, e) => { SyncToolButtons(); LoadStyle(); };
            cv.MatchChanged += (s, e) => SyncToolButtons();
            cv.ViewChanged += (s, e) => { UpdateZoom(); if (bRulers != null) bRulers.Checked = cv.ShowRulers; };
            cv.CursorMoved = UpdatePos;

            Shown += (s, e) => { cv.Fit(); cv.Focus(); };
            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) =>
            {
                // pictures dropped on the editor are added onto the canvas; projects open in their own window
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null) return;
                PointF at = cv.ToImg(cv.PointToClient(new Point(e.X, e.Y)));
                foreach (string f in files)
                {
                    if (Path.GetExtension(f).ToLowerInvariant() == ".snapm") { App.OpenFile(f); continue; }
                    Bitmap b = Canvas.LoadImageFile(f);
                    if (b != null) cv.InsertImage(b, at);
                    else Flash("Can't open " + Path.GetFileName(f) + " as a picture");
                }
            };

            SyncToolButtons();
            LoadStyle();
            LoadScale();
            LoadMarker();
            RefreshMeasurements();
            UpdateUndo();
        }

        int S(float v) { return (int)Math.Round(v * k); }

        internal Canvas CanvasControl { get { return cv; } }
        internal void RefreshAll() { LoadStyle(); LoadScale(); LoadMarker(); RefreshMeasurements(); SyncToolButtons(); UpdateZoom(); }

        public static EditorForm FromProject(string path)
        {
            List<Shape> shapes = new List<Shape>();
            ScaleInfo sc = new ScaleInfo();
            ProjectExtras ex;
            Bitmap img = ProjectFile.Load(path, shapes, sc, out ex);
            EditorForm f = new EditorForm(img, Path.GetFileName(path));
            f.cv.Shapes = shapes;
            f.cv.Scale = sc;
            if (ex.Crop.HasValue)
            {
                Rectangle r = ex.Crop.Value;
                r.Intersect(new Rectangle(0, 0, img.Width, img.Height));
                if (r.Width > 1 && r.Height > 1) f.cv.CropRect = r;
            }
            f.cv.HGuides.AddRange(ex.HGuides);
            f.cv.VGuides.AddRange(ex.VGuides);
            f.lastSaved = path;
            f.RefreshAll();
            return f;
        }

        // ---------------- UI construction ----------------
        ToolStripButton AddBtn(ToolStrip ts, string text, string tip, EventHandler h)
        {
            ToolStripButton b = new ToolStripButton(text);
            b.ToolTipText = tip;
            b.Click += h;
            ts.Items.Add(b);
            return b;
        }

        void BuildTopBar()
        {
            bar = new ClickStrip();
            bar.Font = Font;
            bar.GripStyle = ToolStripGripStyle.Hidden;
            bar.Padding = new Padding(S(4), S(2), S(4), S(2));
            bar.RenderMode = ToolStripRenderMode.System;
            bar.ImageScalingSize = new Size(CmdIcon, CmdIcon);
            // a tidier top row: the main command on each button, related commands in its drop-down (▾)
            ToolStripSplitButton bNew = Split("New capture", "Capture a new region (Ctrl+N) · ▾ = blank canvas", "new", (s, e) => NewCapture());
            MenuItem(bNew, "Blank canvas…", "blank", (s, e) => App.NewBlankCanvas(this));
            Ic(AddBtn(bar, "Open…", "Open an image, PDF page or .snapm project (Ctrl+O)", (s, e) => OpenFile()), "open");
            bar.Items.Add(new ToolStripSeparator());
            ToolStripSplitButton bSave = Split("Save", "Save the picture (and an editable .snapm) to your save folder (Ctrl+S) · ▾ = Save as", "save", (s, e) => QuickSave());
            MenuItem(bSave, "Save as…   (Ctrl+Shift+S)", "saveas", (s, e) => SaveAs());
            ToolStripSplitButton bCopy = Split("Copy", "Copy the marked-up picture (Ctrl+C with nothing selected) · ▾ = copy text or measurements", "copy", (s, e) => CopyImage());
            MenuItem(bCopy, "Copy picture", "copy", (s, e) => CopyImage());
            MenuItem(bCopy, "Copy text from the picture (OCR)", "ocr", (s, e) => CopyText(null));
            MenuItem(bCopy, "Copy all typed text", null, (s, e) => CopyAllText());
            MenuItem(bCopy, "Copy measurements (for Excel)", null, (s, e) => CopyMeasurements());
            bar.Items.Add(new ToolStripSeparator());
            bUndo = IconOnly(AddBtn(bar, "Undo", "Undo (Ctrl+Z)", (s, e) => cv.Undo()), "undo");
            bRedo = IconOnly(AddBtn(bar, "Redo", "Redo (Ctrl+Y)", (s, e) => cv.Redo()), "redo");
            bar.Items.Add(new ToolStripSeparator());

            ToolStripDropDownButton img = new ToolStripDropDownButton("Adjust");     // rotate, mirror, border, shadow, torn edge, grayscale, invert
            img.Image = Icons.Cmd("rotate", CmdIcon);
            img.ImageScaling = ToolStripItemImageScaling.None;
            img.AutoToolTip = false;       // a tooltip would cover the first menu items
            img.DropDown.Font = Font;
            MenuItem(img, "Rotate right 90°", null, (s, e) => cv.ImageOp("rotcw"));
            MenuItem(img, "Rotate left 90°", null, (s, e) => cv.ImageOp("rotccw"));
            MenuItem(img, "Mirror horizontally", null, (s, e) => cv.ImageOp("fliph"));
            MenuItem(img, "Mirror vertically", null, (s, e) => cv.ImageOp("flipv"));
            img.DropDownItems.Add(new ToolStripSeparator());
            MenuItem(img, "Border", "effects", (s, e) => cv.ImageOp("border"));
            MenuItem(img, "Drop shadow", null, (s, e) => cv.ImageOp("shadow"));
            MenuItem(img, "Torn edge", null, (s, e) => cv.ImageOp("torn"));
            img.DropDownItems.Add(new ToolStripSeparator());
            MenuItem(img, "Grayscale", null, (s, e) => cv.ImageOp("gray"));
            MenuItem(img, "Invert colours", null, (s, e) => cv.ImageOp("invert"));
            bar.Items.Add(img);
            bar.Items.Add(new ToolStripSeparator());

            IconOnly(AddBtn(bar, "Zoom out", "Zoom out (Ctrl+−, or Ctrl+mouse wheel)", (s, e) => cv.ZoomStep(-1)), "zoomout");
            lZoomBar = new ToolStripLabel("100%");
            bar.Items.Add(lZoomBar);
            IconOnly(AddBtn(bar, "Zoom in", "Zoom in (Ctrl++, or Ctrl+mouse wheel)", (s, e) => cv.ZoomStep(1)), "zoomin");
            Ic(AddBtn(bar, "Fit", "Fit and centre the image (Home, 0, or double-click the middle mouse button)", (s, e) => cv.Fit()), "fit");
            IconOnly(AddBtn(bar, "Zoom to area", "Zoom to area: drag a box around the part to see (Shift+Z; Esc cancels)", (s, e) => { cv.StartZoomBox(); cv.Focus(); }), "zoomarea");
            bar.Items.Add(new ToolStripSeparator());

            ToolStripDropDownButton view = new ToolStripDropDownButton("View");
            view.Image = Icons.Cmd("panel", CmdIcon);
            view.ImageScaling = ToolStripItemImageScaling.None;
            view.AutoToolTip = false;
            view.DropDown.Font = Font;
            MenuItem(view, "Actual pixels 1:1   (Ctrl+1)", "actual", (s, e) => cv.ZoomCenter(1f));
            view.DropDownItems.Add(new ToolStripSeparator());
            bRulers = MenuItem(view, "Rulers   (Ctrl+R)", "rulers", (s, e) => ToggleRulers());
            bRulers.Checked = cv.ShowRulers;
            view.DropDownItems.Add(new ToolStripSeparator());
            MenuItem(view, "On-screen ruler", "ruler", (s, e) => Overlays.ShowRuler());
            MenuItem(view, "On-screen protractor", "protractor", (s, e) => Overlays.ShowProtractor());
            bar.Items.Add(view);

            // right end, next to the window's own buttons: side panel, pin, settings, help (the first added sits furthest right)
            ToolStripButton bHelp = IconOnly(AddBtn(bar, "About", "About SnapMeasure, shortcuts and the GitHub page", (s, e) => App.ShowAbout(this)), "help");
            bHelp.Alignment = ToolStripItemAlignment.Right;
            ToolStripButton bSet = Ic(AddBtn(bar, "Settings", "Settings: capture keys, what a capture does, save folder, file name, format – and About", (s, e) => Dialogs.ShowSettings(this, 0)), "settings");
            bSet.Alignment = ToolStripItemAlignment.Right;
            ToolStripButton bPin = Ic(AddBtn(bar, "Pin", "Pin the picture on screen, always on top (Ctrl+P) · in the pin, Ctrl+P or double-click brings the editor back", (s, e) => Pin()), "pin");
            bPin.Alignment = ToolStripItemAlignment.Right;
            bPanel = Ic(AddBtn(bar, "Panel", "Show or hide the side panel: scale, measurements & counts, texts (F4)", (s, e) => TogglePanel()), "panel");
            bPanel.Alignment = ToolStripItemAlignment.Right;
            bPanel.Checked = Settings.GetBool("ShowPanel", true);
            ToolStripSeparator sepR = new ToolStripSeparator(); sepR.Alignment = ToolStripItemAlignment.Right;
            bar.Items.Add(sepR);
        }

        /// <summary>Top-bar icon size in pixels (Settings → General → icon size, scaled for the screen).</summary>
        int CmdIcon { get { return S(Math.Max(12, IconSize - 4)); } }

        /// <summary>Tool-column icon size chosen in Settings (16, 20 or 24 at 100 % scaling).</summary>
        static int IconSize
        {
            get { int v = Settings.GetInt("IconSize", 20); return v <= 16 ? 16 : (v >= 24 ? 24 : 20); }
        }

        ToolStripSplitButton Split(string text, string tip, string icon, EventHandler h)
        {
            ToolStripSplitButton b = new ToolStripSplitButton(text);
            b.ToolTipText = tip;
            b.Image = Icons.Cmd(icon, CmdIcon);
            b.ImageScaling = ToolStripItemImageScaling.None;
            b.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            b.ButtonClick += h;
            b.DropDown.Font = Font;
            bar.Items.Add(b);
            return b;
        }

        ToolStripMenuItem MenuItem(ToolStripDropDownItem parent, string text, string icon, EventHandler h)
        {
            ToolStripMenuItem m = new ToolStripMenuItem(text);
            if (icon != null) m.Image = Icons.Cmd(icon, S(16));
            m.Click += h;
            parent.DropDownItems.Add(m);
            return m;
        }

        void TogglePanel()
        {
            props.Visible = !props.Visible;
            bPanel.Checked = props.Visible;
            Settings.Set("ShowPanel", props.Visible ? "1" : "0");
        }

        ToolStripButton Ic(ToolStripButton b, string name)
        {
            b.Image = Icons.Cmd(name, CmdIcon);
            b.ImageScaling = ToolStripItemImageScaling.None;
            b.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            b.TextImageRelation = TextImageRelation.ImageBeforeText;
            return b;
        }

        ToolStripButton IconOnly(ToolStripButton b, string name)
        {
            Ic(b, name);
            b.DisplayStyle = ToolStripItemDisplayStyle.Image;
            return b;
        }

        void ToggleRulers()
        {
            cv.ShowRulers = !cv.ShowRulers;
            bRulers.Checked = cv.ShowRulers;
            Settings.Set("ShowRulers", cv.ShowRulers ? "1" : "0");
            cv.Fit();
        }

        void BuildToolBar()
        {
            toolBar = new ClickStrip();
            toolBar.Font = Font;
            toolBar.Dock = DockStyle.Left;
            toolBar.AutoSize = false;
            toolBar.Width = S(IconSize + 32);
            toolBar.GripStyle = ToolStripGripStyle.Hidden;
            toolBar.LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow;
            toolBar.ImageScalingSize = new Size(S(IconSize), S(IconSize));
            toolBar.Padding = new Padding(S(2), S(3), S(2), S(3));
            toolBar.RenderMode = ToolStripRenderMode.System;
            AddTool(Tool.Select, "Select / move / rotate (V)");
            // order of work: set the scale, measure (straight → paths → areas → angles), count,
            // then draw (lines → shapes), highlight, write, and the picture tools last
            AddHeading("MEASURE");
            AddTool(Tool.Calibrate, "Calibrate scale from a known length (K)");
            AddTool(Tool.Dimension, "Dimension (D)");
            AddTool(Tool.Perimeter, "Length: path along a run; tick Closed for a closed perimeter (W)");
            AddTool(Tool.Area, "Area / perimeter: closed outline, show area, perimeter or both (M; Q = perimeter only)");
            AddTool(Tool.Cutout, "Cut-out: subtract an opening from an area (X)");
            AddTool(Tool.Angle, "Angle – 3 points: arm, vertex, arm (G)");
            AddTool(Tool.Radius, "Radius / diameter – 3 points on a curve or circle (Shift+R)");
            AddTool(Tool.Coord, "Coordinates: first click = origin 0,0, then points show X / Y (I)");
            AddHeading("COUNT");
            AddTool(Tool.Counter, "Number marker 1, 2, 3 … – counts (N)");
            AddTool(Tool.Letter, "Letter marker A, B, C … – counts (Shift+N)");
            AddHeading("DRAW");
            AddTool(Tool.Arrow, "Arrow (A) – right-click it for more line ends");
            AddTool(Tool.Line, "Line (L)");
            AddTool(Tool.PolyDraw, "Polyline – an open line through any number of points, arrowheads optional (Shift+P)");
            AddTool(Tool.Arc, "Arc – 3 points: start, a point on the arc, end (Shift+L)");
            AddTool(Tool.Freehand, "Freehand, with arrowheads if you like (F)");
            AddTool(Tool.Rect, "Rectangle (R)");
            AddTool(Tool.Ellipse, "Ellipse / circle (E)");
            AddTool(Tool.Polygon, "Polygon – any number of edges (P)");
            AddTool(Tool.Cloud, "Revision cloud, with or without a callout (U)");
            AddHeading("HIGHLIGHT");
            AddTool(Tool.Highlight, "Focus: spotlight, grayscale, magnify or pixelate, in several shapes; Strength slider for magnify / pixelate (H, B = pixelate)");
            AddTool(Tool.Marker, "Highlighter pen: drag over text like a marker; Shift = straight stroke (Shift+H)");
            AddHeading("TEXT");
            AddTool(Tool.Text, "Text – click and type, or drag a box (T)");
            AddTool(Tool.Bubble, "Speech bubble (S)");
            AddTool(Tool.Stamp, "Stamp: Approved / Revise / Draft / For construction … (Z)");
            AddTool(Tool.Note, "Note – shown here only, not printed (O)");
            AddHeading("TOOLS");
            AddTool(Tool.Crop, "Crop (C)");
            AddTool(Tool.Ocr, "Copy text from an area – text recognition (Shift+T)");
            // match properties: not a drawing tool, a brush that stays on until Esc / right-click
            bMatch = new ToolStripButton();
            bMatch.Image = Icons.Cmd("match", S(IconSize));
            bMatch.ImageScaling = ToolStripItemImageScaling.None;
            bMatch.DisplayStyle = ToolStripItemDisplayStyle.Image;
            bMatch.AutoSize = false;
            bMatch.Size = new Size(S(IconSize + 24), S(IconSize + 3));
            bMatch.ToolTipText = "Match properties: select a shape, then click the shapes to give its look (Ctrl+Shift+M; Esc or right-click ends)";
            bMatch.Margin = new Padding(0, S(1), 0, S(1));
            bMatch.Click += (s, e) => { ToggleMatch(); cv.Focus(); };
            toolBar.Items.Add(bMatch);
        }

        ToolStripButton bMatch;
        void ToggleMatch()
        {
            if (cv.MatchActive) { cv.EndMatch(); return; }
            if (cv.Selected == null || cv.Sel.Count != 1) { Flash("Match properties: first select the shape whose look you want to copy."); return; }
            cv.StartMatch();
        }

        /// <summary>Small section title in the tool column.</summary>
        void AddHeading(string text)
        {
            ToolStripLabel l = new ToolStripLabel(text);
            l.Font = Native.UiFont("Segoe UI", 5.8f, k, FontStyle.Bold);
            l.ForeColor = Color.FromArgb(110, 120, 135);
            l.AutoSize = false;
            l.Size = new Size(S(IconSize + 28), S(13));
            l.TextAlign = ContentAlignment.BottomCenter;
            l.Margin = new Padding(0, S(4), 0, 0);
            toolBar.Items.Add(l);
        }

        void AddTool(Tool t, string tip)
        {
            ToolStripButton b = new ToolStripButton();
            b.Image = Icons.Make(t, S(IconSize));
            b.ImageScaling = ToolStripItemImageScaling.None;
            b.DisplayStyle = ToolStripItemDisplayStyle.Image;
            b.AutoSize = false;
            b.Size = new Size(S(IconSize + 24), S(IconSize + 3));
            b.ToolTipText = tip;
            b.Margin = new Padding(0, S(1), 0, S(1));
            b.Click += (s, e) => { cv.CurrentTool = t; cv.Focus(); };
            toolButtons[t] = b;
            toolBar.Items.Add(b);
        }

        Label Header(string text)
        {
            Label l = new Label();
            l.UseMnemonic = false;     // show "&" as a character
            l.Text = text;
            l.Font = Native.UiFont("Segoe UI", 9f, k, FontStyle.Bold);
            l.ForeColor = Color.FromArgb(0, 90, 170);
            l.AutoSize = false;
            l.Width = S(236);
            l.Height = S(26);
            l.TextAlign = ContentAlignment.BottomLeft;
            l.Margin = new Padding(0, S(8), 0, S(2));
            return l;
        }

        Panel Row(string label, Control c)
        {
            Panel p = new Panel();
            p.Width = S(236);
            p.Height = Math.Max(S(28), c.Height + S(4));
            p.Margin = new Padding(0);
            Label l = new Label();
            l.Text = label;
            l.AutoSize = false;
            l.SetBounds(0, 0, S(92), p.Height);
            l.TextAlign = ContentAlignment.MiddleLeft;
            c.Left = S(94);
            c.Top = (p.Height - c.Height) / 2;
            p.Controls.Add(l);
            p.Controls.Add(c);
            return p;
        }

        Button ColorButton(EventHandler h)
        {
            Button b = new Button();
            b.FlatStyle = FlatStyle.Flat;
            b.Size = new Size(S(34), S(22));
            b.Click += h;
            return b;
        }

        TrackBar Slider(out Label pct, EventHandler h)
        {
            TrackBar t = new TrackBar();
            t.Minimum = 0; t.Maximum = 100; t.TickStyle = TickStyle.None;
            t.AutoSize = false;
            t.Size = new Size(S(70), S(24));
            t.ValueChanged += h;
            Label l = new Label();
            l.AutoSize = false;
            l.Size = new Size(S(42), S(22));
            l.TextAlign = ContentAlignment.MiddleRight;
            pct = l;
            return t;
        }

        Panel SliderRow(string label, TrackBar t, Label pct)
        {
            Panel host = new Panel();
            host.Size = new Size(S(140), S(26));
            t.Location = new Point(0, 0);
            pct.Location = new Point(S(100), S(2));
            host.Controls.Add(t);
            host.Controls.Add(pct);
            return Row(label, host);
        }

        CheckBox ToggleBase(string tip)
        {
            CheckBox c = new CheckBox();
            c.Appearance = Appearance.Button;
            c.FlatStyle = FlatStyle.Flat;
            c.FlatAppearance.CheckedBackColor = Color.FromArgb(190, 220, 250);
            c.FlatAppearance.BorderColor = Color.FromArgb(170, 170, 170);
            c.Size = new Size(S(24), S(24));
            c.Margin = new Padding(0, 0, S(2), 0);
            c.TextAlign = ContentAlignment.MiddleCenter;
            this.tip.SetToolTip(c, tip);       // the form's one tooltip (disposed with the form)
            return c;
        }

        CheckBox StyleToggle(string text, string tip, FontStyle style)
        {
            CheckBox c = ToggleBase(tip);
            c.Text = text;
            c.Font = Native.UiFont("Segoe UI", 9.5f, this.k, style);
            return c;
        }

        CheckBox AlignToggle(int align, string tip)
        {
            CheckBox c = ToggleBase(tip);
            c.Image = Icons.Cmd(align == 0 ? "alignl" : align == 1 ? "alignc" : "alignr", S(16));
            c.CheckedChanged += (s, e) =>
            {
                if (loading || !c.Checked) { if (!loading && !c.Checked) { loading = true; c.Checked = true; loading = false; } return; }
                ApplyStyle(x => x.Align = align);
                LoadStyle();
            };
            return c;
        }

        /// <summary>Drop-down of line ends drawn as small pictures.</summary>
        ComboBox HeadCombo(bool start)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.DrawMode = DrawMode.OwnerDrawFixed;
            c.ItemHeight = S(20);
            c.Width = S(64);
            c.DropDownWidth = S(150);
            for (int i = 0; i < Icons.HeadNames.Length; i++) c.Items.Add(Icons.HeadNames[i]);
            c.DrawItem += (s, e) =>
            {
                e.DrawBackground();
                if (e.Index < 0) return;
                bool inList = (e.State & DrawItemState.ComboBoxEdit) == 0;
                int y = e.Bounds.Y + (e.Bounds.Height - S(18)) / 2;
                // wide line picture so the end style is easy to see
                using (Bitmap wide = Icons.HeadPreviewWide(e.Index, !start, S(40), S(18)))
                    e.Graphics.DrawImage(wide, e.Bounds.X + S(3), y);
                if (inList)
                    using (SolidBrush b = new SolidBrush(e.ForeColor))
                        e.Graphics.DrawString(Icons.HeadNames[e.Index], c.Font, b, e.Bounds.X + S(48), e.Bounds.Y + (e.Bounds.Height - c.Font.Height) / 2);
            };
            return c;
        }

        static readonly string[] DashNames = { "Solid", "Dashed", "Dotted", "Dash-dot", "Long dash", "Cloud" };

        /// <summary>Line-style drop-down with a picture of each style.</summary>
        ComboBox DashCombo()
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.DrawMode = DrawMode.OwnerDrawFixed;
            c.ItemHeight = S(20);
            c.Width = S(78);
            c.DropDownWidth = S(150);
            c.Items.AddRange(DashNames);
            c.DrawItem += (s, e) =>
            {
                e.DrawBackground();
                if (e.Index < 0) return;
                bool inList = (e.State & DrawItemState.ComboBoxEdit) == 0;
                Graphics g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int x0 = e.Bounds.X + S(4), x1 = e.Bounds.X + S(inList ? 60 : 58), y = e.Bounds.Y + e.Bounds.Height / 2;
                Shape tmp = new Shape(); tmp.Dash = e.Index;
                Color col = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : Color.FromArgb(40, 40, 45);
                if (e.Index == (int)LineStyle.Cloud)
                    using (System.Drawing.Drawing2D.GraphicsPath gp = Renderer.CloudPolyPath(new PointF[] { new PointF(x0, y + S(3)), new PointF(x1, y + S(3)) }, false, 0.5f * k))
                    using (Pen p = new Pen(col, Math.Max(1f, k))) g.DrawPath(p, gp);
                else
                    using (Pen p = Renderer.OutlinePen(tmp, col, Math.Max(1.5f, 1.5f * k))) g.DrawLine(p, x0, y, x1, y);
                if (inList)
                    using (SolidBrush b = new SolidBrush(e.ForeColor))
                        g.DrawString(DashNames[e.Index], c.Font, b, e.Bounds.X + S(66), e.Bounds.Y + (e.Bounds.Height - c.Font.Height) / 2);
            };
            return c;
        }

        void CalloutChanged()
        {
            int m = cCallout.SelectedIndex;
            if (loading || m < 0) return;
            ApplyStyle(x =>
            {
                if (x.Kind != ShapeKind.Cloud) return;
                x.Mode = m;
                if (m != 0 && x.Pts.Count == 2 && cv.Shapes.Contains(x))
                {
                    RectangleF r = Geo.Rect(x.Pts[0], x.Pts[1]);
                    x.Pts.Add(new PointF(r.Right + 70f, r.Bottom + 46f));
                }
                if (m == (int)CloudCallout.Triangle && string.IsNullOrEmpty(x.Text)) x.Text = "1";
            });
            Shape sel = cv.Selected;
            if (m == (int)CloudCallout.TextBox && sel != null && sel.Kind == ShapeKind.Cloud && sel.Pts.Count > 2) cv.EditLabel(sel);
            LoadStyle();
        }

        /// <summary>Stamp date format: rewrites the date line of the selected stamps (same date, new format) and is used for new stamps.</summary>
        void StampDateChanged(int fmt)
        {
            if (loading || fmt < 0) return;
            Settings.Set("StampDate", fmt.ToString(CultureInfo.InvariantCulture));
            ApplyStyle(x =>
            {
                if (x.Kind != ShapeKind.Stamp || !cv.Shapes.Contains(x)) return;
                List<string> lines = new List<string>((x.Text ?? "").Replace("\r", "").Split('\n'));
                DateTime when = DateTime.Now;
                int at = -1;
                for (int i = 1; i < lines.Count; i++) { DateTime d; if (Stamps.TryParseDate(lines[i], out d)) { when = d; at = i; break; } }
                string nd = Stamps.FormatDate(when, fmt);
                if (at >= 0) { if (nd.Length > 0) lines[at] = nd; else lines.RemoveAt(at); }
                else if (nd.Length > 0) lines.Insert(Math.Min(1, lines.Count), nd);
                x.Text = string.Join("\n", lines.ToArray());
            });
        }

        void StampModeChanged()
        {
            int m = cStamp.SelectedIndex;
            if (loading || m < 0) return;
            ApplyStyle(x =>
            {
                if (x.Kind != ShapeKind.Stamp) return;
                x.Mode = m;
                x.Stroke = Stamps.Colors[m];
                if (m < Stamps.Custom)
                {
                    // keep any lines the user added below the stamp word
                    string[] lines = (x.Text ?? "").Replace("\r", "").Split('\n');
                    lines[0] = Stamps.Names[m];
                    x.Text = lines.Length > 1 ? string.Join("\n", lines) : Canvas.StampText(m, "");
                }
            });
            if (m == Stamps.Custom && cv.Selected != null && cv.Selected.Kind == ShapeKind.Stamp) cv.EditLabel(cv.Selected);
            LoadStyle();
        }

        ComboBox Combo(int width, params string[] items)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Items.AddRange(items);
            c.Width = S(width);
            return c;
        }

        /// <summary>One labelled group in the style bar: "Line [colour] [opacity] [width]".</summary>
        FlowLayoutPanel Grp(string label, params Control[] cs)
        {
            FlowLayoutPanel p = new FlowLayoutPanel();
            p.WrapContents = false;
            p.AutoSize = true;
            p.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            p.Margin = new Padding(0, 0, S(10), 0);
            p.Padding = new Padding(0);
            if (!string.IsNullOrEmpty(label))
            {
                Label l = new Label();
                l.Text = label;
                l.AutoSize = true;
                l.ForeColor = Color.FromArgb(80, 80, 80);
                l.Margin = new Padding(0, S(6), S(3), 0);
                p.Controls.Add(l);
            }
            foreach (Control c in cs) { c.Margin = new Padding(0, S(2), S(3), 0); p.Controls.Add(c); }
            return p;
        }

        static readonly Color[] Palette =
        {
            Color.FromArgb(230, 30, 30), Color.FromArgb(255, 140, 0), Color.FromArgb(255, 215, 0), Color.FromArgb(30, 160, 60),
            Color.FromArgb(0, 120, 215), Color.FromArgb(130, 60, 200), Color.FromArgb(30, 30, 30), Color.White, Color.FromArgb(128, 128, 128)
        };
        static readonly string[] PaletteNames = { "Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Black", "White", "Grey" };

        void SetPaletteColor(bool stroke, int i)
        {
            if (StyleTarget() == null) { Flash("Pick a tool or select a shape first"); return; }
            Color c = Palette[i];
            if (stroke) ApplyStyle(x => x.Stroke = c);
            else ApplyStyle(x => { x.Fill = c; if (x.FillOpacity == 0) x.FillOpacity = 50; });
            LoadStyle();
            Flash((stroke ? "Line colour: " : "Fill colour: ") + PaletteNames[i]);
        }

        /// <summary>The style bar right above the canvas, close to the work (replaces the old style panel on the right).</summary>
        void BuildStyleBar()
        {
            styleBar = new FlowLayoutPanel();
            styleBar.Dock = DockStyle.Top;
            styleBar.AutoSize = true;
            styleBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            styleBar.WrapContents = true;
            styleBar.Padding = new Padding(S(6), S(3), S(6), S(3));
            styleBar.BackColor = Color.FromArgb(246, 247, 249);

            lStyleFor = new Label();
            lStyleFor.AutoSize = true;
            lStyleFor.Font = Native.UiFont("Segoe UI", 9f, k, FontStyle.Bold);
            lStyleFor.ForeColor = Color.FromArgb(0, 90, 170);
            lStyleFor.Margin = new Padding(0, S(6), S(12), 0);
            lStyleFor.MinimumSize = new Size(S(150), 0);

            bStroke = ColorButton((s, e) => PickColor(true));
            bFill = ColorButton((s, e) => PickColor(false));
            tip.SetToolTip(bStroke, "Line colour (Alt+L) · quick colours: Alt+1 … Alt+9");
            tip.SetToolTip(bFill, "Fill colour (Alt+F) · quick colours: Alt+Shift+1 … 9 · Alt+Shift+0 = no fill");
            nWidth = new NumericUpDown();
            nWidth.DecimalPlaces = 1; nWidth.Increment = 0.5m; nWidth.Minimum = 0; nWidth.Maximum = 200; nWidth.Width = S(52);
            nWidth.ValueChanged += (s, e) => ApplyStyle(x => x.Width = (float)nWidth.Value);
            tip.SetToolTip(nWidth, "Line width");
            tSO = Slider(out lSO, (s, e) => { lSO.Text = tSO.Value + "%"; ApplyStyle(x => x.StrokeOpacity = tSO.Value); });
            tFO = Slider(out lFO, (s, e) => { lFO.Text = tFO.Value + "%"; ApplyStyle(x => x.FillOpacity = tFO.Value); });
            tip.SetToolTip(tSO, "Line opacity");
            tip.SetToolTip(tFO, "Fill opacity (for pictures: picture opacity)");

            // Focus modes (the yellow text marker is now the highlighter pen): list index + 1 = HighlightMode
            cHlMode = Combo(110, "Spotlight", "Grayscale", "Magnify", "Pixelate");
            // strength: magnify ×1.5 … ×6, pixelate 4 … 40 px blocks
            tStr = new TrackBar();
            tStr.Minimum = 1; tStr.Maximum = 10; tStr.TickStyle = TickStyle.None; tStr.AutoSize = false;
            tStr.Size = new Size(S(90), S(24));
            lStr = new Label(); lStr.AutoSize = false; lStr.Size = new Size(S(44), S(22)); lStr.TextAlign = ContentAlignment.MiddleLeft;
            tStr.ValueChanged += (s, e) =>
            {
                ShowStrength();
                int v = tStr.Value;
                ApplyStyle(x => { if (x.Kind == ShapeKind.Highlight || x.Kind == ShapeKind.Pixelate) { x.Strength = v; x.Cache = null; } });
            };
            tip.SetToolTip(tStr, "Strength: how much to magnify, or how big the pixel blocks are");
            cWhere = Combo(80, "Inside", "Outside");
            tip.SetToolTip(cWhere, "Gray or pixelate the marked area itself (inside) or everything around it (outside)");
            EventHandler focusChanged = (s, e) =>
            {
                if (loading || cHlMode.SelectedIndex < 0) return;
                int b = cHlMode.SelectedIndex + 1;
                bool inside = cWhere.SelectedIndex != 1;
                ApplyStyle(x =>
                {
                    // keep each mode's usual side when switching mode; the Inside / Outside box picks the other side
                    int m = FocusMode(b, s == cWhere ? inside : (b == (int)HighlightMode.Grayscale ? false : true));
                    x.Mode = m;
                    if (m == (int)HighlightMode.Spotlight && x.FillOpacity < 20) x.FillOpacity = 45;
                });
                LoadStyle();
            };
            cHlMode.SelectedIndexChanged += focusChanged;
            cWhere.SelectedIndexChanged += focusChanged;
            cShadow = new CheckBox();
            cShadow.Text = "Shadow";
            cShadow.AutoSize = true;
            cShadow.CheckedChanged += (s, e) => ApplyStyle(x => x.Shadow = cShadow.Checked);
            nFont = new NumericUpDown();
            nFont.Minimum = 6; nFont.Maximum = 200; nFont.Width = S(52);
            nFont.ValueChanged += (s, e) => ApplyStyle(x => x.FontSize = (float)nFont.Value);
            tip.SetToolTip(nFont, "Text size");
            cHeadS = HeadCombo(true);
            cHeadE = HeadCombo(false);
            cHeadS.SelectedIndexChanged += (s, e) => { int h = cHeadS.SelectedIndex; if (h >= 0) ApplyStyle(x => { if (x.HasHeads) x.HeadStart = h; }); };
            cHeadE.SelectedIndexChanged += (s, e) => { int h = cHeadE.SelectedIndex; if (h >= 0) ApplyStyle(x => { if (x.HasHeads) x.HeadEnd = h; }); };
            tip.SetToolTip(cHeadS, "Line end at the start point (also on the right-click menu)");
            tip.SetToolTip(cHeadE, "Line end at the end point (also on the right-click menu)");
            cStamp = Combo(130, Stamps.Names);
            cStamp.SelectedIndexChanged += (s, e) => StampModeChanged();
            tip.SetToolTip(cStamp, "Stamp type – sets the word and colour (double-click a stamp to edit its text)");
            cMarkup = Combo(110, "Underline", "Strike out", "Highlight text");
            cMarkup.SelectedIndexChanged += (s, e) => { int m = cMarkup.SelectedIndex; if (m >= 0) { ApplyStyle(x => { if (x.Kind == ShapeKind.Markup) x.Mode = m; }); LoadStyle(); } };
            cDash = DashCombo();
            cDash.SelectedIndexChanged += (s, e) => { int m = cDash.SelectedIndex; if (m >= 0) ApplyStyle(x => { if (x.HasOutlineStyle && (m != (int)LineStyle.Cloud || x.CanBeCloud)) x.Dash = m; }); };
            tip.SetToolTip(cDash, "Line style: solid, dashed, dotted, dash-dot, long dash or cloud");
            cForm = Combo(96, "Rectangle", "Ellipse", "Triangle", "Diamond", "Rounded");
            cForm.SelectedIndexChanged += (s, e) => { int m = cForm.SelectedIndex; if (m >= 0) ApplyStyle(x => { if (x.Kind == ShapeKind.Highlight) x.Form = m; }); };
            tip.SetToolTip(cForm, "Shape of the highlight");
            cCallout = Combo(130, "No callout", "Revision triangle", "Text box");
            cCallout.SelectedIndexChanged += (s, e) => CalloutChanged();
            tip.SetToolTip(cCallout, "Callout on a leader: a revision triangle with a number, or a text box (double-click to edit the text)");
            cShow = Combo(150, "Area + perimeter", "Area only", "Perimeter only");
            cShow.SelectedIndexChanged += (s, e) =>
            {
                int m = cShow.SelectedIndex;
                if (loading || m < 0) return;
                ApplyStyle(x => { if (x.Kind == ShapeKind.Area || x.Kind == ShapeKind.Radius) x.Mode = m; });
            };
            tip.SetToolTip(cShow, "What the label shows");
            cClosed = new CheckBox();
            cClosed.Text = "Closed";
            cClosed.AutoSize = true;
            cClosed.CheckedChanged += (s, e) =>
            {
                bool c = cClosed.Checked;
                // perimeter: closed = perimeter, open = length · polygon: closed = polygon, open = polyline
                ApplyStyle(x => { if (x.Kind == ShapeKind.Perimeter || x.Kind == ShapeKind.Polygon) x.Mode = c ? 0 : 1; });
                LoadStyle();
            };
            cFrame = new CheckBox();
            cFrame.Text = "Frame";
            cFrame.AutoSize = true;
            cFrame.CheckedChanged += (s, e) => { bool c = cFrame.Checked; ApplyStyle(x => { if (x.Kind == ShapeKind.Text) { x.Frame = c; if (c && x.Width < 0.5f) x.Width = 1.5f; } }); LoadStyle(); };
            tip.SetToolTip(cFrame, "Frame round the text, in the line colour and width");
            cBold = StyleToggle("B", "Bold", FontStyle.Bold);
            cBold.CheckedChanged += (s, e) => ApplyStyle(x => x.Bold = cBold.Checked);
            cItalic = StyleToggle("I", "Italic", FontStyle.Italic);
            cItalic.CheckedChanged += (s, e) => ApplyStyle(x => x.Italic = cItalic.Checked);
            cUnder = StyleToggle("U", "Underline", FontStyle.Underline);
            cUnder.CheckedChanged += (s, e) => ApplyStyle(x => x.Underline = cUnder.Checked);
            cStrike = StyleToggle("S", "Strike out", FontStyle.Strikeout);
            cStrike.CheckedChanged += (s, e) => ApplyStyle(x => x.Strikeout = cStrike.Checked);
            cAlignL = AlignToggle(0, "Align left");
            cAlignC = AlignToggle(1, "Centre");
            cAlignR = AlignToggle(2, "Align right");
            tMarkStart = new TextBox();
            tMarkStart.Width = S(44);
            tMarkStart.Leave += (s, e) => MarkerEdited();
            tMarkStart.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { MarkerEdited(); e.SuppressKeyPress = true; cv.Focus(); } };
            tip.SetToolTip(tMarkStart, "First number (or letter) of this marker series");

            // quick palette: left-click = line colour, right-click = fill colour
            FlowLayoutPanel pal = new FlowLayoutPanel();
            pal.WrapContents = false; pal.AutoSize = true; pal.Margin = new Padding(0, S(4), S(10), 0);
            for (int i = 0; i < Palette.Length; i++)
            {
                int idx = i;
                Panel sw = new Panel();
                sw.Size = new Size(S(16), S(16));
                sw.Margin = new Padding(0, 0, S(2), 0);
                sw.BackColor = Palette[i];
                sw.BorderStyle = BorderStyle.FixedSingle;
                sw.Cursor = Cursors.Hand;
                sw.MouseUp += (s, e) => SetPaletteColor(e.Button != MouseButtons.Right, idx);
                tip.SetToolTip(sw, PaletteNames[i] + " – click = line (Alt+" + (i + 1) + "), right-click = fill (Alt+Shift+" + (i + 1) + ")");
                pal.Controls.Add(sw);
            }

            gLine = Grp("Line", bStroke, tSO, lSO, nWidth);
            gFill = Grp("Fill", bFill, tFO, lFO);
            gFont = Grp("Text", nFont);
            rowTextStyle = Grp("", cBold, cItalic, cUnder, cStrike, cAlignL, cAlignC, cAlignR);
            gHeads = Grp("Start", cHeadS);
            Label lEnd = new Label();
            lEnd.Text = "End";
            lEnd.AutoSize = true;
            lEnd.ForeColor = Color.FromArgb(80, 80, 80);
            lEnd.Margin = new Padding(S(4), S(6), S(3), 0);
            gHeads.Controls.Add(lEnd);
            cHeadE.Margin = new Padding(0, S(2), S(3), 0);
            gHeads.Controls.Add(cHeadE);
            rowHl = Grp("Mode", cHlMode, cWhere);
            rowStrength = Grp("Strength", tStr, lStr);
            rowMarkStart = Grp("Start at", tMarkStart);
            ComboBox cDate = Combo(150, Stamps.DateNames);
            cDate.SelectedIndex = Math.Max(0, Math.Min(Stamps.DateNames.Length - 1, Settings.GetInt("StampDate", 0)));
            cDate.SelectedIndexChanged += (s, e) => StampDateChanged(cDate.SelectedIndex);
            tip.SetToolTip(cDate, "Date line on the stamp (keeps the stamp's own date, just changes how it is written)");
            rowStamp = Grp("Stamp", cStamp);
            Label lDate = new Label(); lDate.Text = "Date"; lDate.AutoSize = true; lDate.ForeColor = Color.FromArgb(80, 80, 80);
            lDate.Margin = new Padding(S(4), S(6), S(3), 0);
            rowStamp.Controls.Add(lDate);
            cDate.Margin = new Padding(0, S(2), S(3), 0);
            rowStamp.Controls.Add(cDate);
            rowMarkup = Grp("Mode", cMarkup);
            gDash = Grp("Style", cDash);
            rowForm = Grp("Shape", cForm);
            rowCallout = Grp("Callout", cCallout);
            rowClosed = Grp("", cClosed);
            rowShow = Grp("Show", cShow);
            rowFrame = Grp("", cFrame);
            gShadow = Grp("", cShadow);
            foreach (Control c in new Control[] { lStyleFor, gLine, gDash, gFill, gFont, rowTextStyle, rowFrame, gHeads, rowHl, rowStrength, rowForm, rowStamp, rowMarkup, rowCallout, rowShow, rowClosed, rowMarkStart, gShadow, pal })
                styleBar.Controls.Add(c);
            ReturnFocusToCanvas(styleBar);
        }

        /// <summary>After using a style-bar control, give the keyboard back to the canvas so tool keys (V, T, K …) work at once.</summary>
        void ReturnFocusToCanvas(Control root)
        {
            foreach (Control c in root.Controls)
            {
                Action back = () => BeginInvoke((Action)(() => { if (!cv.IsEditingText) cv.Focus(); }));
                ComboBox cb = c as ComboBox;
                if (cb != null) { cb.DropDownClosed += (s, e) => back(); continue; }
                if (c is CheckBox || c is Button) { c.Click += (s, e) => back(); continue; }
                if (c is TrackBar) { c.MouseUp += (s, e) => back(); continue; }
                NumericUpDown nu = c as NumericUpDown;
                if (nu != null) { nu.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; back(); } }; continue; }
                if (c.Controls.Count > 0) ReturnFocusToCanvas(c);
            }
        }

        Panel BuildProps()
        {
            Panel outer = new Panel();
            outer.Dock = DockStyle.Right;
            outer.Width = S(262);
            outer.BackColor = SystemColors.Control;
            outer.Padding = new Padding(S(10), S(2), S(6), S(6));
            outer.Visible = Settings.GetBool("ShowPanel", true);
            props = outer;

            FlowLayoutPanel f = new FlowLayoutPanel();
            f.Dock = DockStyle.Fill;
            f.FlowDirection = FlowDirection.TopDown;
            f.WrapContents = false;
            f.AutoScroll = true;

            f.Controls.Add(Header("SCALE"));
            cUnit = Combo(80, ScaleInfo.Units);
            cUnit.SelectedIndexChanged += (s, e) => ScaleEdited();
            tUpp = new TextBox();
            tUpp.Width = S(110);
            tUpp.Leave += (s, e) => ScaleEdited();
            tUpp.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ScaleEdited(); e.SuppressKeyPress = true; cv.Focus(); } };
            nDec = new NumericUpDown();
            nDec.Minimum = 0; nDec.Maximum = 6; nDec.Width = S(60);
            nDec.ValueChanged += (s, e) => ScaleEdited();
            Button bCal = new Button();
            bCal.Text = "Calibrate from a known length…";
            bCal.Size = new Size(S(236), S(28));
            bCal.Click += (s, e) => { cv.CurrentTool = Tool.Calibrate; cv.Focus(); };
            cKeepScale = new CheckBox();
            cKeepScale.Text = "Keep this scale for new captures";
            cKeepScale.AutoSize = true;
            cKeepScale.Checked = Settings.GetBool("KeepScale", false);
            cKeepScale.CheckedChanged += (s, e) => { Settings.Set("KeepScale", cKeepScale.Checked ? "1" : "0"); SaveScaleDefaults(); };

            Button bPreset = new Button();
            bPreset.Text = "Drawing scale (1:50 on A1 …) / saved scales…";
            bPreset.Size = new Size(S(236), S(28));
            bPreset.Click += (s, e) => ScalePresetDialog();

            f.Controls.Add(Row("Unit", cUnit));
            f.Controls.Add(Row("1 pixel =", tUpp));
            f.Controls.Add(Row("Decimals", nDec));
            f.Controls.Add(bCal);
            f.Controls.Add(bPreset);
            f.Controls.Add(cKeepScale);

            f.Controls.Add(Header("MEASUREMENTS & COUNTS"));
            lv = new ListView();
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.HideSelection = false;
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lv.Size = new Size(S(236), S(210));
            lv.Columns.Add("#", S(40));
            lv.Columns.Add("Value", S(96));
            lv.Columns.Add("Label / extra", S(96));
            lv.ShowItemToolTips = true;
            lv.DoubleClick += (s, e) => { if (lv.SelectedItems.Count > 0) { Shape sh = lv.SelectedItems[0].Tag as Shape; if (sh != null) cv.EditLabel(sh); else LabelSeries(lv.SelectedItems[0].Text == "ΣA"); } };
            lv.SelectedIndexChanged += (s, e) =>
            {
                if (loading || lv.SelectedItems.Count == 0) return;
                Shape sh = lv.SelectedItems[0].Tag as Shape;
                if (sh != null && sh != cv.Selected) { cv.CurrentTool = Tool.Select; cv.SelectShape(sh); }
            };
            f.Controls.Add(lv);
            Button bCopyM = new Button();
            bCopyM.Text = "Copy measurements (for Excel)";
            bCopyM.Size = new Size(S(236), S(28));
            bCopyM.Click += (s, e) => CopyMeasurements();
            f.Controls.Add(bCopyM);

            // every text on the picture in one list: click a line to select it, click again (or F2) to edit it here
            f.Controls.Add(Header("TEXTS"));
            lvText = new ListView();
            lvText.View = View.Details;
            lvText.FullRowSelect = true;
            lvText.HideSelection = false;
            lvText.LabelEdit = true;
            lvText.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lvText.Size = new Size(S(236), S(170));
            lvText.Columns.Add("Text", S(176));        // the editable column comes first (ListView edits column 0)
            lvText.Columns.Add("#", S(40));
            lvText.ShowItemToolTips = true;
            lvText.SelectedIndexChanged += (s, e) =>
            {
                if (loading || lvText.SelectedItems.Count == 0) return;
                Shape sh = lvText.SelectedItems[0].Tag as Shape;
                if (sh != null && sh != cv.Selected) { cv.CurrentTool = Tool.Select; cv.SelectShape(sh); }
                BeginInvoke((Action)(() => lvText.Focus()));     // keep the keyboard here so F2 / typing edits the line
            };
            lvText.MouseDoubleClick += (s, e) => { ListViewItem it = lvText.GetItemAt(e.X, e.Y); if (it != null) it.BeginEdit(); };
            // while a line is being edited, Ctrl+A / Ctrl+V / Delete … belong to that edit box, not to the canvas
            lvText.BeforeLabelEdit += (s, e) => textListEditing = true;
            lvText.AfterLabelEdit += (s, e) =>
            {
                textListEditing = false;
                if (e.Label == null) return;                      // edit cancelled
                Shape sh = lvText.Items[e.Item].Tag as Shape;
                if (sh == null) return;
                string t = e.Label.Replace(" " + LineMark + " ", "\n").Replace(LineMark, "\n");
                if (sh.Kind == ShapeKind.Text && t.Trim().Length == 0) { e.CancelEdit = true; return; }
                cv.SetShapeText(sh, t);
                BeginInvoke((Action)RefreshTexts);
            };
            lvText.KeyDown += (s, e) => { if (e.KeyCode == Keys.F2 && lvText.SelectedItems.Count > 0) lvText.SelectedItems[0].BeginEdit(); };
            f.Controls.Add(lvText);
            Button bCopyT = new Button();
            bCopyT.Text = "Copy all text";
            bCopyT.Size = new Size(S(236), S(28));
            bCopyT.Click += (s, e) => CopyAllText();
            f.Controls.Add(bCopyT);

            outer.Controls.Add(f);
            return outer;
        }

        /// <summary>Shown in place of line breaks in the one-line text list (typed back, it becomes a line break again).</summary>
        const string LineMark = "↵";

        /// <summary>Focus: the mode picked in the list (spotlight, grayscale, magnify, pixelate) without its side.</summary>
        static int FocusBase(int mode)
        {
            if (mode == (int)HighlightMode.GrayscaleInside) return (int)HighlightMode.Grayscale;
            if (mode == (int)HighlightMode.PixelateOutside) return (int)HighlightMode.Pixelate;
            return mode;
        }

        bool strMagnify;

        /// <summary>The value next to the Strength slider: ×2.5 for magnify, 12 px for pixelate.</summary>
        void ShowStrength()
        {
            int v = tStr.Value;
            lStr.Text = strMagnify ? "×" + (1 + v * 0.5).ToString("0.0", CultureInfo.InvariantCulture) : Math.Max(3, v * 4) + " px";
        }

        /// <summary>Focus: the stored mode for a list mode and a side.</summary>
        static int FocusMode(int baseMode, bool inside)
        {
            if (baseMode == (int)HighlightMode.Grayscale) return inside ? (int)HighlightMode.GrayscaleInside : (int)HighlightMode.Grayscale;
            if (baseMode == (int)HighlightMode.Pixelate) return inside ? (int)HighlightMode.Pixelate : (int)HighlightMode.PixelateOutside;
            return baseMode;
        }

        /// <summary>Texts the list shows: text boxes, speech bubbles, cloud callouts and stamps, in drawing order.</summary>
        List<Shape> TextShapes()
        {
            return cv.Shapes.FindAll(x => x.Kind == ShapeKind.Text || x.Kind == ShapeKind.Bubble || x.Kind == ShapeKind.Stamp ||
                                          (x.Kind == ShapeKind.Cloud && x.Mode != 0));
        }

        static string TextKindTag(Shape s, int n)
        {
            switch (s.Kind)
            {
                case ShapeKind.Bubble: return "B" + n;
                case ShapeKind.Stamp: return "S" + n;
                case ShapeKind.Cloud: return "C" + n;
                default: return "T" + n;
            }
        }

        void RefreshTexts()
        {
            if (lvText == null) return;
            bool old = loading;
            loading = true;
            lvText.BeginUpdate();
            lvText.Items.Clear();
            Dictionary<ShapeKind, int> num = new Dictionary<ShapeKind, int>();
            foreach (Shape s in TextShapes())
            {
                int n; num.TryGetValue(s.Kind, out n); num[s.Kind] = ++n;
                string shown = (s.Text ?? "").Replace("\r\n", "\n").Replace("\n", " " + LineMark + " ");
                ListViewItem it = new ListViewItem(new string[] { shown, TextKindTag(s, n) });
                it.ToolTipText = "Click again or press F2 to edit · " + LineMark + " = new line";
                it.Tag = s;
                if (s == cv.Selected) it.Selected = true;
                lvText.Items.Add(it);
            }
            lvText.EndUpdate();
            loading = old;
        }

        void CopyAllText()
        {
            List<string> parts = new List<string>();
            foreach (Shape s in TextShapes())
                if (!string.IsNullOrEmpty(s.Text) && s.Text.Trim().Length > 0) parts.Add(s.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            if (parts.Count == 0) { Flash("There is no text on the picture yet"); return; }
            if (!App.TrySetClipboardText(string.Join("\r\n", parts.ToArray()))) { Flash(ClipboardBusy); return; }
            Flash(parts.Count + (parts.Count == 1 ? " text copied" : " texts copied"));
        }

        void BuildStatus()
        {
            status = new StatusStrip();
            status.Font = Font;
            status.SizingGrip = true;
            lPos = new ToolStripStatusLabel("");
            lPos.AutoSize = false;
            lPos.Width = S(260);
            lPos.TextAlign = ContentAlignment.MiddleLeft;
            lZoom = new ToolStripStatusLabel("100%");
            lScale = new ToolStripStatusLabel("");
            lHint = new ToolStripStatusLabel("");
            lHint.Spring = true;
            lHint.TextAlign = ContentAlignment.MiddleLeft;
            status.Items.Add(lPos);
            status.Items.Add(new ToolStripSeparator());
            status.Items.Add(lZoom);
            status.Items.Add(new ToolStripSeparator());
            status.Items.Add(lScale);
            status.Items.Add(new ToolStripSeparator());
            status.Items.Add(lHint);
            Version v = typeof(EditorForm).Assembly.GetName().Version;
            lVersion = new ToolStripStatusLabel("SnapMeasure v" + v.Major + "." + v.Minor + (v.Build > 0 ? "." + v.Build : ""));
            lVersion.ForeColor = Color.FromArgb(0, 102, 204);
            lVersion.IsLink = true;
            lVersion.LinkBehavior = LinkBehavior.HoverUnderline;
            lVersion.ToolTipText = "About SnapMeasure – " + AppInfo.RepoUrl;
            lVersion.Click += (s, e) => App.ShowAbout(this);
            status.Items.Add(new ToolStripSeparator());
            status.Items.Add(lVersion);
        }

        // ---------------- state sync ----------------
        void SyncToolButtons()
        {
            foreach (KeyValuePair<Tool, ToolStripButton> kv in toolButtons) kv.Value.Checked = kv.Key == cv.CurrentTool && !cv.MatchActive;
            if (bMatch != null) bMatch.Checked = cv.MatchActive;
            UpdateGuide();
        }

        DateTime flashUntil = DateTime.MinValue;

        /// <summary>Status bar: what to do next with the current tool, step by step, or what you can do with the selection.</summary>
        void UpdateGuide()
        {
            if (lHint == null || DateTime.Now < flashUntil) return;
            string t = GuideText();
            if (lHint.Text != t) lHint.Text = t;
        }

        /// <summary>Status bar text: the next step in one short sentence.</summary>
        string GuideText()
        {
            if (cv.IsEditingText) return "Typing. Esc or click outside = done.";
            if (cv.MatchActive) return "Match properties: click shapes to give them the look. Esc or right-click = done.";
            if (cv.ZoomBoxActive) return "Zoom to area: drag a box. Esc = cancel.";
            Tool t = cv.CurrentTool;
            int n = cv.CreatingPoints;
            Shape sel = cv.Selected;
            string name = ToolName(t), h;
            switch (t)
            {
                case Tool.Select:
                    if (cv.Sel.Count > 1) return cv.Sel.Count + " shapes selected. Drag to move, Del to delete.";
                    if (sel == null) return Hints[Tool.Select];
                    return KindName(sel) + ": " + SelectedHelp(sel);
                case Tool.Rect: case Tool.Ellipse: case Tool.Highlight: case Tool.Cloud: case Tool.Markup: case Tool.Pixelate: case Tool.Bubble:
                case Tool.Crop: case Tool.Ocr: case Tool.Stamp:
                    if (n < 0) return Hints.TryGetValue(t, out h) ? h : name + ": drag from corner to corner.";
                    return name + ": release to finish.";
                case Tool.Line: case Tool.Arrow: case Tool.Dimension: case Tool.Calibrate:
                    if (n < 0) return name + ": press at the start point.";
                    return name + ": release at the end point." + (t == Tool.Calibrate ? " Then type the real length." : "");
                case Tool.Polygon: case Tool.PolyDraw: case Tool.Area: case Tool.Perimeter: case Tool.Cutout: case Tool.Polyline:
                    if (n < 0) return Hints.TryGetValue(t, out h) ? h : name + ": click the first point.";
                    return name + ": click the next point. Double-click to finish.";
                case Tool.Angle:
                    if (n < 0) return "Angle: click the end of the first arm.";
                    if (n == 2) return "Angle: click the corner.";
                    return "Angle: click the end of the second arm.";
                case Tool.Arc: case Tool.Radius:
                    if (n < 0) return name + ": click the start.";
                    if (n == 2) return name + ": click a point on the curve.";
                    return name + ": click the end.";
                case Tool.Freehand:
                    return n < 0 ? Hints[Tool.Freehand] : "Freehand: release to finish.";
                case Tool.Marker:
                    return n < 0 ? Hints[Tool.Marker] : "Highlighter: release to finish.";
                case Tool.Coord:
                    return !cv.HasOrigin ? "Coordinates: click the origin (0,0)." : "Coordinates: click a point.";
                case Tool.Text:
                    return n < 0 ? Hints[Tool.Text] : "Text: release, then type.";
                default:
                    return Hints.TryGetValue(t, out h) ? h : "";
            }
        }
        static string ToolName(Tool t)
        {
            switch (t)
            {
                case Tool.Rect: return "Rectangle";
                case Tool.Ellipse: return "Ellipse";
                case Tool.Cloud: return "Revision cloud";
                case Tool.Markup: return "Text markup (drag over a line of text)";
                case Tool.Bubble: return "Speech bubble";
                case Tool.Ocr: return "Copy text";
                case Tool.Perimeter: return "Length / perimeter";
                case Tool.Cutout: return "Cut-out";
                case Tool.Polyline: return "Path length";
                case Tool.PolyDraw: return "Polyline";
                case Tool.Marker: return "Highlighter pen";
                case Tool.Radius: return "Radius / diameter";
                case Tool.Highlight: return "Focus";
                default: return t.ToString();
            }
        }

        /// <summary>What you can do with the selected shape, in a few words.</summary>
        static string SelectedHelp(Shape s)
        {
            switch (s.Kind)
            {
                case ShapeKind.Line: case ShapeKind.Arrow: case ShapeKind.Arc:
                    return "drag a point to change it. Right-click for line ends.";
                case ShapeKind.Dimension:
                    return "drag the yellow knob to move the line. Double-click to label.";
                case ShapeKind.Area: case ShapeKind.Perimeter:
                    return "drag a corner, or the yellow knob to move the label. Double-click to label.";
                case ShapeKind.Polygon: case ShapeKind.Polyline:
                    return "drag a corner. Double-click an edge to add one.";
                case ShapeKind.Radius: case ShapeKind.Angle:
                    return "drag a point to change it. Double-click to label.";
                case ShapeKind.Text:
                    return "double-click to edit. Handles resize the box.";
                case ShapeKind.Stamp: case ShapeKind.Note:
                    return "double-click to edit the text.";
                case ShapeKind.Cloud:
                    return s.Mode != 0 ? "drag the yellow knob to move the callout." : "handles resize it.";
                case ShapeKind.Coord:
                    return "drag the point. Right-click to make it the origin.";
                case ShapeKind.Origin:
                    return "drag it: every coordinate updates.";
                case ShapeKind.Counter:
                    return "double-click to name what this series counts.";
                case ShapeKind.Image:
                    return s.Text == "Mouse pointer" ? "drag to move it, or press Delete to remove it." : "drag a corner to resize it.";
                default:
                    return "drag to move it. Del to delete.";
            }
        }
        Shape StyleTarget()
        {
            if (cv.EditingShape != null) return cv.EditingShape;
            if (cv.Selected != null) return cv.Selected;
            if (cv.CurrentTool == Tool.Cutout) return null;
            Shape s;
            return cv.Styles.TryGetValue(cv.CurrentTool, out s) ? s : null;
        }

        static string KindName(Shape s)
        {
            switch (s.Kind)
            {
                case ShapeKind.Rect: return "Rectangle";
                case ShapeKind.Counter: return s.Mode == 1 ? "Letter marker" : "Number marker";
                case ShapeKind.Bubble: return "Speech bubble";
                case ShapeKind.Image: return "Picture";
                case ShapeKind.Polyline: return "Path length";
                case ShapeKind.Cloud: return "Revision cloud";
                case ShapeKind.Note: return "Note (not printed)";
                case ShapeKind.Markup: return "Text markup";
                case ShapeKind.Area: return !s.ShowsArea ? "Perimeter (closed)" : s.Holes != null ? "Area with cut-outs" : "Area";
                case ShapeKind.Perimeter: return s.IsOpenPath ? "Length (open)" : "Perimeter (closed)";
                case ShapeKind.Coord: return "Coordinate point";
                case ShapeKind.Origin: return "Origin 0,0";
                case ShapeKind.Highlight:
                    return s.Mode == (int)HighlightMode.Pixelate || s.Mode == (int)HighlightMode.PixelateOutside ? "Pixelate" : s.Mode == 0 ? "Text marker" : "Focus";
                case ShapeKind.Freehand: return s.IsMarkerPen ? "Highlighter pen" : "Freehand";
                case ShapeKind.Polygon: return s.IsOpenPath ? "Polyline (open polygon)" : "Polygon";
                case ShapeKind.Radius: return "Radius / diameter";
                default: return s.Kind.ToString();
            }
        }

        void LoadStyle()
        {
            Shape s = StyleTarget();
            loading = true;
            foreach (Control c in styleBar.Controls) c.Enabled = s != null || c == lStyleFor;
            if (s == null)
            {
                lStyleFor.Text = cv.CurrentTool == Tool.Cutout ? "Cut-out (uses the area's style)" : cv.CurrentTool == Tool.Ocr ? "Copy text (OCR)" : "Pick a tool or shape";
                rowHl.Visible = rowStrength.Visible = rowMarkStart.Visible = rowTextStyle.Visible = gHeads.Visible = rowStamp.Visible = rowMarkup.Visible = false;
                gDash.Visible = rowForm.Visible = rowCallout.Visible = rowClosed.Visible = rowFrame.Visible = rowShow.Visible = false;
            }
            else
            {
                string sel = cv.Sel.Count > 1 ? " (" + cv.Sel.Count + " selected)" : (cv.Selected != null ? " (selected)" : "");
                lStyleFor.Text = KindName(s) + sel;
                ShapeKind kd = s.Kind;
                bool lineOnly = kd == ShapeKind.Line || kd == ShapeKind.Arrow || kd == ShapeKind.Freehand || kd == ShapeKind.Dimension || kd == ShapeKind.Angle ||
                                kd == ShapeKind.Polyline || kd == ShapeKind.Perimeter || kd == ShapeKind.Arc || kd == ShapeKind.Radius ||
                                (kd == ShapeKind.Polygon && s.IsOpenPath);
                gFill.Visible = !lineOnly && kd != ShapeKind.Note;
                gLine.Visible = kd != ShapeKind.Note;
                gFont.Visible = s.HasTextStyle || kd == ShapeKind.Counter || kd == ShapeKind.Dimension || kd == ShapeKind.Area || kd == ShapeKind.Angle || kd == ShapeKind.Radius ||
                                kd == ShapeKind.Polyline || kd == ShapeKind.Perimeter || kd == ShapeKind.Note;
                gHeads.Visible = s.HasHeads;
                if (s.HasHeads)
                {
                    cHeadS.SelectedIndex = Math.Max(0, Math.Min(9, s.HeadStart));
                    cHeadE.SelectedIndex = Math.Max(0, Math.Min(9, s.HeadEnd));
                }
                rowStamp.Visible = kd == ShapeKind.Stamp;
                if (rowStamp.Visible) cStamp.SelectedIndex = Math.Max(0, Math.Min(Stamps.Names.Length - 1, s.Mode));
                rowMarkup.Visible = kd == ShapeKind.Markup;
                if (rowMarkup.Visible) cMarkup.SelectedIndex = Math.Max(0, Math.Min(2, s.Mode));
                gDash.Visible = s.HasOutlineStyle;
                if (gDash.Visible)
                {
                    // "Cloud" is offered only for closed shapes (rectangle, ellipse, polygon, area, closed perimeter)
                    bool cloudOk = s.CanBeCloud || s.Dash == (int)LineStyle.Cloud;
                    if (cloudOk && cDash.Items.Count < DashNames.Length) cDash.Items.Add(DashNames[(int)LineStyle.Cloud]);
                    if (!cloudOk && cDash.Items.Count == DashNames.Length) cDash.Items.RemoveAt((int)LineStyle.Cloud);
                    cDash.SelectedIndex = Math.Max(0, Math.Min(cDash.Items.Count - 1, s.Dash));
                }
                rowForm.Visible = kd == ShapeKind.Highlight;
                if (rowForm.Visible) cForm.SelectedIndex = Math.Max(0, Math.Min(4, s.Form));
                rowCallout.Visible = kd == ShapeKind.Cloud;
                if (rowCallout.Visible) cCallout.SelectedIndex = Math.Max(0, Math.Min(2, s.Mode));
                // length (W) and polygon: tick Closed to close the outline
                rowClosed.Visible = kd == ShapeKind.Perimeter || (kd == ShapeKind.Polygon && cv.CurrentTool != Tool.Cutout);
                rowShow.Visible = (kd == ShapeKind.Area && cv.CurrentTool != Tool.Cutout) || kd == ShapeKind.Radius;
                if (rowShow.Visible)
                {
                    string[] items = kd == ShapeKind.Radius ? new[] { "Radius + diameter", "Radius only", "Diameter only" }
                                                            : new[] { "Area + perimeter", "Area only", "Perimeter only" };
                    if ((string)cShow.Items[0] != items[0]) { cShow.Items.Clear(); cShow.Items.AddRange(items); }
                    cShow.SelectedIndex = Math.Max(0, Math.Min(2, s.Mode));
                }
                if (rowClosed.Visible) cClosed.Checked = s.Mode == 0;
                // text: the line width IS the frame (0 = no frame), so no separate Frame box
                rowFrame.Visible = false;
                tip.SetToolTip(nWidth, kd == ShapeKind.Text ? "Frame width – 0 = no frame" : "Line width");
                gShadow.Visible = s.CanShadow;
                SetColorButton(bStroke, s.Stroke);
                SetColorButton(bFill, s.Fill);
                nWidth.Value = Math.Max(nWidth.Minimum, Math.Min(nWidth.Maximum, (decimal)s.Width));
                tSO.Value = s.StrokeOpacity; lSO.Text = s.StrokeOpacity + "%";
                tFO.Value = s.FillOpacity; lFO.Text = s.FillOpacity + "%";
                cShadow.Checked = s.Shadow;
                cShadow.Enabled = s.CanShadow;
                nFont.Value = Math.Max(nFont.Minimum, Math.Min(nFont.Maximum, (decimal)s.FontSize));
                rowHl.Visible = s.Kind == ShapeKind.Highlight;
                if (rowHl.Visible)
                {
                    int b = FocusBase(s.Mode);
                    cHlMode.SelectedIndex = b >= 1 ? Math.Min(3, b - 1) : -1;   // an old text marker shows no mode
                    cWhere.Visible = b == (int)HighlightMode.Grayscale || b == (int)HighlightMode.Pixelate;
                    cWhere.SelectedIndex = s.Mode == (int)HighlightMode.Grayscale || s.Mode == (int)HighlightMode.PixelateOutside ? 1 : 0;
                }
                bool magnify = kd == ShapeKind.Highlight && s.Mode == (int)HighlightMode.Magnify;
                rowStrength.Visible = magnify || kd == ShapeKind.Pixelate ||
                                      (kd == ShapeKind.Highlight && FocusBase(s.Mode) == (int)HighlightMode.Pixelate);
                if (rowStrength.Visible) { strMagnify = magnify; tStr.Value = s.ShownStrength(magnify); ShowStrength(); }
                // pixelate / magnify draw no outline of their own width: the Line group only matters for magnify's frame
                if (rowStrength.Visible && !magnify) gLine.Visible = gFill.Visible = false;
                rowMarkStart.Visible = s.Kind == ShapeKind.Counter;
                rowTextStyle.Visible = s.HasTextStyle;
                cBold.Checked = s.Bold;
                cItalic.Checked = s.Italic;
                cUnder.Checked = s.Underline;
                cStrike.Checked = s.Strikeout;
                cAlignL.Checked = s.Align == 0; cAlignC.Checked = s.Align == 1; cAlignR.Checked = s.Align == 2;
            }
            LoadMarker();
            loading = false;
        }

        static void SetColorButton(Button b, Color c)
        {
            b.BackColor = c;
            b.FlatAppearance.MouseOverBackColor = c;
            b.FlatAppearance.BorderColor = Color.FromArgb(120, 120, 120);
            b.Text = "";
            b.AccessibleName = "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        void ApplyStyle(Action<Shape> f)
        {
            if (loading) return;
            Shape editing = cv.EditingShape;
            if (editing != null)
            {
                // typing in a text / bubble right now: style that shape and the tool default
                f(editing);
                Shape tmpl;
                if (cv.Styles.TryGetValue(Canvas.ToolFor(editing), out tmpl) && tmpl != editing) f(tmpl);
                cv.RefreshEditStyle();
            }
            else if (cv.Sel.Count > 0)
            {
                if ((DateTime.Now - lastStyleUndo).TotalMilliseconds > 800) cv.PushUndo(cv.Snapshot());
                lastStyleUndo = DateTime.Now;
                List<Shape> targets = new List<Shape>(cv.Sel);
                // markers: a style change applies to every marker of the same series (numbers or letters)
                foreach (Shape x in cv.Sel)
                    if (x.Kind == ShapeKind.Counter)
                        foreach (Shape m in cv.Shapes)
                            if (m.Kind == ShapeKind.Counter && m.Mode == x.Mode && !targets.Contains(m)) targets.Add(m);
                foreach (Shape x in targets) f(x);
                List<Tool> done = new List<Tool>();
                foreach (Shape x in cv.Sel)
                {
                    Tool tl = Canvas.ToolFor(x);
                    Shape tmpl;
                    if (!done.Contains(tl) && cv.Styles.TryGetValue(tl, out tmpl)) { f(tmpl); done.Add(tl); }
                }
                cv.NotifyDocChanged();
            }
            else
            {
                Shape tmpl;
                if (cv.Styles.TryGetValue(cv.CurrentTool, out tmpl)) f(tmpl);
            }
        }

        void PickColor(bool stroke)
        {
            Shape s = StyleTarget();
            if (s == null) { Flash("Pick a tool or select a shape first"); return; }
            using (ColorDialog d = new ColorDialog())
            {
                d.FullOpen = true;
                d.Color = stroke ? s.Stroke : s.Fill;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Color c = d.Color;
                if (stroke) ApplyStyle(x => x.Stroke = c);
                else ApplyStyle(x => { x.Fill = c; if (x.FillOpacity == 0) x.FillOpacity = 50; });
                LoadStyle();
            }
        }

        /// <summary>Which marker series the panel's 'Start at' box is for: the selected / current marker type.</summary>
        bool MarkerLetters
        {
            get
            {
                Shape s = StyleTarget();
                return s != null && s.Kind == ShapeKind.Counter && s.Mode == 1;
            }
        }

        void LoadMarker()
        {
            bool old = loading;
            loading = true;
            tMarkStart.Text = MarkerLetters ? cv.Scale.MarkerLabel(0, true) : cv.Scale.MarkerStart.ToString(CultureInfo.InvariantCulture);
            loading = old;
        }

        void MarkerEdited()
        {
            if (loading) return;
            bool letters = MarkerLetters;
            string t = tMarkStart.Text.Trim();
            int start = letters ? cv.Scale.LetterStart : cv.Scale.MarkerStart;
            if (letters)
            {
                int n = ScaleInfo.LetterNumber(t);       // A, Z, AA, AB …
                if (n > 0) start = n;
                start = Math.Max(1, Math.Min(18278, start));
            }
            else
            {
                int v;
                if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) start = Math.Max(0, v);
            }
            if (start != (letters ? cv.Scale.LetterStart : cv.Scale.MarkerStart))
            {
                cv.PushUndo(cv.Snapshot());
                if (letters) cv.Scale.LetterStart = start; else cv.Scale.MarkerStart = start;
                cv.NotifyDocChanged();
            }
            LoadMarker();
        }

        void LoadScale()
        {
            bool old = loading;
            loading = true;
            int i = Array.IndexOf(ScaleInfo.Units, cv.Scale.Unit);
            cUnit.SelectedIndex = i >= 0 ? i : ScaleInfo.Units.Length - 1;
            tUpp.Text = cv.Scale.Upp.ToString("G8", CultureInfo.CurrentCulture);
            nDec.Value = Math.Max(0, Math.Min(6, cv.Scale.Decimals));
            loading = old;
            lScale.Text = cv.Scale.Describe();
            lScale.ForeColor = cv.Scale.Calibrated ? SystemColors.ControlText : Color.DarkOrange;
        }

        void ScaleEdited()
        {
            if (loading) return;
            double v;
            if (!double.TryParse(tUpp.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v) &&
                !double.TryParse(tUpp.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) v = cv.Scale.Upp;
            if (v <= 0 || double.IsNaN(v) || double.IsInfinity(v)) v = cv.Scale.Upp;
            string unit = cUnit.SelectedItem as string ?? "px";
            int dec = (int)nDec.Value;
            if (Math.Abs(v - cv.Scale.Upp) > 1e-15 || unit != cv.Scale.Unit || dec != cv.Scale.Decimals)
            {
                cv.PushUndo(cv.Snapshot());
                cv.Scale.Upp = v; cv.Scale.Unit = unit; cv.Scale.Decimals = dec;
                cv.NotifyDocChanged();
            }
            LoadScale();
            SaveScaleDefaults();
        }

        void SaveScaleDefaults()
        {
            Settings.Set("Decimals", cv.Scale.Decimals.ToString(CultureInfo.InvariantCulture));
            if (cKeepScale != null && cKeepScale.Checked)
            {
                Settings.Set("Unit", cv.Scale.Unit);
                Settings.Set("Upp", cv.Scale.Upp.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        void RefreshMeasurements()
        {
            bool old = loading;
            loading = true;
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (MeasureRow r in Measure.Rows(cv.Shapes, cv.Scale, false))
            {
                string extra = string.IsNullOrEmpty(r.Label) ? (r.Extra ?? "") : r.Label + (string.IsNullOrEmpty(r.Extra) ? "" : " · " + r.Extra);
                string name = r.Shape != null ? r.Name : (r.Name == "Letters" ? "ΣA" : "Σ1");     // marker totals
                ListViewItem it = new ListViewItem(new string[] { name, r.Display, extra });
                it.ToolTipText = r.Shape == null ? r.Name + " markers – double-click to say what they count" : "Double-click to label";
                it.Tag = r.Shape;
                if (r.Shape == null) it.ForeColor = Color.FromArgb(0, 90, 170);
                if (r.Shape != null && r.Shape == cv.Selected) it.Selected = true;
                lv.Items.Add(it);
            }
            lv.EndUpdate();
            loading = old;
            LoadScale();
            RefreshTexts();
        }

        void SyncListSelection()
        {
            bool old = loading;
            loading = true;
            foreach (ListViewItem it in lv.Items) it.Selected = it.Tag == cv.Selected;
            if (lvText != null) foreach (ListViewItem it in lvText.Items) it.Selected = it.Tag == cv.Selected;
            loading = old;
        }

        void CopyMeasurements()
        {
            if (!App.TrySetClipboardText(Measure.ExcelText(cv.Shapes, cv.Scale))) { Flash(ClipboardBusy); return; }
            Flash("Measurements, counts and labels copied – paste into Excel");
        }

        void LabelSeries(bool letters)
        {
            Shape m = cv.Shapes.Find(x => x.Kind == ShapeKind.Counter && x.IsLetterMarker == letters);
            if (m != null) cv.EditLabel(m);
        }

        /// <summary>OCR: copies the text in 'area' of the picture (null = everything visible).</summary>
        void CopyText(Rectangle? area)
        {
            cv.EndTextEdit(true);
            Rectangle r = area.HasValue ? area.Value : (cv.CropRect.HasValue ? cv.CropRect.Value : new Rectangle(0, 0, cv.Image.Width, cv.Image.Height));
            try
            {
                if (!TextRecognition.Available)
                {
                    MessageBox.Show(this, "Windows has no text-recognition language installed.\n\nAdd one in Settings → Time & language → Language & region (e.g. English with “Optical character recognition”).",
                        "Copy text", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string t = TextRecognition.Read(cv.Image, r).Text;
                if (t.Trim().Length == 0) { Flash("No text found there"); return; }
                if (!App.TrySetClipboardText(t)) { Flash(ClipboardBusy); return; }
                string one = t.Replace("\r", "").Replace("\n", " · ");
                Flash("Text copied (" + t.Length + " characters): " + (one.Length > 80 ? one.Substring(0, 80) + "…" : one));
            }
            catch (Exception ex) { Flash("Text recognition failed: " + ex.Message); }
        }

        void ScalePresetDialog()
        {
            int iw = cv.CropRect.HasValue ? cv.CropRect.Value.Width : cv.Image.Width;
            ScaleInfo before = cv.Scale.Clone();
            if (Dialogs.ScalePreset(this, cv.Scale, iw, PaperMmPerPx))
            {
                ScaleInfo after = cv.Scale.Clone();
                cv.Scale.Upp = before.Upp; cv.Scale.Unit = before.Unit;
                cv.PushUndo(cv.Snapshot());
                cv.Scale.Upp = after.Upp; cv.Scale.Unit = after.Unit;
                cv.NotifyDocChanged();
                SaveScaleDefaults();
                Flash(cv.Scale.Describe());
            }
        }

        void UpdateZoom()
        {
            string z = Math.Round(cv.Zoom * 100) + "%";
            lZoom.Text = z;
            lZoomBar.Text = z;
        }

        void UpdatePos(PointF ip)
        {
            string t = "X " + Math.Floor(ip.X) + "  Y " + Math.Floor(ip.Y) + " px";
            if (cv.Scale.Calibrated)
                t += "   (" + (ip.X * cv.Scale.Upp).ToString("N" + cv.Scale.Decimals) + ", " + (ip.Y * cv.Scale.Upp).ToString("N" + cv.Scale.Decimals) + " " + cv.Scale.Unit + ")";
            lPos.Text = t;
        }

        void UpdateUndo()
        {
            bUndo.Enabled = cv.CanUndo;
            bRedo.Enabled = cv.CanRedo;
        }

        void Flash(string msg)
        {
            lHint.Text = msg;
            flashUntil = DateTime.Now.AddMilliseconds(3900);
            Timer t = new Timer();
            t.Interval = 4000;
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); if (!IsDisposed) SyncToolButtons(); };
            t.Start();
        }

        // ---------------- commands ----------------
        void NewCapture()
        {
            cv.EndTextEdit(true);
            Hide();
            App.Later(250, () =>
            {
                try { App.CaptureRegion(); }
                finally { if (!IsDisposed) Show(); }
            });
        }

        void OpenFile()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Images, PDF and projects|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.pdf;*.snapm|PDF drawing|*.pdf|SnapMeasure project|*.snapm|All files|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) App.OpenFile(d.FileName);
            }
        }

        void CopyImage()
        {
            cv.EndTextEdit(true);
            bool ok;
            using (Bitmap b = cv.Flatten(Color.White)) ok = App.TrySetClipboardImage(b);
            if (!ok) { Flash(ClipboardBusy); return; }
            dirty = false;
            Flash("Copied to clipboard");
        }

        const string ClipboardBusy = "The clipboard is busy (another program has it open). Try again.";

        protected override void Dispose(bool disposing)
        {
            if (disposing) tip.Dispose();
            try { base.Dispose(disposing); }
            catch (Exception ex) { CloseLog(ex); }     // a clean-up problem must never keep the editor on screen
        }

        void Pin()
        {
            cv.EndTextEdit(true);
            FormWindowState was = WindowState;
            PinForm pin = new PinForm(cv.Flatten(null));
            pin.Source = this;                            // closing the pin (Esc, Ctrl+P, double-click) brings this editor back
            pin.Show();       // a pin is not a save: closing the editor later still asks about unsaved work
            pin.SourceState = was == FormWindowState.Minimized ? FormWindowState.Normal : was;
            WindowState = FormWindowState.Minimized;      // the pin is what you want to see now; the editor waits on the task bar
        }

        /// <summary>Ctrl+S: picture in the chosen format (+ an editable .snapm project), named by the output settings.</summary>
        void QuickSave()
        {
            try
            {
                string dir = Settings.SaveFolder;
                Directory.CreateDirectory(dir);
                string ext = Output.Format;
                bool proj = Output.SaveProject;
                string name = Output.MakeName(Output.Pattern, captureTime, captureTitle, Output.Pattern.Contains("${n") ? Output.NextNumber() : 0);
                // saving the same editor again overwrites its own files instead of piling up copies
                string b = quickBase != null && Path.GetDirectoryName(quickBase) == dir ? quickBase : Output.UniqueBase(dir, name, ext, proj);
                SaveTo(b + "." + ext);
                if (proj) SaveTo(b + ".snapm");
                quickBase = b;
                if (Output.CopyPath) { try { Clipboard.SetText(b + "." + ext); } catch { } }
                Text = "SnapMeasure – " + Path.GetFileName(b + "." + ext);
                Flash("Saved " + Path.GetFileName(b) + "." + ext + (proj ? " + .snapm" : "") + " in " + dir + (Output.CopyPath ? "  (path copied)" : ""));
            }
            catch (Exception ex) { MessageBox.Show(this, "Could not save: " + ex.Message, "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        string quickBase;
        readonly DateTime captureTime = DateTime.Now;
        string captureTitle;

        bool SaveAs()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = Output.SaveFilter;
                d.FileName = lastSaved != null ? Path.GetFileNameWithoutExtension(lastSaved) : Output.MakeName(Output.Pattern, captureTime, captureTitle, 0);
                d.FilterIndex = Math.Max(1, Array.IndexOf(Output.Formats, Output.Format) + 1);
                try { Directory.CreateDirectory(Settings.SaveFolder); d.InitialDirectory = Settings.SaveFolder; } catch { }
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                try { SaveTo(d.FileName); return true; }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not save: " + ex.Message, "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }
        }

        void SaveTo(string path)
        {
            cv.EndTextEdit(true);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".snapm")
            {
                ProjectExtras ex = new ProjectExtras();
                ex.Crop = cv.CropRect;
                ex.HGuides.AddRange(cv.HGuides);
                ex.VGuides.AddRange(cv.VGuides);
                ProjectFile.Save(path, cv.Image, cv.Shapes, cv.Scale, ex);
            }
            else
            {
                // formats without transparency get a white background; the save itself is done safely (temp file + swap)
                bool opaque = ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".pdf";
                using (Bitmap b = cv.Flatten(opaque ? (Color?)Color.White : null)) Output.SaveBitmap(b, path);
            }
            lastSaved = path;
            dirty = false;
            Text = "SnapMeasure – " + Path.GetFileName(path);
            Flash("Saved: " + path);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Alt+1..9 = line colour, Alt+Shift+1..9 = fill colour from the quick palette
            Keys code = keyData & Keys.KeyCode, mods = keyData & Keys.Modifiers;
            if ((mods == Keys.Alt || mods == (Keys.Alt | Keys.Shift)) && code >= Keys.D1 && code <= Keys.D9)
            {
                SetPaletteColor(mods == Keys.Alt, code - Keys.D1);
                return true;
            }
            bool inText = ActiveControl is TextBoxBase || ActiveControl is NumericUpDown || cv.IsEditingText || textListEditing;
            switch (keyData)
            {
                case Keys.Control | Keys.Z: if (!inText) { cv.Undo(); return true; } break;
                case Keys.Control | Keys.Y:
                case Keys.Control | Keys.Shift | Keys.Z: if (!inText) { cv.Redo(); return true; } break;
                case Keys.Control | Keys.C:
                    if (!inText)
                    {
                        if (cv.Sel.Count > 0) { if (cv.CopySelection()) Flash(cv.Sel.Count + " shape(s) copied – Ctrl+V pastes them here or in another editor"); }
                        else CopyImage();
                        return true;
                    }
                    break;
                case Keys.Control | Keys.X: if (!inText) { cv.CutSelection(); return true; } break;
                case Keys.Control | Keys.V:
                    if (!inText)
                    {
                        // shapes first, then a picture onto this canvas (movable and scalable)
                        if (Canvas.ClipboardHasShapes && cv.PasteShapes(null)) return true;
                        if (Canvas.ClipboardHasImage && cv.PasteImage(null)) { Flash("Picture pasted – drag to move, corners to scale (Shift = free)"); return true; }
                        Flash("The clipboard has no picture or shapes to paste");
                        return true;
                    }
                    break;
                case Keys.Control | Keys.A: if (!inText) { cv.SelectAll(); return true; } break;
                case Keys.Control | Keys.D: if (!inText) { cv.DuplicateSelection(); return true; } break;
                case Keys.Control | Keys.Shift | Keys.OemCloseBrackets: if (!inText) { cv.Order(0); return true; } break;
                case Keys.Control | Keys.OemCloseBrackets: if (!inText) { cv.Order(1); return true; } break;
                case Keys.Control | Keys.OemOpenBrackets: if (!inText) { cv.Order(2); return true; } break;
                case Keys.Control | Keys.Shift | Keys.OemOpenBrackets: if (!inText) { cv.Order(3); return true; } break;
                case Keys.Control | Keys.R: ToggleRulers(); return true;
                case Keys.F4: TogglePanel(); return true;
                case Keys.Alt | Keys.L: if (!cv.IsEditingText) { PickColor(true); return true; } break;
                case Keys.Alt | Keys.F: if (!cv.IsEditingText) { PickColor(false); return true; } break;
                case Keys.Alt | Keys.Shift | Keys.D0:
                    if (StyleTarget() != null) { ApplyStyle(x => x.FillOpacity = 0); LoadStyle(); Flash("No fill"); }
                    return true;
                case Keys.Control | Keys.S: QuickSave(); return true;
                case Keys.Control | Keys.Shift | Keys.S: SaveAs(); return true;
                case Keys.Control | Keys.Shift | Keys.M: if (!inText) { ToggleMatch(); return true; } break;
                case Keys.Control | Keys.O: OpenFile(); return true;
                case Keys.Control | Keys.N: NewCapture(); return true;
                case Keys.Control | Keys.P: Pin(); return true;
                case Keys.Home: if (!inText) { cv.Fit(); return true; } break;
                case Keys.Control | Keys.D0: cv.Fit(); return true;
                case Keys.Control | Keys.D1: cv.ZoomCenter(1f); return true;
                case Keys.Control | Keys.Oemplus:
                case Keys.Control | Keys.Add: cv.ZoomStep(1); return true;
                case Keys.Control | Keys.OemMinus:
                case Keys.Control | Keys.Subtract: cv.ZoomStep(-1); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (closeConfirmed) { e.Cancel = false; base.OnFormClosing(e); e.Cancel = false; return; }
            try { cv.EndTextEdit(true); cv.EndMatch(); } catch (Exception ex) { CloseLog(ex); }
            if (dirty && cv.Shapes.Count > 0 && e.CloseReason == CloseReason.UserClosing)
            {
                // plain words on the buttons: "Cancel" used to be read as "close without saving"
                int r = Dialogs.Choose(this, "Close SnapMeasure editor", "You have annotations that are not saved yet.",
                                       "Save…", "Close without saving", "Keep editing");
                if (r == 2 || r < 0 || (r == 0 && !SaveAs())) { e.Cancel = true; return; }
            }
            // the choice is made: nothing may keep the window open any more
            closeConfirmed = true;
            e.Cancel = false;
            base.OnFormClosing(e);
            e.Cancel = false;
            // safety net: if the window is somehow still here a moment later, close it again without asking
            App.Later(400, () => { if (!IsDisposed && Visible) { try { Close(); } catch (Exception ex) { CloseLog(ex); } if (!IsDisposed) Dispose(); } });
        }

        bool closeConfirmed;

        static void CloseLog(Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "SnapMeasure-close.log"), DateTime.Now + "  " + ex + Environment.NewLine); } catch { }
        }
    }
}
