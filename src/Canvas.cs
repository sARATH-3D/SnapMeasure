using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

namespace SnapMeasure
{
    public class DocState
    {
        public Bitmap Image;
        public List<Shape> Shapes;
        public ScaleInfo Scale;
        public Rectangle? Crop;
        public int[] AddedRoom;
    }

    public class Canvas : Control
    {
        public Bitmap Image;
        public List<Shape> Shapes = new List<Shape>();
        public new ScaleInfo Scale = new ScaleInfo();
        public float Zoom = 1f;
        public PointF Pan;
        public float Dpi = 1f;
        public readonly Dictionary<Tool, Shape> Styles = DefaultStyles();

        /// <summary>Selected shapes (in selection order). Selected = the primary (last) one.</summary>
        public readonly List<Shape> Sel = new List<Shape>();
        public Shape Selected
        {
            get { return Sel.Count > 0 ? Sel[Sel.Count - 1] : null; }
            set { Sel.Clear(); if (value != null) Sel.Add(value); }
        }

        /// <summary>Adjustable crop: the part of the image that is shown and exported (null = whole image).</summary>
        public Rectangle? CropRect { get; set; }

        // rulers + guides (view aids; guides are saved in .snapm projects)
        bool showRulers = true;
        public readonly List<float> HGuides = new List<float>(), VGuides = new List<float>();

        public Func<string, string, string> AskText;     // (title, initial) -> text, null = cancelled (used for dimension/area labels)
        public Func<string, string, string> AskNote;     // same, several lines (notes, stamps)
        public Func<string, string, string> AskSymbol;   // 1.4.2: one short text with the symbol picker (symbol markers)
        public Func<double, bool> AskCalibration;        // (pixel length) -> true if scale was changed
        public Action<string> Message;                   // short status-bar message
        public Action<Rectangle> OcrArea;                // OCR tool: copy the text inside this image rectangle
        public event EventHandler StateChanged;           // drawing started / point added / finished: the status-bar guide follows
        public Func<Rectangle, List<Rectangle>> FindWords;   // text recognition for snapping text markup (may be null)
        public event EventHandler DocChanged, SelectionChanged, ViewChanged, ToolChanged;
        public Action<PointF> CursorMoved;

        Tool tool = Tool.Select;
        Shape creating;
        Handle? dragH;
        Shape dragOrig;
        bool dragBody, changedDuringDrag, panning, spaceDown;
        Shape pendingSingle;                     // click on a member of a multi-selection without dragging -> select just it
        PointF dragLast, mouseImg;
        Point mouseScr = new Point(-1000, -1000);
        PointF? snapPt;
        Point panLast;
        DocState before;
        readonly List<DocState> undo = new List<DocState>(), redo = new List<DocState>();

        // marquee selection
        bool marquee;
        PointF marqA, marqB;

        // crop editing
        int cropDrag = -1;                       // 0..7 = handle, 8 = move
        Rectangle cropOrig;
        PointF cropStart;

        // guide dragging
        int guideAxis;                           // 0 none, 1 horizontal guide (y), 2 vertical guide (x)
        int guideIdx = -1;
        float guideVal;
        bool guideOverRuler;

        // paste offsets
        string lastPasteXml;
        int pasteCount;

        // inline text editing
        TextBox editBox;
        Shape editShape;
        bool editIsNew;
        DocState editBefore;
        string editOrigText;

        static TextureBrush checker;

        public Canvas()
        {
            HookSideWheel();
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            // dark mode: the drawing area is a deeper well than the bars round it (44), so the two are told apart at a glance
            BackColor = Theme.Dark ? Color.FromArgb(22, 22, 24) : Color.FromArgb(52, 54, 58);
            TabStop = true;
        }

        // ---------- styles ----------
        static Shape St(ShapeKind k, Color stroke, float width, Color fill, int fillOp, float font)
        {
            Shape s = new Shape();
            s.Kind = k; s.Stroke = stroke; s.Width = width; s.Fill = fill; s.FillOpacity = fillOp; s.FontSize = font;
            return s;
        }

        static Dictionary<Tool, Shape> DefaultStyles()
        {
            Color red = Color.FromArgb(230, 30, 30), blue = Color.FromArgb(0, 120, 215), amber = Color.FromArgb(255, 190, 0);
            Dictionary<Tool, Shape> d = new Dictionary<Tool, Shape>();
            d[Tool.Rect] = St(ShapeKind.Rect, red, 3, Color.Yellow, 0, 16);
            d[Tool.Ellipse] = St(ShapeKind.Ellipse, red, 3, Color.Yellow, 0, 16);
            d[Tool.Line] = St(ShapeKind.Line, red, 3, Color.Yellow, 0, 16);
            d[Tool.Line].Heads = 0;
            d[Tool.Arrow] = St(ShapeKind.Arrow, red, 3, Color.Yellow, 0, 16);
            d[Tool.Arrow].HeadStart = (int)HeadStyle.ClosedFilled;      // like an AutoCAD leader: the head sits where you click first
            d[Tool.Arrow].HeadEnd = 0;
            d[Tool.Freehand] = St(ShapeKind.Freehand, red, 3, Color.Yellow, 0, 16);
            d[Tool.Freehand].HeadEnd = 0;                     // plain stroke; arrowheads are optional
            Shape marker = St(ShapeKind.Freehand, Color.FromArgb(255, 225, 0), 14, Color.Yellow, 0, 16);
            marker.Mode = 1;                                  // highlighter pen: thick, see-through yellow
            marker.StrokeOpacity = 45;
            marker.HeadStart = marker.HeadEnd = 0;
            d[Tool.Marker] = marker;
            d[Tool.Text] = St(ShapeKind.Text, red, 0, Color.White, 0, 20);   // line width 0 = no frame
            d[Tool.Polygon] = St(ShapeKind.Polygon, amber, 2, Color.Yellow, 35, 16);
            d[Tool.PolyDraw] = St(ShapeKind.Polygon, red, 2, Color.Yellow, 0, 16);
            d[Tool.PolyDraw].Mode = 1;                        // open polyline; heads optional; tick Closed for a polygon
            d[Tool.PolyDraw].HeadEnd = 0;
            d[Tool.Dimension] = St(ShapeKind.Dimension, red, 1.5f, Color.White, 0, 15);
            d[Tool.Dimension].HeadStart = d[Tool.Dimension].HeadEnd = (int)HeadStyle.ClosedFilled;
            Color purple = Color.FromArgb(130, 60, 200), green = Color.FromArgb(30, 140, 60);
            d[Tool.Polyline] = St(ShapeKind.Polyline, purple, 2.5f, Color.White, 0, 15);
            d[Tool.Perimeter] = St(ShapeKind.Perimeter, green, 2.5f, Color.White, 0, 15);
            d[Tool.Perimeter].Mode = 1;                       // open path by default; tick "Closed" for a perimeter
            d[Tool.Coord] = St(ShapeKind.Coord, Color.FromArgb(20, 90, 170), 1.5f, Color.White, 0, 14);
            d[Tool.Cutout] = St(ShapeKind.Polygon, red, 2, red, 15, 15);
            d[Tool.Cloud] = St(ShapeKind.Cloud, red, 2.5f, Color.Yellow, 0, 22);      // text size = callout size
            d[Tool.Stamp] = St(ShapeKind.Stamp, Stamps.Colors[0], 3, Color.White, 0, 16);
            d[Tool.Note] = St(ShapeKind.Note, Color.FromArgb(150, 110, 0), 1, Color.FromArgb(255, 214, 64), 100, 16);
            d[Tool.Markup] = St(ShapeKind.Markup, red, 2, Color.Yellow, 45, 16);
            d[Tool.Area] = St(ShapeKind.Area, blue, 2, blue, 22, 15);
            d[Tool.Angle] = St(ShapeKind.Angle, blue, 2, Color.White, 0, 15);
            d[Tool.Radius] = St(ShapeKind.Radius, Color.FromArgb(0, 130, 140), 1.8f, Color.White, 0, 15);
            d[Tool.Radius].Mode = 2;      // new radius measurements show the diameter (first entry of Show)
            d[Tool.Arc] = St(ShapeKind.Arc, red, 3, Color.Yellow, 0, 16);
            // markers look like their buttons: numbers white on a violet disc, letters violet in a violet ring
            d[Tool.Counter] = St(ShapeKind.Counter, Color.White, 2.5f, Icons.CountCol, 100, 18);
            Shape letter = St(ShapeKind.Counter, Icons.CountCol, 2.5f, Color.White, 100, 18);
            letter.Mode = 1;
            d[Tool.Letter] = letter;
            // 1.4.2 symbol marker: dark purple character and ring on a light purple tint, like its button
            Shape sym = St(ShapeKind.Counter, Icons.SymbolInk, 2f, Icons.SymbolFill, 100, 18);
            sym.Mode = 2;
            sym.Text = "Ø";
            d[Tool.Symbol] = sym;
            // fill (paint bucket): the fill colour, no outline
            d[Tool.Fill] = St(ShapeKind.Polygon, Color.FromArgb(0, 120, 212), 0, Color.FromArgb(0, 120, 212), 60, 16);
            d[Tool.Pixelate] = St(ShapeKind.Pixelate, Color.Gray, 3, Color.Gray, 0, 16);
            // 1.4.2: no border by default; the editor shows a thin magenta line round it that is never printed
            d[Tool.Highlight] = St(ShapeKind.Highlight, red, 0, Color.Yellow, 45, 16);
            d[Tool.Highlight].Mode = (int)HighlightMode.Marker;        // Focus starts as the see-through highlight box
            d[Tool.Highlight].Strength = 2;                              // magnify ×2, pixelate 8 px (Strength slider)
            d[Tool.Pixelate].Strength = 3;
            Shape bub = St(ShapeKind.Bubble, Color.FromArgb(40, 40, 40), 2, Color.White, 100, 16);
            bub.Align = 1;
            d[Tool.Bubble] = bub;
            return d;
        }

        public static Tool ToolFor(Shape s)
        {
            if (s.Kind == ShapeKind.Counter && s.Mode == 1) return Tool.Letter;
            if (s.IsSymbolMarker) return Tool.Symbol;
            if (s.IsMarkerPen) return Tool.Marker;
            if (s.Kind == ShapeKind.Polygon && s.IsOpenPath) return Tool.PolyDraw;
            if (IsFillShape(s)) return Tool.Fill;
            return ToolFor(s.Kind);
        }

        /// <summary>1.4.2: an area made by the Fill tool (a closed polygon with no line).</summary>
        public static bool IsFillShape(Shape s)
        {
            return s.Kind == ShapeKind.Polygon && !s.IsOpenPath && s.Width < 0.5f;
        }

        public static Tool ToolFor(ShapeKind k)
        {
            switch (k)
            {
                case ShapeKind.Rect: return Tool.Rect;
                case ShapeKind.Ellipse: return Tool.Ellipse;
                case ShapeKind.Line: return Tool.Line;
                case ShapeKind.Arrow: return Tool.Arrow;
                case ShapeKind.Freehand: return Tool.Freehand;
                case ShapeKind.Text: return Tool.Text;
                case ShapeKind.Polygon: return Tool.Polygon;
                case ShapeKind.Dimension: return Tool.Dimension;
                case ShapeKind.Area: return Tool.Area;
                case ShapeKind.Angle: return Tool.Angle;
                case ShapeKind.Arc: return Tool.Arc;
                case ShapeKind.Radius: return Tool.Radius;
                case ShapeKind.Counter: return Tool.Counter;
                case ShapeKind.Highlight: return Tool.Highlight;
                case ShapeKind.Bubble: return Tool.Bubble;
                case ShapeKind.Cloud: return Tool.Cloud;
                case ShapeKind.Stamp: return Tool.Stamp;
                case ShapeKind.Note: return Tool.Note;
                case ShapeKind.Markup: return Tool.Markup;
                case ShapeKind.Polyline: return Tool.Polyline;
                case ShapeKind.Perimeter: return Tool.Perimeter;
                case ShapeKind.Coord:
                case ShapeKind.Origin: return Tool.Coord;
                default: return Tool.Pixelate;
            }
        }

        public Tool CurrentTool
        {
            get { return tool; }
            set
            {
                EndTextEdit(true);
                EndMatch();
                if (zoomBox) EndZoomBox();
                if (creating != null && creating.IsPoly && tool != value) FinishPoly();
                creating = null;
                marquee = false;
                Tool old = tool;
                tool = value;
                // the Focus tool always starts as a rectangular highlight box; a shape or mode picked on the style bar lasts while the tool is in use
                if (value == Tool.Highlight && old != Tool.Highlight) ResetFocusStyle();
                // a new drawing tool: let go of the last shape, so the style bar shows the tool's own style
                // (the cut-out keeps its area selected, and crop / text recognition don't draw shapes)
                if (value != old && value != Tool.Select && value != Tool.Cutout && value != Tool.Crop && value != Tool.Ocr && Sel.Count > 0)
                {
                    Sel.Clear();
                    FireSel();
                }
                Cursor = tool == Tool.Select ? Cursors.Default : (tool == Tool.Text ? Cursors.IBeam : Cursors.Cross);
                if (ToolChanged != null) ToolChanged(this, EventArgs.Empty);
                if (CropRect.HasValue && ((old == Tool.Crop) != (value == Tool.Crop))) Fit();   // show the whole image while cropping
                Invalidate();
            }
        }

        /// <summary>Focus tool defaults: rectangle, highlight box (the colours the user picked stay).</summary>
        void ResetFocusStyle()
        {
            Shape f = Styles[Tool.Highlight];
            f.Form = (int)HighlightForm.Rectangle;
            f.Mode = (int)HighlightMode.Marker;
            if (f.FillOpacity < 20) f.FillOpacity = 45;
        }

        Shape NewShape(Tool t, PointF p)
        {
            Shape s;
            if (Styles.ContainsKey(t)) s = Styles[t].Clone();
            else { s = new Shape(); s.Kind = (t == Tool.Crop || t == Tool.Ocr) ? ShapeKind.Rect : ShapeKind.Line; s.Width = 1; }
            s.Pts.Clear();
            s.Holes = null;
            s.Text = "";
            s.Angle = 0;
            s.Pts.Add(p);
            if (s.Kind == ShapeKind.Stamp) s.Text = StampText(s.Mode, "");
            return s;
        }

        /// <summary>Open lengths made by the Area tool in 1.3.0 (Closed unticked): ticking Closed on them gives the area
        /// back; also turns selected areas into such lengths (one undo step). The Area tool itself always draws areas.</summary>
        public bool SetAreaClosed(bool closed)
        {
            List<Shape> t = Sel.FindAll(x => closed ? (x.Kind == ShapeKind.Perimeter && x.FromArea > 0 && x.Pts.Count >= 3) : x.Kind == ShapeKind.Area);
            if (t.Count == 0) return false;
            PushUndo(Snapshot());
            foreach (Shape x in t)
            {
                if (closed) { x.Kind = ShapeKind.Area; x.Mode = Math.Max(0, Math.Min(3, x.FromArea - 1)); x.FromArea = 0; }
                else { x.FromArea = Math.Max(0, Math.Min(2, x.Mode)) + 1; x.Kind = ShapeKind.Perimeter; x.Mode = 1; }
                x.Cache = null;
            }
            FireDoc(); FireSel(); Invalidate();
            return true;
        }

        /// <summary>Stamp text: the stamp word, then the date (and anything the user adds below).</summary>
        public static string StampText(int mode, string custom)
        {
            string main = mode >= 0 && mode < Stamps.Names.Length - 1 ? Stamps.Names[mode] : (string.IsNullOrEmpty(custom) ? "CHECKED" : custom);
            string date = Stamps.FormatDate(DateTime.Now, Settings.GetInt("StampDate", 0));
            return date.Length > 0 ? main + "\n" + date : main;
        }

        // ---------- coordinates & view ----------
        public PointF ToImg(Point p) { return new PointF((p.X - Pan.X) / Zoom, (p.Y - Pan.Y) / Zoom); }
        public PointF ToScr(PointF p) { return new PointF(p.X * Zoom + Pan.X, p.Y * Zoom + Pan.Y); }
        float Tol { get { return 6f * Dpi / Zoom; } }
        float RotDist { get { return 30f * Dpi / Zoom; } }

        public bool ShowRulers
        {
            get { return showRulers; }
            set { showRulers = value; ClampPan(); Invalidate(); }
        }
        public float RulerSize { get { return showRulers ? (float)Math.Round(20f * Dpi) : 0f; } }

        /// <summary>The part of the image the view shows: the crop, or the whole image while the crop tool is active.</summary>
        public RectangleF ViewRect
        {
            get
            {
                if (Image == null) return RectangleF.Empty;
                if (CropRect.HasValue && tool != Tool.Crop) return CropRect.Value;
                return new RectangleF(0, 0, Image.Width, Image.Height);
            }
        }

        void ClampPan()
        {
            if (Image == null) return;
            RectangleF vr = ViewRect;
            float w = vr.Width * Zoom, h = vr.Height * Zoom, m = 60f * Dpi;
            if (LockInside)
            {
                // lock on: the view never scrolls past the picture's edges; a picture smaller than the window stays centred
                float rs = RulerSize, aw = Width - rs, ah = Height - rs;
                float px = w <= aw ? rs + (aw - w) / 2f - vr.X * Zoom : Geo.Clamp(Pan.X, Width - w - vr.X * Zoom, rs - vr.X * Zoom);
                float py = h <= ah ? rs + (ah - h) / 2f - vr.Y * Zoom : Geo.Clamp(Pan.Y, Height - h - vr.Y * Zoom, rs - vr.Y * Zoom);
                Pan = new PointF(px, py);
                return;
            }
            Pan = new PointF(Geo.Clamp(Pan.X, m - w - vr.X * Zoom, Width - m - vr.X * Zoom),
                             Geo.Clamp(Pan.Y, m - h - vr.Y * Zoom, Height - m - vr.Y * Zoom));
        }

        void ViewMoved()
        {
            ClampPan();
            LayoutEditBox();
            Invalidate();
            if (ViewChanged != null) ViewChanged(this, EventArgs.Empty);
        }

        public void ZoomAt(float z, Point at)
        {
            z = Math.Max(0.05f, Math.Min(32f, z));
            PointF ip = ToImg(at);
            Zoom = z;
            Pan = new PointF(at.X - ip.X * Zoom, at.Y - ip.Y * Zoom);
            ViewMoved();
        }

        public void ZoomCenter(float z) { ZoomAt(z, new Point(Width / 2, Height / 2)); }

        public void ZoomStep(int dir)
        {
            float[] steps = { 0.05f, 0.1f, 0.125f, 0.25f, 0.33f, 0.5f, 0.67f, 0.75f, 1f, 1.5f, 2f, 3f, 4f, 6f, 8f, 12f, 16f, 24f, 32f };
            float z = Zoom;
            if (dir > 0) { foreach (float s in steps) if (s > Zoom + 0.001f) { z = s; break; } }
            else { for (int i = steps.Length - 1; i >= 0; i--) if (steps[i] < Zoom - 0.001f) { z = steps[i]; break; } }
            ZoomCenter(z);
        }

        /// <summary>Shift while placing point n of a shape: 15° directions, but the angle tool works in 45° steps
        /// (the vertex in 45° directions from the first arm's end, the last arm 45°, 90°, 135° … from the first).</summary>
        PointF ShiftSnap(Shape c, int n, PointF sp)
        {
            if (c.Kind == ShapeKind.Angle)
                return n >= 3 ? AngleArmSnap(c.Pts[1], c.Pts[0], sp) : Geo.SnapAngle(c.Pts[0], sp, 45);
            return Geo.SnapAngle(c.Pts[n - 2], sp, 15);
        }

        /// <summary>Angle arm with Shift: horizontal, vertical or 45° on the page, or a 45° step from the other arm –
        /// whichever is nearest the pointer (so a tilted first arm still allows a level or plumb second arm).</summary>
        internal static PointF AngleArmSnap(PointF vertex, PointF other, PointF p)
        {
            PointF onPage = Geo.SnapAngle(vertex, p, 45);
            PointF fromArm = SnapArm(vertex, other, p, 45);
            return Geo.Dist(onPage, p) <= Geo.Dist(fromArm, p) ? onPage : fromArm;
        }

        /// <summary>p moved round the vertex so the angle between (vertex→other) and (vertex→p) is a multiple of 'step' degrees; its distance stays.</summary>
        internal static PointF SnapArm(PointF vertex, PointF other, PointF p, double step)
        {
            double d = Geo.Dist(vertex, p);
            if (d < 1e-6 || Geo.Dist(vertex, other) < 1e-6) return p;
            double a0 = Math.Atan2(other.Y - vertex.Y, other.X - vertex.X) * 180.0 / Math.PI;
            double a = Math.Atan2(p.Y - vertex.Y, p.X - vertex.X) * 180.0 / Math.PI;
            double rel = Math.Round((a - a0) / step) * step;
            double r = (a0 + rel) * Math.PI / 180.0;
            return new PointF((float)(vertex.X + d * Math.Cos(r)), (float)(vertex.Y + d * Math.Sin(r)));
        }

        /// <summary>Fit the visible image (or crop) in the window and centre it (Home key).</summary>
        public void Fit()
        {
            if (Image == null || Width < 10 || Height < 10) return;
            RectangleF vr = ViewRect;
            float rs = RulerSize, aw = Width - rs, ah = Height - rs;
            float z = Math.Min((aw - 24f) / vr.Width, (ah - 24f) / vr.Height);
            Zoom = Math.Max(0.05f, Math.Min(1f, z));
            Pan = new PointF(rs + (aw - vr.Width * Zoom) / 2f - vr.X * Zoom, rs + (ah - vr.Height * Zoom) / 2f - vr.Y * Zoom);
            ViewMoved();
        }

        // ---------- undo ----------
        public DocState Snapshot()
        {
            DocState s = new DocState();
            s.Image = Image;
            s.Shapes = Shapes.ConvertAll(x => x.Clone());
            s.Scale = Scale.Clone();
            s.Crop = CropRect;
            s.AddedRoom = (int[])AddedRoom.Clone();
            return s;
        }

