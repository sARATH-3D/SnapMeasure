using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
        float k;                         // UI scale of the monitor the window is on (1 = 100 %)
        ToolStrip bar, toolBar;
        ToolStripButton bUndo, bRedo;
        ToolStripLabel lZoomBar;
        StatusStrip status;
        ToolStripStatusLabel lPos, lZoom, lScale, lHint, lNote, lNoteX, lCount;
        System.Windows.Forms.Timer noteTimer;
        bool noteShowing;
        readonly Dictionary<Tool, ToolStripButton> toolButtons = new Dictionary<Tool, ToolStripButton>();
        Label lStyleFor, lSO, lFO;
        Button bStroke, bFill;
        NumericUpDown nWidth, nFont, nDec;
        TrackBar tSO, tFO;
        CheckBox cShadow, cKeepScale;
        ComboBox cWhere;
        ComboBox cUnit, cHlMode, cHeadS, cHeadE, cStamp, cMarkup, cDash, cForm, cCallout, cShow;
        CheckBox cClosed, cFrame, cCeil, cFloor, cReverse;
        TextBox tUpp, tMarkStart;
        Label lUppNote;
        Panel uppBox;

        /// <summary>After the "1 pixel =" box: its unit, and the readable size ("m  = 9.97 mm") when the panel is wide enough.</summary>
        void ShowUppNote()
        {
            if (lUppNote == null || uppBox == null) return;
            string unit = cv.Scale.Calibrated ? cv.Scale.Unit : "px", nice = cv.Scale.PixelSize();
            string full = !cv.Scale.Calibrated || nice.EndsWith(" " + unit) ? unit : unit + "  = " + nice;
            int room = uppBox.Width - lUppNote.Left;
            lUppNote.Text = TextRenderer.MeasureText(full, lUppNote.Font).Width <= room ? full : unit;
        }
        TrackBar tStr;
        Label lStr;
        Panel rowStrength, rowLabelAt;
        ComboBox cLabelAt;
        Panel rowHl, rowMarkStart, rowTextStyle, gLine, gFill, gFont, gHeads, gShadow, props, rowStamp, rowMarkup, gDash, rowForm, rowCallout, rowClosed, rowFrame, rowShow;
        public double? PaperMmPerPx;     // set when the picture is a PDF page: exact paper size per pixel
        FlowLayoutPanel styleBar;
        CheckBox cBold, cItalic, cUnder, cStrike, cAlignL, cAlignC, cAlignR;
        ComboBox cFontName, cAlign;      // text bar (like Greenshot): font, B, I, U, one alignment list
        ComboBox cVAlign;                // 1.4.2: top / middle / bottom inside a text box
        ToolStripButton bRulers;
        ToolStripButton bPanel;
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
            { Tool.Symbol, "Symbol: click where it goes, then type its character." },
            { Tool.Fill, "Fill: click inside a closed area to fill it." },
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
            { Tool.Cutout, "Cut-out: click the corners of the opening inside an area, or of the hollow inside a polygon." },
            { Tool.Cloud, "Cloud: drag around the change." },
            { Tool.Stamp, "Stamp: click where it goes." },
            { Tool.Note, "Note: click where it goes (never printed)." },
            { Tool.Markup, "Text markup: drag over a line of text." },
            { Tool.Ocr, "Copy text: drag over the text (a click = all)." },
        };

        /// <summary>How long each step of opening the last editor took (for the speed benchmark).</summary>
        public static string OpenTiming = "";
        System.Diagnostics.Stopwatch openWatch = System.Diagnostics.Stopwatch.StartNew();
        System.Text.StringBuilder openSteps = new System.Text.StringBuilder();
        void Step(string what) { openSteps.Append(what).Append(' ').Append(openWatch.ElapsedMilliseconds).Append("  "); }

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
            cv.Scale.Rounding = 0;          // Ceil / Floor always start unticked (a project keeps its own)
            cv.ShowRulers = Settings.GetBool("ShowRulers", true);
            StylePrefs.Apply(cv);           // Settings → Styles → Remember my styles
            cv.AskText = (t, init) => Dialogs.Ask(this, t, init, false);
            cv.AskNote = (t, init) => Dialogs.Ask(this, t, init, true);
            cv.AskSymbol = (t, init) => Dialogs.AskSymbol(this, t, init);
            cv.AskCalibration = px => Dialogs.Calibrate(this, px, cv.Scale);
            cv.Message = Flash;
            cv.OcrArea = r => CopyText(r);
            cv.FindWords = r =>
            {
                if (!TextRecognition.Available) return null;
                return TextRecognition.Read(cv.Image, r).Boxes;
            };

            Step("canvas");
            SuspendLayout();             // one layout pass for the whole window, not one per button added
            BuildTopBar(); Step("top");
            BuildToolBar(); Step("tools");
            BuildStyleBar(); Step("style");
            BuildProps(); Step("panel");
            BuildStatus(); Step("status");

            Controls.Add(cv);
            Controls.Add(props);
            Controls.Add(toolHost);
            Controls.Add(styleHost);
            Controls.Add(bar);
            Controls.Add(status);

            cv.DocChanged += (s, e) => { dirty = true; RefreshMeasurements(); UpdateUndo(); LoadMarker(); UpdateGuide(); };
            cv.SelectionChanged += (s, e) => { LoadStyle(); SyncListSelection(); UpdateGuide(); };
            cv.StateChanged += (s, e) => UpdateGuide();
            cv.ToolChanged += (s, e) => { SyncToolButtons(); LoadStyle(); };
            cv.MatchChanged += (s, e) => SyncToolButtons();
            cv.ViewChanged += (s, e) => { UpdateZoom(); if (bRulers != null) bRulers.Checked = cv.ShowRulers; if (!rescaling && cv.Size == viewSize) viewWhole = WholeVisible(); viewSize = cv.Size; };
            cv.CursorMoved = UpdatePos;

            Shown += (s, e) => { cv.Fit(); cv.Focus(); StartNoteTimer(); };
            // a widened panel never takes more than half the window
            Resize += (s, e) =>
            {
                if (props == null || WindowState == FormWindowState.Minimized || ClientSize.Width <= 0) return;
                int max = Math.Max(S(PanelMin), ClientSize.Width / 2);
                if (props.Width > max) props.Width = max;
            };
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

            Step("added");
            SyncToolButtons();
            LoadStyle();
            LoadScale();
            LoadMarker();
            RefreshMeasurements();
            UpdateUndo();
            ResumeLayout(false);
            Lang.Apply(this, tip);       // other languages: the finished window, all at once
            Theme.Apply(this);           // dark mode: recolour the finished window
            Step("filled");
            Shown +=(s, e) => { Step("shown"); BeginInvoke((Action)(() => { Step("painted"); OpenTiming = openSteps.ToString(); })); };
        }

        int S(float v) { return (int)Math.Round(v * k); }

        /// <summary>
        /// Moved to a monitor with other display scaling (e.g. a 150 % laptop and a 100 % monitor): take the size
        /// Windows suggests and rebuild the bars and the side panel at the new scale, so the tool icons, text and
        /// panel stay the right size instead of keeping the size of the monitor the window was opened on.
        /// </summary>
        /// While the window is dragged nothing is rebuilt or resized (that made dragging lag): the new size and the
        /// bars are applied once, together, when the mouse is let go. Moved by keys (Win+Shift+arrow) or by
        /// Windows, it happens straight away.
        protected override void WndProc(ref Message m)
        {
            const int WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
            if (m.Msg == Native.WM_DPICHANGED)
            {
                float nk = (m.WParam.ToInt64() & 0xFFFF) / 96f;
                Native.RECT r = (Native.RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                if (nk >= 0.5f)
                {
                    pendingK = nk;
                    pendingBounds = WindowState == FormWindowState.Normal ? r.ToRectangle() : (Rectangle?)null;
                    // never rebuild inside a rebuild: resizing the window during one can make Windows announce another
                    // change straight away (that left two sets of bars in the window)
                    if (!moving && !rescaling) BeginInvoke((Action)(() => ApplyPendingScale(false)));
                }
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_ENTERSIZEMOVE) moving = true;
            else if (m.Msg == WM_EXITSIZEMOVE)
            {
                moving = false;
                if (pendingK > 0) BeginInvoke((Action)(() => ApplyPendingScale(true)));
                else CheckScaleSoon();
            }
            base.WndProc(ref m);
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (!moving && !rescaling) CheckScaleSoon();
        }

        /// <summary>Safety net: shortly after the window settles, compare the scale Windows uses for it with the
        /// one the bars were made for, in case a change was missed.</summary>
        void CheckScaleSoon()
        {
            if (!IsHandleCreated) return;
            if (scaleCheck == null)
            {
                scaleCheck = new Timer();
                scaleCheck.Interval = 250;
                scaleCheck.Tick += (s, e) =>
                {
                    scaleCheck.Stop();
                    if (moving || rescaling || IsDisposed || pendingK > 0) return;
                    float now = Native.WindowScale(Handle);
                    if (now > 0 && Math.Abs(now - k) > 0.01f) { pendingK = now; ApplyPendingScale(true); }
                };
                Disposed += (s, e) => scaleCheck.Dispose();
            }
            scaleCheck.Stop();
            scaleCheck.Start();
        }

        Timer scaleCheck;

        bool moving, rescaling;
        float pendingK;
        Rectangle? pendingBounds;

        // was the whole picture on screen after the user's last zoom or pan? (view changes caused by the window being
        // resized don't count: Windows shrinks the window for a new monitor before the editor rescales)
        bool viewWhole = true;
        Size viewSize;

        /// <summary>Is the whole picture (or crop) on screen at the current zoom?</summary>
        bool WholeVisible()
        {
            RectangleF vr = cv.ViewRect;
            return vr.Width * cv.Zoom <= cv.Width - cv.RulerSize + 1 && vr.Height * cv.Zoom <= cv.Height - cv.RulerSize + 1;
        }

        void ApplyPendingScale(bool dragged)
        {
            if (rescaling || moving || IsDisposed) return;
            float nk = pendingK;
            Rectangle? nb = pendingBounds;
            pendingK = 0; pendingBounds = null;
            if (nk < 0.5f) return;
            rescaling = true;
            try
            {
                if (Math.Abs(nk - k) > 0.01f) ApplyScale(nk, dragged ? null : nb);
                else if (nb.HasValue && !dragged) Bounds = nb.Value;
            }
            finally { rescaling = false; }
            // a change Windows announced while we were rebuilding: only if the window really is on another monitor now
            if (pendingK > 0)
            {
                pendingK = 0; pendingBounds = null;
                float now = Native.WindowScale(Handle);
                if (now > 0 && Math.Abs(now - k) > 0.01f) { pendingK = now; BeginInvoke((Action)(() => ApplyPendingScale(true))); }
            }
        }

        /// <summary>Settings changed the tool icon size: rebuild the bars at the same scale.</summary>
        internal void RebuildBars()
        {
            if (rescaling || moving || IsDisposed) return;
            rescaling = true;
            try { ApplyScale(k, WindowState == FormWindowState.Normal ? Bounds : (Rectangle?)null); }
            finally { rescaling = false; }
        }

        void ApplyScale(float nk, Rectangle? newBounds)
        {
            float ratio = nk / k;
            k = nk;
            Rectangle target = Bounds;
            if (WindowState == FormWindowState.Normal)
            {
                // dragged: same top-left, size scaled for the new monitor, kept inside its work area
                target = newBounds.HasValue ? newBounds.Value
                       : new Rectangle(Left, Top, (int)Math.Round(Width * ratio), (int)Math.Round(Height * ratio));
                Rectangle wa = Screen.FromPoint(new Point(Left + S(40), Top + S(10))).WorkingArea;
                target.Width = Math.Min(target.Width, wa.Width); target.Height = Math.Min(target.Height, wa.Height);
                target.X = Math.Max(wa.Left, Math.Min(target.X, wa.Right - target.Width));
                target.Y = Math.Max(wa.Top, Math.Min(target.Y, wa.Bottom - target.Height));
            }
            // the view: was the whole picture on screen (then fit it again), else which spot was in the middle
            // (remembered from the last view change: Windows may already have shrunk the window for the new monitor)
            bool wholeVisible = viewWhole || WholeVisible();
            PointF midImg = cv.ToImg(new Point(cv.Width / 2, cv.Height / 2));
            bool panel = props.Visible;
            Control[] old = { props, toolHost, styleHost, bar, status };
            Native.SendMessage(Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);       // WM_SETREDRAW off: one clean repaint at the end
            SuspendLayout();
            try
            {
                foreach (Control c in old) { c.Visible = false; Controls.Remove(c); }
                toolButtons.Clear();
                MinimumSize = Size.Empty;
                if (WindowState == FormWindowState.Normal) Bounds = target;
                MinimumSize = new Size(S(780), S(520));
                Font = Native.UiFont("Segoe UI", 9f, k);
                cv.Dpi = k;
                BuildTopBar();
                BuildToolBar();
                BuildStyleBar();
                BuildProps();
                BuildStatus();
                props.Visible = panel;
                // fill the new bars and panel before they are on the window, so they are laid out only once
                SyncToolButtons();
                LoadMarker();
                RefreshMeasurements();     // also the scale and the texts list
                UpdateUndo();
                UpdateZoom();
                // the canvas stays first in the list, so it still fills what the docked bars leave (same order as at start)
                Controls.Add(props);
                Controls.Add(toolHost);
                Controls.Add(styleHost);
                Controls.Add(bar);
                Controls.Add(status);
                LoadStyle();               // shows the rows for the current tool: needs the bars on the window
                Theme.Apply(this);         // the rebuilt bars: dark mode
            }
            finally
            {
                ResumeLayout(true);
                FitToolBar();
                if (wholeVisible)
                {
                    cv.Fit();
                    // Windows can still resize the window after this (Win+Shift+arrow, snapping): fit again then
                    DateTime until = DateTime.Now.AddSeconds(1);
                    EventHandler refit = null;
                    refit = (s, e) => { if (DateTime.Now > until || IsDisposed) cv.SizeChanged -= refit; else if (cv.Width > 10 && cv.Height > 10) cv.Fit(); };
                    cv.SizeChanged += refit;
                    BeginInvoke((Action)(() => { if (!IsDisposed) cv.Fit(); }));
                }
                else
                {
                    cv.Pan = new PointF(cv.Width / 2f - midImg.X * cv.Zoom, cv.Height / 2f - midImg.Y * cv.Zoom);
                    cv.ZoomAt(cv.Zoom, new Point(cv.Width / 2, cv.Height / 2));    // same zoom, applies the limits
                }
                Native.SendMessage(Handle, 0x000B, (IntPtr)1, IntPtr.Zero);      // WM_SETREDRAW on
                Native.RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0485);   // RDW_INVALIDATE | ERASE | ALLCHILDREN | FRAME
            }
            cv.Focus();
            BeginInvoke((Action)(() => { foreach (Control c in old) c.Dispose(); }));   // after the repaint
        }

        internal Canvas CanvasControl { get { return cv; } }
        internal void RefreshAll() { LoadStyle(); LoadScale(); LoadMarker(); RefreshMeasurements(); SyncToolButtons(); UpdateZoom(); }

        public static EditorForm FromProject(string path)
        {
            List<Shape> shapes = new List<Shape>();
            ScaleInfo sc = new ScaleInfo();
            ProjectExtras ex;
            Bitmap img = ProjectFile.Load(path, shapes, sc, out ex);
            EditorForm f = new EditorForm(img, Path.GetFileName(path));
            f.projectPath = Path.GetFullPath(path);
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
            bar.SuspendLayout();
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
            MenuItem(bSave, "Save as…", "saveas", (s, e) => SaveAs()).ToolTipText = "Ctrl+Shift+S";
            MenuItem(bSave, "Open in Paint", "paint", (s, e) => OpenInPaint());
            MenuItem(bSave, "Print…", "print", (s, e) => PrintPicture());
            ToolStripSplitButton bCopy = Split("Copy", "Copy the marked-up picture (Ctrl+C with nothing selected) · ▾ = copy text or measurements", "copy", (s, e) => CopyImage());
            MenuItem(bCopy, "Copy picture", "copy", (s, e) => CopyImage());
            MenuItem(bCopy, "Copy text from the picture (OCR)", "ocr", (s, e) => CopyText(null));
            MenuItem(bCopy, "Copy all text", null, (s, e) => CopyAllText());
            MenuItem(bCopy, "Copy measurements (for Excel)", null, (s, e) => CopyMeasurements());
            bar.Items.Add(new ToolStripSeparator());
            bUndo = IconOnly(AddBtn(bar, "Undo", "Undo (Ctrl+Z)", (s, e) => cv.Undo()), "undo");
            bRedo = IconOnly(AddBtn(bar, "Redo", "Redo (Ctrl+Y)", (s, e) => cv.Redo()), "redo");
            IconOnly(AddBtn(bar, "Find", "Find and replace text (Ctrl+F)", (s, e) => ShowFind()), "find");     // edits text: with Undo/Redo, away from the zoom magnifiers
            bar.Items.Add(new ToolStripSeparator());

            ToolStripDropDownButton img = new ToolStripDropDownButton("Adjust");     // rotate, mirror, border, shadow, torn edge, grayscale, invert
            img.Image = Icons.Cmd("rotate", CmdIcon);
            img.ImageScaling = ToolStripItemImageScaling.None;
            img.AutoToolTip = false;       // a tooltip would cover the first menu items
            img.DropDown.Font = Font;
            MenuItem(img, "Rotate right 90°", "rotcw", (s, e) => cv.ImageOp("rotcw"));
            MenuItem(img, "Rotate left 90°", "rotccw", (s, e) => cv.ImageOp("rotccw"));
            MenuItem(img, "Mirror horizontally", "fliph", (s, e) => cv.ImageOp("fliph"));
            MenuItem(img, "Mirror vertically", "flipv", (s, e) => cv.ImageOp("flipv"));
            img.DropDownItems.Add(new ToolStripSeparator());
            MenuItem(img, "Border", "border", (s, e) => cv.ImageOp("border"));
            MenuItem(img, "Drop shadow", "shadow", (s, e) => cv.ImageOp("shadow"));
            MenuItem(img, "Torn edge", "torn", (s, e) => cv.ImageOp("torn"));
            // 1.4.2: menus show names only; the shortcut is in the tooltip
            MenuItem(img, "Extend canvas…", "extend", (s, e) => ExtendCanvas()).ToolTipText =
                Lang.T("Ctrl+Plus = 10 px more room · Ctrl+Minus = 10 px less (never cuts the picture)");
            MenuItem(img, "Remove background", "removebg", (s, e) => RemoveBackground()).ToolTipText =
                Lang.T("Make the plain background round the subject transparent (the colour at the picture's edges)");
            img.DropDownItems.Add(new ToolStripSeparator());
            MenuItem(img, "Grayscale", "gray", (s, e) => cv.ImageOp("gray")).ToolTipText = "Ctrl+G";
            MenuItem(img, "Invert colours", "invert", (s, e) => cv.ImageOp("invert")).ToolTipText = "Ctrl+I";
            img.DropDown.ShowItemToolTips = true;
            bSave.DropDown.ShowItemToolTips = true;
            bar.Items.Add(img);
            bar.Items.Add(new ToolStripSeparator());

            IconOnly(AddBtn(bar, "Zoom out", "Zoom out (Ctrl+mouse wheel)", (s, e) => cv.ZoomStep(-1)), "zoomout");
            lZoomBar = new ToolStripLabel("100%");
            bar.Items.Add(lZoomBar);
            IconOnly(AddBtn(bar, "Zoom in", "Zoom in (Ctrl+mouse wheel)", (s, e) => cv.ZoomStep(1)), "zoomin");
            Ic(AddBtn(bar, "Fit", "Fit and centre the image (Home, 0, or double-click the middle mouse button)", (s, e) => cv.Fit()), "fit");
            IconOnly(AddBtn(bar, "Zoom to area", "Zoom to area: drag a box around the part to see (Z; Esc cancels)", (s, e) => { cv.StartZoomBox(); cv.Focus(); }), "zoomarea");
            IconOnly(AddBtn(bar, "Actual pixels", "Actual pixels 1:1 – one screen pixel per picture pixel (Ctrl+1)", (s, e) => { cv.ZoomCenter(1f); cv.Focus(); }), "actual");

            // right end, next to the window's own buttons (the first added sits furthest right):
            // keep inside, rulers | add-ons | side panel, pin, settings, help
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

            // add-ons: tools that float over any window, not just this picture
            ToolStripDropDownButton addons = new ToolStripDropDownButton("Add-ons");
            addons.Image = Icons.Cmd("ruler", CmdIcon);
            addons.ImageScaling = ToolStripItemImageScaling.None;
            addons.AutoToolTip = false;      // no tooltip: it would cover the menu
            addons.DropDown.Font = Font;
            addons.Alignment = ToolStripItemAlignment.Right;
            MenuItem(addons, "On-screen ruler", "ruler", (s, e) => Overlays.ShowRuler()).ToolTipText = Lang.T("Measure anything on screen");
            MenuItem(addons, "On-screen protractor", "protractor", (s, e) => Overlays.ShowProtractor()).ToolTipText = Lang.T("Measure an angle on screen");
            bar.Items.Add(addons);
            ToolStripSeparator sepA = new ToolStripSeparator(); sepA.Alignment = ToolStripItemAlignment.Right;
            bar.Items.Add(sepA);

            // the two on/off switches for the view: rulers, and the lock (everything drawn, moved, pasted or
            // resized stays inside the picture)
            bRulers = IconOnly(AddBtn(bar, "Rulers", "Rulers along the top and left edge (Ctrl+R)", (s, e) => ToggleRulers()), "rulers");
            bRulers.CheckOnClick = false;
            bRulers.Checked = cv.ShowRulers;
            bRulers.Alignment = ToolStripItemAlignment.Right;
            bLock = IconOnly(AddBtn(bar, "Keep inside", "Keep inside the picture: drawing, moving, pasting and resizing stay within the screenshot (Ctrl+L)", (s, e) => ToggleLock()), "lock");
            bLock.CheckOnClick = false;
            cv.LockInside = Settings.GetBool("LockInside", false);
            bLock.Checked = cv.LockInside;
            bLock.Alignment = ToolStripItemAlignment.Right;
            bar.ResumeLayout(false);
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
            toolBar.SuspendLayout();
            toolIconPx = S(IconSize);      // the icons AddTool draws
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
            AddHeading("Measure", Icons.MeasureCol);
            AddTool(Tool.Calibrate, "Calibrate scale from a known length (K)");
            AddTool(Tool.Dimension, "Dimension (D)");
            AddTool(Tool.Perimeter, "Length: path along a run; tick Closed for a closed perimeter (W)");
            AddTool(Tool.Area, "Area / perimeter: click the corners, show area, perimeter or both (M; Q = perimeter only)");
            AddTool(Tool.Cutout, "Cut-out: subtract an opening from an area, or make a hollow in a polygon (X)");
            AddTool(Tool.Angle, "Angle – 3 points: arm, vertex, arm (G)");
            AddTool(Tool.Radius, "Radius / diameter – 3 points on a curve or circle (Shift+R)");
            AddTool(Tool.Coord, "Coordinates: first click = origin 0,0 (double-click it to give it other coordinates), then points show X / Y (I)");
            AddHeading("Count", Icons.CountCol);
            AddTool(Tool.Counter, "Number marker 1, 2, 3 … – counts (N)");
            AddTool(Tool.Letter, "Letter marker A, B, C … – counts (Shift+N)");
            AddTool(Tool.Symbol, "Symbol marker (Shift+M) — type any character, like Ø, ±, ★ or a fixed 3. It doesn't count up.");
            AddHeading("Draw", Icons.DrawCol);
            AddTool(Tool.Arrow, "Arrow (A) – right-click it for more line ends");
            AddTool(Tool.Line, "Line (L)");
            AddTool(Tool.PolyDraw, "Polyline – an open line through any number of points, arrowheads optional (Shift+P)");
            AddTool(Tool.Arc, "Arc – 3 points: start, a point on the arc, end (Shift+L)");
            AddTool(Tool.Freehand, "Freehand, with arrowheads if you like (F)");
            AddTool(Tool.Rect, "Rectangle (R)");
            AddTool(Tool.Ellipse, "Ellipse / circle (E)");
            AddTool(Tool.Polygon, "Polygon – any number of edges (P)");
            AddTool(Tool.Cloud, "Revision cloud, with or without a callout (U)");
            AddTool(Tool.Fill, "Fill: click inside a closed area to fill it, like a paint bucket – lines, shapes and sketches bound it, small gaps are closed (Shift+F)");
            AddHeading("Highlight", Icons.HighlightCol);
            AddTool(Tool.Highlight, "Focus: highlight box, spotlight, grayscale, magnify or pixelate, in several shapes; Strength slider for magnify / pixelate (H, B = pixelate)");
            AddTool(Tool.Marker, "Highlighter pen: drag over text like a marker; Shift = straight stroke (Shift+H)");
            AddHeading("Text", Icons.TextCol);
            AddTool(Tool.Text, "Text – click and type, or drag a box (T)");
            AddTool(Tool.Bubble, "Speech bubble (S)");
            AddTool(Tool.Stamp, "Stamp: Approved / Revise / Draft / For construction … (Shift+Z)");
            AddTool(Tool.Note, "Note – shown here only, not printed (O)");
            AddHeading("Tools", Icons.ToolInk);
            AddTool(Tool.Crop, "Crop (C)");
            AddTool(Tool.Ocr, "Copy text from an area – text recognition (Shift+T)");
            // match properties: not a drawing tool, a brush that stays on until Esc / right-click
            bMatch = new ToolStripButton();
            bMatch.Image = Icons.Cmd("match", S(IconSize));
            bMatch.ImageScaling = ToolStripItemImageScaling.None;
            bMatch.DisplayStyle = ToolStripItemDisplayStyle.Image;
            bMatch.AutoSize = false;
            bMatch.Padding = Padding.Empty;
            bMatch.Size = new Size(S(IconSize + 24), ToolButtonH);
            bMatch.ToolTipText = Lang.T("Match properties: select a shape, then click the shapes to give its look (Ctrl+Shift+M; Esc or right-click ends)");
            bMatch.Margin = new Padding(0, S(1), 0, S(1));
            bMatch.Click += (s, e) => { ToggleMatch(); cv.Focus(); };
            toolBar.Items.Add(bMatch);
            toolBar.ResumeLayout(false);

            // the column sits in a holder that scrolls (mouse wheel) when even the compact column is too tall
            toolHost = new Panel();
            toolHost.Dock = DockStyle.Left;
            toolHost.AutoScroll = true;
            toolHost.BackColor = SystemColors.Control;
            toolHost.Width = toolBar.Width;
            toolBar.Dock = DockStyle.None;
            toolBar.Location = Point.Empty;
            toolHost.Controls.Add(toolBar);
            toolHost.SizeChanged += (s, e) => FitToolBar();
            toolBar.MouseWheel += (s, e) =>
            {
                if (!toolHost.VerticalScroll.Visible) return;
                int y = Math.Max(0, Math.Min(toolHost.VerticalScroll.Maximum, -toolHost.AutoScrollPosition.Y - e.Delta / 120 * S(IconSize + 5) * 2));
                toolHost.AutoScrollPosition = new Point(0, y);
                HandledMouseEventArgs he = e as HandledMouseEventArgs;
                if (he != null) he.Handled = true;    // not passed on to the holder as well
            };
        }

        Panel toolHost;
        bool fittingToolBar;

        /// <summary>Always one column. A short window or a large display scale first gets the compact column (the group
        /// titles become thin dividers, the buttons a little closer), then smaller icons; only if even the smallest do
        /// not fit does the column scroll with the mouse wheel. No tool is ever hidden.</summary>
        void FitToolBar()
        {
            if (fittingToolBar || toolBar == null || toolHost == null) return;
            fittingToolBar = true;
            try
            {
                int avail = toolHost.Height;
                if (avail <= 0) return;
                toolBar.SuspendLayout();
                SetToolBarIcons(S(IconSize));
                SetToolBarCompact(false);
                if (ToolBarNeed() > avail) SetToolBarCompact(true);
                int need = ToolBarNeed();
                foreach (float f in new[] { 0.85f, 0.72f, 0.6f })
                {
                    if (need <= avail) break;
                    SetToolBarIcons((int)Math.Round(S(IconSize) * f));
                    SetToolBarCompact(true);
                    need = ToolBarNeed();
                }
                bool scroll = need > avail;
                int w = S(IconSize + 32);
                toolBar.Width = w;
                toolBar.Height = scroll ? need : avail;
                toolBar.ResumeLayout(true);
                // scroll bars only when needed: a bar exactly as big as its holder could otherwise still get
                // both (one makes room for the other)
                if (!scroll) toolHost.AutoScrollPosition = Point.Empty;
                toolHost.AutoScroll = scroll;
                toolHost.Width = w + (scroll ? SystemInformation.VerticalScrollBarWidth : 0);
                // the real scroll bar can be wider than SystemInformation says (high display scale): make room for
                // it, or a sideways scroll bar appears as well
                if (scroll && toolHost.ClientSize.Width < w) toolHost.Width += w - toolHost.ClientSize.Width;
            }
            finally { fittingToolBar = false; }
        }

        int ToolBarNeed()
        {
            int need = toolBar.Padding.Vertical + S(2);
            foreach (ToolStripItem it in toolBar.Items) if (it.Available) need += it.Height + it.Margin.Vertical;
            return need;
        }

        /// <summary>Pixel size of the tool icons now (smaller when the window is short).</summary>
        int toolIconPx;
        readonly Dictionary<string, Image> toolIconCache = new Dictionary<string, Image>();

        /// <summary>Redraws the tool icons at 'px' pixels (sharp, not scaled bitmaps); kept per size.</summary>
        void SetToolBarIcons(int px)
        {
            if (px == toolIconPx) return;
            toolIconPx = px;
            foreach (KeyValuePair<Tool, ToolStripButton> kv in toolButtons)
            {
                string key = kv.Key + "|" + px;
                Image im;
                if (!toolIconCache.TryGetValue(key, out im)) { im = Icons.Make(kv.Key, px); toolIconCache[key] = im; }
                kv.Value.Image = im;
            }
            if (bMatch != null)
            {
                string key = "match|" + px;
                Image im;
                if (!toolIconCache.TryGetValue(key, out im)) { im = Icons.Cmd("match", px); toolIconCache[key] = im; }
                bMatch.Image = im;
            }
        }

        void SetToolBarCompact(bool on)
        {
            foreach (ToolStripItem it in toolBar.Items)
            {
                if (it is ToolStripLabel)
                {
                    // the group title becomes a thin divider line
                    if (it.Tag == null) it.Tag = it.Text;
                    it.Text = on ? "" : (string)it.Tag;
                    it.Height = on ? S(7) : S(13);
                    it.Margin = new Padding(0, on ? S(1) : S(4), 0, 0);
                }
                else
                {
                    // never shorter than the icon plus the button's border, or the icon gets clipped (looked squashed)
                    it.Height = on ? ToolButtonH - S(1) : ToolButtonH;
                    it.Margin = on ? new Padding(0) : new Padding(0, S(1), 0, S(1));
                }
            }
        }

        ToolStripButton bMatch, bLock;

        void ToggleLock()
        {
            // for this picture only; whether new editors start locked is set in Settings → Editor
            cv.SetLock(!cv.LockInside);
            bLock.Checked = cv.LockInside;
            Flash(cv.LockInside ? "Locked: work stays inside the picture; anything outside is hidden until you unlock" : "Unlocked: shapes may go outside the picture");
            cv.Focus();
        }
        void ToggleMatch()
        {
            if (cv.MatchActive) { cv.EndMatch(); return; }
            if (cv.Selected == null || cv.Sel.Count != 1) { Flash("Match properties: first select the shape whose look you want to copy."); return; }
            cv.StartMatch();
        }

        /// <summary>Small section title in the tool column, in the colour its tools' icons share.</summary>
        void AddHeading(string text, Color group)
        {
            ToolStripLabel l = new ToolStripLabel(text);
            l.Font = Native.UiFont("Segoe UI", 6.4f, k, FontStyle.Bold);
            l.ForeColor = Color.FromArgb(110, 120, 135);     // quiet grey: the icons carry the group colour
            l.AutoSize = false;
            l.Size = new Size(S(IconSize + 28), S(13));
            l.TextAlign = ContentAlignment.BottomCenter;
            l.Margin = new Padding(0, S(4), 0, 0);
            l.ToolTipText = text;
            // compact column: no title, a short thin grey divider instead
            l.Paint += (s, e) =>
            {
                if (l.Text.Length > 0) return;
                int y = l.Height / 2;
                using (Pen p = new Pen(Color.FromArgb(185, 192, 202)))
                    e.Graphics.DrawLine(p, S(8), y, l.Width - S(8), y);
            };
            toolBar.Items.Add(l);
        }

        /// <summary>Tool button height: the icon, 2 px of border above and below, and 1 px of air (whole pixels at any scale).</summary>
        int ToolButtonH { get { return (toolIconPx > 0 ? toolIconPx : S(IconSize)) + 2 * Math.Max(3, S(3)); } }

        void AddTool(Tool t, string tip)
        {
            ToolStripButton b = new ToolStripButton();
            b.Image = Icons.Make(t, S(IconSize));
            b.ImageScaling = ToolStripItemImageScaling.None;
            b.DisplayStyle = ToolStripItemDisplayStyle.Image;
            b.AutoSize = false;
            b.Padding = Padding.Empty;
            b.Size = new Size(S(IconSize + 24), ToolButtonH);
            b.ToolTipText = tip;
            b.Margin = new Padding(0, S(1), 0, S(1));
            b.Click += (s, e) => { cv.CurrentTool = t; cv.Focus(); };
            // the tool in use: a bar in its group's colour along the column's edge
            Color group = Icons.Group(t);
            b.Paint += (s, e) =>
            {
                if (!b.Checked) return;
                using (SolidBrush br = new SolidBrush(group))
                    e.Graphics.FillRectangle(br, 0, S(3), Math.Max(3, S(3)), b.Height - 2 * S(3));
            };
            toolButtons[t] = b;
            toolBar.Items.Add(b);
        }

        /// <summary>Text colour of headings and the current tool's name (near-black, softer than pure black on grey).</summary>
        static readonly Color Ink = Color.FromArgb(36, 40, 48);

        /// <summary>Default outline of a magnify lens.</summary>
        static readonly Color MagnifyOutline = Color.FromArgb(230, 0, 200);

        Label Header(string text)
        {
            Label l = new Label();
            l.UseMnemonic = false;     // show "&" as a character
            l.Text = text;
            // dark and heavier, in sentence case: blue is kept for what can be clicked
            l.Font = Native.UiFont("Segoe UI", 9.5f, k, FontStyle.Bold);
            l.ForeColor = Ink;
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
            LazyItemHeight(c, S(20));    // (setting ItemHeight here made the window early: ~50 ms per list)
            c.Width = S(64);
            c.DropDownWidth = S(150);
            c.Items.AddRange(Icons.HeadNames);
            c.DrawItem += (s, e) =>
            {
                e.DrawBackground();
                if (e.Index < 0) return;
                bool inList = (e.State & DrawItemState.ComboBoxEdit) == 0;
                int y = e.Bounds.Y + (e.Bounds.Height - S(18)) / 2;
                // wide line picture so the end style is easy to see
                using (Bitmap wide = Icons.HeadPreviewWide(Icons.HeadMenu[e.Index], !start, S(40), S(18)))
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
            LazyItemHeight(c, S(20));
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
                Color col = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : (Theme.Dark ? Theme.Ink : Color.FromArgb(40, 40, 45));
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
                if (m != 0) x.Angle = 0f;          // a cloud with a callout stays level
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

        /// <summary>The font list of the text bar. Only the font in use is in the list until it is opened (filling it
        /// with every installed font takes time a new editor shouldn't spend).</summary>
        ComboBox FontCombo()
        {
            ComboBox c = Combo(132, Renderer.DefaultFont);
            c.SelectedIndex = 0;
            c.MaxDropDownItems = 20;
            c.DropDownWidth = S(200);
            bool filled = false;
            c.DropDown += (s, e) =>
            {
                if (filled) return;
                filled = true;
                string cur = c.SelectedItem as string;
                bool was = loading; loading = true;
                try
                {
                    c.BeginUpdate();
                    c.Items.Clear();
                    List<string> names = new List<string>();
                    foreach (FontFamily ff in FontFamily.Families) if (ff.IsStyleAvailable(FontStyle.Regular)) names.Add(ff.Name);
                    names.Sort(StringComparer.OrdinalIgnoreCase);
                    c.Items.AddRange(names.ToArray());
                    int i = cur == null ? -1 : c.Items.IndexOf(cur);
                    c.SelectedIndex = i >= 0 ? i : Math.Max(0, c.Items.IndexOf(Renderer.DefaultFont));
                }
                catch (Exception) { }
                finally { c.EndUpdate(); loading = was; }
            };
            c.SelectedIndexChanged += (s, e) =>
            {
                string name = c.SelectedItem as string;
                if (loading || string.IsNullOrEmpty(name)) return;
                string v = name == Renderer.DefaultFont ? null : name;
                ApplyStyle(x => { if (x.HasTextStyle) { x.FontName = v; x.Cache = null; } });
            };
            tip.SetToolTip(c, Lang.T("Font"));
            return c;
        }

        /// <summary>Shows a font in the font list (adds it when the list isn't filled yet).</summary>
        void ShowFontName(string name)
        {
            if (cFontName == null) return;
            int i = cFontName.Items.IndexOf(name);
            if (i < 0) i = cFontName.Items.Add(name);
            cFontName.SelectedIndex = i;
        }

        ComboBox AlignCombo()
        {
            ComboBox c = Combo(96, "Left", "Centre", "Right");
            LazyItemHeight(c, S(20));
            c.DrawItem += (s, e) =>
            {
                e.DrawBackground();
                if (e.Index < 0) return;
                using (Image im = Icons.Cmd(e.Index == 0 ? "alignl" : e.Index == 1 ? "alignc" : "alignr", S(16)))
                    e.Graphics.DrawImage(im, e.Bounds.X + S(3), e.Bounds.Y + (e.Bounds.Height - im.Height) / 2);
                Color fc = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : c.ForeColor;
                TextRenderer.DrawText(e.Graphics, Lang.T((string)c.Items[e.Index]), c.Font, new Rectangle(e.Bounds.X + S(23), e.Bounds.Y, e.Bounds.Width - S(23), e.Bounds.Height), fc,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
            c.SelectedIndexChanged += (s, e) =>
            {
                int a = c.SelectedIndex;
                if (loading || a < 0) return;
                ApplyStyle(x => x.Align = a);
                LoadStyle();
            };
            tip.SetToolTip(c, Lang.T("Text alignment"));
            return c;
        }

        ComboBox Combo(int width, params string[] items)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Items.AddRange(items);
            c.Width = S(width);
            return c;
        }

        /// <summary>
        /// Picture drop-downs (line ends, line style). Setting ItemHeight straight away made the list's window at once, and
        /// filling a live list costs a few ms per item: ~120 ms per editor for lists that are hidden until a line tool is
        /// picked. The height is set when the window is made, so nothing is built until the row is first shown.
        /// </summary>
        void LazyItemHeight(ComboBox c, int itemH)
        {
            c.DrawMode = DrawMode.OwnerDrawFixed;
            c.HandleCreated += (s, e) =>
            {
                if (c.ItemHeight == itemH) return;
                c.ItemHeight = itemH;
                // the box grows natively, without telling its group: lay the group (and the style bar) out again,
                // or the bottom of the box is cut off
                if (IsHandleCreated) BeginInvoke((Action)FitStyleBar);
            };
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
                l.ForeColor = Theme.Dark ? Theme.Label : Color.FromArgb(80, 80, 80);      // dark mode: white, like the other text on the bar
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
            if (FillFirst(StyleTarget())) stroke = false;     // the Fill tool and its fills have no line: any click colours the fill
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
            styleBar.SuspendLayout();
            // always one row: what does not fit goes behind the » button at its end (FitStyleBar)
            styleBar.AutoSize = false;
            styleBar.WrapContents = false;
            styleBar.Padding = new Padding(S(6), S(3), S(6), S(3));
            styleBar.BackColor = Color.FromArgb(246, 247, 249);

            lStyleFor = new Label();
            lStyleFor.Font = Native.UiFont("Segoe UI", 9f, k, FontStyle.Bold);
            lStyleFor.ForeColor = Ink;
            lStyleFor.Margin = new Padding(0, S(6), S(12), 0);
            // one fixed width whatever it says ("Pick a tool or shape", "Rectangle (selected)" …), so the groups
            // after it never move; a longer name ends in "…" and shows whole on hover
            lStyleFor.AutoSize = false;
            lStyleFor.AutoEllipsis = true;
            lStyleFor.Size = new Size(S(180), lStyleFor.PreferredHeight);

            bStroke = ColorButton((s, e) => PickColor(true));
            bFill = ColorButton((s, e) => PickColor(false));
            tip.SetToolTip(bStroke, Lang.T("Line colour (Alt+L) · quick colours: Alt+1 … Alt+9"));
            tip.SetToolTip(bFill, Lang.T("Fill colour (Alt+F) · quick colours: Alt+Shift+1 … 9 · Alt+Shift+0 = no fill"));
            nWidth = new NumericUpDown();
            nWidth.DecimalPlaces = 1; nWidth.Increment = 0.5m; nWidth.Minimum = 0; nWidth.Maximum = 200; nWidth.Width = S(52);
            nWidth.ValueChanged += (s, e) => ApplyStyle(x => x.Width = (float)nWidth.Value);
            tip.SetToolTip(nWidth, Lang.T("Line width"));
            tSO = Slider(out lSO, (s, e) => { lSO.Text = tSO.Value + "%"; ApplyStyle(x => x.StrokeOpacity = tSO.Value); });
            tFO = Slider(out lFO, (s, e) => { lFO.Text = tFO.Value + "%"; ApplyStyle(x => x.FillOpacity = tFO.Value); });
            tip.SetToolTip(tSO, Lang.T("Line opacity"));
            tip.SetToolTip(tFO, Lang.T("Fill opacity (for pictures: picture opacity)"));

            // Focus modes: list index = HighlightMode (0 = Highlight, the see-through box; the default)
            cHlMode = Combo(110, "Highlight", "Spotlight", "Grayscale", "Magnify", "Pixelate", "Blur");    // list position: see HlListToMode
            tip.SetToolTip(cHlMode, Lang.T("Highlight = see-through box in the fill colour · Spotlight, Grayscale, Magnify, Pixelate, Blur · Cut-out tool (X) cuts holes in a focus"));
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
            tip.SetToolTip(tStr, Lang.T("Strength: how much to magnify, or how big the pixel blocks are"));
            cWhere = Combo(80, "Inside", "Outside");
            tip.SetToolTip(cWhere, Lang.T("Gray or pixelate the marked area itself (inside) or everything around it (outside)"));
            EventHandler focusChanged = (s, e) =>
            {
                if (loading || cHlMode.SelectedIndex < 0) return;
                int b = HlListToMode(cHlMode.SelectedIndex);
                bool inside = cWhere.SelectedIndex != 1;
                ApplyStyle(x =>
                {
                    // keep each mode's usual side when switching mode; the Inside / Outside box picks the other side
                    int m = FocusMode(b, s == cWhere ? inside : (b == (int)HighlightMode.Grayscale ? false : true));
                    // switching to magnify: the lens gets a magenta outline (the line colour can be changed after)
                    if (m == (int)HighlightMode.Magnify && x.Mode != m) { x.Stroke = MagnifyOutline; x.Width = Math.Max(x.Width, 2f); }
                    x.Mode = m;
                    if ((m == (int)HighlightMode.Spotlight || m == (int)HighlightMode.Marker) && x.FillOpacity < 20) x.FillOpacity = 45;
                });
                LoadStyle();
            };
            cHlMode.SelectedIndexChanged += focusChanged;
            cWhere.SelectedIndexChanged += focusChanged;
            cShadow = new CheckBox();
            cShadow.Text = Lang.T("Shadow");
            cShadow.AutoSize = true;
            cShadow.CheckedChanged += (s, e) => ApplyStyle(x => x.Shadow = cShadow.Checked);
            nFont = new NumericUpDown();
            nFont.Minimum = 6; nFont.Maximum = 200; nFont.Width = S(52);
            nFont.ValueChanged += (s, e) => ApplyStyle(x => x.FontSize = (float)nFont.Value);
            tip.SetToolTip(nFont, Lang.T("Text size"));
            cHeadS = HeadCombo(true);
            cHeadE = HeadCombo(false);
            cHeadS.SelectedIndexChanged += (s, e) => { int h = cHeadS.SelectedIndex; if (h >= 0) { int st = Icons.HeadMenu[h]; ApplyStyle(x => { if (x.HasHeads) x.HeadStart = st; }); } };
            cHeadE.SelectedIndexChanged += (s, e) => { int h = cHeadE.SelectedIndex; if (h >= 0) { int st = Icons.HeadMenu[h]; ApplyStyle(x => { if (x.HasHeads) x.HeadEnd = st; }); } };
            tip.SetToolTip(cHeadS, Lang.T("Line end at the start point (also on the right-click menu)"));
            tip.SetToolTip(cHeadE, Lang.T("Line end at the end point (also on the right-click menu)"));
            cStamp = Combo(130, Stamps.Names);
            cStamp.SelectedIndexChanged += (s, e) => StampModeChanged();
            tip.SetToolTip(cStamp, Lang.T("Stamp type – sets the word and colour (double-click a stamp to edit its text)"));
            cMarkup = Combo(110, "Underline", "Strike out", "Highlight text");
            cMarkup.SelectedIndexChanged += (s, e) => { int m = cMarkup.SelectedIndex; if (m >= 0) { ApplyStyle(x => { if (x.Kind == ShapeKind.Markup) x.Mode = m; }); LoadStyle(); } };
            cDash = DashCombo();
            cDash.SelectedIndexChanged += (s, e) => { int m = cDash.SelectedIndex; if (m >= 0) ApplyStyle(x => { if (x.HasOutlineStyle && (m != (int)LineStyle.Cloud || x.CanBeCloud)) x.Dash = m; }); };
            tip.SetToolTip(cDash, Lang.T("Line style: solid, dashed, dotted, dash-dot, long dash or cloud"));
            cForm = Combo(96, "Rectangle", "Ellipse", "Triangle", "Diamond", "Rounded", "Perimeter");
            cForm.SelectedIndexChanged += (s, e) => { int m = cForm.SelectedIndex; if (m >= 0) ApplyStyle(x => { if (x.Kind == ShapeKind.Highlight) SetFocusForm(x, m); }); };
            tip.SetToolTip(cForm, Lang.T("Shape of the focus · Perimeter = click its corners like an area (double-click or right-click to finish)"));
            cCallout = Combo(130, "No callout", "Revision triangle", "Text box");
            cCallout.SelectedIndexChanged += (s, e) => CalloutChanged();
            tip.SetToolTip(cCallout, Lang.T("Callout on a leader: a revision triangle with a number, or a text box (double-click to edit the text)"));
            cShow = Combo(150, "Area + perimeter", "Area only", "Perimeter only", "None");
            cShow.SelectedIndexChanged += (s, e) =>
            {
                int i = cShow.SelectedIndex;
                if (loading || i < 0) return;
                // the list holds either the area choices or the radius choices: each kind only takes its own
                bool radiusList = (string)cShow.Items[0] == RadiusShow[0];
                int m = radiusList ? RadiusShowMode[Math.Min(i, RadiusShowMode.Length - 1)] : i;
                ApplyStyle(x =>
                {
                    if (radiusList && x.Kind == ShapeKind.Radius) x.Mode = m;
                    else if (!radiusList && x.Kind == ShapeKind.Area) x.Mode = Math.Min(m, 3);
                });
            };
            tip.SetToolTip(cShow, Lang.T("What the label shows"));
            cClosed = new CheckBox();
            cClosed.Text = Lang.T("Closed");
            cClosed.AutoSize = true;
            cClosed.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                bool c = cClosed.Checked;
                // an open length made by the Area tool in 1.3.0: ticking Closed gives the area back
                if (c && cv.Sel.Count > 0) cv.SetAreaClosed(true);
                // perimeter: closed = perimeter, open = length · polygon: closed = polygon, open = polyline
                if (cv.Sel.Count == 0 || cv.Sel.Exists(x => x.Kind == ShapeKind.Polygon || (x.Kind == ShapeKind.Perimeter && x.FromArea == 0)))
                    ApplyStyle(x => { if (x.Kind == ShapeKind.Polygon || (x.Kind == ShapeKind.Perimeter && x.FromArea == 0)) x.Mode = c ? 0 : 1; });
                LoadStyle();
            };
            cFrame = new CheckBox();
            cFrame.Text = Lang.T("Frame");
            cFrame.AutoSize = true;
            cFrame.CheckedChanged += (s, e) => { bool c = cFrame.Checked; ApplyStyle(x => { if (x.Kind == ShapeKind.Text) { x.Frame = c; if (c && x.Width < 0.5f) x.Width = 1.5f; } }); LoadStyle(); };
            tip.SetToolTip(cFrame, Lang.T("Frame round the text, in the line colour and width"));
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
            tip.SetToolTip(tMarkStart, Lang.T("First number (or letter) of this marker series"));

            // quick palette: left-click = line colour, right-click = fill colour
            FlowLayoutPanel pal = new FlowLayoutPanel();
            pal.WrapContents = false; pal.AutoSize = true; pal.Margin = Padding.Empty;
            pal.BackColor = styleBar.BackColor;
            palBar = pal;
            for (int i = 0; i < Palette.Length; i++)
            {
                int idx = i;
                Panel sw = new Panel();
                sw.Size = new Size(S(16), S(16));
                sw.Margin = new Padding(0, 0, S(2), 0);
                sw.BackColor = Palette[i];
                Theme.Keep(sw);
                sw.BorderStyle = BorderStyle.FixedSingle;
                sw.Cursor = Cursors.Hand;
                sw.MouseUp += (s, e) => SetPaletteColor(e.Button != MouseButtons.Right, idx);
                tip.SetToolTip(sw, PaletteNames[i] + " – click = line (Alt+" + (i + 1) + "), right-click = fill (Alt+Shift+" + (i + 1) + ")");
                pal.Controls.Add(sw);
                palSwatches.Add(sw);
            }
            // last: the screen colour picker (pick any colour on any screen). It shows the last colour picked, with a pipette on it
            Panel more = new Panel();
            more.Size = new Size(S(16), S(16));
            Theme.Keep(more);
            more.Margin = new Padding(S(3), 0, 0, 0);
            more.BorderStyle = BorderStyle.FixedSingle;
            more.Cursor = Cursors.Hand;
            more.Paint += (s, e) => DrawPickerSwatch(e.Graphics, more.ClientRectangle);
            more.MouseUp += (s, e) => PickScreenColor();
            tip.SetToolTip(more, Lang.T("Colour picker – pick a colour anywhere on screen: click = line colour, right-click = fill colour; the HEX code is copied (Ctrl+Shift+C)"));
            pickerSwatch = more;
            pal.Controls.Add(more);

            gLine = Grp("Line", bStroke, tSO, lSO, nWidth);
            gFill = Grp("Fill", bFill, tFO, lFO);
            gFont = Grp("Text", nFont);
            // text bar, simplified like Greenshot: font, Bold, Italic, Underline and one alignment list
            // (strike-out is on the text's right-click menu; the old align buttons stay off the bar)
            cFontName = FontCombo();
            cAlign = AlignCombo();
            // 1.4.2 symbol picker: a label (it never takes the focus, so the text being typed stays open)
            Label lSym = new Label();
            lSym.Text = "Ω";
            lSym.UseMnemonic = false;
            lSym.AutoSize = false;
            lSym.Size = new Size(S(24), S(22));
            lSym.TextAlign = ContentAlignment.MiddleCenter;
            lSym.BorderStyle = BorderStyle.FixedSingle;
            lSym.Cursor = Cursors.Hand;
            lSym.Margin = new Padding(S(2), S(1), S(2), 0);
            tip.SetToolTip(lSym, Lang.T("Insert a symbol: Ø ° ± × ≈ ≤ ≥ √ Δ π µ ² ³ ½ … (also on the text's right-click menu)"));
            ContextMenuStrip symMenu = new ContextMenuStrip();
            cv.AddSymbolItems(symMenu.Items);
            lSym.MouseUp += (s, e) =>
            {
                if (!cv.IsEditingText)
                {
                    // a text shape selected: open it for typing, at the end of its text
                    Shape t = cv.Selected;
                    if (t != null && (t.Kind == ShapeKind.Text || t.Kind == ShapeKind.Bubble || t.Kind == ShapeKind.Stamp)) cv.BeginTextEdit(t, false);
                    else { Flash(Lang.T("Click into a text first, then pick a symbol")); return; }
                }
                symMenu.Show(lSym, new Point(0, lSym.Height));
            };
            cVAlign = Combo(76, "Top", "Middle", "Bottom");
            cVAlign.SelectedIndexChanged += (s, e) =>
            {
                int v = cVAlign.SelectedIndex;
                if (loading || v < 0) return;
                ApplyStyle(x => { if (x.Kind == ShapeKind.Text) { x.VAlign = v; x.Cache = null; } });
                cv.Invalidate();
            };
            tip.SetToolTip(cVAlign, Lang.T("Where the text sits in the box: top, middle or bottom (drag the box taller to see it)"));
            rowTextStyle = Grp("", cFontName, cBold, cItalic, cUnder, cAlign, cVAlign, lSym);
            gHeads = Grp("Start", cHeadS);
            Label lEnd = new Label();
            lEnd.Text = Lang.T("End");
            lEnd.AutoSize = true;
            lEnd.ForeColor = Theme.Dark ? Theme.Label : Color.FromArgb(80, 80, 80);
            lEnd.Margin = new Padding(S(4), S(6), S(3), 0);
            gHeads.Controls.Add(lEnd);
            cHeadE.Margin = new Padding(0, S(2), S(3), 0);
            gHeads.Controls.Add(cHeadE);
            rowHl = Grp("Mode", cHlMode, cWhere);
            rowStrength = Grp("Strength", tStr, lStr);
            cReverse = new CheckBox();
            cReverse.Text = Lang.T("Reverse");
            cReverse.AutoSize = true;
            cReverse.Margin = new Padding(S(6), S(4), 0, 0);
            cReverse.CheckedChanged += (s, e) => ReverseEdited();
            tip.SetToolTip(cReverse, Lang.T("Count down: Z, Y, X … A for letters, 10, 9, 8 … 1 for numbers"));
            rowMarkStart = Grp("Start at", tMarkStart, cReverse);
            ComboBox cDate = Combo(150, Stamps.DateNames);
            cDate.SelectedIndex = Math.Max(0, Math.Min(Stamps.DateNames.Length - 1, Settings.GetInt("StampDate", 0)));
            cDate.SelectedIndexChanged += (s, e) => StampDateChanged(cDate.SelectedIndex);
            tip.SetToolTip(cDate, Lang.T("Date line on the stamp (keeps the stamp's own date, just changes how it is written)"));
            rowStamp = Grp("Stamp", cStamp);
            Label lDate = new Label(); lDate.Text = Lang.T("Date"); lDate.AutoSize = true; lDate.ForeColor = Color.FromArgb(80, 80, 80);
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
            // 1.4.2: where a dimension's value sits: on the line (in a gap), above or below it
            cLabelAt = Combo(110, "On the line", "Above", "Below");
            cLabelAt.SelectedIndexChanged += (s, e) => { int v = cLabelAt.SelectedIndex; if (v >= 0) ApplyStyle(x => { if (x.Kind == ShapeKind.Dimension) { x.Align = v; x.Cache = null; } }); };
            tip.SetToolTip(cLabelAt, Lang.T("Where the value sits: on the dimension line, above it or below it"));
            rowLabelAt = Grp("Text", cLabelAt);
            rowFrame = Grp("", cFrame);
            gShadow = Grp("", cShadow);
            // the groups every shape has come first (Line, Fill, Shadow), so they stay put; the shape's own after them
            styleOrder = new Control[] { lStyleFor, gLine, gFill, gShadow, gDash, gFont, rowTextStyle, rowFrame, gHeads, rowHl, rowStrength, rowForm, rowStamp, rowMarkup, rowCallout, rowShow, rowLabelAt, rowClosed, rowMarkStart };
            foreach (Control c in styleOrder) styleBar.Controls.Add(c);
            ReturnFocusToCanvas(styleBar);
            ReturnFocusToCanvas(pal);
            styleBar.ResumeLayout(false);

            // » at the end of the row: the groups that do not fit, in a small panel under it
            bMore = new Button();
            bMore.Text = "»";
            bMore.FlatStyle = FlatStyle.Flat;
            bMore.FlatAppearance.BorderSize = 0;
            bMore.BackColor = styleBar.BackColor;
            bMore.Font = Native.UiFont("Segoe UI", 11f, k, FontStyle.Bold);
            bMore.Size = new Size(S(30), S(28));
            bMore.TabStop = false;
            bMore.Visible = false;
            bMore.Click += (s, e) => ShowMoreStyle();
            tip.SetToolTip(bMore, Lang.T("More options for this tool (the window is too narrow to show them all)"));

            // the quick colours stay at the right end whatever the tool, so the hand finds them in the same place
            styleHost = new Panel();
            styleHost.Dock = DockStyle.Top;
            styleHost.BackColor = styleBar.BackColor;
            styleHost.Controls.Add(styleBar);
            styleHost.Controls.Add(bMore);
            styleHost.Controls.Add(pal);
            styleBar.Location = Point.Empty;
            styleHost.Resize += (s, e) => FitStyleBar();
            FitStyleBar();
        }

        Panel styleHost;
        FlowLayoutPanel palBar;
        bool fittingStyle;
        Control[] styleOrder;
        readonly List<Control> palSwatches = new List<Control>();
        Button bMore;
        Form morePanel;
        int styleShown;           // the bar's width that is shown: the groups after it are behind »

        /// <summary>» : the groups that do not fit on the row, in a small panel under the button. They go back to the
        /// row when it closes (click outside it, or Esc).</summary>
        void ShowMoreStyle()
        {
            if (morePanel != null) { morePanel.Close(); return; }
            List<Control> hidden = new List<Control>();
            foreach (Control c in styleOrder) if (c.Parent == styleBar && c.Visible && c.Right > styleShown) hidden.Add(c);
            if (hidden.Count == 0) return;
            FlowLayoutPanel fp = new FlowLayoutPanel();
            fp.WrapContents = true;
            fp.AutoSize = true;
            fp.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            fp.MaximumSize = new Size(Math.Max(S(320), Math.Min(S(640), ClientSize.Width - S(40))), 0);
            fp.Padding = new Padding(S(8), S(6), S(4), S(6));
            fp.BackColor = styleBar.BackColor;
            fp.SuspendLayout();
            foreach (Control c in hidden) fp.Controls.Add(c);
            fp.ResumeLayout(true);
            Form f = new Form();
            f.FormBorderStyle = FormBorderStyle.FixedSingle;
            f.ControlBox = false;
            f.Text = "";
            f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.Manual;
            f.KeyPreview = true;
            f.Font = Font;
            f.BackColor = styleBar.BackColor;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.Controls.Add(fp);
            Point at = bMore.PointToScreen(new Point(0, bMore.Height));
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            Size sz = f.GetPreferredSize(Size.Empty);
            f.Location = new Point(Math.Max(wa.Left, Math.Min(at.X + bMore.Width - sz.Width, wa.Right - sz.Width)), at.Y);
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
            // closes when another window gets the focus (a colour dialog opened from it, too)
            f.Deactivate += (s, e) => { if (!f.IsDisposed) f.BeginInvoke((Action)(() => { if (!f.IsDisposed && !f.ContainsFocus) f.Close(); })); };
            f.FormClosed += (s, e) =>
            {
                morePanel = null;
                if (IsDisposed) return;
                styleBar.SuspendLayout();
                foreach (Control c in hidden) styleBar.Controls.Add(c);
                for (int i = 0; i < styleOrder.Length; i++) styleBar.Controls.SetChildIndex(styleOrder[i], i);
                styleBar.ResumeLayout(true);
                FitStyleBar();
            };
            morePanel = f;
            f.Show(this);
        }

        void FitStyleBar()
        {
            if (fittingStyle || styleHost == null) return;
            fittingStyle = true;
            try
            {
                // a group keeps the height it measured before its drop-down boxes grew (they grow natively when
                // their handle is made): make each group tall enough for what is in it, so nothing is cut off
                bool grew = false;
                foreach (Control g in styleBar.Controls)
                {
                    if (!(g is FlowLayoutPanel)) continue;
                    int need = 0;
                    foreach (Control x in g.Controls) if (x.Visible) need = Math.Max(need, x.Bottom + x.Margin.Bottom);
                    need += g.Padding.Bottom;
                    if (need > g.Height && g.MinimumSize.Height < need) { g.MinimumSize = new Size(0, need); grew = true; }
                }
                if (grew) styleBar.PerformLayout();
                // strictly one row, whatever the window width: the canvas (and its rulers) never move down.
                // The groups that do not fit are hidden behind » ; the quick colours stay at the right end.
                int right = S(10);
                Size ps = styleBar.GetPreferredSize(Size.Empty);      // one row (no wrapping)
                // short of room, the quick colours go first (Alt+1…8 still pick them); the colour picker always stays
                if (morePanel == null)
                {
                    bool showSw = true;
                    foreach (Control c in palSwatches) if (!c.Visible) { c.Visible = true; }
                    if (ps.Width + S(8) + palBar.GetPreferredSize(Size.Empty).Width + right > styleHost.ClientSize.Width) showSw = false;
                    if (!showSw) foreach (Control c in palSwatches) c.Visible = false;
                }
                Size palSize = palBar.GetPreferredSize(Size.Empty);
                int h = Math.Max(ps.Height, palSize.Height + S(14));
                if (styleHost.Height != h) styleHost.Height = h;
                if (palBar.Size != palSize) palBar.Size = palSize;
                int palX = Math.Max(S(240), styleHost.ClientSize.Width - palSize.Width - right);
                palBar.Location = new Point(palX, Math.Max(0, (h - palSize.Height) / 2));
                palBar.BringToFront();
                int room = palX - S(8);
                // lay the bar out at its full length, then show only the groups that end inside the room
                if (styleBar.Size != new Size(ps.Width, h)) styleBar.Size = new Size(ps.Width, h);
                styleBar.PerformLayout();
                int shown = ps.Width;
                bool cut = ps.Width > room;
                if (cut)
                {
                    int avail = room - bMore.Width - S(4);
                    shown = styleBar.Padding.Left;
                    foreach (Control c in styleOrder)
                        if (c.Parent == styleBar && c.Visible)
                        {
                            if (c.Right > avail) break;
                            shown = c.Right + c.Margin.Right;
                        }
                }
                styleShown = shown;
                styleBar.Width = shown;
                bMore.Visible = cut || morePanel != null;
                bMore.Location = new Point(shown + S(2), (h - bMore.Height) / 2);
            }
            finally { fittingStyle = false; }
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
            outer.Width = S(Math.Max(PanelMin, Math.Min(1600, Settings.GetInt("PanelWidth", PanelMin))));
            // dark mode: the bars' colour, so top bar, rulers and panel make one frame round the darker drawing area
            outer.BackColor = Theme.Dark ? Theme.Elevated : SystemColors.Control;
            outer.Padding = new Padding(0, 0, S(6), S(6));        // top flush with the ruler
            outer.Visible = Settings.GetBool("ShowPanel", true);
            props = outer;

            FlowLayoutPanel f = new FlowLayoutPanel();
            f.Dock = DockStyle.Fill;
            f.FlowDirection = FlowDirection.TopDown;
            f.WrapContents = false;
            f.AutoScroll = true;
            f.Padding = new Padding(S(4), 0, 0, 0);
            f.ClientSizeChanged += (s, e) => LayoutPanel();      // panel dragged wider, or a scrollbar came or went
            propsFlow = f;

            // the panel's left edge: drag it to make the panel wider (to read long labels), double-click = normal width
            Panel grip = new Panel();
            grip.Dock = DockStyle.Left;
            grip.Width = S(6);
            grip.Cursor = Cursors.SizeWE;
            // the same soft line as the ruler's edge, so ruler and panel meet in one clean corner
            grip.Paint += (s, e) => { using (Pen p = new Pen(Theme.Dark ? Theme.Border : Color.FromArgb(170, 170, 170))) e.Graphics.DrawLine(p, S(2), 0, S(2), grip.Height); };
            tip.SetToolTip(grip, Lang.T("Drag to make the panel wider or narrower · double-click = normal width"));
            int dragX = 0, dragW = 0;
            bool dragging = false;
            grip.MouseDown += (s, e) => { if (e.Button != MouseButtons.Left) return; dragging = true; dragX = Cursor.Position.X; dragW = outer.Width; };
            grip.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                int w = dragW + (dragX - Cursor.Position.X);
                w = Math.Max(S(PanelMin), Math.Min(Math.Max(S(PanelMin), ClientSize.Width / 2), w));
                if (w != outer.Width) { outer.Width = w; outer.Update(); }
            };
            grip.MouseUp += (s, e) =>
            {
                if (!dragging) return;
                dragging = false;
                Settings.Set("PanelWidth", ((int)Math.Round(outer.Width / k)).ToString(CultureInfo.InvariantCulture));
                cv.Focus();
            };
            grip.DoubleClick += (s, e) => { outer.Width = S(PanelMin); Settings.Set("PanelWidth", PanelMin.ToString(CultureInfo.InvariantCulture)); };
            outer.Resize += (s, e) => LayoutPanel();

            f.Controls.Add(Header("Scale"));
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
            bCal.Text = Lang.T("Calibrate from a known length…");
            bCal.Size = new Size(S(236), S(28));
            bCal.Click += (s, e) => { cv.CurrentTool = Tool.Calibrate; cv.Focus(); };
            cKeepScale = new CheckBox();
            cKeepScale.Text = Lang.T("Keep this scale for new captures");
            cKeepScale.AutoSize = true;
            cKeepScale.Checked = Settings.GetBool("KeepScale", false);
            cKeepScale.CheckedChanged += (s, e) => { Settings.Set("KeepScale", cKeepScale.Checked ? "1" : "0"); SaveScaleDefaults(); };

            Button bPreset = new Button();
            bPreset.Text = Lang.T("Drawing scales…");
            bPreset.Size = new Size(S(236), S(28));
            bPreset.AutoEllipsis = true;          // one line: "…" when the panel is too narrow for all of it
            tip.SetToolTip(bPreset, Lang.T("Set the scale from the drawing (1:50 on A1 …), or use a saved scale"));
            bPreset.Click += (s, e) => ScalePresetDialog();

            f.Controls.Add(Row("Unit", cUnit));
            // the exact value to type in, then its unit and the readable size (the same as the status bar shows)
            lUppNote = new Label();
            lUppNote.AutoSize = true;
            lUppNote.ForeColor = Color.FromArgb(90, 96, 106);
            Panel upp = new Panel();
            upp.Size = new Size(S(140), Math.Max(tUpp.PreferredHeight, S(22)));
            tUpp.Location = new Point(0, 0);
            lUppNote.Location = new Point(tUpp.Width + S(5), Math.Max(0, (upp.Height - lUppNote.PreferredHeight) / 2));
            upp.Controls.Add(tUpp); upp.Controls.Add(lUppNote);
            Panel rowUpp = Row("1 pixel =", upp);
            // the row grows with the panel: the readable size joins the unit when there is room for it
            upp.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            upp.Resize += (s, e) => ShowUppNote();
            uppBox = upp;
            f.Controls.Add(rowUpp);
            // Ceil / Floor on the Decimals row, right after the (narrow: 0–6) decimals box
            cCeil = new CheckBox(); cCeil.Text = Lang.T("Ceil"); cCeil.AutoSize = true;
            cFloor = new CheckBox(); cFloor.Text = Lang.T("Floor"); cFloor.AutoSize = true;
            nDec.Width = S(42);
            cCeil.Font = cFloor.Font = Font;     // measured before they are on the window: with the editor's (scaled) font
            Panel rounding = new Panel();
            rounding.Size = new Size(nDec.Width + S(6) + cCeil.PreferredSize.Width + S(4) + cFloor.PreferredSize.Width,
                                     Math.Max(nDec.PreferredSize.Height, Math.Max(cCeil.PreferredSize.Height, S(22))));
            nDec.Location = new Point(0, (rounding.Height - nDec.Height) / 2);
            cCeil.Location = new Point(nDec.Width + S(6), (rounding.Height - cCeil.PreferredSize.Height) / 2);
            cFloor.Location = new Point(cCeil.Left + cCeil.PreferredSize.Width + S(4), cCeil.Top);
            rounding.Controls.Add(nDec); rounding.Controls.Add(cCeil); rounding.Controls.Add(cFloor);
            Panel rowDec = Row("Decimals", rounding);
            tip.SetToolTip(cCeil, Lang.T("Round every value up (ceiling) at the chosen decimals"));
            tip.SetToolTip(cFloor, Lang.T("Round every value down (floor) at the chosen decimals"));
            // only one of the two: ticking one clears the other; neither = round to the nearest
            cCeil.CheckedChanged += (s, e) => { if (loading) return; if (cCeil.Checked) { loading = true; cFloor.Checked = false; loading = false; } RoundingEdited(); };
            cFloor.CheckedChanged += (s, e) => { if (loading) return; if (cFloor.Checked) { loading = true; cCeil.Checked = false; loading = false; } RoundingEdited(); };
            f.Controls.Add(rowDec);
            f.Controls.Add(bCal);
            f.Controls.Add(bPreset);
            f.Controls.Add(cKeepScale);

            f.Controls.Add(Header("Measurements & counts"));
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
            lv.DoubleClick += (s, e) => { if (lv.SelectedItems.Count > 0) { Shape sh = lv.SelectedItems[0].Tag as Shape; if (sh != null) cv.EditLabel(sh); else if (lv.SelectedItems[0].Text == "ΣA" || lv.SelectedItems[0].Text == "Σ1") LabelSeries(lv.SelectedItems[0].Text == "ΣA"); } };
            lv.SelectedIndexChanged += (s, e) =>
            {
                if (loading || lv.SelectedItems.Count == 0) return;
                Shape sh = lv.SelectedItems[0].Tag as Shape;
                if (sh != null && sh != cv.Selected) { cv.CurrentTool = Tool.Select; cv.SelectShape(sh); }
            };
            f.Controls.Add(lv);
            Button bCopyM = new Button();
            bCopyM.Text = Lang.T("Copy measurements (for Excel)");
            bCopyM.Size = new Size(S(236), S(28));
            bCopyM.Click += (s, e) => CopyMeasurements();
            f.Controls.Add(bCopyM);

            // every text on the picture in one list: click a line to select it, click again (or F2) to edit it here
            f.Controls.Add(Header("Texts"));
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
            bCopyT.Text = Lang.T("Copy all text");
            bCopyT.Size = new Size(S(236), S(28));
            bCopyT.Click += (s, e) => CopyAllText();
            f.Controls.Add(bCopyT);

            outer.Controls.Add(f);
            outer.Controls.Add(grip);      // added last = docked first: the grip takes the left edge, the lists fill the rest
            LayoutPanel();
            return outer;
        }

        const int PanelMin = 262;          // the panel's normal (and smallest) width, at 100 % scaling
        FlowLayoutPanel propsFlow;
        int laidOut = int.MinValue;

        /// <summary>Stretches the panel's rows, buttons and lists to its width; the label columns take the extra room.</summary>
        void LayoutPanel()
        {
            if (props == null || propsFlow == null || lv == null || lvText == null) return;
            // fill the room inside the panel: its width less the grip, the padding, the rows' margins and – in a short
            // window – the vertical scrollbar, so the panel never needs to scroll sideways
            int room = propsFlow.ClientSize.Width - propsFlow.Padding.Horizontal - S(6);
            int d = Math.Max(-S(20), room - S(236));
            if (laidOut == d) return;
            laidOut = d;
            propsFlow.SuspendLayout();
            foreach (Control c in propsFlow.Controls)
                if (!(c is CheckBox)) c.Width = S(236) + d;
            lv.Columns[2].Width = S(96) + d;
            lvText.Columns[0].Width = S(176) + d;
            propsFlow.ResumeLayout(true);
        }

        /// <summary>Radius Show list, in the order the user wants, and the stored mode of each entry (see Renderer.RadiusText).</summary>
        static readonly string[] RadiusShow = { "Diameter", "Radius", "Arc length", "Diameter + Arc length", "Radius + Arc length", "All", "None" };
        static readonly int[] RadiusShowMode = { 2, 1, 6, 3, 4, 7, 5 };

        /// <summary>Shown in place of line breaks in the one-line text list (typed back, it becomes a line break again).</summary>
        const string LineMark = "↵";

        /// <summary>Focus form change. A drawn focus keeps its place: a box becomes a perimeter through its (turned)
        /// corners; a perimeter becomes the box round its corners.</summary>
        static void SetFocusForm(Shape x, int form)
        {
            bool toPoly = form == (int)HighlightForm.Perimeter;
            if (x.Pts.Count >= 2 && toPoly != x.IsFocusPoly)
            {
                if (toPoly)
                {
                    RectangleF r = Geo.Rect(x.Pts[0], x.Pts[1]);
                    PointF c = Geo.Center(r);
                    x.Pts.Clear();
                    foreach (PointF p in new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom) })
                        x.Pts.Add(Geo.Rot(p, c, x.Angle));
                    x.Angle = 0f;
                }
                else
                {
                    RectangleF r = ShapeOps.Bounds(x);
                    x.Pts.Clear();
                    x.Pts.Add(r.Location);
                    x.Pts.Add(new PointF(r.Right, r.Bottom));
                }
            }
            x.Form = form;
            x.Cache = null;
        }

        /// <summary>Focus: the mode picked in the list (spotlight, grayscale, magnify, pixelate) without its side.</summary>
        static int FocusBase(int mode)
        {
            if (mode == (int)HighlightMode.GrayscaleInside) return (int)HighlightMode.Grayscale;
            if (mode == (int)HighlightMode.PixelateOutside) return (int)HighlightMode.Pixelate;
            if (mode == (int)HighlightMode.BlurOutside) return (int)HighlightMode.Blur;
            return mode;
        }

        bool strMagnify, strBlur;

        /// <summary>The value next to the Strength slider: ×2.5 for magnify, 12 px for pixelate.</summary>
        void ShowStrength()
        {
            int v = tStr.Value;
            lStr.Text = strMagnify ? "×" + (1 + v * 0.5).ToString("0.0", CultureInfo.InvariantCulture) : strBlur ? (v * 3) + " px" : Math.Max(3, v * 4) + " px";
        }

        /// <summary>Focus list position → mode: the first five are the modes 0–4 themselves, the sixth is Blur.</summary>
        static int HlListToMode(int i) { return i == 5 ? (int)HighlightMode.Blur : i; }
        static int HlModeToList(int b) { return b == (int)HighlightMode.Blur ? 5 : Math.Max(0, Math.Min((int)HighlightMode.Pixelate, b)); }

        /// <summary>Focus: the stored mode for a list mode and a side.</summary>
        static int FocusMode(int baseMode, bool inside)
        {
            if (baseMode == (int)HighlightMode.Grayscale) return inside ? (int)HighlightMode.GrayscaleInside : (int)HighlightMode.Grayscale;
            if (baseMode == (int)HighlightMode.Pixelate) return inside ? (int)HighlightMode.Pixelate : (int)HighlightMode.PixelateOutside;
            if (baseMode == (int)HighlightMode.Blur) return inside ? (int)HighlightMode.Blur : (int)HighlightMode.BlurOutside;
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
            // the once-per-100 thank-you line (hidden until it is due): a link, and a small x that ends it for good
            lNote = new ToolStripStatusLabel("");
            lNote.IsLink = true;
            lNote.LinkBehavior = LinkBehavior.HoverUnderline;
            if (Theme.Dark) lNote.LinkColor = lNote.ActiveLinkColor = lNote.VisitedLinkColor = Theme.Link;
            lNote.Visible = false;
            lNote.ToolTipText = "Optional. Nothing in SnapMeasure is locked.";
            lNote.Click += (s, e) => { Usage.NoteClose(); HideNote(); App.OpenSupport(); };
            lNoteX = new ToolStripStatusLabel("✕");
            lNoteX.ForeColor = SystemColors.GrayText;
            lNoteX.Visible = false;
            lNoteX.ToolTipText = "Don't show this again";
            lNoteX.Click += (s, e) => { Usage.NoteClose(); HideNote(); };
            status.Items.Add(lNote);
            status.Items.Add(lNoteX);
            status.Items.Add(lHint);
            lCount = new ToolStripStatusLabel(Usage.CountText);
            lCount.ForeColor = SystemColors.GrayText;
            lCount.ToolTipText = Lang.T("Captures on this PC. Never sent anywhere.");
            lCount.Visible = Usage.ShowCount;
            Version v = typeof(EditorForm).Assembly.GetName().Version;
            ToolStripStatusLabel lVersion = new ToolStripStatusLabel("SnapMeasure v" + v.Major + "." + v.Minor + "." + v.Build);
            lVersion.ForeColor = Color.FromArgb(0, 102, 204);
            lVersion.IsLink = true;
            lVersion.LinkBehavior = LinkBehavior.HoverUnderline;
            if (Theme.Dark) lVersion.LinkColor = lVersion.ActiveLinkColor = lVersion.VisitedLinkColor = Theme.Link;
            lVersion.ToolTipText = "About SnapMeasure – " + AppInfo.RepoUrl;
            lVersion.Click += (s, e) => App.ShowAbout(this);
            status.Items.Add(new ToolStripSeparator());
            status.Items.Add(lCount);
            status.Items.Add(new ToolStripSeparator());
            status.Items.Add(lVersion);
            if (noteShowing) ShowNote();            // the bar was rebuilt (monitor change) while the note was up
        }

        // ---------------- the once-per-100 thank-you line ----------------
        /// <summary>The capture that reached 100, 200 … also showed a tray message; the next editor opened shows the line in
        /// its status bar too: 2 seconds after it opens, for 20 seconds (or until ✕). No window, no sound, no focus change.</summary>
        void StartNoteTimer()
        {
            if (!App.NoteForEditor) return;
            App.NoteForEditor = false;
            noteTimer = new System.Windows.Forms.Timer();
            noteTimer.Interval = 2000;
            bool up = false;                       // false = waiting to show, true = showing (waiting to hide)
            noteTimer.Tick += (s, e) =>
            {
                noteTimer.Stop();
                if (IsDisposed) return;
                if (up) { HideNote(); return; }
                bool busy = cv.CreatingPoints > 0 || cv.IsEditingText || Control.MouseButtons != MouseButtons.None;
                if (busy) { noteTimer.Interval = 3000; noteTimer.Start(); return; }     // not while drawing: in a moment
                noteShowing = true;
                ShowNote();
                up = true;
                noteTimer.Interval = 20000;
                noteTimer.Start();
            };
            noteTimer.Start();
        }
        void ShowNote()
        {
            if (lNote == null) return;
            lNote.Text = "☕ " + Usage.CountText + " – " + Lang.T("thanks for using SnapMeasure. Optional: buy me a coffee");
            lNote.Visible = lNoteX.Visible = true;
            lHint.Visible = false;
        }

        /// <summary>Self-test: any drop-down on the top bar that lists the same text twice ("" = none).</summary>
        internal string DuplicateMenuItems()
        {
            string found = "";
            foreach (ToolStripItem i in bar.Items)
            {
                ToolStripDropDownItem dd = i as ToolStripDropDownItem;
                if (dd == null) continue;
                HashSet<string> seen = new HashSet<string>();
                foreach (ToolStripItem c in dd.DropDownItems)
                    if (!(c is ToolStripSeparator) && !seen.Add(c.Text)) found += dd.Text + ": " + c.Text + "; ";
            }
            return found;
        }

        /// <summary>Self-test: show the thank-you line now (as at capture 100), for a snapshot.</summary>
        internal void DemoNote()
        {
            if (lCount != null) lCount.Text = Usage.CountText;
            noteShowing = true;
            ShowNote();
        }

        void HideNote()
        {
            noteShowing = false;
            if (lNote == null) return;
            lNote.Visible = lNoteX.Visible = false;
            lHint.Visible = true;
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
            string t = Lang.T(GuideText());
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
                    if (n == 2) return "Angle: click the corner. Shift = 45° steps.";
                    return "Angle: click the end of the second arm. Shift = 45°, 90°, 135° …";
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
                    return "drag it: every coordinate updates. Double-click to give it other coordinates.";
                case ShapeKind.Counter:
                    return s.IsSymbolMarker ? "double-click to change its character." : "double-click to name what this series counts.";
                case ShapeKind.Image:
                    return s.Text == "Mouse pointer" ? "drag to move it, or press Delete to remove it." : "drag a corner to stretch it, hold Shift to keep its proportions.";
                default:
                    return "drag to move it. Del to delete.";
            }
        }
        /// <summary>1.4.2: a shape that is only a fill (the Fill tool, or an area it filled): colour picks go to its fill.</summary>
        static bool FillFirst(Shape s)
        {
            return s != null && Canvas.IsFillShape(s);
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
                case ShapeKind.Counter: return s.Mode == 2 ? "Symbol marker" : s.Mode == 1 ? "Letter marker" : "Number marker";
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
                    return s.Mode == (int)HighlightMode.Pixelate || s.Mode == (int)HighlightMode.PixelateOutside ? "Pixelate" : s.Mode == (int)HighlightMode.Blur || s.Mode == (int)HighlightMode.BlurOutside ? "Blur" : s.Mode == 0 ? "Highlight" : "Focus";
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
            palBar.Enabled = s != null;
            if (s == null)
            {
                lStyleFor.Text = Lang.T(cv.CurrentTool == Tool.Cutout ? "Cut-out (uses the area's or polygon's style)" : cv.CurrentTool == Tool.Ocr ? "Copy text (OCR)" : "Pick a tool or shape");
                bFill.Visible = true;
                Label fillLabel0 = gFill.Controls.Count > 0 ? gFill.Controls[0] as Label : null;
                if (fillLabel0 != null) fillLabel0.Text = Lang.T("Fill");
                rowHl.Visible = rowStrength.Visible = rowMarkStart.Visible = rowTextStyle.Visible = gHeads.Visible = rowStamp.Visible = rowMarkup.Visible = false;
                gDash.Visible = rowForm.Visible = rowCallout.Visible = rowClosed.Visible = rowFrame.Visible = rowShow.Visible = rowLabelAt.Visible = false;
            }
            else
            {
                string sel = cv.Sel.Count > 1 ? " (" + cv.Sel.Count + " " + Lang.T("selected") + ")" : (cv.Selected != null ? " (" + Lang.T("selected") + ")" : "");
                lStyleFor.Text = Lang.T(KindName(s)) + sel;
                ShapeKind kd = s.Kind;
                bool lineOnly = kd == ShapeKind.Line || kd == ShapeKind.Arrow || kd == ShapeKind.Freehand || kd == ShapeKind.Dimension || kd == ShapeKind.Angle ||
                                kd == ShapeKind.Polyline || kd == ShapeKind.Perimeter || kd == ShapeKind.Arc || kd == ShapeKind.Radius ||
                                (kd == ShapeKind.Polygon && s.IsOpenPath);
                // Line, Fill and Shadow keep their place for every shape (greyed out where they do not apply), so
                // the groups after them do not jump sideways when another tool or shape is picked
                gFill.Visible = gLine.Visible = gShadow.Visible = kd != ShapeKind.Note;
                if (lineOnly) gFill.Enabled = false;
                // a picture has no fill colour: its slider is the picture's opacity
                bool pic = kd == ShapeKind.Image;
                bFill.Visible = !pic;
                Label fillLabel = gFill.Controls.Count > 0 ? gFill.Controls[0] as Label : null;
                if (fillLabel != null) fillLabel.Text = Lang.T(pic ? "Opacity" : "Fill");
                gFont.Visible = s.HasTextStyle || kd == ShapeKind.Counter || kd == ShapeKind.Dimension || kd == ShapeKind.Area || kd == ShapeKind.Angle || kd == ShapeKind.Radius ||
                                kd == ShapeKind.Polyline || kd == ShapeKind.Perimeter || kd == ShapeKind.Note;
                gHeads.Visible = s.HasHeads;
                if (s.HasHeads)
                {
                    cHeadS.SelectedIndex = Icons.HeadIndex(s.HeadStart);      // -1 = an older end that is no longer in the list
                    cHeadE.SelectedIndex = Icons.HeadIndex(s.HeadEnd);
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
                if (rowForm.Visible) cForm.SelectedIndex = Math.Max(0, Math.Min((int)HighlightForm.Perimeter, s.Form));
                rowCallout.Visible = kd == ShapeKind.Cloud;
                if (rowCallout.Visible) cCallout.SelectedIndex = Math.Max(0, Math.Min(2, s.Mode));
                // length (W) and polygon: tick Closed to close the outline. The Area tool (M) is always closed: no tick.
                rowClosed.Visible = kd == ShapeKind.Perimeter || (kd == ShapeKind.Polygon && cv.CurrentTool != Tool.Cutout && !(cv.Selected == null && cv.CurrentTool == Tool.Fill));
                rowLabelAt.Visible = kd == ShapeKind.Dimension;
                if (rowLabelAt.Visible) cLabelAt.SelectedIndex = Math.Max(0, Math.Min(2, s.Align));
                rowShow.Visible = (kd == ShapeKind.Area && cv.CurrentTool != Tool.Cutout) || kd == ShapeKind.Radius;
                if (rowShow.Visible)
                {
                    string[] items = kd == ShapeKind.Radius ? RadiusShow : new[] { "Area + perimeter", "Area only", "Perimeter only", "None" };
                    if ((string)cShow.Items[0] != items[0]) { cShow.Items.Clear(); cShow.Items.AddRange(items); }
                    // radius: list position ↔ stored mode; an old "radius + diameter" (0) shows no entry
                    cShow.SelectedIndex = kd == ShapeKind.Radius ? Array.IndexOf(RadiusShowMode, s.Mode) : Math.Max(0, Math.Min(items.Length - 1, s.Mode));
                }
                if (rowClosed.Visible) cClosed.Checked = s.Mode == 0 && s.FromArea == 0;
                // text: the line width IS the frame (0 = no frame), so no separate Frame box
                rowFrame.Visible = false;
                tip.SetToolTip(nWidth, kd == ShapeKind.Text ? "Frame width – 0 = no frame" : "Line width");
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
                    cHlMode.SelectedIndex = HlModeToList(b);
                    // Inside / Outside stays in place for every mode (so nothing on the bar moves); it only matters for grayscale and pixelate
                    cWhere.Visible = true;
                    cWhere.Enabled = b == (int)HighlightMode.Grayscale || b == (int)HighlightMode.Pixelate || b == (int)HighlightMode.Blur;
                    cWhere.SelectedIndex = s.Mode == (int)HighlightMode.Grayscale || s.Mode == (int)HighlightMode.PixelateOutside || s.Mode == (int)HighlightMode.BlurOutside ? 1 : 0;
                }
                bool magnify = kd == ShapeKind.Highlight && s.Mode == (int)HighlightMode.Magnify;
                rowStrength.Visible = magnify || kd == ShapeKind.Pixelate ||
                                      (kd == ShapeKind.Highlight && (FocusBase(s.Mode) == (int)HighlightMode.Pixelate || FocusBase(s.Mode) == (int)HighlightMode.Blur));
                if (rowStrength.Visible) { strMagnify = magnify; strBlur = kd == ShapeKind.Highlight && FocusBase(s.Mode) == (int)HighlightMode.Blur; tStr.Value = strBlur && s.Strength <= 0 ? 3 : s.ShownStrength(magnify); ShowStrength(); }
                // pixelate has no fill (the pixelate tool no outline either): grey them out but keep their place,
                // so Shape, Mode and Strength stay put when the mode changes. A pixelate focus keeps its border.
                if (rowStrength.Visible && !magnify) { gFill.Enabled = false; if (kd == ShapeKind.Pixelate) gLine.Enabled = false; }
                rowMarkStart.Visible = s.IsCountingMarker;
                rowTextStyle.Visible = s.HasTextStyle;
                cBold.Checked = s.Bold;
                cItalic.Checked = s.Italic;
                cUnder.Checked = s.Underline;
                cStrike.Checked = s.Strikeout;
                cAlignL.Checked = s.Align == 0; cAlignC.Checked = s.Align == 1; cAlignR.Checked = s.Align == 2;
                cAlign.SelectedIndex = Math.Max(0, Math.Min(2, s.Align));
                cVAlign.Visible = kd == ShapeKind.Text;
                cVAlign.SelectedIndex = Math.Max(0, Math.Min(2, s.VAlign));
                ShowFontName(s.HasTextStyle ? s.FontFamilyName : Renderer.DefaultFont);
            }
            LoadMarker();
            loading = false;
            FitStyleBar();      // groups shown / hidden: the bar may now need one row more or less
        }

        static void SetColorButton(Button b, Color c)
        {
            Theme.Keep(b);                    // the colour the user picked: never recoloured by dark mode
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
                // markers: a style change applies to every marker of the same series (numbers or letters);
                // a symbol marker is styled on its own
                foreach (Shape x in cv.Sel)
                    if (x.IsCountingMarker)
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
                d.CustomColors = LoadCustomColors();
                if (d.ShowDialog(this) != DialogResult.OK) return;
                SaveCustomColors(d.CustomColors);
                Color c = d.Color;
                if (stroke) ApplyStyle(x => x.Stroke = c);
                else ApplyStyle(x => { x.Fill = c; if (x.FillOpacity == 0) x.FillOpacity = 50; });
                Settings.Set("PickedColor", (c.ToArgb() & 0xFFFFFF).ToString(CultureInfo.InvariantCulture));
                if (pickerSwatch != null) pickerSwatch.Invalidate();
                LoadStyle();
            }
        }

        Panel pickerSwatch;

        /// <summary>The screen colour picker: the colour clicked becomes the line colour (right-click: the fill colour)
        /// of the tool or the selected shapes, and its HEX code goes to the clipboard.</summary>
        void PickScreenColor()
        {
            bool fill;
            Color? got;
            try { got = ScreenColorPicker.Pick(out fill); }
            catch (Exception ex) { ErrorLog.Write("colour picker", ex); return; }
            Activate();
            if (!got.HasValue) { cv.Focus(); return; }
            Color c = Color.FromArgb(255, got.Value);
            string hex = "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
            Settings.Set("PickedColor", (c.ToArgb() & 0xFFFFFF).ToString(CultureInfo.InvariantCulture));
            if (pickerSwatch != null) pickerSwatch.Invalidate();
            App.TrySetClipboardText(hex);
            if (StyleTarget() == null)
            {
                Flash(hex + " " + Lang.T("copied") + " – " + Lang.T("pick a tool or select a shape to use it"));
            }
            else
            {
                if (FillFirst(StyleTarget())) fill = true;
                if (!fill) ApplyStyle(x => x.Stroke = c);
                else ApplyStyle(x => { x.Fill = c; if (x.FillOpacity == 0) x.FillOpacity = 50; });
                LoadStyle();
                Flash((fill ? Lang.T("Fill colour: ") : Lang.T("Line colour: ")) + hex + " (" + Lang.T("copied") + ")");
            }
            cv.Focus();
        }

        /// <summary>The colour picker's swatch: the last colour picked (light grey before any), a pipette on top in
        /// black or white, whichever shows on that colour.</summary>
        static void DrawPickerSwatch(Graphics g, Rectangle r)
        {
            int v = Settings.GetInt("PickedColor", -1);
            Color c = v < 0 ? Color.FromArgb(236, 238, 242) : Color.FromArgb(255, Color.FromArgb(v));
            using (SolidBrush b = new SolidBrush(c)) g.FillRectangle(b, r);
            bool dark = c.R * 0.299 + c.G * 0.587 + c.B * 0.114 < 140;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            GraphicsState st = g.Save();
            g.TranslateTransform(r.X, r.Y);
            g.ScaleTransform(r.Width / 32f, r.Height / 32f);
            Color ink = dark ? Color.White : Color.FromArgb(36, 40, 48);
            using (Pen p = new Pen(ink, 4.5f)) { p.StartCap = LineCap.Triangle; p.EndCap = LineCap.Round; g.DrawLine(p, 6, 26, 19, 13); }
            using (Pen p = new Pen(ink, 3f)) { p.StartCap = p.EndCap = LineCap.Round; g.DrawLine(p, 15, 11, 21, 17); }
            using (SolidBrush b = new SolidBrush(ink)) g.FillEllipse(b, 18, 4, 10, 10);
            g.Restore(st);
        }

        /// <summary>The 16 custom colours of the Windows colour picker, kept between editors.</summary>
        static int[] LoadCustomColors()
        {
            string[] parts = Settings.Get("CustomColors", "").Split(',');
            List<int> list = new List<int>();
            foreach (string p in parts)
            {
                int v;
                if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) list.Add(v & 0xFFFFFF);
            }
            while (list.Count < 16) list.Add(0xFFFFFF);
            return list.GetRange(0, 16).ToArray();
        }

        static void SaveCustomColors(int[] colors)
        {
            if (colors == null) return;
            string[] s = Array.ConvertAll(colors, c => (c & 0xFFFFFF).ToString(CultureInfo.InvariantCulture));
            Settings.Set("CustomColors", string.Join(",", s));
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
            bool letters = MarkerLetters;
            tMarkStart.Text = letters ? cv.Scale.MarkerLabel(0, true) : cv.Scale.MarkerStart.ToString(CultureInfo.InvariantCulture);
            if (cReverse != null)
            {
                cReverse.Checked = letters ? cv.Scale.LetterReverse : cv.Scale.MarkerReverse;
                cReverse.Text = Lang.T("Reverse");
            }
            loading = old;
        }

        void ReverseEdited()
        {
            if (loading) return;
            bool letters = MarkerLetters, on = cReverse.Checked;
            if (on == (letters ? cv.Scale.LetterReverse : cv.Scale.MarkerReverse)) return;
            cv.PushUndo(cv.Snapshot());
            if (letters)
            {
                cv.Scale.LetterReverse = on;
                if (on && cv.Scale.LetterStart <= 1) cv.Scale.LetterStart = 26;          // count down from Z
                else if (!on && cv.Scale.LetterStart == 26) cv.Scale.LetterStart = 1;   // and back up from A
            }
            else
            {
                cv.Scale.MarkerReverse = on;
                int n = Measure.Count(cv.Shapes, false);
                if (on && n > 0) cv.Scale.MarkerStart = n;            // the series ends at 1
                else if (on && cv.Scale.MarkerStart < 1) cv.Scale.MarkerStart = 1;
                else if (!on && n > 0 && cv.Scale.MarkerStart == n) cv.Scale.MarkerStart = 1;   // back to 1, 2, 3 …
            }
            cv.NotifyDocChanged();
            LoadMarker();
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
                // a countdown can't start below the number of markers (it would go under 1)
                if (cv.Scale.MarkerReverse) start = Math.Max(Math.Max(1, Measure.Count(cv.Shapes, false)), start);
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
            ShowUppNote();
            nDec.Value = Math.Max(0, Math.Min(6, cv.Scale.Decimals));
            if (cCeil != null) { cCeil.Checked = cv.Scale.Rounding == 1; cFloor.Checked = cv.Scale.Rounding == 2; }
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

        void RoundingEdited()
        {
            int m = cCeil.Checked ? 1 : cFloor.Checked ? 2 : 0;
            if (m != cv.Scale.Rounding)
            {
                cv.PushUndo(cv.Snapshot());
                cv.Scale.Rounding = m;
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

        Font totalFont;
        Font TotalFont { get { if (totalFont == null) totalFont = new Font(lv.Font, FontStyle.Bold); return totalFont; } }

        void RefreshMeasurements()
        {
            bool old = loading;
            loading = true;
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (MeasureRow r in Measure.Rows(cv.Shapes, cv.Scale, false))
            {
                string extra = string.IsNullOrEmpty(r.Label) ? (r.Extra ?? "") : r.Label + (string.IsNullOrEmpty(r.Extra) ? "" : " · " + r.Extra);
                bool symRow = r.Type == "Symbol marker";
                string name = r.Shape != null ? r.Name : symRow ? "Σ" + r.Name : (r.Name == "Letters" ? "ΣA" : "Σ1");     // marker totals
                ListViewItem it = new ListViewItem(new string[] { name, r.Display, extra });
                string full = r.Display + (extra.Length > 0 ? "  ·  " + extra : "");
                it.ToolTipText = full + "\n" + (symRow ? Lang.T("Symbol markers") + " " + r.Name
                    : r.Shape == null ? r.Name + " markers – double-click to say what they count" : "Double-click to label");
                it.Tag = r.Shape;
                if (r.Shape == null) it.Font = TotalFont;      // marker totals: bold, not blue (blue = can be clicked)
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

        // ---------- 1.4.2 Find (Ctrl+F): annotation text, labels, values, tool names and (optionally) the picture's text ----------
        Form findForm;
        TextBox findBox;
        TextRecognition.Result findOcr;
        object findOcrImg;

        class FindHit
        {
            public string Text;
            public Shape Shape;
            public Tool? Tool;
            public Rectangle? Box;
            public override string ToString() { return Text; }
        }

        // Find options (the ⋮ menu, like a PDF editor's): kept while the editor is open
        bool findCase, findWhole, findAccents = true, findLabels = true, findTools = true, findPicture;

        static string StripMarks(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            string d = s.Normalize(NormalizationForm.FormD);
            StringBuilder b = new StringBuilder(d.Length);
            foreach (char c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) b.Append(c);
            return b.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>The search as a pattern, with the Case sensitive and Whole words options.</summary>
        Regex FindRegex(string q)
        {
            string pat = Regex.Escape(q);
            if (findWhole) pat = @"(?<![\p{L}\p{N}_])" + pat + @"(?![\p{L}\p{N}_])";
            return new Regex(pat, findCase ? RegexOptions.CultureInvariant : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>Whether a text matches the search (with Ignore accents, "e" also finds "é").</summary>
        Func<string, bool> FindMatcher(string q)
        {
            Regex rx = FindRegex(q), rx2 = findAccents ? FindRegex(StripMarks(q)) : null;
            return s => !string.IsNullOrEmpty(s) && (rx.IsMatch(s) || (rx2 != null && rx2.IsMatch(StripMarks(s))));
        }

        void ShowFind()
        {
            cv.EndTextEdit(true);
            if (findForm != null && !findForm.IsDisposed) { findForm.Activate(); findBox.Focus(); findBox.SelectAll(); return; }
            Form f = new Form();
            f.Text = Lang.T("Find and replace");
            f.FormBorderStyle = FormBorderStyle.SizableToolWindow;
            f.ShowInTaskbar = false;
            f.AutoScaleMode = AutoScaleMode.None;      // laid out with S() already: no second enlargement from the font
            f.Font = Font;
            f.Owner = this;
            f.StartPosition = FormStartPosition.Manual;
            f.ClientSize = new Size(S(360), S(390));
            f.MinimumSize = new Size(S(300), S(250));
            Point at = PointToScreen(new Point(Math.Max(0, ClientSize.Width - f.Width - S(24)), S(90)));
            f.Location = at;
            int cw = f.ClientSize.Width;
            AnchorStyles lr = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            // labels stay visible once the boxes have text in them (the grey hint disappears as you type)
            int lw = S(84);
            Label lFind = new Label(), lRep = new Label();
            lFind.Text = Lang.T("Find"); lRep.Text = Lang.T("Replace with");
            lFind.AutoSize = lRep.AutoSize = false;
            lFind.TextAlign = lRep.TextAlign = ContentAlignment.MiddleLeft;
            lFind.SetBounds(S(8), S(8), lw, S(24));
            lRep.SetBounds(S(8), S(40), lw, S(24));
            TextBox t = new TextBox();
            t.SetBounds(S(8) + lw, S(8), cw - S(8) - lw - S(104), S(24));
            t.Anchor = lr;
            // Case sensitive and Whole words as switch buttons, so it shows which are on; the rest in the ⋮ menu
            CheckBox bCase = new CheckBox(), bWhole = new CheckBox();
            bCase.Appearance = bWhole.Appearance = Appearance.Button;
            bCase.TextAlign = bWhole.TextAlign = ContentAlignment.MiddleCenter;
            bCase.UseMnemonic = bWhole.UseMnemonic = false;
            bCase.Text = "Aa"; bWhole.Text = "ab|";
            bCase.Checked = findCase; bWhole.Checked = findWhole;
            bCase.SetBounds(cw - S(100), S(7), S(30), S(26));
            bWhole.SetBounds(cw - S(68), S(7), S(30), S(26));
            bCase.Anchor = bWhole.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            tip.SetToolTip(bCase, Lang.T("Case sensitive"));
            tip.SetToolTip(bWhole, Lang.T("Whole words"));
            // ⋮ = the other search options
            Button bOpt = new Button();
            bOpt.Text = "⋮"; bOpt.UseMnemonic = false; bOpt.FlatStyle = FlatStyle.System;
            bOpt.SetBounds(cw - S(36), S(7), S(28), S(26));
            bOpt.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            tip.SetToolTip(bOpt, Lang.T("More search options"));
            TextBox rep = new TextBox();
            rep.SetBounds(S(8) + lw, S(40), cw - S(16) - lw, S(24));
            rep.Anchor = lr;
            Button bRep = new Button(), bAll = new Button();
            bRep.Text = Lang.T("Replace"); bAll.Text = Lang.T("Replace all");
            bRep.FlatStyle = bAll.FlatStyle = FlatStyle.System;
            bRep.SetBounds(S(8) + lw, S(72), S(90), S(26));
            bAll.SetBounds(S(104) + lw, S(72), S(100), S(26));
            tip.SetToolTip(bRep, Lang.T("Replace in the chosen result, then go to the next one"));
            tip.SetToolTip(bAll, Lang.T("Replace in every annotation text (one undo step)"));
            Label lInfo = new Label();
            lInfo.AutoSize = false; lInfo.AutoEllipsis = true;
            lInfo.TextAlign = ContentAlignment.MiddleLeft;
            lInfo.SetBounds(S(8), S(104), cw - S(16), S(20));
            lInfo.Anchor = lr;
            ListBox lb = new ListBox();
            lb.IntegralHeight = false;
            lb.SetBounds(S(8), S(128), cw - S(16), f.ClientSize.Height - S(136));
            lb.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
            f.Controls.Add(lFind); f.Controls.Add(t); f.Controls.Add(bCase); f.Controls.Add(bWhole); f.Controls.Add(bOpt);
            f.Controls.Add(lRep); f.Controls.Add(rep); f.Controls.Add(bRep); f.Controls.Add(bAll); f.Controls.Add(lInfo); f.Controls.Add(lb);
            // Replace works only on text you typed: the buttons are on only when there is some to replace
            Func<string, int> replaceable = q =>
            {
                if (q.Length == 0) return 0;
                Regex rx = FindRegex(q);
                int n = 0;
                foreach (Shape x in cv.Shapes) if (!string.IsNullOrEmpty(x.Text)) n += rx.Matches(x.Text).Count;
                return n;
            };
            // after a replace, the list says so instead of a bare "Nothing found"
            string afterReplace = null;
            Action search = () =>
            {
                string q = t.Text.Trim();
                lb.BeginUpdate();
                lb.Items.Clear();
                List<FindHit> found = FindAll(q, findPicture);
                foreach (FindHit h in found) lb.Items.Add(h);
                if (q.Length > 0 && lb.Items.Count == 0)
                    lb.Items.Add(new FindHit { Text = afterReplace != null ? Lang.T("No more matches for") + " “" + q + "”" : Lang.T("Nothing found") });
                lb.EndUpdate();
                string count = q.Length == 0 ? "" : found.Count + " " + Lang.T(found.Count == 1 ? "result" : "results");
                lInfo.Text = afterReplace != null ? afterReplace + (found.Count > 0 ? " – " + count + " " + Lang.T("left") : "") : count;
                afterReplace = null;
                bRep.Enabled = bAll.Enabled = replaceable(q) > 0;
            };
            bCase.CheckedChanged += (s, e) => { findCase = bCase.Checked; search(); };
            bWhole.CheckedChanged += (s, e) => { findWhole = bWhole.Checked; search(); };
            // the options menu: ticks, like a PDF editor's find bar
            ContextMenuStrip opt = new ContextMenuStrip();
            Func<string, Func<bool>, Action<bool>, ToolStripMenuItem> addOpt = (text, get, set) =>
            {
                ToolStripMenuItem mi = new ToolStripMenuItem(Lang.T(text));
                mi.CheckOnClick = true;
                mi.Checked = get();
                mi.CheckedChanged += (s, e) => { set(mi.Checked); search(); };
                opt.Items.Add(mi);
                return mi;
            };
            addOpt("Ignore accents", () => findAccents, v => findAccents = v);
            opt.Items.Add(new ToolStripSeparator());
            addOpt("Include labels and values", () => findLabels, v => findLabels = v);
            addOpt("Include tool names", () => findTools, v => findTools = v);
            ToolStripMenuItem miPic = null;
            miPic = addOpt("Include the picture's text (OCR)", () => findPicture, v =>
            {
                if (v && !TextRecognition.Available)
                {
                    Flash(Lang.T("Windows has no text-recognition language installed."));
                    findPicture = false;
                    BeginInvoke(new Action(() => miPic.Checked = false));
                    return;
                }
                findPicture = v;
            });
            opt.Closing += (s, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };   // tick several in one go
            bOpt.Click += (s, e) => opt.Show(bOpt, new Point(0, bOpt.Height));
            // Replace: the chosen result (or the first), then on to the next; Replace all: every annotation text
            bRep.Click += (s, e) =>
            {
                string q = t.Text.Trim();
                if (q.Length == 0) return;
                FindHit h = lb.SelectedItem as FindHit;
                if (h == null || h.Shape == null)
                    foreach (object o in lb.Items) { FindHit x = o as FindHit; if (x != null && x.Shape != null && FindRegex(q).IsMatch(x.Shape.Text ?? "")) { h = x; break; } }
                if (h == null || h.Shape == null || !cv.Shapes.Contains(h.Shape)) { lInfo.Text = Lang.T("Nothing to replace"); return; }
                Regex rx = FindRegex(q);
                string old = h.Shape.Text ?? "";
                if (!rx.IsMatch(old)) { lInfo.Text = Lang.T("Only text you typed can be replaced"); return; }
                cv.PushUndo(cv.Snapshot());
                h.Shape.Text = rx.Replace(old, rep.Text.Replace("$", "$$"), 1);
                h.Shape.Cache = null;
                cv.NotifyDocChanged(); cv.Invalidate();
                int idx = lb.SelectedIndex;
                afterReplace = Lang.T("Replaced 1");
                search();
                FindHit first = lb.Items.Count > 0 ? lb.Items[0] as FindHit : null;
                if (first != null && (first.Shape != null || first.Box != null || first.Tool != null))
                    lb.SelectedIndex = Math.Max(0, Math.Min(lb.Items.Count - 1, idx));
            };
            bAll.Click += (s, e) =>
            {
                string q = t.Text.Trim();
                if (q.Length == 0) return;
                Regex rx = FindRegex(q);
                int n = 0;
                foreach (Shape x in cv.Shapes) if (!string.IsNullOrEmpty(x.Text)) n += rx.Matches(x.Text).Count;
                if (n == 0) { lInfo.Text = Lang.T("Nothing to replace"); return; }
                cv.PushUndo(cv.Snapshot());
                foreach (Shape x in cv.Shapes)
                    if (!string.IsNullOrEmpty(x.Text) && rx.IsMatch(x.Text)) { x.Text = rx.Replace(x.Text, rep.Text.Replace("$", "$$")); x.Cache = null; }
                cv.NotifyDocChanged(); cv.Invalidate();
                afterReplace = Lang.T("Replaced") + " " + n;
                search();
                Flash(Lang.T("Replaced") + " " + n + " – Ctrl+Z " + Lang.T("to undo"));
            };
            t.TextChanged += (s, e) => search();
            lb.SelectedIndexChanged += (s, e) => GoTo(lb.SelectedItem as FindHit);
            t.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down)
                {
                    e.SuppressKeyPress = true;
                    if (lb.Items.Count > 0) lb.SelectedIndex = e.KeyCode == Keys.Enter ? (lb.SelectedIndex + 1) % lb.Items.Count : Math.Min(lb.Items.Count - 1, lb.SelectedIndex + 1);
                }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; f.Close(); }
            };
            rep.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; bRep.PerformClick(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; f.Close(); }
            };
            lb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
            f.FormClosed += (s, e) => { cv.FindMark = null; cv.Invalidate(); findForm = null; opt.Dispose(); };
            findForm = f; findBox = t;
            bRep.Enabled = bAll.Enabled = false;
            Theme.Apply(f);                 // dark mode, like every other window
            lInfo.ForeColor = Theme.Dark ? Theme.Label2 : SystemColors.GrayText;
            if (Theme.Dark) lb.BorderStyle = BorderStyle.None;      // the list's own edge drew a bright white frame
            if (Theme.Dark)
                foreach (CheckBox sw in new[] { bCase, bWhole })
                {
                    sw.FlatStyle = FlatStyle.Flat;
                    sw.BackColor = Theme.Fill; sw.ForeColor = Theme.Label;
                    sw.FlatAppearance.BorderColor = Theme.Separator;
                    sw.FlatAppearance.CheckedBackColor = Theme.AccentFill;
                    sw.FlatAppearance.MouseOverBackColor = Theme.FillHover;
                }
            f.Show(this);
            t.Focus();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendCue(IntPtr h, int msg, IntPtr w, string l);

        /// <summary>Grey hint text in an empty text box.</summary>
        static void SetCue(TextBox t, string cue)
        {
            t.HandleCreated += (s, e) => SendCue(t.Handle, 0x1501, (IntPtr)1, cue);     // EM_SETCUEBANNER
        }

        /// <summary>The matches for 'q' (with the Find options): shapes, tools, then the picture's own text.</summary>
        List<FindHit> FindAll(string q, bool ocr)
        {
            List<FindHit> hits = new List<FindHit>();
            if (q.Length == 0) return hits;
            Func<string, bool> has = FindMatcher(q);
            Dictionary<Shape, string> names = Renderer.Labels(cv.Shapes, cv.Scale);
            Dictionary<Shape, MeasureRow> rows = new Dictionary<Shape, MeasureRow>();
            foreach (MeasureRow r in Measure.Rows(cv.Shapes, cv.Scale, true)) if (r.Shape != null) rows[r.Shape] = r;
            foreach (Shape s in cv.Shapes)
            {
                string n; names.TryGetValue(s, out n);
                MeasureRow r; rows.TryGetValue(s, out r);
                string kind = Lang.T(KindName(s));
                string text = (s.Text ?? "").Replace("\r", "").Replace("\n", " ");
                string val = r != null ? r.Display : "";
                if (has(text) || (findLabels && (has(n) || has(kind) || has(val) || (r != null && has(r.Label)))))
                {
                    if (n == text) n = null;      // a symbol marker's label is its own text: show it once
                    string show = (string.IsNullOrEmpty(n) ? "" : n + "  ") + kind + (text.Length > 0 ? " – " + text : val.Length > 0 ? " – " + val : "");
                    hits.Add(new FindHit { Text = show.Length > 90 ? show.Substring(0, 88) + "…" : show, Shape = s });
                }
            }
            if (findTools)
            foreach (KeyValuePair<Tool, ToolStripButton> kv in toolButtons)
            {
                string tipText = kv.Value.ToolTipText ?? "";
                if (has(tipText)) hits.Add(new FindHit { Text = "🛠 " + Lang.T("Tool") + ": " + (tipText.Length > 80 ? tipText.Substring(0, 78) + "…" : tipText), Tool = kv.Key });
            }
            if (ocr && cv.Image != null)
            {
                try
                {
                    if (findOcr == null || !ReferenceEquals(findOcrImg, cv.Image))
                    {
                        Cursor = Cursors.WaitCursor;
                        findOcr = TextRecognition.Read(cv.Image, null);
                        findOcrImg = cv.Image;
                    }
                    foreach (List<TextRecognition.Word> line in findOcr.Lines)
                    {
                        string lt = string.Join(" ", line.ConvertAll(w => w.Text).ToArray());
                        if (!has(lt)) continue;
                        Rectangle box = line[0].Box;
                        foreach (TextRecognition.Word w in line) if (has(w.Text)) { box = w.Box; break; }
                        hits.Add(new FindHit { Text = "🔍 " + (lt.Length > 80 ? lt.Substring(0, 78) + "…" : lt), Box = box });
                    }
                }
                catch (Exception ex) { Flash(Lang.T("Could not read the picture's text:") + " " + ex.Message); }
                finally { Cursor = Cursors.Default; }
            }
            return hits;
        }

        void GoTo(FindHit h)
        {
            if (h == null) return;
            if (h.Tool.HasValue) { cv.FindMark = null; cv.CurrentTool = h.Tool.Value; cv.Invalidate(); return; }
            RectangleF r;
            if (h.Shape != null)
            {
                if (!cv.Shapes.Contains(h.Shape)) return;
                if (cv.CurrentTool != Tool.Select) cv.CurrentTool = Tool.Select;
                cv.SelectShape(h.Shape);
                r = ShapeOps.Bounds(h.Shape);
            }
            else if (h.Box.HasValue) { cv.SelectShape(null); r = h.Box.Value; }
            else return;
            cv.FindMark = r;
            // zoom so the match is well visible, with room round it (never closer than 2×)
            float pad = Math.Max(40f, Math.Max(r.Width, r.Height) * 1.5f);
            RectangleF view = RectangleF.Inflate(r, pad, pad);
            cv.ZoomToRect(view);
            if (cv.Zoom > 2f) cv.ZoomCenterOn(new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f), 2f);
            cv.Invalidate();
        }

        void RemoveBackground()
        {
            cv.EndTextEdit(true);
            int n;
            try { n = cv.RemoveBackground(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (n == 0) Flash(Lang.T("No plain background found at the picture's edges."));
            else Flash(Lang.T("Background removed – save as PNG to keep it see-through (Ctrl+Z undoes it)"));
        }

        void LabelSeries(bool letters)
        {
            Shape m = cv.Shapes.Find(x => x.IsCountingMarker && x.IsLetterMarker == letters);
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
                t += "   (" + cv.Scale.Num(ip.X) + ", " + cv.Scale.Num(ip.Y) + " " + cv.Scale.Unit + ")";
            lPos.Text = t;
        }

        void UpdateUndo()
        {
            bUndo.Enabled = cv.CanUndo;
            bRedo.Enabled = cv.CanRedo;
        }

        void Flash(string msg)
        {
            lHint.Text = Lang.T(msg);
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
            // minimized, not hidden: a hidden window comes back as the newest taskbar button, so the old capture
            // would sit after the new one; minimized it keeps its place and the new capture comes after it
            FormWindowState was = WindowState;
            WindowState = FormWindowState.Minimized;
            List<Form> before = new List<Form>();
            foreach (Form f in Application.OpenForms) before.Add(f);
            App.Later(350, () =>       // after the minimize animation, so the editor is not in the picture
            {
                try { App.CaptureRegionToEditor(); }
                finally
                {
                    if (!IsDisposed)
                    {
                        // the new capture opened in its own editor: this one stays on the taskbar, out of its way;
                        // cancelled (or copied / saved without an editor): this one comes back
                        Form opened = null;
                        foreach (Form f in Application.OpenForms) if (f is EditorForm && f != this && !before.Contains(f)) opened = f;
                        if (opened != null) { if (!opened.IsDisposed) opened.Activate(); }
                        else { WindowState = was == FormWindowState.Minimized ? FormWindowState.Normal : was; Activate(); }
                    }
                }
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

        void OpenInPaint()
        {
            cv.EndTextEdit(true);
            try { using (Bitmap b = cv.Flatten(Color.White)) App.OpenInPaint(b); }
            catch (Exception ex) { MessageBox.Show(this, Lang.T("Could not open Paint:") + " " + ex.Message, "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        void PrintPicture()
        {
            cv.EndTextEdit(true);
            try { using (Bitmap b = cv.Flatten(Color.White)) App.PrintPicture(this, b, Text); }
            catch (Exception ex) { MessageBox.Show(this, Lang.T("Could not print:") + " " + ex.Message, "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        /// <summary>Ctrl+Plus / Ctrl+Minus: 10 px more or less on every side; new room takes the colour last chosen in Extend canvas.</summary>
        void StepCanvas(bool grow)
        {
            cv.EndTextEdit(true);
            if (cv.Image == null) return;
            try
            {
                if (grow)
                {
                    int ci = Settings.GetInt("ExtendColour", 0);
                    Color back = ci == 2 ? Color.Transparent : ci == 3 ? Color.Black : Color.White;
                    if (ci == 1) { try { back = cv.Image.GetPixel(0, 0); } catch (Exception) { } }
                    cv.ExtendCanvas(10, 10, 10, 10, back);
                }
                else if (!cv.ShrinkCanvas(10)) { Flash(Lang.T("No added room left – Ctrl+Minus stops at the picture's own edge.")); return; }
                Flash(cv.Image.Width + " × " + cv.Image.Height + " px");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        /// <summary>Adjust → Extend canvas: room around the picture (for notes or arrows outside it).</summary>
        void ExtendCanvas()
        {
            cv.EndTextEdit(true);
            if (cv.Image == null) return;
            Color edge;
            try { edge = cv.Image.GetPixel(0, 0); } catch (Exception) { edge = Color.White; }
            int l, t, r, b; Color back;
            if (!Dialogs.ExtendCanvas(this, edge, out l, out t, out r, out b, out back)) return;
            try { cv.ExtendCanvas(l, t, r, b, back); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Error); }
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
                // an opened project: Ctrl+S saves over that .snapm (no new picture or project)
                if (projectPath != null)
                {
                    SaveTo(projectPath);
                    if (Output.CopyPath) { try { Clipboard.SetText(projectPath); } catch { } }
                    Flash("Saved " + Path.GetFileName(projectPath) + (Output.CopyPath ? "  (path copied)" : ""));
                    return;
                }
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
        string projectPath;      // the .snapm this editor was opened from (Ctrl+S saves over it)
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
                case Keys.Control | Keys.L: if (!inText) { ToggleLock(); return true; } break;
                // 1.4.2: Ctrl+I = invert, Ctrl+G = grayscale (in a text box Ctrl+I stays italic)
                case Keys.Control | Keys.I: if (!inText) { cv.ImageOp("invert"); Flash(Lang.T("Colours inverted (Ctrl+Z undoes it)")); return true; } break;
                case Keys.Control | Keys.G: if (!inText) { cv.ImageOp("gray"); Flash(Lang.T("Grayscale (Ctrl+Z undoes it)")); return true; } break;
                case Keys.Control | Keys.F: if (!inText) { ShowFind(); return true; } break;
                case Keys.F4: TogglePanel(); return true;
                case Keys.Alt | Keys.L: if (!cv.IsEditingText) { PickColor(true); return true; } break;
                case Keys.Alt | Keys.F: if (!cv.IsEditingText) { PickColor(false); return true; } break;
                case Keys.Alt | Keys.Shift | Keys.D0:
                    if (StyleTarget() != null) { ApplyStyle(x => x.FillOpacity = 0); LoadStyle(); Flash("No fill"); }
                    return true;
                case Keys.Control | Keys.S: QuickSave(); return true;
                case Keys.Control | Keys.Shift | Keys.S: SaveAs(); return true;
                case Keys.Control | Keys.Shift | Keys.M: if (!inText) { ToggleMatch(); return true; } break;
                case Keys.Control | Keys.Shift | Keys.C: if (!inText) { PickScreenColor(); return true; } break;
                case Keys.Control | Keys.O: OpenFile(); return true;
                case Keys.Control | Keys.N: NewCapture(); return true;
                case Keys.Control | Keys.P: Pin(); return true;
                case Keys.Home: if (!inText) { cv.Fit(); return true; } break;
                case Keys.Control | Keys.D0: cv.Fit(); return true;
                case Keys.Control | Keys.D1: cv.ZoomCenter(1f); return true;
                case Keys.Control | Keys.Oemplus:
                case Keys.Control | Keys.Add: StepCanvas(true); return true;           // 1.4: 10 px more round the picture (zoom: Ctrl+wheel)
                case Keys.Control | Keys.OemMinus:
                case Keys.Control | Keys.Subtract: StepCanvas(false); return true;     // 10 px less
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (closeConfirmed) { e.Cancel = false; base.OnFormClosing(e); e.Cancel = false; return; }
            try { cv.EndTextEdit(true); cv.EndMatch(); } catch (Exception ex) { CloseLog(ex); }
            if (dirty && cv.Shapes.Count > 0 && e.CloseReason == CloseReason.UserClosing)
            {
                // short and plain, like Windows' own "save changes?" box; "Don't save" says what it does
                int r = Dialogs.Choose(this, "SnapMeasure", "Save your drawing before closing?", "Save…", "Don't save", "Cancel");
                if (r == 2 || r < 0 || (r == 0 && !SaveAs())) { e.Cancel = true; return; }
            }
            if (StylePrefs.On) StylePrefs.Save(cv);       // the styles used here become the ones new editors start with
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
