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
        public Func<double, bool> AskCalibration;        // (pixel length) -> true if scale was changed
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
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.FromArgb(52, 54, 58);
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
            d[Tool.Freehand] = St(ShapeKind.Freehand, red, 3, Color.Yellow, 0, 16);
            d[Tool.Text] = St(ShapeKind.Text, red, 2, Color.White, 0, 20);
            d[Tool.Polygon] = St(ShapeKind.Polygon, amber, 2, Color.Yellow, 35, 16);
            d[Tool.Dimension] = St(ShapeKind.Dimension, red, 1.5f, Color.White, 0, 15);
            d[Tool.Area] = St(ShapeKind.Area, blue, 2, blue, 22, 15);
            d[Tool.Angle] = St(ShapeKind.Angle, blue, 2, Color.White, 0, 15);
            d[Tool.Counter] = St(ShapeKind.Counter, Color.White, 2.5f, red, 100, 18);
            Shape letter = St(ShapeKind.Counter, Color.White, 2.5f, blue, 100, 18);
            letter.Mode = 1;
            d[Tool.Letter] = letter;
            d[Tool.Pixelate] = St(ShapeKind.Pixelate, Color.Gray, 3, Color.Gray, 0, 16);
            d[Tool.Highlight] = St(ShapeKind.Highlight, red, 2, Color.Yellow, 45, 16);
            Shape bub = St(ShapeKind.Bubble, Color.FromArgb(40, 40, 40), 2, Color.White, 100, 16);
            bub.Align = 1;
            d[Tool.Bubble] = bub;
            return d;
        }

        public static Tool ToolFor(Shape s)
        {
            if (s.Kind == ShapeKind.Counter && s.Mode == 1) return Tool.Letter;
            return ToolFor(s.Kind);
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
                case ShapeKind.Counter: return Tool.Counter;
                case ShapeKind.Highlight: return Tool.Highlight;
                case ShapeKind.Bubble: return Tool.Bubble;
                default: return Tool.Pixelate;
            }
        }

        public Tool CurrentTool
        {
            get { return tool; }
            set
            {
                EndTextEdit(true);
                if (creating != null && creating.IsPoly && tool != value) FinishPoly();
                creating = null;
                marquee = false;
                Tool old = tool;
                tool = value;
                Cursor = tool == Tool.Select ? Cursors.Default : (tool == Tool.Text ? Cursors.IBeam : Cursors.Cross);
                if (ToolChanged != null) ToolChanged(this, EventArgs.Empty);
                if (CropRect.HasValue && ((old == Tool.Crop) != (value == Tool.Crop))) Fit();   // show the whole image while cropping
                Invalidate();
            }
        }

        Shape NewShape(Tool t, PointF p)
        {
            Shape s;
            if (Styles.ContainsKey(t)) s = Styles[t].Clone();
            else { s = new Shape(); s.Kind = t == Tool.Crop ? ShapeKind.Rect : ShapeKind.Line; s.Width = 1; }
            s.Pts.Clear();
            s.Text = "";
            s.Angle = 0;
            s.Pts.Add(p);
            return s;
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
            return s;
        }

        public void PushUndo(DocState s)
        {
            undo.Add(s);
            if (undo.Count > 300) undo.RemoveAt(0);
            redo.Clear();
        }

        void Restore(DocState s)
        {
            // keep the same shapes selected (by position) so the style panel shows their restored values
            List<int> idx = new List<int>();
            List<ShapeKind> kinds = new List<ShapeKind>();
            foreach (Shape x in Sel) { idx.Add(Shapes.IndexOf(x)); kinds.Add(x.Kind); }
            bool cropChanged = s.Crop != CropRect;
            Image = s.Image; Shapes = s.Shapes; Scale = s.Scale; CropRect = s.Crop;
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
            Sel.AddRange(Shapes);
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
                        if (im != null) { ProjectFile.CheckImageSize(im.Width, im.Height); b = Effects.To32(new Bitmap(im)); }
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
                    return Effects.To32(new Bitmap(im));
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>Adds a picture on top of the canvas: fitted into the visible area (never enlarged), centred on 'at' or the view.</summary>
        public void InsertImage(Bitmap b, PointF? at)
        {
            EndTextEdit(true);
            RectangleF vis = RectangleF.Intersect(ViewRect, Geo.Rect(ToImg(new Point((int)RulerSize, (int)RulerSize)), ToImg(new Point(Width, Height))));
            if (vis.Width < 4 || vis.Height < 4) vis = ViewRect;
            float sc = Math.Min(1f, Math.Min(vis.Width * 0.8f / b.Width, vis.Height * 0.8f / b.Height));
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

        public void DuplicateSelection()
        {
            if (Sel.Count == 0) return;
            EndTextEdit(true);
            PushUndo(Snapshot());
            List<Shape> src = SelInOrder();
            Sel.Clear();
            foreach (Shape s in src) { Shape c = s.Clone(); c.Cache = null; c.Offset(12, 12); Shapes.Add(c); Sel.Add(c); }
            FireDoc(); FireSel(); Invalidate();
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
            if (op == "border" || op == "shadow" || op == "torn") BakeCrop();
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
            Shapes = ns;
            Sel.Clear();
            Fit(); FireDoc(); FireSel();
        }

        // ---------- snapping ----------
        bool SnapTool { get { return tool == Tool.Dimension || tool == Tool.Area || tool == Tool.Polygon || tool == Tool.Calibrate || tool == Tool.Line || tool == Tool.Arrow || tool == Tool.Select || tool == Tool.Angle; } }

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
                if (creating != null && creating.IsPoly && creating.Pts.Count > 3)
                {
                    float d = Geo.Dist(creating.Pts[0], ip);
                    if (d < best) { best = d; found = creating.Pts[0]; }
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
            if (pts.Count >= 2 && Geo.Dist(pts[pts.Count - 1], pts[0]) < 0.5f) pts.RemoveAt(pts.Count - 1);
            if (pts.Count >= 3) AddShape(c); else Invalidate();
        }

        bool ClickBuilt(Shape s) { return s != null && (s.IsPoly || s.Kind == ShapeKind.Angle); }

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

            TextBox tb = new TextBox();
            tb.Multiline = true;
            tb.AcceptsReturn = true;
            tb.WordWrap = s.Kind == ShapeKind.Bubble;
            tb.ScrollBars = ScrollBars.None;
            tb.BorderStyle = BorderStyle.FixedSingle;
            tb.Font = new Font("Segoe UI", Math.Max(6f, s.FontSize * Zoom), s.Style, GraphicsUnit.Pixel);
            tb.ForeColor = Color.FromArgb(255, s.Stroke);
            tb.BackColor = s.FillOpacity > 0 ? Color.FromArgb(255, s.Fill) : Color.White;
            if (tb.BackColor.GetBrightness() < 0.25f && tb.ForeColor.GetBrightness() < 0.25f) tb.BackColor = Color.White;
            tb.TextAlign = TbAlign(s.Align);
            tb.Text = editOrigText.Replace("\r\n", "\n").Replace("\n", "\r\n");
            if (s.Kind == ShapeKind.Bubble) s.Text = "";     // bubble body stays visible behind the editor
            editBox = tb;
            tb.TextChanged += (o, e) => LayoutEditBox();
            tb.KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; BeginInvoke((Action)(() => EndTextEdit(false))); }
                else if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; BeginInvoke((Action)(() => EndTextEdit(true))); }
            };
            tb.Leave += (o, e) => EndTextEdit(true);
            LayoutEditBox();
            Controls.Add(tb);
            tb.Focus();
            tb.SelectionStart = tb.TextLength;
            Invalidate();
        }

        /// <summary>Style panel changed bold/italic/alignment/size while typing: update the edit box.</summary>
        public void RefreshEditStyle()
        {
            if (editBox == null) return;
            editBox.Font = new Font("Segoe UI", Math.Max(6f, editShape.FontSize * Zoom), editShape.Style, GraphicsUnit.Pixel);
            editBox.TextAlign = TbAlign(editShape.Align);
            editBox.ForeColor = Color.FromArgb(255, editShape.Stroke);
            LayoutEditBox();
            Invalidate();
        }

        public Shape EditingShape { get { return editShape; } }

        void LayoutEditBox()
        {
            if (editBox == null) return;
            Shape s = editShape;
            float newSize = Math.Max(6f, s.FontSize * Zoom);
            if (Math.Abs(editBox.Font.Size - newSize) > 0.1f || editBox.Font.Style != s.Style)
                editBox.Font = new Font("Segoe UI", newSize, s.Style, GraphicsUnit.Pixel);
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
            Controls.Remove(tb);
            tb.Dispose();

            if (!commit)
            {
                if (!editIsNew) s.Text = editOrigText;
            }
            else if (editIsNew)
            {
                if (text.Trim().Length > 0 || s.Kind == ShapeKind.Bubble) { s.Text = text; AddShape(s); }
            }
            else if (text != editOrigText)
            {
                s.Text = editOrigText;
                PushUndo(editBefore);
                if (s.Kind == ShapeKind.Text && text.Trim().Length == 0) { Shapes.Remove(s); Sel.Remove(s); FireSel(); }
                else s.Text = text;
                FireDoc();
            }
            else s.Text = editOrigText;
            editBefore = null;
            Invalidate();
            if (Visible) Focus();
        }

        // ---------- context menu ----------
        void ShowContextMenu(Point at, PointF ip)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = new Font("Segoe UI", 9f * Dpi / Native.SystemScale);
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
            m.Items.Add(new ToolStripSeparator());
            add("Bring to front", "Ctrl+Shift+]", any, () => Order(0));
            add("Bring forward", "Ctrl+]", any, () => Order(1));
            add("Send backward", "Ctrl+[", any, () => Order(2));
            add("Send to back", "Ctrl+Shift+[", any, () => Order(3));
            m.Items.Add(new ToolStripSeparator());
            add("Select all", "Ctrl+A", Shapes.Count > 0, SelectAll);
            if (CropRect.HasValue) add("Remove crop", "", true, RemoveCrop);
            if (HGuides.Count + VGuides.Count > 0) add("Clear guides", "", true, () => { HGuides.Clear(); VGuides.Clear(); Invalidate(); });
            m.Closed += (s, e) => BeginInvoke((Action)(() => m.Dispose()));
            m.Show(this, at);
        }

        void ShowRulerMenu(Point at)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = new Font("Segoe UI", 9f * Dpi / Native.SystemScale);
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
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (editBox != null)
            {
                EndTextEdit(true);
                if (e.Button == MouseButtons.Left) return;    // this click just finishes typing
            }
            Focus();
            PointF ip = ToImg(e.Location);
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
            switch (tool)
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
                        if (hit != null && hit.Kind == ShapeKind.Text) { BeginTextEdit(hit, false); break; }
                        BeginTextEdit(NewShape(Tool.Text, ip), true);
                        break;
                    }
                case Tool.Counter:
                case Tool.Letter:
                    AddShape(NewShape(tool, ip));
                    break;
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
                    {
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
                            if (shift && !snapPt.HasValue && n >= 2) p = Geo.SnapAngle(creating.Pts[n - 2], sp, 15);
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
                case Tool.Area:
                    {
                        if (creating == null)
                        {
                            Sel.Clear(); FireSel();
                            creating = NewShape(tool, sp);
                            creating.Pts.Add(sp);
                        }
                        else if (e.Clicks >= 2) FinishPoly();
                        else
                        {
                            int n = creating.Pts.Count;
                            PointF p = sp;
                            if (shift && !snapPt.HasValue && n >= 2) p = Geo.SnapAngle(creating.Pts[n - 2], sp, 15);
                            if (n > 3 && Geo.Dist(p, creating.Pts[0]) < 9f * Dpi / Zoom) { FinishPoly(); return; }
                            creating.Pts[n - 1] = p;
                            creating.Pts.Add(p);
                        }
                        Invalidate();
                        break;
                    }
                case Tool.Freehand:
                    Sel.Clear(); FireSel();
                    creating = NewShape(tool, ip);
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
            if (s.Kind == ShapeKind.Text || s.Kind == ShapeKind.Bubble) { BeginTextEdit(s, false); return; }
            if (s.Kind == ShapeKind.Dimension || s.Kind == ShapeKind.Area)
            {
                string title = s.Kind == ShapeKind.Dimension ? "Dimension label prefix (e.g. W =)" : "Area label (e.g. Room 1)";
                string t = AskText != null ? AskText(title, s.Text) : null;
                if (t != null && t != s.Text) { PushUndo(Snapshot()); s.Text = t; FireDoc(); Invalidate(); }
                return;
            }
            if (s.IsPoly)
            {
                // double-click on an edge inserts a new vertex there
                for (int i = 0; i < s.Pts.Count; i++)
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
        }

        void InvalidateRulers()
        {
            if (!showRulers) return;
            int rs = (int)RulerSize + 1;
            Invalidate(new Rectangle(0, 0, Width, rs));
            Invalidate(new Rectangle(0, 0, rs, Height));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            PointF ip = ToImg(e.Location);
            mouseImg = ip;
            mouseScr = e.Location;
            InvalidateRulers();
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
                PointF sp = tool == Tool.Freehand || tool == Tool.Crop ? ip : Snap(ip);
                int n = creating.Pts.Count;
                PointF p0 = creating.Pts[0];
                switch (tool)
                {
                    case Tool.Freehand:
                        if (Geo.Dist(creating.Pts[n - 1], ip) > 1.2f / Zoom) creating.Pts.Add(ip);
                        break;
                    case Tool.Polygon:
                    case Tool.Area:
                    case Tool.Angle:
                        creating.Pts[n - 1] = (shift && !snapPt.HasValue && n >= 2) ? Geo.SnapAngle(creating.Pts[n - 2], sp, 15) : sp;
                        break;
                    case Tool.Rect:
                    case Tool.Ellipse:
                    case Tool.Pixelate:
                    case Tool.Crop:
                    case Tool.Highlight:
                    case Tool.Bubble:
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
                Invalidate();
                return;
            }

            if (dragH.HasValue && Selected != null)
            {
                Handle h = dragH.Value;
                PointF p = h.Kind == HKind.Rotate || h.Kind == HKind.Box ? ip : Snap(ip);
                if (shift && !snapPt.HasValue && h.Kind == HKind.Point && h.Index < 2 &&
                    (Selected.Kind == ShapeKind.Line || Selected.Kind == ShapeKind.Arrow || Selected.Kind == ShapeKind.Dimension))
                    p = Geo.SnapAngle(Selected.Pts[1 - h.Index], p, 15);
                ShapeOps.DragHandle(Selected, dragOrig, h, p, shift);
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
                if (h.HasValue) Cursor = h.Value.Kind == HKind.Rotate ? Cursors.Hand : (h.Value.Kind == HKind.Center ? Cursors.SizeAll : Cursors.Cross);
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
            if (!Nullable.Equals(oldSnap, snapPt) || tool != Tool.Select) Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
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
                        if (!Sel.Contains(s) && r.IntersectsWith(ShapeOps.Bounds(s))) Sel.Add(s);
                    FireSel();
                }
                Invalidate();
                return;
            }
            if (creating != null && creating.Kind == ShapeKind.Angle)
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
                    if (creating != null) creating = null;
                    else if (Sel.Count > 0) { Sel.Clear(); FireSel(); }
                    else CurrentTool = Tool.Select;
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
                case Keys.R: CurrentTool = Tool.Rect; break;
                case Keys.E: CurrentTool = Tool.Ellipse; break;
                case Keys.L: CurrentTool = Tool.Line; break;
                case Keys.A: CurrentTool = Tool.Arrow; break;
                case Keys.F: CurrentTool = Tool.Freehand; break;
                case Keys.T: CurrentTool = Tool.Text; break;
                case Keys.P: CurrentTool = Tool.Polygon; break;
                case Keys.D: CurrentTool = Tool.Dimension; break;
                case Keys.M: CurrentTool = Tool.Area; break;
                case Keys.G: CurrentTool = Tool.Angle; break;
                case Keys.K: CurrentTool = Tool.Calibrate; break;
                case Keys.N: CurrentTool = e.Shift ? Tool.Letter : Tool.Counter; break;
                case Keys.B: CurrentTool = Tool.Pixelate; break;
                case Keys.C: CurrentTool = Tool.Crop; break;
                case Keys.H: CurrentTool = Tool.Highlight; break;
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
            FireDoc(); Invalidate();
        }

        // ---------- painting ----------
        static TextureBrush Checker
        {
            get
            {
                if (checker == null)
                {
                    Bitmap b = new Bitmap(16, 16);
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
                return checker;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (Image == null) return;

            RectangleF vr = ViewRect;
            bool clip = CropRect.HasValue && tool != Tool.Crop;
            GraphicsState st = g.Save();
            g.TranslateTransform(Pan.X, Pan.Y);
            g.ScaleTransform(Zoom, Zoom);
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillRectangle(sh, vr.X + 4f / Zoom, vr.Y + 4f / Zoom, vr.Width, vr.Height);
            if (clip) g.SetClip(vr);
            g.FillRectangle(Checker, 0, 0, Image.Width, Image.Height);
            g.InterpolationMode = Zoom >= 1.99f ? InterpolationMode.NearestNeighbor : (Zoom < 1f ? InterpolationMode.HighQualityBicubic : InterpolationMode.Bilinear);
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(Image, new RectangleF(0, 0, Image.Width, Image.Height), new RectangleF(0, 0, Image.Width, Image.Height), GraphicsUnit.Pixel);
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            Shape skip = (editShape != null && editShape.Kind == ShapeKind.Text) ? editShape : null;
            Renderer.DrawAll(g, Shapes, Image, Scale, skip);
            if (editShape != null && editIsNew && editShape.Kind == ShapeKind.Bubble)
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
                    Renderer.Draw(g, creating, Image, Scale, creating.Kind == ShapeKind.Area ? "A" + (CountKind(ShapeKind.Area) + 1) : null);
                }
            }
            g.Restore(st);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (tool == Tool.Crop) DrawCropOverlay(g);
            DrawGuides(g);

            if (creating != null && (creating.Kind == ShapeKind.Ellipse || creating.Kind == ShapeKind.Rect) && creating.Pts.Count >= 2 && tool != Tool.Crop)
                DrawCenterMark(g, ToScr(Geo.Center(Geo.Rect(creating.Pts[0], creating.Pts[1]))));
            if (Sel.Count > 0 && editBox == null) DrawSelection(g);
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
            DrawRulers(g);
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

        void DrawGuides(Graphics g)
        {
            if (HGuides.Count + VGuides.Count == 0 && guideAxis == 0) return;
            float rs = RulerSize;
            using (Pen p = new Pen(Color.FromArgb(220, 0, 170, 255), 1f))
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
                using (Pen p = new Pen(Color.FromArgb(255, 0, 140, 255), 1f))
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
            Color bgc = Color.FromArgb(244, 244, 244), ink = Color.FromArgb(80, 80, 80);
            Font f = Renderer.GetFont(10f * Dpi, false);
            SmoothingMode sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush bg = new SolidBrush(bgc))
            using (SolidBrush tb = new SolidBrush(ink))
            using (Pen tp = new Pen(ink, 1f))
            using (Pen edge = new Pen(Color.FromArgb(170, 170, 170), 1f))
            using (Pen red = new Pen(Color.FromArgb(230, 30, 30), 2f))
            using (Pen blue = new Pen(Color.FromArgb(0, 140, 255), 2f))
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
                    case HKind.DimOffset:
                    case HKind.Tail:
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
            switch (tool)
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
                case Tool.Angle:
                    if (p.Count == 2) t = "Arm " + Scale.Len(Geo.Dist(p[0], p[1])) + "\nClick the vertex, then the end of the second arm";
                    else if (p.Count >= 3) t = "∠ " + ScaleInfo.AngleText(Geo.AngleAt(p[0], p[1], p[2])) + "\nClick to finish · Shift = 15° steps";
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
                    if (p.Count >= 2)
                    {
                        t = "Segment " + Scale.Len(Geo.Dist(p[p.Count - 2], p[p.Count - 1]));
                        if (p.Count >= 3) t += "\nArea " + Scale.Area(Math.Abs(Geo.SignedArea(p)));
                        t += "\nDouble-click / Enter = finish · Backspace = undo point";
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