        /// <summary>1.4.2: room added round the picture (left, top, right, bottom) by Ctrl+Plus or Extend canvas.
        /// Ctrl+Minus only takes this room away again: it never cuts into the picture itself.</summary>
        public int[] AddedRoom = new int[4];

        public void PushUndo(DocState s)
        {
            undo.Add(s);
            List<DocState> dropped = new List<DocState>(redo);
            if (undo.Count > 300) { dropped.Add(undo[0]); undo.RemoveAt(0); }
            redo.Clear();
            foreach (DocState d in dropped) ReleaseImage(d.Image);
        }

        /// <summary>Disposes a bitmap that is no longer the current picture or part of any undo / redo step.</summary>
        void ReleaseImage(Bitmap b)
        {
            if (b == null || ReferenceEquals(b, Image)) return;
            if ((before != null && ReferenceEquals(before.Image, b)) || (editBefore != null && ReferenceEquals(editBefore.Image, b))) return;
            foreach (DocState d in undo) if (ReferenceEquals(d.Image, b)) return;
            foreach (DocState d in redo) if (ReferenceEquals(d.Image, b)) return;
            Renderer.ReleaseCache(b);
            b.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (layer != null) { layer.Dispose(); layer = null; }
                if (pic != null) { pic.Dispose(); pic = null; }
                if (picTimer != null) { picTimer.Dispose(); picTimer = null; }
                if (editBox != null)
                {
                    Font ef = editBox.Font;
                    Controls.Remove(editBox);
                    editBox.Dispose();
                    editBox = null;
                    if (ef != null) ef.Dispose();
                }
                // several undo steps share one bitmap when only the shapes changed: dispose each bitmap once
                List<Bitmap> owned = new List<Bitmap>();
                Action<Bitmap> own = b => { if (b != null && !owned.Exists(x => ReferenceEquals(x, b))) owned.Add(b); };
                own(Image);
                foreach (DocState d in undo) own(d.Image);
                foreach (DocState d in redo) own(d.Image);
                if (before != null) own(before.Image);
                if (editBefore != null) own(editBefore.Image);
                undo.Clear(); redo.Clear(); before = null; editBefore = null;
                Image = null;
                foreach (Bitmap b in owned) { Renderer.ReleaseCache(b); b.Dispose(); }
            }
            base.Dispose(disposing);
        }

        void Restore(DocState s)
        {
            // keep the same shapes selected (by position) so the style panel shows their restored values
            List<int> idx = new List<int>();
            List<ShapeKind> kinds = new List<ShapeKind>();
            foreach (Shape x in Sel) { idx.Add(Shapes.IndexOf(x)); kinds.Add(x.Kind); }
            bool cropChanged = s.Crop != CropRect;
            Image = s.Image; Shapes = s.Shapes; Scale = s.Scale; CropRect = s.Crop;
            AddedRoom = s.AddedRoom != null ? (int[])s.AddedRoom.Clone() : new int[4];
            Sel.Clear();
            for (int i = 0; i < idx.Count; i++)
                if (idx[i] >= 0 && idx[i] < Shapes.Count && Shapes[idx[i]].Kind == kinds[i]) Sel.Add(Shapes[idx[i]]);
            creating = null;
            if (cropChanged) Fit();
            FireDoc(); FireSel(); Invalidate();
        }

        public bool CanUndo { get { return undo.Count > 0; } }
        public bool CanRedo { get { return redo.Count > 0; } }

        public void Undo()
        {
            EndTextEdit(true);
            if (creating != null) { creating = null; Invalidate(); return; }
            if (undo.Count == 0) return;
            redo.Add(Snapshot());
            DocState s = undo[undo.Count - 1]; undo.RemoveAt(undo.Count - 1);
            Restore(s);
        }

        public void Redo()
        {
            EndTextEdit(true);
            if (redo.Count == 0) return;
            undo.Add(Snapshot());
            DocState s = redo[redo.Count - 1]; redo.RemoveAt(redo.Count - 1);
            Restore(s);
        }

        /// <summary>Moves/shrinks a new bubble so the body and its tail lie inside 'area' (the visible image).
        /// Returns the tail point.</summary>
        public static PointF FitBubble(ref RectangleF r, RectangleF area)
        {
            const float m = 2f;
            float W = area.Width, H = area.Height, ox = area.X, oy = area.Y;
            r.Width = Math.Max(8f, Math.Min(r.Width, W - 2 * m));
            r.Height = Math.Max(8f, Math.Min(r.Height, H - 2 * m));
            float tail = Math.Max(28f, r.Height * 0.55f);
            float room = H - 2 * m - r.Height;               // vertical space left for the tail
            if (tail > room) tail = Math.Max(0f, room);
            r.X = Geo.Clamp(r.X, ox + m, ox + W - m - r.Width);
            r.Y = Geo.Clamp(r.Y, oy + m, oy + H - m - r.Height);
            float tx = r.X + r.Width * 0.22f;
            if (r.Bottom + tail <= oy + H - m) return new PointF(tx, r.Bottom + tail);   // tail below (default)
            if (r.Y - tail >= oy + m) return new PointF(tx, r.Y - tail);                 // no room below: point up
            r.Y = oy + H - m - r.Height - tail;                                          // make room below
            return new PointF(tx, r.Bottom + tail);
        }

        void FireDoc() { if (DocChanged != null) DocChanged(this, EventArgs.Empty); }
        void FireSel() { if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty); }

        public void NotifyDocChanged() { FireDoc(); Invalidate(); }

        void AddShape(Shape s)
        {
            PushUndo(Snapshot());
            Shapes.Add(s);
            KeepInside(new List<Shape> { s });
            Selected = s;
            FireDoc(); FireSel(); Invalidate();
        }

        List<Shape> SelInOrder() { return Shapes.FindAll(x => Sel.Contains(x)); }

        public void DeleteSelected()
        {
            if (Sel.Count == 0) return;
            PushUndo(Snapshot());
            foreach (Shape s in Sel) Shapes.Remove(s);
            Sel.Clear();
            FireDoc(); FireSel(); Invalidate();
        }

        public void SelectShape(Shape s)
        {
            Selected = s;
            FireSel(); Invalidate();
        }

        public void SelectAll()
        {
            EndTextEdit(true);
            if (tool != Tool.Select) CurrentTool = Tool.Select;
            Sel.Clear();
            foreach (Shape s in Shapes) if (!HiddenByLock(s)) Sel.Add(s);
            FireSel(); Invalidate();
        }

        // ---------- clipboard & order ----------
        public static bool ClipboardHasShapes
        {
            get { try { return Clipboard.ContainsData(ProjectFile.ClipFormat); } catch { return false; } }
        }

        public bool CopySelection()
        {
            if (Sel.Count == 0) return false;
            try
            {
                DataObject d = new DataObject();
                d.SetData(ProjectFile.ClipFormat, ProjectFile.ShapesToXml(SelInOrder()));
                Clipboard.SetDataObject(d, true);
                return true;
            }
            catch { return false; }
        }

        public void CutSelection() { if (CopySelection()) DeleteSelected(); }

        /// <summary>Pastes shapes from the clipboard. at = put the group's centre there; null = offset from the original.</summary>
        public bool PasteShapes(PointF? at)
        {
            string xml;
            try { xml = Clipboard.GetData(ProjectFile.ClipFormat) as string; } catch { return false; }
            List<Shape> list;
            try { list = ProjectFile.ShapesFromXml(xml); } catch { return false; }
            if (list.Count == 0) return false;
            EndTextEdit(true);
            float dx, dy;
            if (at.HasValue)
            {
                RectangleF b = GroupBounds(list);
                PointF c = Geo.Center(b);
                dx = at.Value.X - c.X; dy = at.Value.Y - c.Y;
            }
            else
            {
                pasteCount = xml == lastPasteXml ? pasteCount + 1 : 1;
                lastPasteXml = xml;
                dx = dy = 12f * pasteCount;
            }
            PushUndo(Snapshot());
            Sel.Clear();
            foreach (Shape s in list) { s.Offset(dx, dy); Shapes.Add(s); Sel.Add(s); }
            KeepInside(list);
            if (tool != Tool.Select) CurrentTool = Tool.Select;
            FireDoc(); FireSel(); Invalidate();
            return true;
        }

        public static bool ClipboardHasImage
        {
            get { try { return Clipboard.ContainsImage() || Clipboard.ContainsFileDropList(); } catch { return false; } }
        }

        /// <summary>Pastes a picture from the clipboard (image or copied image file) as a movable, scalable image shape.</summary>
        public bool PasteImage(PointF? at)
        {
            Bitmap b = null;
            try
            {
                if (Clipboard.ContainsImage())
                    using (Image im = Clipboard.GetImage())
                        if (im != null) { ProjectFile.CheckImageSize(im.Width, im.Height); b = Effects.To32(im); }
                if (b == null && Clipboard.ContainsFileDropList())
                    foreach (string f in Clipboard.GetFileDropList())
                    {
                        b = LoadImageFile(f);
                        if (b != null) break;
                    }
            }
            catch (Exception) { if (b != null) b.Dispose(); return false; }
            if (b == null) return false;
            InsertImage(b, at);
            return true;
        }

        /// <summary>Loads a picture file for inserting; null if it isn't a readable image.</summary>
        public static Bitmap LoadImageFile(string path)
        {
            try
            {
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (Array.IndexOf(new string[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" }, ext) < 0) return null;
                using (System.IO.FileStream fs = System.IO.File.OpenRead(path))
                using (System.Drawing.Image im = System.Drawing.Image.FromStream(fs, false, true))
                {
                    ProjectFile.CheckImageSize(im.Width, im.Height);
                    return Effects.To32(im);
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>Adds a picture on top of the canvas at its real size (1 picture pixel = 1 pixel here), centred on 'at' or
        /// the view; only a picture bigger than the whole picture being edited is made smaller to fit it (never enlarged).
        /// Right-click → Reset picture brings back the real size.</summary>
        public void InsertImage(Bitmap b, PointF? at)
        {
            EndTextEdit(true);
            RectangleF vis = RectangleF.Intersect(ViewRect, Geo.Rect(ToImg(new Point((int)RulerSize, (int)RulerSize)), ToImg(new Point(Width, Height))));
            if (vis.Width < 4 || vis.Height < 4) vis = ViewRect;
            RectangleF room = Image != null ? KeepArea : vis;
            if (room.Width < 4 || room.Height < 4) room = vis;
            float sc = Math.Min(1f, Math.Min(room.Width / b.Width, room.Height / b.Height));
            float w = Math.Max(4f, b.Width * sc), h = Math.Max(4f, b.Height * sc);
            PointF c = at.HasValue ? at.Value : Geo.Center(vis);
            Shape s = new Shape();
            s.Kind = ShapeKind.Image;
            s.Img = b;
            s.FillOpacity = 100;
            s.Width = 0;
            s.Stroke = Color.FromArgb(60, 60, 60);
            s.Pts.Add(new PointF(c.X - w / 2f, c.Y - h / 2f));
            s.Pts.Add(new PointF(c.X + w / 2f, c.Y + h / 2f));
            if (tool != Tool.Select) CurrentTool = Tool.Select;
            AddShape(s);
        }

        /// <summary>A picture back to its real size (1 picture pixel = 1 pixel) and upright, around its middle.</summary>
        public void ResetPicture(Shape s)
        {
            if (s == null || s.Kind != ShapeKind.Image || s.Img == null || s.Pts.Count < 2) return;
            EndTextEdit(true);
            PushUndo(Snapshot());
            PointF c = ShapeOps.Center(s);
            float w = s.Img.Width, h = s.Img.Height;
            s.Pts[0] = new PointF(c.X - w / 2f, c.Y - h / 2f);
            s.Pts[1] = new PointF(c.X + w / 2f, c.Y + h / 2f);
            s.Angle = 0;
            s.Cache = null;
            FireDoc(); FireSel(); Invalidate();
        }

        /// <summary>How see-through a picture is (100 = solid); the same as the Fill opacity slider for a picture.</summary>
        public void SetPictureOpacity(Shape s, int percent)
        {
            if (s == null || s.Kind != ShapeKind.Image || s.FillOpacity == percent) return;
            PushUndo(Snapshot());
            s.FillOpacity = Math.Max(0, Math.Min(100, percent));
            s.Cache = null;
            FireDoc(); FireSel(); Invalidate();
        }

        public void DuplicateSelection()
        {
            if (Sel.Count == 0) return;
            EndTextEdit(true);
            PushUndo(Snapshot());
            List<Shape> src = SelInOrder();
            Sel.Clear();
            foreach (Shape s in src) { Shape c = s.Clone(); c.Cache = null; c.Offset(12, 12); Shapes.Add(c); Sel.Add(c); }
            KeepInside(new List<Shape>(Sel));
            FireDoc(); FireSel(); Invalidate();
        }

        // ---------- lock: keep everything inside the picture ----------
        /// <summary>On: drawing, moving, pasting and resizing stay inside the picture (or its crop).</summary>
        public bool LockInside;

        RectangleF KeepArea { get { return CropRect.HasValue ? (RectangleF)CropRect.Value : new RectangleF(0, 0, Image.Width, Image.Height); } }

        /// <summary>A point held inside the picture while the lock is on.</summary>
        PointF Keep(PointF p)
        {
            if (!LockInside || Image == null) return p;
            RectangleF a = KeepArea;
            return new PointF(Geo.Clamp(p.X, a.X, a.Right), Geo.Clamp(p.Y, a.Y, a.Bottom));
        }

        /// <summary>Slides shapes back inside the picture as a group (lock on). Bigger than the picture = top-left aligned.</summary>
        void KeepInside(List<Shape> list)
        {
            if (!LockInside || Image == null || list == null || list.Count == 0) return;
            RectangleF a = KeepArea, b = KeepBounds(list[0]);
            foreach (Shape s in list) b = RectangleF.Union(b, KeepBounds(s));
            float dx = 0, dy = 0;
            if (b.Right > a.Right) dx = a.Right - b.Right;
            if (b.X + dx < a.X) dx = a.X - b.X;
            if (b.Bottom > a.Bottom) dy = a.Bottom - b.Bottom;
            if (b.Y + dy < a.Y) dy = a.Y - b.Y;
            if (dx != 0 || dy != 0) foreach (Shape s in list) { s.Offset(dx, dy); s.Cache = null; }
        }

        /// <summary>What has to stay inside: a measurement's points (its value box may overhang a little, and moving it
        /// would put it off the lines it measures); for everything else the whole drawn shape.</summary>
        static RectangleF KeepBounds(Shape s)
        {
            if ((s.IsMeasure || s.IsPoly || s.Kind == ShapeKind.Line || s.Kind == ShapeKind.Arrow || s.Kind == ShapeKind.Arc) && s.Pts.Count > 0)
            {
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                foreach (PointF p in s.Pts) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
                return RectangleF.FromLTRB(x0, y0, x1, y1);
            }
            return ShapeOps.Bounds(s);
        }

        /// <summary>Lock on: a shape lying wholly outside the picture is hidden (not drawn, not picked) until the lock is off.
        /// Nothing is moved; a shape that sticks out is only cut at the picture's edge.</summary>
        public bool HiddenByLock(Shape s)
        {
            if (!LockInside || Image == null) return false;
            return !KeepArea.IntersectsWith(ShapeOps.Bounds(s));
        }

        /// <summary>Switch the lock: shapes stay where they are; outside parts are hidden and the view stays on the picture.</summary>
        public void SetLock(bool on)
        {
            LockInside = on;
            if (on)
            {
                Sel.RemoveAll(HiddenByLock);
                FireSel();
            }
            ClampPan();
            Invalidate();
            if (ViewChanged != null) ViewChanged(this, EventArgs.Empty);
        }

        static RectangleF GroupBounds(List<Shape> list)
        {
            RectangleF r = RectangleF.Empty;
            bool first = true;
            foreach (Shape s in list)
            {
                RectangleF b = ShapeOps.Bounds(s);
                r = first ? b : RectangleF.Union(r, b);
                first = false;
            }
            return r;
        }

        /// <summary>Draw order: 0 = bring to front, 1 = bring forward, 2 = send backward, 3 = send to back.</summary>
        public void Order(int op)
        {
            if (Sel.Count == 0) return;
            List<Shape> sel = SelInOrder();
            List<Shape> before0 = new List<Shape>(Shapes);
            PushUndo(Snapshot());
            int n = Shapes.Count;
            switch (op)
            {
                case 0: foreach (Shape s in sel) Shapes.Remove(s); Shapes.AddRange(sel); break;
                case 3: foreach (Shape s in sel) Shapes.Remove(s); Shapes.InsertRange(0, sel); break;
                case 1:
                    for (int i = n - 2; i >= 0; i--)
                        if (Sel.Contains(Shapes[i]) && !Sel.Contains(Shapes[i + 1])) { Shape t = Shapes[i]; Shapes[i] = Shapes[i + 1]; Shapes[i + 1] = t; }
                    break;
                case 2:
                    for (int i = 1; i < n; i++)
                        if (Sel.Contains(Shapes[i]) && !Sel.Contains(Shapes[i - 1])) { Shape t = Shapes[i]; Shapes[i] = Shapes[i - 1]; Shapes[i - 1] = t; }
                    break;
            }
            bool same = true;
            for (int i = 0; i < n; i++) if (Shapes[i] != before0[i]) { same = false; break; }
            if (same) undo.RemoveAt(undo.Count - 1);
            FireDoc(); Invalidate();
        }

        // ---------- crop ----------
        void DoCrop(RectangleF rf)
        {
            Rectangle r = Rectangle.Round(rf);
            r.Intersect(new Rectangle(0, 0, Image.Width, Image.Height));
            if (r.Width < 2 || r.Height < 2) return;
            PushUndo(Snapshot());
            CropRect = r;
            Sel.Clear();
            FireDoc(); FireSel(); Invalidate();
        }

        public bool HasCrop { get { return CropRect.HasValue; } }

        public void RemoveCrop()
        {
            if (!CropRect.HasValue) return;
            PushUndo(Snapshot());
            CropRect = null;
            Fit(); FireDoc();
        }

        /// <summary>Makes the crop permanent (used before effects that work on the image edge). Caller pushes undo.</summary>
        void BakeCrop()
        {
            if (!CropRect.HasValue) return;
            Rectangle r = CropRect.Value;
            // added room that the crop cut away is gone too
            AddedRoom = RoomAfterCrop(r);
            Bitmap b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
                g.DrawImage(Image, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
            Image = b;
            List<Shape> moved = new List<Shape>();
            foreach (Shape s in Shapes) { Shape c = s.Clone(); c.Offset(-r.X, -r.Y); c.Cache = null; moved.Add(c); }
            Shapes = moved;
            for (int i = 0; i < HGuides.Count; i++) HGuides[i] -= r.Y;
            for (int i = 0; i < VGuides.Count; i++) VGuides[i] -= r.X;
            CropRect = null;
        }

        /// <summary>The added room left on each side once cropped to 'r' – never more than the crop itself is wide or tall.</summary>
        int[] RoomAfterCrop(Rectangle r)
        {
            int[] m = AddedRoom;
            return new int[] { Math.Min(r.Width, Math.Max(0, m[0] - r.X)), Math.Min(r.Height, Math.Max(0, m[1] - r.Y)),
                               Math.Min(r.Width, Math.Max(0, m[2] - (Image.Width - r.Right))), Math.Min(r.Height, Math.Max(0, m[3] - (Image.Height - r.Bottom))) };
        }

        static PointF[] BoxPts(RectangleF r)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            return new PointF[] { new PointF(r.X, r.Y), new PointF(cx, r.Y), new PointF(r.Right, r.Y), new PointF(r.Right, cy),
                                  new PointF(r.Right, r.Bottom), new PointF(cx, r.Bottom), new PointF(r.X, r.Bottom), new PointF(r.X, cy) };
        }

        int CropHandleAt(Point sp)
        {
            if (!CropRect.HasValue) return -1;
            PointF[] hp = BoxPts(CropRect.Value);
            float r = 7f * Dpi;
            for (int i = 0; i < 8; i++)
            {
                PointF q = ToScr(hp[i]);
                if (Math.Abs(q.X - sp.X) <= r && Math.Abs(q.Y - sp.Y) <= r) return i;
            }
            return -1;
        }

        /// <summary>Whole-image operations: effects, rotate and mirror. Annotations follow the image.</summary>
        public void ImageOp(string op)
        {
            if (Image == null) return;
            EndTextEdit(true);
            creating = null;
            PushUndo(Snapshot());
            // edge effects work on what you see: make the crop permanent first (same undo step)
            Bitmap preBake = Image;
            if (op == "border" || op == "shadow" || op == "torn") BakeCrop();
            Bitmap baked = ReferenceEquals(preBake, Image) ? null : Image;   // temporary cropped copy, replaced below
            int W = Image.Width, H = Image.Height;
            Bitmap nb;
            Func<PointF, PointF> map = null;
            float ad = 0;
            bool flip = false;
            switch (op)
            {
                case "border":
                    {
                        const int bw = 4;
                        nb = Effects.Border(Image, bw, Color.FromArgb(70, 70, 70));
                        map = p => new PointF(p.X + bw, p.Y + bw);
                        break;
                    }
                case "shadow": nb = Effects.DropShadow(Image); break;
                case "torn": nb = Effects.TornEdge(Image); break;
                case "gray": nb = Effects.Grayscale(Image); break;
                case "invert": nb = Effects.Invert(Image); break;
                case "rotcw": nb = Effects.RotateFlip(Image, RotateFlipType.Rotate90FlipNone); map = p => new PointF(H - p.Y, p.X); ad = 90; break;
                case "rotccw": nb = Effects.RotateFlip(Image, RotateFlipType.Rotate270FlipNone); map = p => new PointF(p.Y, W - p.X); ad = -90; break;
                case "fliph": nb = Effects.RotateFlip(Image, RotateFlipType.RotateNoneFlipX); map = p => new PointF(W - p.X, p.Y); flip = true; break;
                case "flipv": nb = Effects.RotateFlip(Image, RotateFlipType.RotateNoneFlipY); map = p => new PointF(p.X, H - p.Y); flip = true; break;
                default: undo.RemoveAt(undo.Count - 1); return;
            }
            List<Shape> ns = new List<Shape>();
            foreach (Shape s in Shapes)
            {
                Shape c = s.Clone();
                c.Cache = null;
                if (map != null) ShapeOps.Transform(c, map, ad, flip);
                ns.Add(c);
            }
            if (map != null && CropRect.HasValue)
            {
                Rectangle r = CropRect.Value;
                PointF a = map(new PointF(r.X, r.Y)), b = map(new PointF(r.Right, r.Bottom));
                CropRect = Rectangle.Round(Geo.Rect(a, b));
            }
            if (op == "border")
            {
                for (int i = 0; i < HGuides.Count; i++) HGuides[i] += 4;
                for (int i = 0; i < VGuides.Count; i++) VGuides[i] += 4;
            }
            else if (map != null) { HGuides.Clear(); VGuides.Clear(); }
            Image = nb;
            if (baked != null) ReleaseImage(baked);
            Shapes = ns;
            // the added room turns with the picture; a border, shadow or torn edge makes it part of the picture
            int[] m = AddedRoom;
            if (op == "rotcw") AddedRoom = new int[] { m[3], m[0], m[1], m[2] };
            else if (op == "rotccw") AddedRoom = new int[] { m[1], m[2], m[3], m[0] };
            else if (op == "fliph") AddedRoom = new int[] { m[2], m[1], m[0], m[3] };
            else if (op == "flipv") AddedRoom = new int[] { m[0], m[3], m[2], m[1] };
            else if (op != "gray" && op != "invert") AddedRoom = new int[4];
            Sel.Clear();
            Fit(); FireDoc(); FireSel();
        }

        /// <summary>Adjust → Extend canvas: more room round the picture (pixels per side, in 'back'). The shapes, guides and
        /// scale stay on the same spots of the picture. A crop is made permanent first (same undo step).</summary>
        public void ExtendCanvas(int left, int top, int right, int bottom, Color back)
        {
            if (Image == null) return;
            left = Math.Max(0, left); top = Math.Max(0, top); right = Math.Max(0, right); bottom = Math.Max(0, bottom);
            if (left + top + right + bottom == 0) return;
            int baseW = CropRect.HasValue ? CropRect.Value.Width : Image.Width, baseH = CropRect.HasValue ? CropRect.Value.Height : Image.Height;
            ProjectFile.CheckImageSize(baseW + left + right, baseH + top + bottom);       // too big: throws before anything changes
            EndTextEdit(true);
            creating = null;
            PushUndo(Snapshot());
            Bitmap preBake = Image;
            BakeCrop();
            Bitmap baked = ReferenceEquals(preBake, Image) ? null : Image;
            int W = Image.Width + left + right, H = Image.Height + top + bottom;
            Bitmap nb = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(nb))
            {
                g.Clear(back);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(Image, new Rectangle(left, top, Image.Width, Image.Height), 0, 0, Image.Width, Image.Height, GraphicsUnit.Pixel);
            }
            List<Shape> ns = new List<Shape>();
            foreach (Shape s in Shapes)
            {
                Shape c = s.Clone();
                c.Cache = null;
                c.Offset(left, top);
                ns.Add(c);
            }
            for (int i = 0; i < HGuides.Count; i++) HGuides[i] += top;
            for (int i = 0; i < VGuides.Count; i++) VGuides[i] += left;
            AddedRoom = new int[] { AddedRoom[0] + left, AddedRoom[1] + top, AddedRoom[2] + right, AddedRoom[3] + bottom };
            Image = nb;
            if (baked != null) ReleaseImage(baked);
            Shapes = ns;
            Sel.Clear();
            Fit(); FireDoc(); FireSel();
        }

        /// <summary>Ctrl+Minus: takes up to 'px' of the added room off each side (the opposite of ExtendCanvas). 1.4.2: it
        /// stops at the original picture edge, so the picture itself is never cut. Shapes, guides and scale stay on the
        /// same spots of the picture. Returns false when there is no added room left to take away.</summary>
        public bool ShrinkCanvas(int px)
        {
            if (Image == null || px <= 0) return false;
            // what is left of the added room once a crop is made permanent
            int[] m = CropRect.HasValue ? RoomAfterCrop(CropRect.Value) : AddedRoom;
            int l = Math.Min(px, m[0]), t = Math.Min(px, m[1]), r = Math.Min(px, m[2]), b = Math.Min(px, m[3]);
            // never below 16 px: a crop can lie wholly inside the added room
            int vw = CropRect.HasValue ? CropRect.Value.Width : Image.Width, vh = CropRect.HasValue ? CropRect.Value.Height : Image.Height;
            int over = l + r - Math.Max(0, vw - 16);
            if (over > 0) { int cut = Math.Min(over, r); r -= cut; l = Math.Max(0, l - (over - cut)); }
            over = t + b - Math.Max(0, vh - 16);
            if (over > 0) { int cut = Math.Min(over, b); b -= cut; t = Math.Max(0, t - (over - cut)); }
            if (l + t + r + b == 0) return false;
            EndTextEdit(true);
            creating = null;
            PushUndo(Snapshot());
            Bitmap preBake = Image;
            BakeCrop();
            Bitmap baked = ReferenceEquals(preBake, Image) ? null : Image;
            Bitmap nb = new Bitmap(Image.Width - l - r, Image.Height - t - b, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(nb))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(Image, new Rectangle(0, 0, nb.Width, nb.Height), l, t, nb.Width, nb.Height, GraphicsUnit.Pixel);
            }
            List<Shape> ns = new List<Shape>();
            foreach (Shape s in Shapes)
            {
                Shape c = s.Clone();
                c.Cache = null;
                c.Offset(-l, -t);
                ns.Add(c);
            }
            for (int i = 0; i < HGuides.Count; i++) HGuides[i] -= t;
            for (int i = 0; i < VGuides.Count; i++) VGuides[i] -= l;
            AddedRoom = new int[] { AddedRoom[0] - l, AddedRoom[1] - t, AddedRoom[2] - r, AddedRoom[3] - b };
            Image = nb;
            if (baked != null) ReleaseImage(baked);
            Shapes = ns;
            Sel.Clear();
            Fit(); FireDoc(); FireSel();
            return true;
        }

        // ---------- snapping ----------
        bool SnapTool
        {
            get
            {
                return tool == Tool.Dimension || tool == Tool.Area || tool == Tool.Polygon || tool == Tool.PolyDraw || tool == Tool.Calibrate || tool == Tool.Line || tool == Tool.Arrow ||
                       tool == Tool.Select || tool == Tool.Angle || tool == Tool.Polyline || tool == Tool.Perimeter || tool == Tool.Cutout || tool == Tool.Coord || PolyAs != tool ||
                       tool == Tool.Arc || tool == Tool.Radius || tool == Tool.Freehand;     // 1.4.2: freehand ends snap too
            }
        }

        /// <summary>The lines of a shape that a freehand end can snap onto (closed outlines end where they start), or null.</summary>
        static List<PointF> SnapOutline(Shape s)
        {
            if (s.Pts.Count < 2) return null;
            List<PointF> cloud = Renderer.CloudOutline(s);       // scallops are followed, not the box under them
            if (cloud != null) return cloud;
            switch (s.Kind)
            {
                case ShapeKind.Arc: case ShapeKind.Radius:
                    return s.Pts.Count >= 3 ? Geo.ArcPoints(s.Pts[0], s.Pts[1], s.Pts[2], 96) : new List<PointF>(s.Pts);
                case ShapeKind.Line: case ShapeKind.Arrow: case ShapeKind.Dimension: case ShapeKind.Freehand: case ShapeKind.Angle:
                    return s.IsMarkerPen ? null : new List<PointF>(s.Pts);
                case ShapeKind.Polygon: case ShapeKind.Area: case ShapeKind.Perimeter: case ShapeKind.Polyline:
                    {
                        List<PointF> e = new List<PointF>(s.Pts);
                        if (!s.IsOpenPath && s.Pts.Count >= 3) e.Add(s.Pts[0]);
                        return e;
                    }
                case ShapeKind.Rect: case ShapeKind.Cloud:
                    {
                        RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
                        return Turned(new List<PointF> { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom), new PointF(r.Left, r.Top) }, s.Angle, r);
                    }
                case ShapeKind.Ellipse:
                    {
                        RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
                        List<PointF> e = new List<PointF>();
                        for (int i = 0; i <= 120; i++)
                        {
                            double a = i * Math.PI * 2 / 120;
                            e.Add(new PointF(r.X + r.Width / 2f + (float)Math.Cos(a) * r.Width / 2f, r.Y + r.Height / 2f + (float)Math.Sin(a) * r.Height / 2f));
                        }
                        return Turned(e, s.Angle, r);
                    }
                default: return null;
            }
        }

        /// <summary>The outline turned by the shape's angle about the middle of its box, as it is drawn.</summary>
        static List<PointF> Turned(List<PointF> pts, float angle, RectangleF r)
        {
            if (Math.Abs(angle) <= 0.01f) return pts;
            PointF[] a = pts.ToArray();
            using (Matrix m = new Matrix()) { m.RotateAt(angle, Geo.Center(r)); m.TransformPoints(a); }
            return new List<PointF>(a);
        }

        /// <summary>The tool as far as clicking goes: a Focus with the Perimeter form is drawn corner by corner, like an area.</summary>
        Tool PolyAs
        {
            get
            {
                if (tool != Tool.Highlight) return tool;
                bool perim = creating != null ? creating.IsFocusPoly : Styles[Tool.Highlight].Form == (int)HighlightForm.Perimeter;
                return perim ? Tool.Polygon : tool;
            }
        }

        PointF Snap(PointF ip)
        {
            snapPt = null;
            if ((ModifierKeys & Keys.Alt) != 0) return ip;
            float best = 9f * Dpi / Zoom;
            PointF? found = null;
            if (SnapTool)
            {
                foreach (Shape s in Shapes)
                {
                    if (s == creating || (Sel.Contains(s) && (dragH.HasValue || dragBody))) continue;
                    foreach (PointF p in s.Pts)
                    {
                        float d = Geo.Dist(p, ip);
                        if (d < best) { best = d; found = p; }
                    }
                    if (s.Kind == ShapeKind.Rect || s.Kind == ShapeKind.Ellipse || s.Kind == ShapeKind.Bubble || s.Kind == ShapeKind.Image)
                    {
                        PointF c = ShapeOps.Center(s);
                        float d = Geo.Dist(c, ip);
                        if (d < best) { best = d; found = c; }
                    }
                }
                if (creating != null && creating.IsPoly && !creating.IsOpenPath && creating.Pts.Count > 3)
                {
                    float d = Geo.Dist(creating.Pts[0], ip);
                    if (d < best) { best = d; found = creating.Pts[0]; }
                }
                if (tool == Tool.Freehand)
                {
                    // 1.4.2: a freehand stroke closes onto its own start, and its ends snap onto other shapes' lines
                    if (creating != null && creating.Pts.Count > 8)
                    {
                        float d = Geo.Dist(creating.Pts[0], ip);
                        if (d < best) { best = d; found = creating.Pts[0]; }
                    }
                    if (!found.HasValue)
                    {
                        float bestL = best;
                        foreach (Shape s in Shapes)
                        {
                            if (s == creating) continue;
                            List<PointF> e = SnapOutline(s);
                            if (e == null) continue;
                            for (int i = 0; i + 1 < e.Count; i++)
                            {
                                float d = Geo.SegDist(ip, e[i], e[i + 1]);
                                if (d < bestL) { bestL = d; found = Geo.Project(ip, e[i], e[i + 1]); }
                            }
                        }
                    }
                }
            }
            if (found.HasValue) { snapPt = found; return found.Value; }
            // guides: snap each axis on its own
            float gtol = 7f * Dpi / Zoom;
            float x = ip.X, y = ip.Y;
            bool g = false;
            float bx = gtol, by = gtol;
            foreach (float v in VGuides) if (Math.Abs(v - ip.X) < bx) { bx = Math.Abs(v - ip.X); x = v; g = true; }
            foreach (float v in HGuides) if (Math.Abs(v - ip.Y) < by) { by = Math.Abs(v - ip.Y); y = v; g = true; }
            if (g) { snapPt = new PointF(x, y); return snapPt.Value; }
            return ip;
        }

        /// <summary>The pointer over a handle shows which way it stretches: ↔ ↕ ⤡ ⤢ for box edges and corners
        /// (following the shape's rotation), a four-way arrow for points, a hand for the rotate knob.</summary>
        static Cursor HandleCursor(Handle h, Shape s)
        {
            switch (h.Kind)
            {
                case HKind.Rotate: return Cursors.Hand;
                case HKind.LabelSize: return Cursors.SizeNWSE;
                case HKind.Box:
                    {
                        // from which handle it is (0 = top-left, clockwise), not from the centre: on a wide box such as a
                        // stamp the corners lie only ~20° off the middle line and would get a side-to-side arrow
                        double ang = h.Index * 45.0 - 135.0 + (s.Rotatable ? s.Angle : 0f);
                        ang %= 180.0;
                        if (ang < 0) ang += 180.0;                    // a stretch goes both ways: 0–180° is enough
                        if (ang < 22.5 || ang >= 157.5) return Cursors.SizeWE;
                        if (ang < 67.5) return Cursors.SizeNWSE;      // down-right / up-left
                        if (ang < 112.5) return Cursors.SizeNS;
                        return Cursors.SizeNESW;
                    }
                default: return Cursors.SizeAll;                    // points, tails, offsets, centre: move them
            }
        }

        Handle? HandleAt(Point sp)
        {
            if (Sel.Count != 1) return null;
            List<Handle> hs = ShapeOps.Handles(Selected, RotDist);
            float r = 6.5f * Dpi;
            // rotate/centre/tail knobs take priority over box handles they may overlap
            for (int pass = 0; pass < 2; pass++)
                for (int i = hs.Count - 1; i >= 0; i--)
                {
                    bool special = hs[i].Kind != HKind.Box && hs[i].Kind != HKind.Point;
                    if ((pass == 0) != special) continue;
                    PointF h = ToScr(hs[i].Pos);
                    if (Math.Abs(h.X - sp.X) <= r && Math.Abs(h.Y - sp.Y) <= r) return hs[i];
                }
            return null;
        }

        Shape HitShape(PointF ip)
        {
            // lock on: what lies outside the picture is hidden, so it cannot be picked either
            if (LockInside && Image != null && !KeepArea.Contains(ip)) return null;
            Shape p = Selected;
            if (p != null && ShapeOps.Hit(p, ip, Tol)) return p;
            for (int i = Shapes.Count - 1; i >= 0; i--) if (ShapeOps.Hit(Shapes[i], ip, Tol)) return Shapes[i];
            return null;
        }

        /// <summary>Guide under the screen point: axis 1 = horizontal, 2 = vertical.</summary>
        bool GuideAt(Point sp, out int axis, out int idx)
        {
            axis = 0; idx = -1;
            float r = 4f * Dpi;
            for (int i = 0; i < HGuides.Count; i++)
                if (Math.Abs(HGuides[i] * Zoom + Pan.Y - sp.Y) <= r) { axis = 1; idx = i; return true; }
            for (int i = 0; i < VGuides.Count; i++)
                if (Math.Abs(VGuides[i] * Zoom + Pan.X - sp.X) <= r) { axis = 2; idx = i; return true; }
            return false;
        }

        bool InTopRuler(Point p) { return showRulers && p.Y < RulerSize && p.X >= RulerSize; }
        bool InLeftRuler(Point p) { return showRulers && p.X < RulerSize && p.Y >= RulerSize; }

        // ---------- polygon creation ----------
        public void FinishPoly()
        {
            if (creating == null || !creating.IsPoly) return;
            Shape c = creating;
            creating = null;
            List<PointF> pts = c.Pts;
            if (pts.Count > 0) pts.RemoveAt(pts.Count - 1);        // rubber-band point
            while (pts.Count >= 2 && Geo.Dist(pts[pts.Count - 1], pts[pts.Count - 2]) < 0.5f) pts.RemoveAt(pts.Count - 1);
            if (c.IsOpenPath)
            {
                if (pts.Count >= 2) AddShape(c); else Invalidate();
                return;
            }
            if (pts.Count >= 2 && Geo.Dist(pts[pts.Count - 1], pts[0]) < 0.5f) pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) { Invalidate(); return; }
            if (tool == Tool.Cutout) { AttachCutout(pts); return; }
            AddShape(c);
        }

        /// <summary>Cut-out tool: the outline becomes an opening in the area it was drawn in.</summary>
        void AttachCutout(List<PointF> pts)
        {
            PointF c = Geo.Centroid(pts);
            Shape target = null;
            if (CanHaveHoles(Selected) && HoleTargetContains(Selected, c)) target = Selected;
            for (int i = Shapes.Count - 1; i >= 0 && target == null; i--)
                if (CanHaveHoles(Shapes[i]) && HoleTargetContains(Shapes[i], c)) target = Shapes[i];
            if (target == null)
            {
                if (Message != null) Message("Draw the cut-out inside an area (M), a polygon (P) or a focus (H)");
                Invalidate();
                return;
            }
            PushUndo(Snapshot());
            if (target.Holes == null) target.Holes = new List<List<PointF>>();
            target.Holes.Add(new List<PointF>(pts));
            Selected = target;
            FireDoc(); FireSel(); Invalidate();
            if (Message != null)
                Message(target.Kind == ShapeKind.Highlight ? "Cut out of the focus (right-click → Remove cut-outs to undo all)"
                    : target.Kind == ShapeKind.Area ? "Cut-out subtracted: net area " + Scale.Area(Measure.NetArea(target))
                                                      : "Hollow cut out of the polygon (right-click → Remove cut-outs to undo all)");
        }

        /// <summary>Shapes a cut-out can go into: an area (an opening, subtracted) or a closed polygon (a hollow section).</summary>
        public static bool CanHaveHoles(Shape s)
        {
            if (s == null) return false;
            if (s.Kind == ShapeKind.Highlight) return s.Pts.Count >= (s.IsFocusPoly ? 3 : 2);   // a focus (any form)
            return s.Pts.Count >= 3 && (s.Kind == ShapeKind.Area || (s.Kind == ShapeKind.Polygon && !s.IsOpenPath));
        }

        /// <summary>Whether a cut-out drawn around p belongs to s: inside its outline (a focus: inside its form).</summary>
        static bool HoleTargetContains(Shape s, PointF p)
        {
            if (s.Kind != ShapeKind.Highlight) return Geo.InPolygon(p, s.Pts);
            using (GraphicsPath gp = Renderer.HighlightPath(s))
            {
                gp.FillMode = FillMode.Winding;                          // a point inside an earlier cut-out still counts
                return gp.IsVisible(p);
            }
        }

        /// <summary>Moves the origin onto a coordinate point; every other point is then measured from there.</summary>
        public void MakeOrigin(Shape point)
        {
            Shape o = Renderer.Origin(Shapes);
            if (o == null || point == null) return;
            PushUndo(Snapshot());
            o.Pts[0] = point.Pts[0];
            FireDoc(); Invalidate();
            if (Message != null) Message("Origin moved – all coordinates now count from this point");
        }

        /// <summary>1.4.2 Fill tool: fills the closed area round 'ip' with the Fill tool's colour, like a paint bucket. The area is
        /// bounded by annotation lines and by colour edges in the picture; gaps of a few pixels are closed. The fill is a
        /// polygon (holes kept) placed under the annotations. Returns false (with a message) when nothing was filled.</summary>
        public bool FillAt(PointF ip)
        {
            if (Image == null) return false;
            List<KeyValuePair<List<PointF>, float>> lines = new List<KeyValuePair<List<PointF>, float>>();
            foreach (Shape s in Shapes)
            {
                if (IsFillShape(s)) continue;     // earlier fills are colour, not boundaries
                List<PointF> e = SnapOutline(s);
                if (e == null) continue;
                lines.Add(new KeyValuePair<List<PointF>, float>(e, s.Width));
                // cut-outs are outlines too: the fill stops at them
                if (s.Holes != null && CanHaveHoles(s))
                    foreach (List<PointF> h in s.Holes)
                        if (h.Count >= 3) lines.Add(new KeyValuePair<List<PointF>, float>(new List<PointF>(h) { h[0] }, s.Width));
            }
            string why;
            List<List<PointF>> loops;
            Cursor wasCursor = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;     // for the odd slow one
            try { loops = Regions.FillOutline(lines, ip, FillGap, out why); }
            catch (Exception ex) { loops = null; why = Lang.T("Could not fill:") + " " + ex.Message; }
            finally { Cursor.Current = wasCursor; }
            if (loops == null) { if (Message != null) Message(why ?? "Nothing to fill there"); return false; }
            Shape f = NewShape(Tool.Fill, ip);
            f.Kind = ShapeKind.Polygon;
            f.Mode = 0;
            f.Form = 1;         // marked as a fill: its style bar offers no line or Closed
            f.Pts = loops[0];
            f.Holes = loops.Count > 1 ? loops.GetRange(1, loops.Count - 1) : null;
            f.Cache = null;
            // already filled there: the same area takes the new colour; a part of it gets its own fill on top
            int old = -1;
            for (int i = Shapes.Count - 1; i >= 0 && old < 0; i--)
                if (IsFillShape(Shapes[i]) && InFill(Shapes[i], ip)) old = i;
            if (old >= 0)
            {
                double was = Math.Abs(Geo.SignedArea(Shapes[old].Pts)), now = Math.Abs(Geo.SignedArea(f.Pts));
                if (Math.Abs(was - now) <= was * 0.05)
                {
                    PushUndo(Snapshot());
                    Shape o = Shapes[old];
                    o.Fill = f.Fill; o.FillOpacity = f.FillOpacity; o.Cache = null;
                    Selected = null; Sel.Clear();
                    FireDoc(); FireSel(); Invalidate();
                    if (Message != null) Message("Fill colour changed – Ctrl+Z to undo");
                    return true;
                }
            }
            PushUndo(Snapshot());
            Shapes.Insert(old + 1, f);      // under the lines that bound it, above an older fill
            Selected = null; Sel.Clear();
            FireDoc(); FireSel(); Invalidate();
            if (Message != null) Message("Filled – change its colour with Fill, or Ctrl+Z to undo");
            return true;
        }

        static bool InFill(Shape s, PointF p)
        {
            if (s.Pts.Count < 3) return false;
            using (GraphicsPath gp = new GraphicsPath(FillMode.Alternate))
            {
                gp.AddPolygon(s.Pts.ToArray());
                if (s.Holes != null) foreach (List<PointF> h in s.Holes) if (h.Count >= 3) gp.AddPolygon(h.ToArray());
                return gp.IsVisible(p);
            }
        }

        /// <summary>Fill tool: gaps up to about twice this many pixels in a line are closed.</summary>
        public int FillGap = 2;

        /// <summary>1.4.2 Adjust → Remove background: the plain colour at the picture's edges becomes transparent.
        /// Returns how many pixels were cleared (0 = no plain background found, nothing changed).</summary>
        public int RemoveBackground()
        {
            if (Image == null) return 0;
            EndTextEdit(true);
            creating = null;
            int removed;
            Bitmap nb;
            // a crop is not yet permanent: look at the visible part only
            if (CropRect.HasValue)
                using (Bitmap part = Image.Clone(CropRect.Value, PixelFormat.Format32bppArgb))
                    nb = Regions.RemoveBackground(part, 40, out removed);
            else nb = Regions.RemoveBackground(Image, 40, out removed);
            if (nb == null) return 0;
            PushUndo(Snapshot());
            Bitmap preBake = Image;
            BakeCrop();
            Bitmap baked = ReferenceEquals(preBake, Image) ? null : Image;
            Image = nb;
            if (baked != null) ReleaseImage(baked);
            Invalidate(); FireDoc();
            return removed;
        }

        /// <summary>1.4.2: the origin's own coordinates (default 0 ; 0), e.g. 1250 ; 300 to match the drawing's grid.</summary>
        public void SetOriginValue()
        {
            if (!HasOrigin) return;
            string cur = Scale.Fmt(Scale.OriginX) + " ; " + Scale.Fmt(Scale.OriginY);
            string t = AskText != null ? AskText(Lang.T("Origin coordinates X ; Y in") + " " + Scale.Unit + " (e.g. 1250 ; 300)", cur) : null;
            if (t == null) return;
            double x, y;
            if (!ParseXY(t, out x, out y)) { if (Message != null) Message("Type two numbers: X ; Y"); return; }
            if (x == Scale.OriginX && y == Scale.OriginY) return;
            PushUndo(Snapshot());
            Scale.OriginX = x; Scale.OriginY = y;
            FireDoc(); Invalidate();
            if (Message != null) Message(Lang.T("Origin is now X") + " " + Scale.Fmt(x) + " ; Y " + Scale.Fmt(y));
        }

        /// <summary>"1250 ; 300", "1250 300" or "1250, 300" (the current culture's numbers, or invariant ones).</summary>
        public static bool ParseXY(string t, out double x, out double y)
        {
            x = y = 0;
            if (t == null) return false;
            t = t.Trim();
            if (t.Length == 0 || t == "0") return t.Length > 0;
            string[] p = t.Split(';');
            if (p.Length != 2) p = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 2 && CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator != ",") p = t.Split(',');
            if (p.Length != 2) return false;
            return Num(p[0], out x) && Num(p[1], out y);
        }

        static bool Num(string s, out double v)
        {
            s = s.Trim();
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v)) return true;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v);
        }

        /// <summary>Removes the cut-outs of the selected area.</summary>
        public void ClearCutouts()
        {
            Shape s = Selected;
            if (s == null || s.Holes == null) return;
            PushUndo(Snapshot());
            s.Holes = null;
            FireDoc(); Invalidate();
        }

        /// <summary>Line ends of the selected lines / arrows / dimensions. start: true = first point.</summary>
        public void SetHeads(bool start, int style)
        {
            List<Shape> t = Sel.FindAll(x => x.HasHeads);
            if (t.Count == 0) return;
            PushUndo(Snapshot());
            foreach (Shape s in t) { if (start) s.HeadStart = style; else s.HeadEnd = style; }
            Shape tmpl;
            foreach (Shape s in t) if (Styles.TryGetValue(ToolFor(s), out tmpl)) { if (start) tmpl.HeadStart = style; else tmpl.HeadEnd = style; }
            FireDoc(); FireSel(); Invalidate();
        }

        public void SwapHeads()
        {
            List<Shape> t = Sel.FindAll(x => x.HasHeads);
            if (t.Count == 0) return;
            PushUndo(Snapshot());
            foreach (Shape s in t) { int h = s.HeadStart; s.HeadStart = s.HeadEnd; s.HeadEnd = h; }
            FireDoc(); FireSel(); Invalidate();
        }

        /// <summary>Asks for a label (measurements, notes, stamps) or the marker series name.</summary>
        public void EditLabel(Shape s)
        {
            if (s == null) return;
            if (s.IsSymbolMarker)
            {
                string cur = s.Text ?? "";
                string t = AskSymbol != null ? AskSymbol("Symbol marker: a symbol or short text", cur) : null;
                if (t != null && t.Length > 0 && t != cur) { PushUndo(Snapshot()); s.Text = t; s.Cache = null; Styles[Tool.Symbol].Text = t; FireDoc(); Invalidate(); }
                return;
            }
            if (s.Kind == ShapeKind.Counter)
            {
                bool letters = s.IsLetterMarker;
                string cur = letters ? Scale.LetterLabel : Scale.NumberLabel;
                string t = AskText != null ? AskText((letters ? "Letter" : "Number") + " markers count… (e.g. Blind panel)", cur) : null;
                if (t != null && t != cur) { PushUndo(Snapshot()); if (letters) Scale.LetterLabel = t; else Scale.NumberLabel = t; FireDoc(); Invalidate(); }
                return;
            }
            if (s.Kind == ShapeKind.Stamp || (s.Kind == ShapeKind.Cloud && s.Mode != 0 && s.Pts.Count > 2)) { BeginTextEdit(s, false); return; }   // typed in place
            string title;
            bool multi = false;
            switch (s.Kind)
            {
                case ShapeKind.Coord: title = "Point label (e.g. Bracket B3)"; break;
                case ShapeKind.Dimension: title = "Dimension label prefix (e.g. W =)"; break;
                case ShapeKind.Area: title = "Area label (e.g. Spandrel L01)"; break;
                case ShapeKind.Polyline: title = "Length label (e.g. Sealant run)"; break;
                case ShapeKind.Perimeter: title = "Perimeter label (e.g. Gasket W12)"; break;
                case ShapeKind.Angle: title = "Angle label (e.g. Roof pitch)"; break;
                case ShapeKind.Radius: title = "Radius label (e.g. Curved mullion)"; break;
                case ShapeKind.Note: title = "Note (shown here only – not on saved pictures)"; multi = true; break;
                default: return;
            }
            Func<string, string, string> ask = multi ? AskNote : AskText;
            string v = ask != null ? ask(title, s.Text) : null;
            if (v == null || v == s.Text) return;
            if (s.Kind == ShapeKind.Note && v.Trim().Length == 0) return;
            PushUndo(Snapshot());
            s.Text = v;
            FireDoc(); Invalidate();
        }

        /// <summary>The captured mouse pointer as a picture on top of the capture (part of the capture: no undo step).
        /// Select it to move it, or press Delete to remove it.</summary>
        public void AddPointerPicture(Bitmap pointer, Point at)
        {
            Shape s = new Shape();
            s.Kind = ShapeKind.Image;
            s.Img = pointer;
            s.FillOpacity = 100;
            s.Width = 0;
            s.Stroke = Color.FromArgb(60, 60, 60);
            s.Text = "Mouse pointer";
            s.Pts.Add(new PointF(at.X, at.Y));
            s.Pts.Add(new PointF(at.X + pointer.Width, at.Y + pointer.Height));
            Shapes.Add(s);
            Invalidate();
        }

        /// <summary>Changes a shape's text from outside the canvas (the editor's text list), as one undo step.</summary>
        public void SetShapeText(Shape s, string text)
        {
            if (s == null || !Shapes.Contains(s) || (s.Text ?? "") == text) return;
            EndTextEdit(true);
            PushUndo(Snapshot());
            s.Text = text;
            s.Cache = null;
            FireDoc(); Invalidate();
        }



        /// <summary>Text markup: tighten the dragged box to the words found under it (Windows text recognition).</summary>
        RectangleF SnapToText(RectangleF r)
        {
            if (FindWords == null || Image == null) return r;
            Rectangle look = Rectangle.Round(RectangleF.Inflate(r, r.Height * 0.8f + 8f, r.Height * 0.8f + 8f));
            look.Intersect(new Rectangle(0, 0, Image.Width, Image.Height));
            if (look.Width < 4 || look.Height < 4) return r;
            List<Rectangle> words;
            try { words = FindWords(look); } catch (Exception) { return r; }
            if (words == null || words.Count == 0) return r;
            float y0 = r.Y - r.Height * 0.5f, y1 = r.Bottom + r.Height * 0.5f;
            RectangleF u = RectangleF.Empty;
            bool any = false;
            foreach (Rectangle w in words)
            {
                float cy = w.Y + w.Height / 2f;
                if (cy < y0 || cy > y1 || w.Right <= r.X || w.X >= r.Right) continue;
                u = any ? RectangleF.Union(u, w) : w;
                any = true;
            }
            if (!any) return r;
            u.Inflate(1.5f, 1.5f);
            return u;
        }

        // ---------- inline text editing ----------
        public bool IsEditingText { get { return editBox != null; } }

        static HorizontalAlignment TbAlign(int a) { return a == 1 ? HorizontalAlignment.Center : (a == 2 ? HorizontalAlignment.Right : HorizontalAlignment.Left); }

        public void BeginTextEdit(Shape s, bool isNew)
        {
            EndTextEdit(true);
            editShape = s;
            editIsNew = isNew;
            editBefore = isNew ? null : Snapshot();
            editOrigText = s.Text ?? "";
            Selected = isNew ? null : s;
            FireSel();

            bool boxed = s.Kind == ShapeKind.Bubble || s.Kind == ShapeKind.Stamp || s.Kind == ShapeKind.Cloud;
            TextBox tb = new TextBox();
            tb.Multiline = true;
            tb.AcceptsReturn = true;
            tb.WordWrap = boxed || Renderer.WrapWidth(s) > 0;
            tb.ScrollBars = ScrollBars.None;
            tb.BorderStyle = BorderStyle.FixedSingle;
            float fsz = s.Kind == ShapeKind.Stamp ? 16f : s.FontSize;
            Font oldTbFont = tb.Font;
            tb.Font = new Font(s.HasTextStyle ? s.FontFamilyName : Renderer.DefaultFont, Math.Max(6f, fsz * Zoom), s.Kind == ShapeKind.Stamp ? FontStyle.Bold : s.Style, GraphicsUnit.Pixel);
            if (!ReferenceEquals(oldTbFont, tb.Font) && !ReferenceEquals(oldTbFont, Font) && !ReferenceEquals(oldTbFont, Control.DefaultFont)) oldTbFont.Dispose();
            tb.ForeColor = Color.FromArgb(255, s.Stroke);
            tb.BackColor = s.FillOpacity > 0 && s.Kind != ShapeKind.Cloud ? Color.FromArgb(255, s.Fill) : Color.White;
            if (tb.BackColor.GetBrightness() < 0.25f && tb.ForeColor.GetBrightness() < 0.25f) tb.BackColor = Color.White;
            tb.TextAlign = s.Kind == ShapeKind.Stamp || s.Kind == ShapeKind.Cloud ? HorizontalAlignment.Center : TbAlign(s.Align);
            tb.Text = editOrigText.Replace("\r\n", "\n").Replace("\n", "\r\n");
            if (boxed) s.Text = "";     // the body stays visible behind the editor without its old text
            editBox = tb;
            tb.TextChanged += (o, e) => LayoutEditBox();
            tb.KeyDown += (o, e) =>
            {
                // Esc and Ctrl+Enter both keep what you typed (Esc used to throw it away)
                if (e.KeyCode == Keys.Escape || (e.KeyCode == Keys.Enter && e.Control)) { e.SuppressKeyPress = true; BeginInvoke((Action)(() => EndTextEdit(true))); }
            };
            tb.Leave += (o, e) => EndTextEdit(true);
            tb.ContextMenuStrip = EditMenu(tb);       // 1.4.2: right-click = cut / copy / paste and the symbol picker
            LayoutEditBox();
            Controls.Add(tb);
            tb.Focus();
            if (s.Kind == ShapeKind.Stamp && isNew) tb.SelectionStart = 0;          // type the stamp word on the first line
            else if (s.Kind == ShapeKind.Stamp) { int nl = tb.Text.IndexOf('\r'); tb.SelectionStart = 0; tb.SelectionLength = nl >= 0 ? nl : tb.TextLength; }
            else tb.SelectionStart = tb.TextLength;
            Invalidate();
            FireState();
        }

        /// <summary>1.4.2: types 's' at the caret of the text being edited. Returns false when no text is being edited.</summary>
        public bool InsertText(string s)
        {
            if (editBox == null || string.IsNullOrEmpty(s)) return false;
            editBox.SelectedText = s;
            editBox.Focus();
            return true;
        }

        /// <summary>The symbols as a menu; clicking one types it into the text being edited.</summary>
        public void AddSymbolItems(ToolStripItemCollection items)
        {
            ToolStripMenuItem more = null;
            for (int i = 0; i < Dialogs.Symbols.Length; i++)
            {
                string sy = Dialogs.Symbols[i];
                ToolStripMenuItem it = new ToolStripMenuItem(sy);
                it.Click += (o, e) => InsertText(sy);
                // the first row on the menu itself, the rest one level down so the menu stays short
                if (i < 11) items.Add(it);
                else
                {
                    if (more == null) { more = new ToolStripMenuItem(Lang.T("More symbols")); items.Add(more); }
                    more.DropDownItems.Add(it);
                }
            }
        }

        ContextMenuStrip EditMenu(TextBox tb)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add(Lang.T("Cut"), null, (o, e) => tb.Cut());
            m.Items.Add(Lang.T("Copy"), null, (o, e) => tb.Copy());
            m.Items.Add(Lang.T("Paste"), null, (o, e) => tb.Paste());
            m.Items.Add(Lang.T("Select all"), null, (o, e) => tb.SelectAll());
            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem sym = new ToolStripMenuItem(Lang.T("Insert symbol"));
            AddSymbolItems(sym.DropDownItems);
            m.Items.Add(sym);
            tb.Disposed += (o, e) => m.Dispose();
            return m;
        }

        /// <summary>Style panel changed bold/italic/alignment/size while typing: update the edit box.</summary>
        public void RefreshEditStyle()
        {
            if (editBox == null) return;
            SetEditFont(new Font(editShape.HasTextStyle ? editShape.FontFamilyName : Renderer.DefaultFont, Math.Max(6f, editShape.FontSize * Zoom), editShape.Style, GraphicsUnit.Pixel));
            editBox.TextAlign = TbAlign(editShape.Align);
            editBox.ForeColor = Color.FromArgb(255, editShape.Stroke);
            LayoutEditBox();
            Invalidate();
        }

        /// <summary>Gives the edit box a new font and disposes the one it replaces.</summary>
        void SetEditFont(Font f)
        {
            Font old = editBox.Font;
            editBox.Font = f;
            if (old != null && !ReferenceEquals(old, f)) old.Dispose();
        }

        public Shape EditingShape { get { return editShape; } }

        void LayoutEditBox()
        {
            if (editBox == null) return;
            Shape s = editShape;
            float newSize = Math.Max(6f, (s.Kind == ShapeKind.Stamp ? 16f : s.FontSize) * Zoom);
            FontStyle fst = s.Kind == ShapeKind.Stamp ? FontStyle.Bold : s.Style;
            string fam = s.HasTextStyle ? Renderer.GetFont(12f, FontStyle.Regular, s.FontFamilyName).FontFamily.Name : Renderer.DefaultFont;   // (a missing family falls back)
            if (Math.Abs(editBox.Font.Size - newSize) > 0.1f || editBox.Font.Style != fst || editBox.Font.FontFamily.Name != fam)
                SetEditFont(new Font(fam, newSize, fst, GraphicsUnit.Pixel));
            if (s.Kind == ShapeKind.Stamp || s.Kind == ShapeKind.Cloud)
            {
                RectangleF r = s.Kind == ShapeKind.Stamp ? Geo.Rect(s.Pts[0], s.Pts[1]) : Renderer.CalloutRect(s);
                PointF a = ToScr(r.Location);
                int w = Math.Max((int)(r.Width * Zoom - 8), (int)(90 * Dpi));
                Size need = TextRenderer.MeasureText(editBox.Text + "W", editBox.Font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                int h = Math.Max((int)(r.Height * Zoom - 8), need.Height + 8);
                editBox.Bounds = new Rectangle((int)(a.X + 4), (int)(a.Y + 4), w, h);
                return;
            }
            if (s.Kind == ShapeKind.Text && Renderer.WrapWidth(s) > 0)
            {
                PointF a = ToScr(s.Pts[0]);
                int w = Math.Max((int)(Renderer.WrapWidth(s) * Zoom) + 8, (int)(60 * Dpi));
                Size need = TextRenderer.MeasureText(editBox.Text + "W", editBox.Font, new Size(w - 8, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                int boxH = (int)((s.Pts[1].Y - s.Pts[0].Y) * Zoom) + 6;     // a box dragged taller keeps its height while typing
                editBox.Bounds = new Rectangle((int)a.X - 3, (int)a.Y - 3, w, Math.Max(need.Height + 10, boxH));
                return;
            }
            if (s.Kind == ShapeKind.Bubble)
            {
                RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
                PointF a = ToScr(r.Location);
                float pad = Math.Max(4f, s.Width + 4f) * Zoom;
                int w = Math.Max((int)(r.Width * Zoom - 2 * pad), (int)(70 * Dpi));
                int h = Math.Max((int)(r.Height * Zoom - 2 * pad), editBox.Font.Height + 8);
                editBox.Bounds = new Rectangle((int)(a.X + pad), (int)(a.Y + pad), w, h);
            }
            else
            {
                PointF a = ToScr(s.Pts[0]);
                Size sz = TextRenderer.MeasureText(editBox.Text + "W", editBox.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.TextBoxControl);
                editBox.Bounds = new Rectangle((int)a.X - 3, (int)a.Y - 3, Math.Max(sz.Width + 14, (int)(90 * Dpi)), sz.Height + 8);
            }
        }

        public void EndTextEdit(bool commit)
        {
            if (editBox == null) return;
            TextBox tb = editBox;
            editBox = null;
            Shape s = editShape;
            editShape = null;
            string text = tb.Text.Replace("\r\n", "\n");
            Font tbFont = tb.Font;
            Controls.Remove(tb);
            tb.Dispose();
            if (tbFont != null) tbFont.Dispose();

            if (!commit)
            {
                if (!editIsNew) s.Text = editOrigText;
            }
            else if (editIsNew)
            {
                if (text.Trim().Length > 0 || s.Kind == ShapeKind.Bubble || s.Kind == ShapeKind.Cloud) { s.Text = text; AddShape(s); }
            }
            else if (text != editOrigText)
            {
                s.Text = editOrigText;
                PushUndo(editBefore);
                if (s.Kind == ShapeKind.Text && text.Trim().Length == 0) { Shapes.Remove(s); Sel.Remove(s); FireSel(); }
                else { s.Text = text; KeepInside(new List<Shape> { s }); }     // lock: longer text stays on the picture
                FireDoc();
            }
            else s.Text = editOrigText;
            editBefore = null;
            Invalidate();
            if (Visible) Focus();
            FireState();
        }

        // ---------- context menu ----------
        void ShowContextMenu(Point at, PointF ip)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = Native.UiFont("Segoe UI", 9f, Dpi);
            bool any = Sel.Count > 0;
            Func<string, string, bool, Action, ToolStripMenuItem> add = (text, keys, enabled, a) =>
            {
                ToolStripMenuItem it = new ToolStripMenuItem(text);
                it.ShortcutKeyDisplayString = keys;
                it.Enabled = enabled;
                it.Click += (s, e) => a();
                m.Items.Add(it);
                return it;
            };
            add("Cut", "Ctrl+X", any, CutSelection);
            add("Copy", "Ctrl+C", any, () => CopySelection());
            add("Paste here", "Ctrl+V", ClipboardHasShapes, () => PasteShapes(ip));
            add("Paste picture here", ClipboardHasShapes ? "" : "Ctrl+V", ClipboardHasImage, () => PasteImage(ip));
            add("Duplicate", "Ctrl+D", any, DuplicateSelection);
            add("Delete", "Del", any, DeleteSelected);
            Shape one = Sel.Count == 1 ? Selected : null;
            if (Sel.Exists(x => x.HasHeads))
            {
                m.Items.Add(new ToolStripSeparator());
                int curS = Sel.Find(x => x.HasHeads).HeadStart, curE = Sel.Find(x => x.HasHeads).HeadEnd;
                for (int end = 0; end < 2; end++)
                {
                    bool start = end == 0;
                    ToolStripMenuItem sub = new ToolStripMenuItem(start ? "Start head" : "End head");
                    sub.Image = Icons.HeadPreview(start ? curS : curE, !start, (int)(16 * Dpi));
                    sub.DropDown.Font = m.Font;
                    for (int hi = 0; hi < Icons.HeadMenu.Length; hi++)
                    {
                        int st = Icons.HeadMenu[hi];
                        ToolStripMenuItem it = new ToolStripMenuItem(Icons.HeadNames[hi]);
                        it.Image = Icons.HeadPreview(st, !start, (int)(16 * Dpi));
                        it.Checked = (start ? curS : curE) == st;
                        it.Click += (s, e) => SetHeads(start, st);
                        sub.DropDownItems.Add(it);
                    }
                    m.Items.Add(sub);
                }
                add("Swap start / end heads", "", true, SwapHeads);
            }
            if (one != null && (one.IsMeasure || one.Kind == ShapeKind.Counter || one.Kind == ShapeKind.Note || one.Kind == ShapeKind.Stamp))
            {
                m.Items.Add(new ToolStripSeparator());
                string what = one.IsSymbolMarker ? "Change the symbol…" : one.Kind == ShapeKind.Counter ? (one.IsLetterMarker ? "Label the letter series…" : "Label the number series…")
                            : one.Kind == ShapeKind.Note ? "Edit note…" : one.Kind == ShapeKind.Stamp ? "Edit stamp text…" : "Label…";
                add(what, "double-click", true, () => EditLabel(one));
                if (one.Kind == ShapeKind.Coord) add("Make this point the origin (0,0)", "", HasOrigin, () => MakeOrigin(one));
                if (one.Kind == ShapeKind.Coord) add("Origin coordinates…", "", HasOrigin, SetOriginValue);
            }
            if (one != null && one.Kind == ShapeKind.Origin)
            {
                m.Items.Add(new ToolStripSeparator());
                add("Origin coordinates…", "double-click", true, SetOriginValue);
            }
            if (CanHaveHoles(one))
            {
                if (one.Kind != ShapeKind.Area) m.Items.Add(new ToolStripSeparator());
                add(one.Kind == ShapeKind.Area ? "Add a cut-out (opening)" : one.Kind == ShapeKind.Highlight ? "Add a cut-out" : "Add a cut-out (hollow section)", "X", true, () => CurrentTool = Tool.Cutout);
                if (one.Holes != null) add("Remove cut-outs", "", true, ClearCutouts);
            }
            if (one != null && one.HasTextStyle)
            {
                // strike-out lives here (the text bar keeps just font, B, I, U and alignment)
                m.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem so = add("Strike out", "", true, () =>
                {
                    PushUndo(Snapshot());
                    one.Strikeout = !one.Strikeout;
                    one.Cache = null;
                    FireDoc(); FireSel(); Invalidate();
                });
                so.Checked = one.Strikeout;
            }
            if (one != null && one.Kind == ShapeKind.Image && one.Img != null)
            {
                // a pasted or inserted picture: back to its real size, and how see-through it is
                m.Items.Add(new ToolStripSeparator());
                add("Reset picture (real size, not rotated)", "", true, () => ResetPicture(one));
                ToolStripMenuItem op = new ToolStripMenuItem("Picture opacity");
                op.DropDown.Font = m.Font;
                foreach (int v in new[] { 100, 75, 50, 25 })
                {
                    int vv = v;
                    ToolStripMenuItem it = new ToolStripMenuItem(v + " %");
                    it.Checked = one.FillOpacity == v;
                    it.Click += (s, e) => SetPictureOpacity(one, vv);
                    op.DropDownItems.Add(it);
                }
                m.Items.Add(op);
            }
            m.Items.Add(new ToolStripSeparator());
            add("Bring to front", "Ctrl+Shift+]", any, () => Order(0));
            add("Bring forward", "Ctrl+]", any, () => Order(1));
            add("Send backward", "Ctrl+[", any, () => Order(2));
            add("Send to back", "Ctrl+Shift+[", any, () => Order(3));
            m.Items.Add(new ToolStripSeparator());
            add("Select all", "Ctrl+A", Shapes.Count > 0, SelectAll);
            if (CropRect.HasValue) add("Remove crop", "", true, RemoveCrop);
            if (HGuides.Count + VGuides.Count > 0) add("Clear guides", "", true, () => { HGuides.Clear(); VGuides.Clear(); Invalidate(); });
            m.Closed += (s, e) => BeginInvoke((Action)(() =>
            {
                // line-end previews are bitmaps made for this menu only
                List<Image> icons = new List<Image>();
                foreach (ToolStripItem top in m.Items)
                {
                    ToolStripMenuItem mi = top as ToolStripMenuItem;
                    if (mi == null) continue;
                    if (mi.Image != null) icons.Add(mi.Image);
                    foreach (ToolStripItem sub in mi.DropDownItems) if (sub.Image != null) icons.Add(sub.Image);
                }
                Font mf = m.Font;
                m.Dispose();
                foreach (Image ic in icons) ic.Dispose();
                mf.Dispose();
            }));
            m.Show(this, at);
        }

        void ShowRulerMenu(Point at)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = Native.UiFont("Segoe UI", 9f, Dpi);
            ToolStripMenuItem clr = new ToolStripMenuItem("Clear all guides");
            clr.Enabled = HGuides.Count + VGuides.Count > 0;
            clr.Click += (s, e) => { HGuides.Clear(); VGuides.Clear(); Invalidate(); };
            m.Items.Add(clr);
            ToolStripMenuItem hide = new ToolStripMenuItem("Hide rulers");
            hide.ShortcutKeyDisplayString = "Ctrl+R";
            hide.Click += (s, e) => { ShowRulers = false; if (ViewChanged != null) ViewChanged(this, EventArgs.Empty); };
            m.Items.Add(hide);
            m.Closed += (s, e) => BeginInvoke((Action)(() => m.Dispose()));
            m.Show(this, at);
        }

        // ---------- mouse ----------
        /// <summary>For the status-bar guide: -1 = not drawing, otherwise how many points the shape being drawn has.</summary>
        public int CreatingPoints { get { return creating == null ? -1 : creating.Pts.Count; } }
        public bool HasOrigin { get { return Renderer.Origin(Shapes) != null; } }

        void FireState() { if (StateChanged != null) StateChanged(this, EventArgs.Empty); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (ZoomBoxMouse(e, 0)) return;
            if (MatchMouse(e)) { Focus(); return; }
            MouseDownCore(e);
            FireState();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (ZoomBoxMouse(e, 2)) return;
            if (matchSource != null) return;
            MouseUpCore(e);
            FireState();
        }

        void MouseDownCore(MouseEventArgs e)
        {
            if (editBox != null)
            {
                EndTextEdit(true);
                if (e.Button == MouseButtons.Left) return;    // this click just finishes typing
            }
            Focus();
            PointF ip = ToImg(e.Location);
            if (tool != Tool.Select && tool != Tool.Crop) ip = Keep(ip);     // lock: new shapes start inside the picture
            bool shift = (ModifierKeys & Keys.Shift) != 0, ctrl = (ModifierKeys & Keys.Control) != 0;

            if (e.Button == MouseButtons.Middle && e.Clicks == 2) { Fit(); return; }
            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && spaceDown))
            {
                panning = true; panLast = e.Location; Cursor = Cursors.SizeAll;
                return;
            }
            // rulers: drag out a new guide, right-click for the ruler menu
            if (InTopRuler(e.Location) || InLeftRuler(e.Location) || (showRulers && e.X < RulerSize && e.Y < RulerSize))
            {
                if (e.Button == MouseButtons.Right) { ShowRulerMenu(e.Location); return; }
                if (e.Button == MouseButtons.Left && Image != null)
                {
                    if (InTopRuler(e.Location)) { guideAxis = 1; guideIdx = -1; guideVal = (float)Math.Round(ip.Y); guideOverRuler = true; }
                    else if (InLeftRuler(e.Location)) { guideAxis = 2; guideIdx = -1; guideVal = (float)Math.Round(ip.X); guideOverRuler = true; }
                }
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                if (creating != null && creating.IsPoly) { FinishPoly(); return; }
                if (creating != null) { creating = null; Invalidate(); return; }
                if (Image == null) return;
                Shape hit = HitShape(ip);
                if (hit != null && !Sel.Contains(hit)) { Selected = hit; FireSel(); Invalidate(); }
                ShowContextMenu(e.Location, ip);
                return;
            }
            if (e.Button != MouseButtons.Left || Image == null) return;

            PointF sp = Snap(ip);
            switch (PolyAs)
            {
                case Tool.Select:
                    {
                        if (e.Clicks == 2) { DoubleClickSelect(ip); return; }
                        Handle? h = HandleAt(e.Location);
                        if (h.HasValue)
                        {
                            if (ctrl && h.Value.Kind == HKind.Point && Selected.IsPoly && Selected.Pts.Count > 3)
                            {
                                PushUndo(Snapshot());
                                Selected.Pts.RemoveAt(h.Value.Index);
                                FireDoc(); Invalidate();
                                return;
                            }
                            if (ctrl && h.Value.Kind == HKind.HolePoint && Selected.Holes != null)
                            {
                                // Ctrl+click a cut-out corner: remove that corner (a three-corner cut-out goes altogether)
                                int hi = h.Value.Index / ShapeOps.HoleStride, pi = h.Value.Index % ShapeOps.HoleStride;
                                if (hi < Selected.Holes.Count)
                                {
                                    PushUndo(Snapshot());
                                    if (Selected.Holes[hi].Count > 3) Selected.Holes[hi].RemoveAt(pi);
                                    else Selected.Holes.RemoveAt(hi);
                                    if (Selected.Holes.Count == 0) Selected.Holes = null;
                                    Selected.Cache = null;
                                    FireDoc(); Invalidate();
                                }
                                return;
                            }
                            before = Snapshot(); changedDuringDrag = false;
                            dragH = h; dragOrig = Selected.Clone();
                            return;
                        }
                        Shape hit = HitShape(ip);
                        pendingSingle = null;
                        if (hit != null)
                        {
                            if (shift || ctrl)
                            {
                                if (Sel.Contains(hit)) Sel.Remove(hit); else Sel.Add(hit);
                                FireSel(); Invalidate();
                                if (!Sel.Contains(hit)) return;
                            }
                            else if (!Sel.Contains(hit)) { Selected = hit; FireSel(); }
                            else if (Sel.Count > 1) pendingSingle = hit;
                            before = Snapshot(); changedDuringDrag = false; dragBody = true; dragLast = ip;
                            Invalidate();
                            return;
                        }
                        int ga, gi;
                        if (GuideAt(e.Location, out ga, out gi))
                        {
                            guideAxis = ga; guideIdx = gi; guideOverRuler = false;
                            guideVal = ga == 1 ? HGuides[gi] : VGuides[gi];
                            return;
                        }
                        if (!shift && !ctrl && Sel.Count > 0) { Sel.Clear(); FireSel(); }
                        marquee = true; marqA = marqB = ip;
                        Invalidate();
                        break;
                    }
                case Tool.Text:
                    {
                        Shape hit = HitShape(ip);
                        if (hit != null && (hit.Kind == ShapeKind.Text || hit.Kind == ShapeKind.Stamp)) { BeginTextEdit(hit, false); break; }
                        // click = text that grows as you type; drag = a text box of that width (the text wraps inside)
                        Sel.Clear(); FireSel();
                        creating = NewShape(Tool.Text, ip);
                        creating.Pts.Add(ip);
                        break;
                    }
                case Tool.Coord:
                    {
                        if (!HasOrigin)
                        {
                            Shape o = NewShape(Tool.Coord, sp);
                            o.Kind = ShapeKind.Origin;
                            AddShape(o);
                            if (Message != null) Message("Origin 0,0 placed – now click the points you want coordinates for (Y counts upwards, like CAD)");
                        }
                        else
                        {
                            Shape c = NewShape(Tool.Coord, sp);
                            c.Pts.Add(new PointF(sp.X + 40f * Dpi / Zoom, sp.Y - 34f * Dpi / Zoom));
                            AddShape(c);
                        }
                        break;
                    }
                case Tool.Counter:
                case Tool.Letter:
                    {
                        Shape mk = NewShape(tool, ip);
                        // a new marker looks like the rest of its series (size, colours, shadow …)
                        Shape sib = Shapes.Find(x => x.Kind == ShapeKind.Counter && x.Mode == mk.Mode);
                        if (sib != null) MatchProps(sib, mk);
                        AddShape(mk);
                        bool letters = tool == Tool.Letter;
                        int count = Measure.Count(Shapes, letters);
                        if (!letters && Scale.MarkerReverse && count > Scale.MarkerStart)
                        {
                            // a countdown never goes below 1: raise the start so every number stays unique
                            Scale.MarkerStart = count;
                            NotifyDocChanged();
                            if (Message != null) Message("Countdown reached 1 – the start number was raised to " + count);
                        }
                        else if (letters && Scale.LetterReverse && count == Math.Max(1, Scale.LetterStart) + 1 && Message != null)
                            Message("Letters ran past A – starting again at Z");
                    }
                    break;
                case Tool.Symbol:
                    {
                        // 1.4.2: any character; it never counts. The last one typed is offered again.
                        string last = Styles[Tool.Symbol].Text;
                        string t = AskSymbol != null ? AskSymbol("Symbol marker: a symbol or short text", string.IsNullOrEmpty(last) ? "Ø" : last) : last;
                        if (string.IsNullOrEmpty(t)) break;
                        Shape mk = NewShape(tool, ip);
                        mk.Text = t;
                        Styles[Tool.Symbol].Text = t;
                        AddShape(mk);
                    }
                    break;
                case Tool.Fill:
                    FillAt(ip);
                    break;
                case Tool.Note:
                    {
                        Shape hit = HitShape(ip);
                        if (hit != null && hit.Kind == ShapeKind.Note) { Selected = hit; FireSel(); EditLabel(hit); break; }
                        string t = AskNote != null ? AskNote("Note (shown here only – not on saved pictures)", "") : null;
                        if (t == null || t.Trim().Length == 0) break;
                        Shape n = NewShape(Tool.Note, ip);
                        n.Text = t;
                        AddShape(n);
                        break;
                    }
                case Tool.Crop:
                    {
                        int hi = CropHandleAt(e.Location);
                        if (hi >= 0 || (CropRect.HasValue && CropRect.Value.Contains(Point.Round(ip))))
                        {
                            before = Snapshot(); changedDuringDrag = false;
                            cropDrag = hi >= 0 ? hi : 8;
                            cropOrig = CropRect.Value;
                            cropStart = ip;
                            return;
                        }
                        Sel.Clear(); FireSel();
                        creating = NewShape(tool, ip);
                        creating.Pts.Add(ip);
                        break;
                    }
                case Tool.Angle:
                case Tool.Arc:
                case Tool.Radius:
                    {
                        // three clicks: angle = arm, vertex, arm · arc / radius = start, a point on the curve, end
                        if (creating == null)
                        {
                            Sel.Clear(); FireSel();
                            creating = NewShape(tool, sp);
                            creating.Pts.Add(sp);
                        }
                        else
                        {
                            int n = creating.Pts.Count;
                            PointF p = sp;
                            if (shift && !snapPt.HasValue && n >= 2) p = ShiftSnap(creating, n, sp);
                            creating.Pts[n - 1] = p;
                            if (n >= 3)
                            {
                                Shape c = creating;
                                creating = null;
                                if (Geo.Dist(c.Pts[0], c.Pts[1]) > 1f && Geo.Dist(c.Pts[2], c.Pts[1]) > 1f) AddShape(c);
                            }
                            else creating.Pts.Add(p);
                        }
                        Invalidate();
                        break;
                    }
                case Tool.Polygon:
                case Tool.PolyDraw:
                case Tool.Area:
                case Tool.Polyline:
                case Tool.Perimeter:
                case Tool.Cutout:
                    {
                        if (creating == null)
                        {
                            // the cut-out tool keeps the area selected, so the opening goes into that one
                            if (tool != Tool.Cutout) { Sel.Clear(); FireSel(); }
                            creating = NewShape(tool, sp);
                            creating.Pts.Add(sp);
                        }
                        else if (e.Clicks >= 2) FinishPoly();
                        else
                        {
                            int n = creating.Pts.Count;
                            PointF p = sp;
                            if (shift && !snapPt.HasValue && n >= 2) p = Geo.SnapAngle(creating.Pts[n - 2], sp, 15);
                            if (n > 3 && !creating.IsOpenPath && Geo.Dist(p, creating.Pts[0]) < 9f * Dpi / Zoom) { FinishPoly(); return; }
                            creating.Pts[n - 1] = p;
                            creating.Pts.Add(p);
                        }
                        Invalidate();
                        break;
                    }
                case Tool.Freehand:
                case Tool.Marker:
                    Sel.Clear(); FireSel();
                    creating = NewShape(tool, tool == Tool.Freehand ? Snap(ip) : ip);     // 1.4.2: the stroke starts on a nearby shape
                    break;
                default:
                    Sel.Clear(); FireSel();
                    creating = NewShape(tool, sp);
                    creating.Pts.Add(sp);
                    if (tool == Tool.Dimension) creating.Pts.Add(sp);
                    break;
            }
        }

        void DoubleClickSelect(PointF ip)
        {
            Shape s = HitShape(ip);
            if (s == null) return;
            Selected = s; FireSel();
            if (s.Kind == ShapeKind.Text || s.Kind == ShapeKind.Bubble || s.Kind == ShapeKind.Stamp ||
                (s.Kind == ShapeKind.Cloud && s.Mode != (int)CloudCallout.None && s.Pts.Count > 2)) { BeginTextEdit(s, false); return; }
            if (s.IsPoly)
            {
                // double-click on an edge inserts a new vertex there
                int segs = s.IsOpenPath ? s.Pts.Count - 1 : s.Pts.Count;
                for (int i = 0; i < segs; i++)
                {
                    PointF a = s.Pts[i], b = s.Pts[(i + 1) % s.Pts.Count];
                    if (Geo.SegDist(ip, a, b) <= Tol + s.Width)
                    {
                        PushUndo(Snapshot());
                        s.Pts.Insert(i + 1, Geo.Project(ip, a, b));
                        FireDoc(); Invalidate();
                        return;
                    }
                }
            }
            if (s.Kind == ShapeKind.Origin) { SetOriginValue(); return; }
            if (s.IsMeasure || s.Kind == ShapeKind.Note || s.Kind == ShapeKind.Stamp || s.Kind == ShapeKind.Counter) EditLabel(s);
        }

        void InvalidateRulers()
        {
            if (!showRulers) return;
            int rs = (int)RulerSize + 1;
            // the rulers are drawn on top of the cached layer: no need to redraw the picture and shapes
            lightInvalidate = true;
            try
            {
                Invalidate(new Rectangle(0, 0, Width, rs));
                Invalidate(new Rectangle(0, 0, rs, Height));
            }
            finally { lightInvalidate = false; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (ZoomBoxMouse(e, 1)) return;
            if (matchSource != null) { Cursor = Cursors.Hand; return; }
            PointF ip = ToImg(e.Location);
            mouseImg = ip;
            mouseScr = e.Location;
            InvalidateRulers();
            if (LoupeTool(tool)) InvalidateLight();     // the magnifier follows the pointer (drawn on top of the cached layer)
            if (CursorMoved != null) CursorMoved(ip);
            if (panning)
            {
                Pan = new PointF(Pan.X + e.X - panLast.X, Pan.Y + e.Y - panLast.Y);
                panLast = e.Location;
                ViewMoved();
                return;
            }
            if (guideAxis != 0)
            {
                guideVal = (float)Math.Round(guideAxis == 1 ? ip.Y : ip.X);
                guideOverRuler = guideAxis == 1 ? e.Y < RulerSize : e.X < RulerSize;
                Cursor = guideAxis == 1 ? Cursors.HSplit : Cursors.VSplit;
                Invalidate();
                return;
            }
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            PointF? oldSnap = snapPt;

            if (cropDrag >= 0)
            {
                Rectangle r = cropOrig;
                float l = r.X, t = r.Y, rt = r.Right, bt = r.Bottom;
                if (cropDrag == 8)
                {
                    float dx = ip.X - cropStart.X, dy = ip.Y - cropStart.Y;
                    dx = Geo.Clamp(dx, -r.X, Image.Width - r.Right);
                    dy = Geo.Clamp(dy, -r.Y, Image.Height - r.Bottom);
                    l += dx; rt += dx; t += dy; bt += dy;
                }
                else
                {
                    int i = cropDrag;
                    float x = Geo.Clamp(ip.X, 0, Image.Width), y = Geo.Clamp(ip.Y, 0, Image.Height);
                    if (i == 0 || i == 6 || i == 7) l = x;
                    if (i == 2 || i == 3 || i == 4) rt = x;
                    if (i == 0 || i == 1 || i == 2) t = y;
                    if (i == 4 || i == 5 || i == 6) bt = y;
                }
                Rectangle nr = Rectangle.Round(RectangleF.FromLTRB(Math.Min(l, rt), Math.Min(t, bt), Math.Max(l, rt), Math.Max(t, bt)));
                if (nr.Width >= 2 && nr.Height >= 2) { CropRect = nr; changedDuringDrag = true; }
                Invalidate();
                return;
            }

            if (marquee)
            {
                marqB = ip;
                Invalidate();
                return;
            }

            if (creating != null)
            {
                if (tool != Tool.Crop) ip = Keep(ip);
                PointF sp = tool == Tool.Freehand || tool == Tool.Marker || tool == Tool.Crop || tool == Tool.Ocr || tool == Tool.Markup ? ip : Snap(ip);
                int n = creating.Pts.Count;
                PointF p0 = creating.Pts[0];
                switch (PolyAs)
                {
                    case Tool.Marker:
                        // Shift = a straight stroke (e.g. along a line of text)
                        if (shift) { creating.Pts.Clear(); creating.Pts.Add(p0); creating.Pts.Add(ip); }
                        else if (Geo.Dist(creating.Pts[n - 1], ip) > 1.2f / Zoom) creating.Pts.Add(ip);
                        break;
                    case Tool.Freehand:
                        if (Geo.Dist(creating.Pts[n - 1], ip) > 1.2f / Zoom) creating.Pts.Add(ip);
                        Snap(ip);       // shows where the end will snap when the button is let go
                        break;
                    case Tool.Polygon:
                    case Tool.PolyDraw:
                    case Tool.Area:
                    case Tool.Angle:
                    case Tool.Arc:
                    case Tool.Radius:
                    case Tool.Polyline:
                    case Tool.Perimeter:
                    case Tool.Cutout:
                        creating.Pts[n - 1] = (shift && !snapPt.HasValue && n >= 2) ? ShiftSnap(creating, n, sp) : sp;
                        break;
                    case Tool.Text:
                        if (creating.Pts.Count >= 2) creating.Pts[1] = ip;
                        break;
                    case Tool.Rect:
                    case Tool.Ellipse:
                    case Tool.Pixelate:
                    case Tool.Crop:
                    case Tool.Highlight:
                    case Tool.Bubble:
                    case Tool.Cloud:
                    case Tool.Stamp:
                    case Tool.Markup:
                    case Tool.Ocr:
                        {
                            PointF p = sp;
                            if (shift)
                            {
                                float dx = p.X - p0.X, dy = p.Y - p0.Y, d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                                p = new PointF(p0.X + (dx < 0 ? -d : d), p0.Y + (dy < 0 ? -d : d));
                            }
                            creating.Pts[1] = p;
                            break;
                        }
                    default: // line, arrow, dimension, calibrate
                        {
                            PointF p = (shift && !snapPt.HasValue) ? Geo.SnapAngle(p0, sp, 15) : sp;
                            creating.Pts[1] = p;
                            break;
                        }
                }
                InvalidateLight();     // the shape being drawn is not in the cached layer
                return;
            }

            if (dragH.HasValue && Selected != null)
            {
                Handle h = dragH.Value;
                PointF p = h.Kind == HKind.Rotate ? ip : h.Kind == HKind.Box ? Keep(ip) : Keep(Snap(ip));
                if (shift && !snapPt.HasValue && h.Kind == HKind.Point && h.Index < 2 &&
                    (Selected.Kind == ShapeKind.Line || Selected.Kind == ShapeKind.Arrow || Selected.Kind == ShapeKind.Dimension))
                    p = Geo.SnapAngle(Selected.Pts[1 - h.Index], p, 15);
                // angle: an arm end snaps the angle to 45° steps from the other arm; the vertex snaps its first arm to 45° directions
                if (shift && !snapPt.HasValue && h.Kind == HKind.Point && Selected.Kind == ShapeKind.Angle && Selected.Pts.Count >= 3)
                {
                    List<PointF> q = Selected.Pts;
                    if (h.Index == 0) p = AngleArmSnap(q[1], q[2], p);
                    else if (h.Index == 2) p = AngleArmSnap(q[1], q[0], p);
                    else if (h.Index == 1) p = Geo.SnapAngle(q[0], p, 45);
                }
                ShapeOps.DragHandle(Selected, dragOrig, h, p, shift);
                if (Selected.Kind == ShapeKind.Counter && h.Kind == HKind.Box) MatchMarkerSeries(Selected);
                changedDuringDrag = true;
                Invalidate();
                return;
            }
            if (dragBody && Sel.Count > 0)
            {
                float dx = ip.X - dragLast.X, dy = ip.Y - dragLast.Y;
                if (dx != 0 || dy != 0)
                {
                    foreach (Shape s in Sel) s.Offset(dx, dy);
                    KeepInside(new List<Shape>(Sel));      // lock: the selection stops at the picture edge
                    dragLast = ip;
                    changedDuringDrag = true;
                    pendingSingle = null;
                    Invalidate();
                }
                return;
            }

            if (InTopRuler(e.Location)) Cursor = Cursors.HSplit;
            else if (InLeftRuler(e.Location)) Cursor = Cursors.VSplit;
            else if (tool == Tool.Select)
            {
                snapPt = null;
                Handle? h = HandleAt(e.Location);
                int ga, gi;
                if (h.HasValue) Cursor = HandleCursor(h.Value, Selected);
                else if (HitShape(ip) != null) Cursor = Cursors.SizeAll;
                else if (GuideAt(e.Location, out ga, out gi)) Cursor = ga == 1 ? Cursors.HSplit : Cursors.VSplit;
                else Cursor = Cursors.Default;
            }
            else if (tool == Tool.Crop)
            {
                int hi = CropHandleAt(e.Location);
                if (hi >= 0) Cursor = (hi == 1 || hi == 5) ? Cursors.SizeNS : (hi == 3 || hi == 7) ? Cursors.SizeWE : (hi == 0 || hi == 4) ? Cursors.SizeNWSE : Cursors.SizeNESW;
                else if (CropRect.HasValue && CropRect.Value.Contains(Point.Round(ip))) Cursor = Cursors.SizeAll;
                else Cursor = Cursors.Cross;
            }
            else
            {
                Cursor = tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
                Snap(ip);
            }
            if (!Nullable.Equals(oldSnap, snapPt) || tool != Tool.Select) InvalidateLight();
        }

        void MouseUpCore(MouseEventArgs e)
        {
            if (panning)
            {
                panning = false;
                Cursor = tool == Tool.Select ? Cursors.Default : Cursors.Cross;
                return;
            }
            if (guideAxis != 0)
            {
                List<float> list = guideAxis == 1 ? HGuides : VGuides;
                if (guideOverRuler) { if (guideIdx >= 0) list.RemoveAt(guideIdx); }     // dropped back on the ruler = delete
                else if (guideIdx >= 0) list[guideIdx] = guideVal;
                else list.Add(guideVal);
                guideAxis = 0; guideIdx = -1;
                Invalidate();
                return;
            }
            if (cropDrag >= 0)
            {
                if (changedDuringDrag && before != null) { PushUndo(before); FireDoc(); }
                cropDrag = -1; before = null; changedDuringDrag = false;
                Invalidate();
                return;
            }
            if (marquee)
            {
                marquee = false;
                RectangleF r = Geo.Rect(marqA, marqB);
                if (r.Width * Zoom > 3 || r.Height * Zoom > 3)
                {
                    foreach (Shape s in Shapes)
                        if (!Sel.Contains(s) && !HiddenByLock(s) && r.IntersectsWith(ShapeOps.Bounds(s))) Sel.Add(s);
                    FireSel();
                }
                Invalidate();
                return;
            }
            if (creating != null && (creating.Kind == ShapeKind.Angle || creating.Kind == ShapeKind.Arc || creating.Kind == ShapeKind.Radius))
            {
                // press-drag-release draws the first arm in one go
                if (creating.Pts.Count == 2 && Geo.Dist(creating.Pts[0], creating.Pts[1]) * Zoom > 6f * Dpi)
                {
                    creating.Pts.Add(creating.Pts[1]);
                    Invalidate();
                }
                return;
            }
            if (creating != null && !creating.IsPoly)
            {
                Shape c = creating;
                creating = null;
                switch (tool)
                {
                    case Tool.Crop:
                        if (c.Pts.Count >= 2) DoCrop(Geo.Rect(c.Pts[0], c.Pts[1]));
                        break;
                    case Tool.Ocr:
                        {
                            Rectangle r = c.Pts.Count >= 2 ? Rectangle.Round(Geo.Rect(c.Pts[0], c.Pts[1])) : Rectangle.Empty;
                            if (r.Width * Zoom < 6 || r.Height * Zoom < 6) r = Rectangle.Round(ViewRect);     // a click reads the whole picture
                            r.Intersect(new Rectangle(0, 0, Image.Width, Image.Height));
                            Invalidate();
                            if (r.Width > 2 && r.Height > 2 && OcrArea != null) OcrArea(r);
                            return;
                        }
                    case Tool.Text:
                        {
                            RectangleF r = Geo.Rect(c.Pts[0], c.Pts[1]);
                            if (r.Width * Zoom < 24 * Dpi) { c.Pts.Clear(); c.Pts.Add(new PointF(r.X, r.Y)); }   // a click: text grows as you type
                            else { c.Pts[0] = r.Location; c.Pts[1] = new PointF(r.Right, r.Y); }            // a drag: fixed-width text box
                            Invalidate();
                            BeginTextEdit(c, true);
                            return;
                        }
                    case Tool.Stamp:
                        {
                            RectangleF r = Geo.Rect(c.Pts[0], c.Pts[1]);
                            if (r.Width * Zoom < 12 || r.Height * Zoom < 12)
                                r = new RectangleF(c.Pts[0].X, c.Pts[0].Y, 230f * Dpi / Zoom, 84f * Dpi / Zoom);   // a click makes a default-size stamp
                            c.Pts[0] = r.Location;
                            c.Pts[1] = new PointF(r.Right, r.Bottom);
                            if (c.Mode == Stamps.Custom)
                            {
                                // your own stamp: type straight onto it
                                string dt = Stamps.FormatDate(DateTime.Now, Settings.GetInt("StampDate", 0));
                                c.Text = dt.Length > 0 ? "\n" + dt : "";
                                Invalidate();
                                BeginTextEdit(c, true);
                                return;
                            }
                            AddShape(c);
                            break;
                        }
                    case Tool.Cloud:
                        {
                            RectangleF r = Geo.Rect(c.Pts[0], c.Pts[1]);
                            if (r.Width * Zoom < 6 || r.Height * Zoom < 6) break;
                            if (c.Mode != (int)CloudCallout.None)
                            {
                                // the callout sits down-right of the cloud, on the leader
                                RectangleF vr = ViewRect;
                                float dx = 70f * Dpi / Zoom, dy = 46f * Dpi / Zoom;
                                PointF tag = new PointF(r.Right + dx, r.Bottom + dy);
                                if (tag.X > vr.Right - dx * 0.5f) tag.X = r.X - dx;
                                if (tag.Y > vr.Bottom - dy * 0.5f) tag.Y = r.Y - dy;
                                c.Pts.Add(tag);
                                if (c.Mode == (int)CloudCallout.Triangle)
                                {
                                    int n = 1;
                                    foreach (Shape x in Shapes) if (x.Kind == ShapeKind.Cloud && x.Mode == (int)CloudCallout.Triangle) n++;
                                    c.Text = n.ToString(CultureInfo.InvariantCulture);
                                }
                                else
                                {
                                    Invalidate();
                                    BeginTextEdit(c, true);
                                    return;
                                }
                            }
                            AddShape(c);
                            break;
                        }
                    case Tool.Markup:
                        {
                            RectangleF r = Geo.Rect(c.Pts[0], c.Pts[1]);
                            if (r.Width * Zoom < 4) break;
                            if (r.Height * Zoom < 4) r = new RectangleF(r.X, r.Y - 8f / Zoom, r.Width, 16f / Zoom);   // a flat drag: look around the line
                            RectangleF snapped = SnapToText(r);
                            c.Pts[0] = snapped.Location;
                            c.Pts[1] = new PointF(snapped.Right, snapped.Bottom);
                            AddShape(c);
                            break;
                        }
                    case Tool.Calibrate:
                        {
                            float L = c.Pts.Count >= 2 ? Geo.Dist(c.Pts[0], c.Pts[1]) : 0;
                            if (L >= 2 && AskCalibration != null)
                            {
                                DocState b = Snapshot();
                                if (AskCalibration(L)) { PushUndo(b); FireDoc(); CurrentTool = Tool.Select; }
                            }
                            break;
                        }
                    case Tool.Freehand:
                    case Tool.Marker:
                        if (tool == Tool.Freehand && c.Pts.Count >= 2)
                        {
                            // 1.4.2: the end snaps onto a nearby point or line (or closes onto the start)
                            creating = c;       // Snap needs the stroke to find its own start
                            PointF end = Snap(c.Pts[c.Pts.Count - 1]);
                            creating = null;
                            if (snapPt.HasValue) c.Pts.Add(end);
                            snapPt = null;
                        }
                        if (c.Pts.Count >= 2) AddShape(c);
                        break;
                    case Tool.Bubble:
                        {
                            RectangleF r = Geo.Rect(c.Pts[0], c.Pts[1]);
                            if (r.Width < 12 / Zoom || r.Height < 12 / Zoom)
                                r = new RectangleF(c.Pts[0].X, c.Pts[0].Y, 170, 70);   // a click makes a default-size bubble
                            PointF tail = FitBubble(ref r, ViewRect);
                            c.Pts[0] = r.Location;
                            c.Pts[1] = new PointF(r.Right, r.Bottom);
                            c.Pts.Add(tail);
                            Invalidate();
                            BeginTextEdit(c, true);
                            return;
                        }
                    default:
                        if (c.Pts.Count >= 2 && Geo.Dist(c.Pts[0], c.Pts[1]) >= 3f / Zoom) AddShape(c);
                        break;
                }
                Invalidate();
                return;
            }
            if (dragH.HasValue || dragBody)
            {
                if (changedDuringDrag && Sel.Count > 0) KeepInside(new List<Shape>(Sel));   // lock: e.g. a shape turned over the edge
                if (changedDuringDrag && before != null) { PushUndo(before); FireDoc(); }
                else if (pendingSingle != null) { Selected = pendingSingle; FireSel(); }
                dragH = null; dragOrig = null; dragBody = false; changedDuringDrag = false; before = null; pendingSingle = null;
                snapPt = null;
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) != 0)
            {
                ZoomAt(Zoom * (e.Delta > 0 ? 1.25f : 0.8f), e.Location);
                return;
            }
            float step = e.Delta / 120f * 60f * Dpi;
            if ((ModifierKeys & Keys.Shift) != 0) Pan = new PointF(Pan.X + step, Pan.Y);
            else Pan = new PointF(Pan.X, Pan.Y + step);
            ViewMoved();
        }

        // when a bar above or beside the canvas grows or shrinks (the style bar changes with the selection), the canvas
        // moves inside the window: move the view the other way so the picture stays put on the screen
        Point placedAt = new Point(int.MinValue, 0);
        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (placedAt.X != int.MinValue && Image != null && (Left != placedAt.X || Top != placedAt.Y))
            {
                Pan = new PointF(Pan.X - (Left - placedAt.X), Pan.Y - (Top - placedAt.Y));
                ViewMoved();
            }
            placedAt = Location;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            // lock on: a bigger or smaller window still shows only the picture (centred when it fits)
            if (LockInside && Image != null && Width > 10 && Height > 10) ViewMoved();
        }

        /// <summary>Moves the view by screen pixels (the limits still apply).</summary>
        public void PanBy(float dx, float dy)
        {
            Pan = new PointF(Pan.X + dx, Pan.Y + dy);
            ViewMoved();
        }

        /// <summary>Side scrolling (tilt wheel, MX thumb wheel, touchpad): WM_MOUSEHWHEEL pans left / right.</summary>
        public void ScrollSideways(int delta)
        {
            Pan = new PointF(Pan.X - delta / 120f * 60f * Dpi, Pan.Y);
            ViewMoved();
        }

        /// <summary>Side scrolling is sent to the focused control (maybe a toolbar box); send it to the canvas under the pointer instead.</summary>
        sealed class SideWheelFilter : IMessageFilter
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != 0x020E) return false;
                Canvas cv = Control.FromHandle(WindowFromPoint(Cursor.Position)) as Canvas;
                if (cv == null) return false;
                cv.ScrollSideways((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
                return true;
            }
        }
        static bool sideWheelHooked;
        static void HookSideWheel()
        {
            if (sideWheelHooked) return;
            sideWheelHooked = true;
            Application.AddMessageFilter(new SideWheelFilter());
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEHWHEEL = 0x020E;
            if (m.Msg == WM_MOUSEHWHEEL)
            {
                ScrollSideways((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
                m.Result = (IntPtr)1;     // tell the mouse driver it was handled, or some stop sending it
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>If another window takes the mouse in the middle of a drag, cancel it cleanly
        /// (otherwise a guide or selection box could be left hanging and dropped on the next click).</summary>
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture) return;
            bool changed = false;
            if (guideAxis != 0) { guideAxis = 0; guideIdx = -1; changed = true; }
            if (marquee) { marquee = false; changed = true; }
            if (changed) Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            snapPt = null;
            mouseScr = new Point(-1000, -1000);
            Invalidate();
        }

        // ---------- keyboard ----------
        protected override bool IsInputKey(Keys k)
        {
            Keys code = k & Keys.KeyCode;
            if (code == Keys.Left || code == Keys.Right || code == Keys.Up || code == Keys.Down || code == Keys.Space || code == Keys.Enter || code == Keys.Escape || code == Keys.Home) return true;
            return base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { if (!spaceDown) { spaceDown = true; Cursor = Cursors.Hand; } e.Handled = true; return; }
            if (e.Control || e.Alt) return;
            int step = e.Shift ? 10 : 1;
            if (tool == Tool.Crop)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape) { CurrentTool = Tool.Select; e.Handled = true; return; }
                if (e.KeyCode == Keys.Delete) { RemoveCrop(); e.Handled = true; return; }
            }
            switch (e.KeyCode)
            {
                case Keys.Escape:
                    if (zoomBox) EndZoomBox();
                    else if (matchSource != null) EndMatch();
                    else if (creating != null) creating = null;
                    else
                    {
                        // one Esc: drop the selection and go back to the Select tool together
                        if (Sel.Count > 0) { Sel.Clear(); FireSel(); }
                        if (tool != Tool.Select) CurrentTool = Tool.Select;
                    }
                    Invalidate(); break;
                case Keys.Enter: FinishPoly(); break;
                case Keys.Back:
                    if (creating != null && creating.IsPoly && creating.Pts.Count > 2) { creating.Pts.RemoveAt(creating.Pts.Count - 2); Invalidate(); }
                    break;
                case Keys.Delete: DeleteSelected(); break;
                case Keys.Home:
                case Keys.D0:
                case Keys.NumPad0: Fit(); break;
                case Keys.Left: Nudge(-step, 0); break;
                case Keys.Right: Nudge(step, 0); break;
                case Keys.Up: Nudge(0, -step); break;
                case Keys.Down: Nudge(0, step); break;
                case Keys.V: CurrentTool = Tool.Select; break;
                case Keys.R: CurrentTool = e.Shift ? Tool.Radius : Tool.Rect; break;      // Shift+R = radius / diameter
                case Keys.E: CurrentTool = Tool.Ellipse; break;
                case Keys.L: CurrentTool = e.Shift ? Tool.Arc : Tool.Line; break;         // Shift+L = arc
                case Keys.A: CurrentTool = Tool.Arrow; break;
                case Keys.F: CurrentTool = e.Shift ? Tool.Fill : Tool.Freehand; break;     // 1.4.2: Shift+F = fill
                case Keys.T: CurrentTool = e.Shift ? Tool.Ocr : Tool.Text; break;
                case Keys.W: Styles[Tool.Perimeter].Mode = 1; CurrentTool = Tool.Perimeter; break;   // open path length
                case Keys.I: CurrentTool = Tool.Coord; break;
                case Keys.Q: Styles[Tool.Area].Mode = 2; CurrentTool = Tool.Area; break;   // closed perimeter = area tool showing the perimeter only
                case Keys.X: CurrentTool = Tool.Cutout; break;
                case Keys.U: CurrentTool = Tool.Cloud; break;
                case Keys.Z: if (e.Shift) CurrentTool = Tool.Stamp; else StartZoomBox(); break;   // 1.4.2: Z = zoom to area, Shift+Z = stamp
                case Keys.O: CurrentTool = Tool.Note; break;
                case Keys.P: CurrentTool = e.Shift ? Tool.PolyDraw : Tool.Polygon; break;   // Shift+P = polyline
                case Keys.D: CurrentTool = Tool.Dimension; break;
                case Keys.M:
                    if (e.Shift) { CurrentTool = Tool.Symbol; break; }      // 1.4.2: Shift+M = symbol marker
                    if (Styles[Tool.Area].Mode == 2) Styles[Tool.Area].Mode = 0; CurrentTool = Tool.Area; break;
                case Keys.G: CurrentTool = Tool.Angle; break;
                case Keys.K: CurrentTool = Tool.Calibrate; break;
                case Keys.N: CurrentTool = e.Shift ? Tool.Letter : Tool.Counter; break;
                case Keys.B:
                    // pixelate = a Focus mode: pick the tool (rectangle), then switch it to pixelate
                    CurrentTool = Tool.Highlight;
                    Styles[Tool.Highlight].Mode = (int)HighlightMode.Pixelate;
                    if (ToolChanged != null) ToolChanged(this, EventArgs.Empty);
                    break;
                case Keys.C: CurrentTool = Tool.Crop; break;
                case Keys.H:
                    // H = Focus, always back to a rectangular highlight box; Shift+H = highlighter pen
                    if (e.Shift) { CurrentTool = Tool.Marker; break; }
                    if (CurrentTool == Tool.Highlight) { ResetFocusStyle(); if (ToolChanged != null) ToolChanged(this, EventArgs.Empty); }
                    else CurrentTool = Tool.Highlight;
                    break;
                case Keys.S: CurrentTool = Tool.Bubble; break;
                default: return;
            }
            e.Handled = true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                spaceDown = false;
                Cursor = tool == Tool.Select ? Cursors.Default : Cursors.Cross;
            }
        }

        void Nudge(int dx, int dy)
        {
            if (Sel.Count == 0) return;
            PushUndo(Snapshot());
            foreach (Shape s in Sel) s.Offset(dx, dy);
            KeepInside(new List<Shape>(Sel));
            FireDoc(); Invalidate();
        }

        // ---------- painting ----------
        static TextureBrush Checker
        {
            get
            {
                if (checker == null)
                {
                    using (Bitmap b = new Bitmap(16, 16))      // the brush keeps its own copy of the pattern
                    {
                        using (Graphics g = Graphics.FromImage(b))
                        {
                            g.Clear(Color.White);
                            using (SolidBrush br = new SolidBrush(Color.FromArgb(225, 225, 225)))
                            {
                                g.FillRectangle(br, 0, 0, 8, 8);
                                g.FillRectangle(br, 8, 8, 8, 8);
                            }
                        }
                        checker = new TextureBrush(b);
                    }
                }
                return checker;
            }
        }

        // does the picture have see-through pixels? (only then is the checkerboard drawn under it; asked once per picture)
        object alphaImg; bool alphaSeen;
        bool PictureHasAlpha()
        {
            if (alphaImg == Image) return alphaSeen;
            alphaImg = Image;
            alphaSeen = false;
            if (!System.Drawing.Image.IsAlphaPixelFormat(Image.PixelFormat)) return false;
            if (Image.PixelFormat != PixelFormat.Format32bppArgb && Image.PixelFormat != PixelFormat.Format32bppPArgb) return alphaSeen = true;
            BitmapData d = Image.LockBits(new Rectangle(0, 0, Image.Width, Image.Height), ImageLockMode.ReadOnly, Image.PixelFormat);
            try
            {
                int[] row = new int[Image.Width];
                for (int y = 0; y < Image.Height && !alphaSeen; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), row, 0, row.Length);
                    for (int x = 0; x < row.Length; x++) if ((uint)row[x] < 0xFF000000u) { alphaSeen = true; break; }
                }
            }
            finally { Image.UnlockBits(d); }
            return alphaSeen;
        }

        bool paintErrorLogged;

        protected override void OnPaint(PaintEventArgs e)
        {
            // a drawing error must never turn into an endless row of error boxes (each box repaints the canvas):
            // log it once, show a short note on the canvas, and keep the editor usable so the work can be saved
            try { PaintCore(e); }
            catch (Exception ex)
            {
                if (!paintErrorLogged) { paintErrorLogged = true; ErrorLog.Write("paint", ex); }
                layerDirty = true;
                try
                {
                    e.Graphics.ResetTransform(); e.Graphics.ResetClip();
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 40, 40)))
                        e.Graphics.DrawString("Could not draw everything: " + ex.Message + "  (Ctrl+Z undoes the last change)", Font, b, 8, 8);
                }
                catch { }
                return;
            }
            if (zoomBox && zbStart.HasValue)
                using (Pen p = new Pen(Color.FromArgb(0, 120, 215), 1.5f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(30, 0, 120, 215)))
                {
                    p.DashStyle = DashStyle.Dash;
                    Rectangle r = ZoomBoxRect();
                    e.Graphics.FillRectangle(b, r);
                    e.Graphics.DrawRectangle(p, r);
                }
            if (FindMark.HasValue)
            {
                // 1.4.2 Find: the match in the picture (editor only, never saved)
                RectangleF fr = FindMark.Value;
                PointF a = ToScr(fr.Location), c = ToScr(new PointF(fr.Right, fr.Bottom));
                RectangleF sr = RectangleF.FromLTRB(a.X - 3, a.Y - 3, c.X + 3, c.Y + 3);
                using (Pen p = new Pen(Renderer.GuideMagenta, 2f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(40, Renderer.GuideMagenta)))
                {
                    e.Graphics.ResetTransform();
                    e.Graphics.FillRectangle(b, sr);
                    e.Graphics.DrawRectangle(p, sr.X, sr.Y, sr.Width, sr.Height);
                }
            }
        }

        /// <summary>1.4.2 Find: a box round the current match (picture coordinates), shown until the next search.</summary>
        public RectangleF? FindMark;

        // ---------- zoom to area (Shift+Z or the button next to Fit): drag a box, the view zooms to it ----------
        bool zoomBox;
        Point? zbStart;
        Point zbEnd;
        public bool ZoomBoxActive { get { return zoomBox; } }
        public void StartZoomBox()
        {
            if (Image == null) return;
            zoomBox = true; zbStart = null;
            Cursor = Cursors.Cross;
            Invalidate();
            FireState();
        }
        void EndZoomBox()
        {
            zoomBox = false; zbStart = null;
            Cursor = Cursors.Default;
            Invalidate();
            FireState();
        }
        Rectangle ZoomBoxRect()
        {
            Point a = zbStart.Value;
            return Rectangle.FromLTRB(Math.Min(a.X, zbEnd.X), Math.Min(a.Y, zbEnd.Y), Math.Max(a.X, zbEnd.X), Math.Max(a.Y, zbEnd.Y));
        }
        /// <summary>1.4.2: zoom to z with picture point ip in the middle of the window.</summary>
        public void ZoomCenterOn(PointF ip, float z)
        {
            if (Image == null) return;
            float rs = RulerSize, aw = Width - rs, ah = Height - rs;
            Zoom = Math.Max(0.05f, Math.Min(32f, z));
            Pan = new PointF(rs + aw / 2f - ip.X * Zoom, rs + ah / 2f - ip.Y * Zoom);
            ViewMoved();
        }

        /// <summary>Zoom so that this part of the picture (image pixels) fills the window.</summary>
        public void ZoomToRect(RectangleF ir)
        {
            if (Image == null || ir.Width < 1 || ir.Height < 1) return;
            float rs = RulerSize, aw = Width - rs, ah = Height - rs;
            Zoom = Math.Max(0.05f, Math.Min(32f, Math.Min((aw - 24f) / ir.Width, (ah - 24f) / ir.Height)));
            Pan = new PointF(rs + aw / 2f - (ir.X + ir.Width / 2f) * Zoom, rs + ah / 2f - (ir.Y + ir.Height / 2f) * Zoom);
            ViewMoved();
        }
        /// <summary>Mouse handling while zoom-to-area is on (what: 0 down, 1 move, 2 up); true = the event was used.</summary>
        bool ZoomBoxMouse(MouseEventArgs e, int what)
        {
            if (!zoomBox) return false;
            if (what == 0)
            {
                if (e.Button == MouseButtons.Right) { EndZoomBox(); return true; }
                if (e.Button == MouseButtons.Left) { zbStart = e.Location; zbEnd = e.Location; }
                return e.Button == MouseButtons.Left;
            }
            if (what == 1) { if (zbStart.HasValue) { zbEnd = e.Location; InvalidateLight(); return true; } return false; }
            if (e.Button != MouseButtons.Left || !zbStart.HasValue) return false;
            zbEnd = e.Location;
            Rectangle r = ZoomBoxRect();
            EndZoomBox();
            if (r.Width >= 6 && r.Height >= 6)
            {
                PointF a = ToImg(new Point(r.Left, r.Top)), b = ToImg(new Point(r.Right, r.Bottom));
                ZoomToRect(RectangleF.FromLTRB(a.X, a.Y, b.X, b.Y));
            }
            else ZoomAt(Zoom * 2f, r.Location);     // a click zooms in twice around that point
            return true;
        }

        // ---------- match properties (Ctrl+Shift+M): copy the selected shape's look onto the shapes you click ----------
        Shape matchSource;
        public bool MatchActive { get { return matchSource != null; } }
        public event EventHandler MatchChanged;
        public void StartMatch()
        {
            Shape s = Selected;
            if (s == null || Sel.Count != 1) return;
            EndTextEdit(true);
            matchSource = s.Clone();
            Cursor = Cursors.Hand;
            if (MatchChanged != null) MatchChanged(this, EventArgs.Empty);
        }
        public void EndMatch()
        {
            if (matchSource == null) return;
            matchSource = null;
            Cursor = Cursors.Default;
            Invalidate();
            if (MatchChanged != null) MatchChanged(this, EventArgs.Empty);
        }

        /// <summary>Copy colours, opacity, line width / style, text size and style, line ends and shadow – never the text, points or labels.</summary>
        public static void MatchProps(Shape src, Shape t)
        {
            t.Stroke = src.Stroke; t.StrokeOpacity = src.StrokeOpacity;
            t.Fill = src.Fill; t.FillOpacity = src.FillOpacity;
            t.Width = src.Width;
            if (t.HasOutlineStyle && (src.Dash != (int)LineStyle.Cloud || t.CanBeCloud)) t.Dash = src.Dash;
            t.FontSize = src.FontSize;
            t.Bold = src.Bold; t.Italic = src.Italic; t.Underline = src.Underline; t.Strikeout = src.Strikeout; t.FontName = src.FontName;
            if (t.HasHeads && src.HasHeads) { t.HeadStart = src.HeadStart; t.HeadEnd = src.HeadEnd; }
            if (t.CanShadow) t.Shadow = src.Shadow;
            if (src.Kind == t.Kind) t.Strength = src.Strength;
            t.Cache = null;
        }

        /// <summary>Markers: every marker of the same series (numbers or letters) takes the look of 'src' – size,
        /// colours, line, text style and shadow – and so do the next ones placed (the tool's default).</summary>
        public void MatchMarkerSeries(Shape src)
        {
            if (src == null || !src.IsCountingMarker) return;      // a symbol marker has no series: only itself changes
            foreach (Shape m in Shapes)
                if (m != src && m.Kind == ShapeKind.Counter && m.Mode == src.Mode) MatchProps(src, m);
            Shape tmpl;
            if (Styles.TryGetValue(src.IsLetterMarker ? Tool.Letter : Tool.Counter, out tmpl) && tmpl != src) MatchProps(src, tmpl);
            layerDirty = true;
        }

        /// <summary>Mouse handling while the brush is on; true = the event was used.</summary>
        bool MatchMouse(MouseEventArgs e)
        {
            if (matchSource == null) return false;
            if (e.Button == MouseButtons.Right) { EndMatch(); return true; }
            if (e.Button != MouseButtons.Left) return true;
            Shape t = HitShape(ToImg(e.Location));
            if (t == null) return true;
            PushUndo(Snapshot());
            MatchProps(matchSource, t);
            if (t.Kind == ShapeKind.Counter) MatchMarkerSeries(t);
            FireDoc(); Invalidate();
            return true;
        }

        // ---------- cached layer: background, picture and finished shapes ----------
        Bitmap layer;
        bool layerDirty = true, lightInvalidate;
        object layerImg; float layerZoom; PointF layerPan; RectangleF layerVr; bool layerClip; Shape layerSkip;

        /// <summary>Repaint for a pointer move only: the picture and the finished shapes have not changed.</summary>
        void InvalidateLight()
        {
            lightInvalidate = true;
            try { Invalidate(); } finally { lightInvalidate = false; }
        }

        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            if (!lightInvalidate) layerDirty = true;     // something may have changed: redraw the layer next time
            base.OnInvalidated(e);
        }

        // ---------- the picture scaled down to the current zoom, kept so panning only copies it ----------
        // (the picture is never changed in place – edits give a new bitmap – so the bitmap itself is the key)
        Bitmap pic;
        object picImg; float picZoom;
        float zoomSeen; PointF panSeen; int viewSeenAt;
        bool quickPic, quickUsed;
        Timer picTimer;
        const long PicCacheMax = 16000000L;       // pixels (64 MB); bigger views are drawn straight

        /// <summary>Picture and checkerboard, in picture coordinates (g is already scaled by Zoom).
        /// While the view keeps moving (wheel zoom, panning) a quicker filter is used; the sharp picture follows once it rests.</summary>
        void DrawPicture(Graphics g)
        {
            int w = Image.Width, h = Image.Height;
            int now = Environment.TickCount;
            bool changed = Zoom != zoomSeen || Pan != panSeen;
            if (changed)
            {
                if (unchecked(now - viewSeenAt) < 300)       // the second change in a row: the view is moving
                {
                    quickPic = true;
                    if (picTimer == null)
                    {
                        picTimer = new Timer { Interval = 200 };
                        picTimer.Tick += (s, e) => { picTimer.Stop(); quickPic = false; if (quickUsed) { quickUsed = false; Invalidate(); } };
                    }
                    picTimer.Stop(); picTimer.Start();
                }
                zoomSeen = Zoom; panSeen = Pan; viewSeenAt = now;
            }
            if (Zoom < 1f)
            {
                int cw = Math.Max(1, (int)Math.Ceiling(w * Zoom)), ch = Math.Max(1, (int)Math.Ceiling(h * Zoom));
                bool fresh = pic != null && picImg == Image && picZoom == Zoom;
                if (!fresh && (long)cw * ch <= PicCacheMax)
                {
                    // zooming with the wheel: a quick picture now, the sharp one once the wheel rests
                    if (quickPic)
                    {
                        if (PictureHasAlpha()) g.FillRectangle(Checker, 0, 0, w, h);
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(Image, new RectangleF(0, 0, w, h), new RectangleF(0, 0, w, h), GraphicsUnit.Pixel);
                        quickUsed = true;
                        return;
                    }
                    if (pic == null || pic.Width != cw || pic.Height != ch)
                    {
                        if (pic != null) pic.Dispose();
                        pic = new Bitmap(cw, ch, PixelFormat.Format32bppPArgb);
                    }
                    using (Graphics pg = Graphics.FromImage(pic))
                    {
                        pg.Clear(Color.Transparent);
                        pg.ScaleTransform(Zoom, Zoom);
                        if (PictureHasAlpha()) pg.FillRectangle(Checker, 0, 0, w, h);
                        pg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        pg.PixelOffsetMode = PixelOffsetMode.Half;
                        pg.DrawImage(Image, new RectangleF(0, 0, w, h), new RectangleF(0, 0, w, h), GraphicsUnit.Pixel);
                    }
                    picImg = Image; picZoom = Zoom;
                    fresh = true;
                }
                if (fresh)
                {
                    // one picture pixel per screen pixel: a plain copy
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(pic, new RectangleF(0, 0, cw / Zoom, ch / Zoom), new RectangleF(0, 0, cw, ch), GraphicsUnit.Pixel);
                    return;
                }
            }
            else if (pic != null && picImg != Image) { pic.Dispose(); pic = null; picImg = null; }    // a new picture: let the old copy go

            // zoomed in (or a huge view): only the part of the picture in the window
            RectangleF src = new RectangleF(0, 0, w, h);
            if (Zoom >= 1f)
            {
                PointF a = ToImg(new Point(0, 0)), b = ToImg(new Point(Width, Height));
                RectangleF vis = RectangleF.FromLTRB((float)Math.Floor(a.X) - 2, (float)Math.Floor(a.Y) - 2, (float)Math.Ceiling(b.X) + 2, (float)Math.Ceiling(b.Y) + 2);
                src.Intersect(vis);
                if (src.Width <= 0 || src.Height <= 0) return;
            }
            if (PictureHasAlpha()) g.FillRectangle(Checker, src);
            InterpolationMode im;
            if (Zoom >= 1.99f || Math.Abs(Zoom - 1f) < 0.001f) im = InterpolationMode.NearestNeighbor;     // 1:1 is a plain copy
            else if (quickPic) { im = InterpolationMode.NearestNeighbor; quickUsed = true; }
            else im = Zoom < 1f ? InterpolationMode.HighQualityBicubic : InterpolationMode.Bilinear;
            g.InterpolationMode = im;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(Image, src, src, GraphicsUnit.Pixel);
        }

        void PaintStatic(Graphics g, RectangleF vr, bool clip, Shape skip)
        {
            g.Clear(BackColor);
            GraphicsState st = g.Save();
            g.TranslateTransform(Pan.X, Pan.Y);
            g.ScaleTransform(Zoom, Zoom);
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillRectangle(sh, vr.X + 4f / Zoom, vr.Y + 4f / Zoom, vr.Width, vr.Height);
            if (clip) g.SetClip(vr);
            DrawPicture(g);
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            Renderer.DrawAll(g, Shapes, Image, Scale, skip);
            g.Restore(st);
        }

        void PaintCore(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (Image == null) return;

            RectangleF vr = ViewRect;
            // lock on: whatever lies outside the picture is hidden until the lock is off
            bool clip = (CropRect.HasValue && tool != Tool.Crop) || LockInside;
            Shape skip = (editShape != null && editShape.Kind == ShapeKind.Text) ? editShape : null;
            // the picture and the finished shapes come from a cached layer while only the pointer moves
            // (drawing a shape, the magnifier, snapping); any other change redraws the layer
            if (layer == null || layerDirty || layer.Width != Width || layer.Height != Height || layerImg != Image || layerZoom != Zoom ||
                layerPan != Pan || layerVr != vr || layerClip != clip || layerSkip != skip)
            {
                if (layer == null || layer.Width != Width || layer.Height != Height)
                {
                    if (layer != null) layer.Dispose();
                    layer = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppPArgb);
                }
                using (Graphics lg = Graphics.FromImage(layer)) PaintStatic(lg, vr, clip, skip);
                layerDirty = false; layerImg = Image; layerZoom = Zoom; layerPan = Pan; layerVr = vr; layerClip = clip; layerSkip = skip;
            }
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(layer, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
            GraphicsState st = g.Save();
            g.TranslateTransform(Pan.X, Pan.Y);
            g.ScaleTransform(Zoom, Zoom);
            if (clip) g.SetClip(vr);
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            if (editShape != null && editIsNew && (editShape.Kind == ShapeKind.Bubble || editShape.Kind == ShapeKind.Stamp || editShape.Kind == ShapeKind.Cloud))
            {
                if (editShape.Shadow) Renderer.DrawShadow(g, editShape, Image, Scale, null);
                Renderer.Draw(g, editShape, Image, Scale, null);
            }
            if (creating != null && tool != Tool.Crop)
            {
                if (tool == Tool.Calibrate)
                {
                    using (Pen p = new Pen(Color.Lime, 1.5f / Zoom)) { p.DashStyle = DashStyle.Dash; g.DrawLine(p, creating.Pts[0], creating.Pts[1]); }
                }
                else
                {
                    if (creating.Shadow && creating.CanShadow) Renderer.DrawShadow(g, creating, Image, Scale, null);
                    string nm = creating.Kind == ShapeKind.Area ? (creating.ShowsArea ? "A" : "P") + (CountLabels(creating.ShowsArea ? "A" : "P") + 1)
                              : creating.Kind == ShapeKind.Polyline ? "L" + (CountKind(ShapeKind.Polyline) + 1)
                              : creating.Kind == ShapeKind.Perimeter ? "P" + (CountKind(ShapeKind.Perimeter) + 1) : null;
                    if (tool == Tool.Text)
                    {
                        if (creating.Pts.Count >= 2)
                            using (Pen p = new Pen(Color.FromArgb(0, 120, 215), 1.2f / Zoom))
                            {
                                p.DashStyle = DashStyle.Dash;
                                RectangleF r = Geo.Rect(creating.Pts[0], creating.Pts[1]);
                                float h = Math.Max(r.Height, creating.FontSize * 1.3f);
                                g.DrawRectangle(p, r.X, r.Y, Math.Max(1f, r.Width), h);
                            }
                    }
                    else if (tool == Tool.Cutout || tool == Tool.Ocr)
                        using (Pen p = new Pen(tool == Tool.Ocr ? Color.FromArgb(0, 120, 215) : Color.FromArgb(220, 30, 30), 1.5f / Zoom))
                        using (SolidBrush b = new SolidBrush(tool == Tool.Ocr ? Color.FromArgb(30, 0, 120, 215) : Color.FromArgb(40, 220, 30, 30)))
                        {
                            p.DashStyle = DashStyle.Dash;
                            if (tool == Tool.Ocr && creating.Pts.Count >= 2)
                            {
                                RectangleF r = Geo.Rect(creating.Pts[0], creating.Pts[1]);
                                g.FillRectangle(b, r); g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                            }
                            else if (creating.Pts.Count >= 3) { g.FillPolygon(b, creating.Pts.ToArray()); g.DrawPolygon(p, creating.Pts.ToArray()); }
                            else if (creating.Pts.Count == 2) g.DrawLines(p, creating.Pts.ToArray());
                        }
                    else if (creating.IsFocusPoly)
                    {
                        // a focus perimeter being clicked: its effect, plus a dashed outline so every edge shows
                        Renderer.Draw(g, creating, Image, Scale, nm);
                        using (Pen p = new Pen(Color.FromArgb(0, 120, 215), 1.5f / Zoom))
                        {
                            p.DashStyle = DashStyle.Dash;
                            if (creating.Pts.Count >= 3) g.DrawPolygon(p, creating.Pts.ToArray());
                            else if (creating.Pts.Count == 2) g.DrawLines(p, creating.Pts.ToArray());
                        }
                    }
                    else Renderer.Draw(g, creating, Image, Scale, nm);
                }
            }
            g.Restore(st);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (tool == Tool.Crop) DrawCropOverlay(g);
            DrawGuides(g);

            if (creating != null && (creating.Kind == ShapeKind.Ellipse || creating.Kind == ShapeKind.Rect) && creating.Pts.Count >= 2 && tool != Tool.Crop)
                DrawCenterMark(g, ToScr(Geo.Center(Geo.Rect(creating.Pts[0], creating.Pts[1]))));
            if (Sel.Count > 0 && editBox == null)
            {
                // lock on: the selection frame and handles stop at the picture too (a handle on the edge still shows)
                GraphicsState ss = g.Save();
                if (LockInside)
                {
                    PointF a = ToScr(new PointF(vr.X, vr.Y)), b = ToScr(new PointF(vr.Right, vr.Bottom));
                    float m = 7f * Dpi;
                    g.SetClip(RectangleF.FromLTRB(a.X - m, a.Y - m, b.X + m, b.Y + m));
                }
                DrawSelection(g);
                g.Restore(ss);
            }
            if (Sel.Count == 1 && Selected.Kind == ShapeKind.Note && editBox == null) DrawNotePopup(g, Selected);
            if (marquee)
            {
                PointF a = ToScr(marqA), b = ToScr(marqB);
                RectangleF r = Geo.Rect(a, b);
                using (SolidBrush br = new SolidBrush(Color.FromArgb(40, 0, 120, 215))) g.FillRectangle(br, r);
                using (Pen p = new Pen(Color.FromArgb(0, 120, 215), 1f)) { p.DashStyle = DashStyle.Dash; g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height); }
            }
            if (snapPt.HasValue)
            {
                PointF s = ToScr(snapPt.Value);
                float r = 5f * Dpi;
                using (Pen p = new Pen(Color.Lime, 2f)) g.DrawRectangle(p, s.X - r, s.Y - r, 2 * r, 2 * r);
            }
            if (creating != null) DrawReadout(g);
            else if (dragH.HasValue && dragH.Value.Kind == HKind.Rotate && Selected != null) DrawRotateReadout(g);
            else if (cropDrag >= 0 && CropRect.HasValue) ReadoutBox(g, CropRect.Value.Width + " × " + CropRect.Value.Height + " px");
            else if (guideAxis != 0) ReadoutBox(g, guideOverRuler ? "Release on the ruler to delete" : (guideAxis == 1 ? "Y = " : "X = ") + LenOrPx(guideVal));
            DrawLoupe(g);
            DrawRulers(g);
        }

        /// <summary>Tools that place exact points: they get the magnifier at the pointer.</summary>
        static bool LoupeTool(Tool t)
        {
            return t == Tool.Calibrate || t == Tool.Dimension || t == Tool.Area || t == Tool.Polyline || t == Tool.Perimeter ||
                   t == Tool.Coord || t == Tool.Angle || t == Tool.Cutout || t == Tool.Radius;
        }

        /// <summary>Magnifier for the measuring tools: the picture around the pointer (or the point it snaps to) at 4× the
        /// current zoom, with a cross-hair on the exact spot and the segment being drawn.</summary>
        void DrawLoupe(Graphics g)
        {
            // also while dragging a point of a dimension, area, length, angle, coordinate … to put it somewhere exact
            bool draggingPoint = dragH.HasValue && dragH.Value.Kind == HKind.Point && Selected != null && (Selected.IsMeasure || Selected.IsPoly);
            if ((!LoupeTool(tool) && !draggingPoint) || Image == null || panning || editBox != null) return;
            float rs = RulerSize;
            if (mouseScr.X < rs || mouseScr.Y < rs || mouseScr.X >= Width || mouseScr.Y >= Height) return;
            int size = (int)(132 * Dpi);
            const float mag = 4f;
            float srcW = size / (Zoom * mag);                    // picture pixels shown across the loupe
            PointF c = snapPt.HasValue ? snapPt.Value : mouseImg;
            RectangleF src = new RectangleF(c.X - srcW / 2f, c.Y - srcW / 2f, srcW, srcW);
            float gap = 26 * Dpi;
            float x = mouseScr.X + gap, y = mouseScr.Y + gap;
            if (x + size > Width - 4) x = mouseScr.X - gap - size;
            if (y + size > Height - 4) y = mouseScr.Y - gap - size;
            x = Math.Max(rs + 2, x); y = Math.Max(rs + 2, y);
            RectangleF dst = new RectangleF(x, y, size, size);
            Func<PointF, PointF> map = q => new PointF(dst.X + (q.X - src.X) / srcW * size, dst.Y + (q.Y - src.Y) / srcW * size);

            GraphicsState st = g.Save();
            g.SetClip(dst);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(60, 60, 60))) g.FillRectangle(bg, dst);
            // only the part that lies on the picture (no smeared edge pixels)
            RectangleF onImg = RectangleF.Intersect(src, new RectangleF(0, 0, Image.Width, Image.Height));
            if (onImg.Width > 0 && onImg.Height > 0)
            {
                PointF a = map(onImg.Location), b = map(new PointF(onImg.Right, onImg.Bottom));
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(Image, RectangleF.FromLTRB(a.X, a.Y, b.X, b.Y), onImg, GraphicsUnit.Pixel);
                g.PixelOffsetMode = PixelOffsetMode.Default;
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // the segment being drawn, so you can see where it will land
            if (creating != null && creating.Pts.Count >= 2)
                using (Pen sp = new Pen(Color.FromArgb(220, 255, 0, 200), 1.5f))
                    g.DrawLine(sp, map(creating.Pts[creating.Pts.Count - 2]), map(creating.Pts[creating.Pts.Count - 1]));
            PointF m = map(c);
            using (Pen ch = new Pen(Color.FromArgb(230, 255, 40, 40), 1f))
            {
                g.DrawLine(ch, dst.X, m.Y, m.X - 5, m.Y); g.DrawLine(ch, m.X + 5, m.Y, dst.Right, m.Y);
                g.DrawLine(ch, m.X, dst.Y, m.X, m.Y - 5); g.DrawLine(ch, m.X, m.Y + 5, m.X, dst.Bottom);
            }
            if (snapPt.HasValue) using (Pen sq = new Pen(Color.Lime, 2f)) g.DrawRectangle(sq, m.X - 6, m.Y - 6, 12, 12);
            g.Restore(st);
            using (Pen border = new Pen(Color.White, 2f)) g.DrawRectangle(border, dst.X, dst.Y, dst.Width, dst.Height);
            using (Pen outer = new Pen(Color.FromArgb(90, 90, 90), 1f)) g.DrawRectangle(outer, dst.X - 1.5f, dst.Y - 1.5f, dst.Width + 3, dst.Height + 3);
        }

        string LenOrPx(float v) { return Scale.Calibrated ? Scale.Len(v) : v.ToString("N0") + " px"; }

        void DrawCropOverlay(Graphics g)
        {
            RectangleF? r = null;
            if (creating != null && creating.Pts.Count >= 2) r = Geo.Rect(creating.Pts[0], creating.Pts[1]);
            else if (CropRect.HasValue) r = CropRect.Value;
            if (!r.HasValue) return;
            PointF a = ToScr(r.Value.Location), b = ToScr(new PointF(r.Value.Right, r.Value.Bottom));
            RectangleF sr = RectangleF.FromLTRB(a.X, a.Y, b.X, b.Y);
            // dim everything outside the crop – image and any shapes drawn past its edges
            using (Region rg = new Region(new RectangleF(0, 0, Width, Height)))
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            {
                rg.Exclude(sr);
                g.FillRegion(dim, rg);
            }
            using (Pen p = new Pen(Color.White, 1.5f)) { p.DashStyle = DashStyle.Dash; g.DrawRectangle(p, sr.X, sr.Y, sr.Width, sr.Height); }
            if (creating == null)
            {
                float hr = 4.5f * Dpi;
                foreach (PointF q0 in BoxPts(r.Value))
                {
                    PointF q = ToScr(q0);
                    g.FillRectangle(Brushes.White, q.X - hr, q.Y - hr, 2 * hr, 2 * hr);
                    using (Pen pp = new Pen(Color.FromArgb(0, 120, 215), 1.5f)) g.DrawRectangle(pp, q.X - hr, q.Y - hr, 2 * hr, 2 * hr);
                }
            }
        }

        /// <summary>Guide lines (and their marks on the rulers) are magenta, so they stand out from blue / cyan drawings.</summary>
        public static readonly Color GuideColor = Color.FromArgb(255, 0, 200);

        void DrawGuides(Graphics g)
        {
            if (HGuides.Count + VGuides.Count == 0 && guideAxis == 0) return;
            float rs = RulerSize;
            using (Pen p = new Pen(Color.FromArgb(230, GuideColor), 1f))
            {
                for (int i = 0; i < HGuides.Count; i++)
                {
                    if (guideAxis == 1 && guideIdx == i) continue;
                    float y = HGuides[i] * Zoom + Pan.Y;
                    g.DrawLine(p, rs, y, Width, y);
                }
                for (int i = 0; i < VGuides.Count; i++)
                {
                    if (guideAxis == 2 && guideIdx == i) continue;
                    float x = VGuides[i] * Zoom + Pan.X;
                    g.DrawLine(p, x, rs, x, Height);
                }
            }
            if (guideAxis != 0 && !guideOverRuler)
                using (Pen p = new Pen(GuideColor, 1f))
                {
                    p.DashStyle = DashStyle.Dash;
                    if (guideAxis == 1) { float y = guideVal * Zoom + Pan.Y; g.DrawLine(p, rs, y, Width, y); }
                    else { float x = guideVal * Zoom + Pan.X; g.DrawLine(p, x, rs, x, Height); }
                }
        }

        static double Nice(double v)
        {
            if (v <= 0 || double.IsNaN(v) || double.IsInfinity(v)) return 1;
            double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
            double m = v / p;
            return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * p;
        }

        void DrawRulers(Graphics g)
        {
            if (!showRulers || Image == null) return;
            float rs = RulerSize;
            double upp = Scale.Calibrated ? Scale.Upp : 1.0;
            string unit = Scale.Calibrated ? Scale.Unit : "px";
            double uPerScr = upp / Zoom;
            double major = Nice(70.0 * Dpi * uPerScr);
            int sub = 10;
            if (major / sub / uPerScr < 6 * Dpi) sub = 5;
            if (major / sub / uPerScr < 6 * Dpi) sub = 2;
            double minor = major / sub;
            int dec = major >= 1 ? 0 : (int)Math.Ceiling(-Math.Log10(major) - 1e-9);
            Color bgc = Theme.Dark ? Theme.Elevated : Color.FromArgb(244, 244, 244), ink = Theme.Dark ? Theme.Label2 : Color.FromArgb(80, 80, 80);
            Font f = Renderer.GetFont(10f * Dpi, false);
            SmoothingMode sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush bg = new SolidBrush(bgc))
            using (SolidBrush tb = new SolidBrush(ink))
            using (Pen tp = new Pen(ink, 1f))
            using (Pen edge = new Pen(Theme.Dark ? Theme.Border : Color.FromArgb(170, 170, 170), 1f))
            using (Pen red = new Pen(Color.FromArgb(230, 30, 30), 2f))
            using (Pen blue = new Pen(GuideColor, 2f))           // guide marks on the rulers
            {
                // top ruler
                g.FillRectangle(bg, rs, 0, Width - rs, rs);
                g.DrawLine(edge, rs, rs - 1, Width, rs - 1);
                GraphicsState st = g.Save();
                g.SetClip(new RectangleF(rs, 0, Width - rs, rs));
                double u0 = ToImg(new Point((int)rs, 0)).X * upp, u1 = ToImg(new Point(Width, 0)).X * upp;
                long k0 = (long)Math.Floor(u0 / minor) - 1, k1 = (long)Math.Ceiling(u1 / minor) + 1;
                if (k1 - k0 < 4000)
                    for (long k = k0; k <= k1; k++)
                    {
                        double u = k * minor;
                        float x = (float)(u / upp * Zoom + Pan.X);
                        bool mj = k % sub == 0, half = sub % 2 == 0 && k % (sub / 2) == 0;
                        float len = mj ? rs * 0.62f : (half ? rs * 0.4f : rs * 0.22f);
                        g.DrawLine(tp, x, rs - len, x, rs);
                        if (mj) g.DrawString((Math.Abs(u) < minor * 1e-3 ? 0 : u).ToString("F" + dec, CultureInfo.CurrentCulture), f, tb, x + 1, -1);
                    }
                foreach (float v in VGuides) { float x = v * Zoom + Pan.X; g.DrawLine(blue, x, 0, x, rs); }
                if (mouseScr.X >= rs) g.DrawLine(red, mouseScr.X, 0, mouseScr.X, rs);
                g.Restore(st);

                // left ruler (labels rotated)
                g.FillRectangle(bg, 0, rs, rs, Height - rs);
                g.DrawLine(edge, rs - 1, rs, rs - 1, Height);
                st = g.Save();
                g.SetClip(new RectangleF(0, rs, rs, Height - rs));
                u0 = ToImg(new Point(0, (int)rs)).Y * upp; u1 = ToImg(new Point(0, Height)).Y * upp;
                k0 = (long)Math.Floor(u0 / minor) - 1; k1 = (long)Math.Ceiling(u1 / minor) + 1;
                if (k1 - k0 < 4000)
                    for (long k = k0; k <= k1; k++)
                    {
                        double u = k * minor;
                        float y = (float)(u / upp * Zoom + Pan.Y);
                        bool mj = k % sub == 0, half = sub % 2 == 0 && k % (sub / 2) == 0;
                        float len = mj ? rs * 0.62f : (half ? rs * 0.4f : rs * 0.22f);
                        g.DrawLine(tp, rs - len, y, rs, y);
                        if (mj)
                        {
                            GraphicsState s2 = g.Save();
                            g.TranslateTransform(-1, y - 1);
                            g.RotateTransform(-90);
                            g.DrawString((Math.Abs(u) < minor * 1e-3 ? 0 : u).ToString("F" + dec, CultureInfo.CurrentCulture), f, tb, 0, 0);
                            g.Restore(s2);
                        }
                    }
                foreach (float v in HGuides) { float y = v * Zoom + Pan.Y; g.DrawLine(blue, 0, y, rs, y); }
                if (mouseScr.Y >= rs) g.DrawLine(red, 0, mouseScr.Y, rs, mouseScr.Y);
                g.Restore(st);

                // corner: unit
                g.FillRectangle(bg, 0, 0, rs, rs);
                g.DrawRectangle(edge, 0, 0, rs - 1, rs - 1);
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(unit, Renderer.GetFont(9f * Dpi, false), tb, new RectangleF(0, 0, rs, rs), sf);
                }
            }
            g.SmoothingMode = sm;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
        }

        int CountLabels(string prefix)
        {
            int n = 0;
            foreach (string v in Renderer.Labels(Shapes, Scale).Values) if (v.StartsWith(prefix, StringComparison.Ordinal)) n++;
            return n;
        }

        int CountKind(ShapeKind k)
        {
            int n = 0;
            foreach (Shape s in Shapes) if (s.Kind == k) n++;
            return n;
        }

        void DrawCenterMark(Graphics g, PointF c)
        {
            float r = 5f * Dpi;
            using (Pen p = new Pen(Color.FromArgb(0, 150, 255), 1.5f))
            {
                g.DrawEllipse(p, c.X - r, c.Y - r, 2 * r, 2 * r);
                g.DrawLine(p, c.X - r * 1.8f, c.Y, c.X + r * 1.8f, c.Y);
                g.DrawLine(p, c.X, c.Y - r * 1.8f, c.X, c.Y + r * 1.8f);
            }
        }

        void DrawSelection(Graphics g)
        {
            if (Sel.Count > 1)
            {
                using (Pen p = new Pen(Color.FromArgb(0, 150, 255), 1f))
                {
                    p.DashStyle = DashStyle.Dash;
                    foreach (Shape x in Sel)
                    {
                        RectangleF b = ShapeOps.Bounds(x);
                        PointF a = ToScr(b.Location), c = ToScr(new PointF(b.Right, b.Bottom));
                        g.DrawRectangle(p, a.X - 3, a.Y - 3, c.X - a.X + 6, c.Y - a.Y + 6);
                    }
                }
                return;
            }
            Shape s = Selected;
            using (Pen p = new Pen(Color.FromArgb(0, 150, 255), 1f))
            {
                p.DashStyle = DashStyle.Dash;
                if (s.Kind == ShapeKind.Text || s.IsBox)
                {
                    PointF[] o = ShapeOps.Outline(s);
                    for (int i = 0; i < o.Length; i++) o[i] = ToScr(o[i]);
                    g.DrawPolygon(p, o);
                }
                else if (!(s.Kind == ShapeKind.Line || s.Kind == ShapeKind.Arrow || s.Kind == ShapeKind.Angle))
                {
                    RectangleF b = ShapeOps.Bounds(s);
                    PointF a = ToScr(b.Location), c = ToScr(new PointF(b.Right, b.Bottom));
                    g.DrawRectangle(p, a.X - 3, a.Y - 3, c.X - a.X + 6, c.Y - a.Y + 6);
                }
            }
            List<Handle> hs = ShapeOps.Handles(s, RotDist);
            float r = 4.5f * Dpi;
            PointF center = ToScr(ShapeOps.Center(s));
            foreach (Handle h in hs)
            {
                PointF q = ToScr(h.Pos);
                RectangleF rr = new RectangleF(q.X - r, q.Y - r, 2 * r, 2 * r);
                switch (h.Kind)
                {
                    case HKind.Rotate:
                        using (Pen dp = new Pen(Color.FromArgb(160, 0, 150, 255), 1f)) { dp.DashStyle = DashStyle.Dot; g.DrawLine(dp, center, q); }
                        rr.Inflate(1.5f, 1.5f);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(40, 190, 70))) g.FillEllipse(b, rr);
                        using (Pen pp = new Pen(Color.White, 1.5f)) g.DrawEllipse(pp, rr);
                        break;
                    case HKind.Center:
                        DrawCenterMark(g, q);
                        break;
                    case HKind.HolePoint:
                        // a corner of a cut-out: round, so it is told apart from the outline's square corners
                        g.FillEllipse(Brushes.White, rr);
                        using (Pen pp = new Pen(Color.FromArgb(230, 120, 0), 1.5f)) g.DrawEllipse(pp, rr);
                        break;
                    case HKind.DimOffset:
                    case HKind.Tail:
                    case HKind.Label:
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 200, 0))) g.FillEllipse(b, rr);
                        using (Pen pp = new Pen(Color.FromArgb(0, 120, 215), 1.5f)) g.DrawEllipse(pp, rr);
                        break;
                    default:
                        g.FillRectangle(Brushes.White, rr);
                        using (Pen pp = new Pen(Color.FromArgb(0, 120, 215), 1.5f)) g.DrawRectangle(pp, rr.X, rr.Y, rr.Width, rr.Height);
                        break;
                }
            }
        }

        /// <summary>The text of the selected non-printing note, shown next to its icon (screen only).</summary>
        void DrawNotePopup(Graphics g, Shape s)
        {
            RectangleF nr = Renderer.NoteRect(s);
            PointF a = ToScr(new PointF(nr.Right, nr.Y));
            Font f = Renderer.GetFont(12f * Dpi, false);
            string t = string.IsNullOrEmpty(s.Text) ? "(empty note)" : s.Text;
            SizeF sz = g.MeasureString(t, f, (int)(320 * Dpi));
            float x = a.X + 8 * Dpi, y = a.Y;
            if (x + sz.Width + 16 > Width) x = ToScr(nr.Location).X - sz.Width - 24 * Dpi;
            if (y + sz.Height + 30 > Height) y = Math.Max(0, Height - sz.Height - 30 * Dpi);
            RectangleF box = new RectangleF(x, y, sz.Width + 14, sz.Height + 26 * Dpi);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(250, 255, 247, 200))) g.FillRectangle(b, box);
            using (Pen p = new Pen(Color.FromArgb(200, 160, 30), 1f)) g.DrawRectangle(p, box.X, box.Y, box.Width, box.Height);
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(60, 50, 10)))
            {
                g.DrawString(t, f, ink, new RectangleF(x + 7, y + 5, sz.Width + 2, sz.Height + 2));
                g.DrawString("Note – not printed · double-click to edit", Renderer.GetFont(9.5f * Dpi, false), Brushes.Gray, x + 7, y + sz.Height + 8);
            }
        }

        void ReadoutBox(Graphics g, string t)
        {
            PointF sp = ToScr(mouseImg);
            Font f = Renderer.GetFont(12f * Dpi, false);
            SizeF sz = g.MeasureString(t, f);
            float x = sp.X + 18 * Dpi, y = sp.Y + 18 * Dpi;
            if (x + sz.Width + 10 > Width) x = sp.X - sz.Width - 18 * Dpi;
            if (y + sz.Height + 8 > Height) y = sp.Y - sz.Height - 18 * Dpi;
            RectangleF box = new RectangleF(x, y, sz.Width + 10, sz.Height + 6);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(225, 25, 25, 25))) g.FillRectangle(b, box);
            g.DrawString(t, f, Brushes.White, x + 5, y + 3);
        }

        void DrawRotateReadout(Graphics g)
        {
            float a;
            if (Selected.Rotatable) a = Selected.Angle;
            else
            {
                PointF c = ShapeOps.Center(dragOrig);
                a = Geo.NormAngle((float)(Math.Atan2(mouseImg.Y - c.Y, mouseImg.X - c.X) * 180.0 / Math.PI) + 90f);
            }
            ReadoutBox(g, a.ToString("N1") + "°   (Shift = 15° steps)");
        }

        void DrawReadout(Graphics g)
        {
            string t = null;
            List<PointF> p = creating.Pts;
            switch (PolyAs)
            {
                case Tool.Dimension:
                case Tool.Line:
                case Tool.Arrow:
                case Tool.Calibrate:
                    if (p.Count >= 2)
                    {
                        float L = Geo.Dist(p[0], p[1]);
                        t = (tool == Tool.Calibrate ? L.ToString("N1") + " px" : Scale.Len(L)) + "   ∠ " + Geo.AngleDeg(p[0], p[1]).ToString("N1") + "°";
                    }
                    break;
                case Tool.Arc:
                case Tool.Radius:
                    if (p.Count == 2) t = "Click a point on the curve, then its end";
                    else if (p.Count >= 3)
                    {
                        PointF o; float rad;
                        t = Geo.Circle3(p[0], p[1], p[2], out o, out rad)
                            ? "R = " + Scale.Len(rad) + "   Ø = " + Scale.Len(2 * rad) + "\nArc " + Scale.Len((float)Geo.ArcLength(p[0], p[1], p[2])) + " · click the end to finish"
                            : "Points in a straight line – move the middle point";
                    }
                    break;
                case Tool.Angle:
                    if (p.Count == 2) t = "Arm " + Scale.Len(Geo.Dist(p[0], p[1])) + "\nClick the vertex, then the end of the second arm";
                    else if (p.Count >= 3) t = "∠ " + Scale.Angle(Geo.AngleAt(p[0], p[1], p[2])) + "\nClick to finish · Shift = 45° steps";
                    break;
                case Tool.Rect:
                case Tool.Ellipse:
                case Tool.Pixelate:
                case Tool.Crop:
                case Tool.Highlight:
                case Tool.Bubble:
                    if (p.Count >= 2)
                    {
                        RectangleF r = Geo.Rect(p[0], p[1]);
                        t = Scale.Calibrated ? Scale.Len(r.Width) + " × " + Scale.Len(r.Height) : Math.Round(r.Width) + " × " + Math.Round(r.Height) + " px";
                    }
                    break;
                case Tool.Polygon:
                case Tool.Area:
                case Tool.Cutout:
                    if (p.Count >= 2)
                    {
                        t = "Segment " + Scale.Len(Geo.Dist(p[p.Count - 2], p[p.Count - 1]));
                        if (p.Count >= 3) t += "\n" + (tool == Tool.Cutout ? "Cut-out " : "Area ") + Scale.Area(Math.Abs(Geo.SignedArea(p)));
                        t += "\nDouble-click / Enter = finish · Backspace = undo point";
                    }
                    break;
                case Tool.Polyline:
                case Tool.PolyDraw:
                    if (p.Count >= 2)
                        t = "Segment " + Scale.Len(Geo.Dist(p[p.Count - 2], p[p.Count - 1])) + "\nTotal " + Scale.Len(Geo.Perimeter(p, false)) +
                            "\nDouble-click / Enter = finish · Backspace = undo point";
                    break;
                case Tool.Perimeter:
                    if (p.Count >= 2)
                        t = "Segment " + Scale.Len(Geo.Dist(p[p.Count - 2], p[p.Count - 1])) +
                            (creating.IsOpenPath ? "\nTotal " : "\nPerimeter ") + Scale.Len(Geo.Perimeter(p, !creating.IsOpenPath)) +
                            "\nDouble-click / Enter = finish · Backspace = undo point";
                    break;
                case Tool.Cloud:
                case Tool.Stamp:
                case Tool.Markup:
                case Tool.Ocr:
                    if (p.Count >= 2)
                    {
                        RectangleF r = Geo.Rect(p[0], p[1]);
                        t = tool == Tool.Ocr ? "Release to copy the text inside" : Math.Round(r.Width) + " × " + Math.Round(r.Height) + " px";
                    }
                    break;
            }
            if (t != null) ReadoutBox(g, t);
        }

        public Bitmap Flatten(Color? background) { return Renderer.Flatten(Image, Shapes, Scale, background, CropRect); }

        /// <summary>A plain canvas to draw on.</summary>
        public static Bitmap BlankImage(int w, int h, Color background)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b)) g.Clear(background);
            return b;
        }
    }
}
