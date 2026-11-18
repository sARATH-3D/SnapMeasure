using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace SnapMeasure
{
    public enum ShapeKind { Rect, Ellipse, Line, Arrow, Freehand, Text, Polygon, Dimension, Area, Counter, Pixelate, Highlight, Bubble, Angle, Image }

    public enum Tool { Select, Rect, Ellipse, Line, Arrow, Freehand, Text, Polygon, Dimension, Area, Calibrate, Counter, Pixelate, Crop, Highlight, Bubble, Letter, Angle }

    public enum HighlightMode { Marker = 0, Spotlight = 1, Grayscale = 2, Magnify = 3 }

    public class Shape
    {
        public ShapeKind Kind;
        public List<PointF> Pts = new List<PointF>();
        public Color Stroke = Color.Red;
        public Color Fill = Color.Yellow;
        public int StrokeOpacity = 100;
        public int FillOpacity;
        public float Width = 2f;
        public string Text = "";
        public float FontSize = 16f;
        public float Angle;          // degrees, clockwise, about the shape centre (Rect, Ellipse, Text)
        public bool Shadow;
        public int Mode;             // HighlightMode for Highlight shapes; 0 = numbers, 1 = letters for markers
        public bool Bold, Italic;    // Text and speech bubble
        public int Align;            // 0 left, 1 centre, 2 right (Text, speech bubble)
        public int Heads = 1;        // Line / Arrow: 0 none, 1 end, 2 start, 3 both
        public Bitmap Img;           // Image shapes: the pasted picture (shared between undo copies, never modified)

        public FontStyle Style { get { return (Bold ? FontStyle.Bold : FontStyle.Regular) | (Italic ? FontStyle.Italic : FontStyle.Regular); } }
        public bool HasTextStyle { get { return Kind == ShapeKind.Text || Kind == ShapeKind.Bubble; } }
        public bool IsLetterMarker { get { return Kind == ShapeKind.Counter && Mode == 1; } }

        // render cache (pixelate); not serialized
        public Bitmap Cache;
        public string CacheKey;

        public Shape Clone()
        {
            Shape s = (Shape)MemberwiseClone();
            s.Pts = new List<PointF>(Pts);
            return s;
        }

        public void Offset(float dx, float dy)
        {
            for (int i = 0; i < Pts.Count; i++) Pts[i] = new PointF(Pts[i].X + dx, Pts[i].Y + dy);
        }

        public bool IsPoly { get { return Kind == ShapeKind.Polygon || Kind == ShapeKind.Area; } }
        public bool IsBox { get { return Kind == ShapeKind.Rect || Kind == ShapeKind.Ellipse || Kind == ShapeKind.Pixelate || Kind == ShapeKind.Highlight || Kind == ShapeKind.Bubble || Kind == ShapeKind.Image; } }
        public bool Rotatable { get { return Kind == ShapeKind.Rect || Kind == ShapeKind.Ellipse || Kind == ShapeKind.Text || Kind == ShapeKind.Image; } }
        public bool HasHeads { get { return Kind == ShapeKind.Line || Kind == ShapeKind.Arrow; } }
        public bool CanShadow { get { return Kind != ShapeKind.Dimension && Kind != ShapeKind.Area && Kind != ShapeKind.Pixelate && Kind != ShapeKind.Highlight; } }
    }

    /// <summary>Document-level settings: measuring scale and marker numbering.</summary>
    public class ScaleInfo
    {
        public double Upp = 1.0;      // real units per image pixel
        public string Unit = "px";
        public int Decimals = 2;
        public int MarkerStart = 1;   // first number of the number-marker series
        public int LetterStart = 1;   // first letter of the letter-marker series (1 = A)

        public ScaleInfo Clone() { return (ScaleInfo)MemberwiseClone(); }
        public bool Calibrated { get { return !(Unit == "px" && Math.Abs(Upp - 1.0) < 1e-12); } }

        public string Len(double px) { return (px * Upp).ToString("N" + Decimals, CultureInfo.CurrentCulture) + " " + Unit; }
        public string Area(double px2) { return (px2 * Upp * Upp).ToString("N" + Decimals, CultureInfo.CurrentCulture) + " " + Unit + "²"; }
        public string Describe()
        {
            if (!Calibrated) return "Scale: not set (pixels)";
            return "Scale: 1 px = " + Upp.ToString("G6", CultureInfo.CurrentCulture) + " " + Unit;
        }

        public string MarkerLabel(int index, bool letters)
        {
            if (letters)
            {
                int v = ((Math.Max(1, LetterStart) - 1 + index) % 26 + 26) % 26;   // always one character: … Y, Z, A, B …
                return ((char)('A' + v)).ToString();
            }
            return (MarkerStart + index).ToString(CultureInfo.InvariantCulture);
        }

        public static string AngleText(double deg) { return deg.ToString("N1", CultureInfo.CurrentCulture) + "°"; }

        public static readonly string[] Units = { "mm", "cm", "m", "in", "ft", "px" };
    }

    public static class Geo
    {
        public static float Dist(PointF a, PointF b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        public static PointF Mid(PointF a, PointF b) { return new PointF((a.X + b.X) / 2f, (a.Y + b.Y) / 2f); }

        public static float SegDist(PointF p, PointF a, PointF b) { return Dist(p, Project(p, a, b)); }

        public static PointF Project(PointF p, PointF a, PointF b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            if (l2 < 1e-9f) return a;
            float t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2;
            t = Math.Max(0f, Math.Min(1f, t));
            return new PointF(a.X + t * dx, a.Y + t * dy);
        }

        public static PointF Rot(PointF p, PointF c, float deg)
        {
            if (deg == 0f) return p;
            double r = deg * Math.PI / 180.0, cs = Math.Cos(r), sn = Math.Sin(r);
            double dx = p.X - c.X, dy = p.Y - c.Y;
            return new PointF((float)(c.X + dx * cs - dy * sn), (float)(c.Y + dx * sn + dy * cs));
        }

        public static float NormAngle(float a)
        {
            a %= 360f;
            if (a > 180f) a -= 360f;
            if (a <= -180f) a += 360f;
            return a;
        }

        public static bool InPolygon(PointF p, IList<PointF> poly)
        {
            bool c = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if (((poly[i].Y > p.Y) != (poly[j].Y > p.Y)) &&
                    (p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X))
                    c = !c;
            }
            return c;
        }

        public static double SignedArea(IList<PointF> p)
        {
            double a = 0;
            for (int i = 0; i < p.Count; i++)
            {
                PointF q = p[i], r = p[(i + 1) % p.Count];
                a += (double)q.X * r.Y - (double)r.X * q.Y;
            }
            return a / 2.0;
        }

        public static double Perimeter(IList<PointF> p, bool closed)
        {
            double s = 0;
            for (int i = 0; i + 1 < p.Count; i++) s += Dist(p[i], p[i + 1]);
            if (closed && p.Count > 2) s += Dist(p[p.Count - 1], p[0]);
            return s;
        }

        public static PointF Average(IList<PointF> p)
        {
            float sx = 0, sy = 0;
            foreach (PointF q in p) { sx += q.X; sy += q.Y; }
            return new PointF(sx / Math.Max(1, p.Count), sy / Math.Max(1, p.Count));
        }

        public static PointF Centroid(IList<PointF> p)
        {
            double A = SignedArea(p);
            if (Math.Abs(A) < 1e-6) return Average(p);
            double cx = 0, cy = 0;
            for (int i = 0; i < p.Count; i++)
            {
                PointF q = p[i], r = p[(i + 1) % p.Count];
                double cr = (double)q.X * r.Y - (double)r.X * q.Y;
                cx += (q.X + r.X) * cr;
                cy += (q.Y + r.Y) * cr;
            }
            PointF c = new PointF((float)(cx / (6 * A)), (float)(cy / (6 * A)));
            if (InPolygon(c, p)) return c;
            PointF avg = Average(p);
            return InPolygon(avg, p) ? avg : c;
        }

        public static RectangleF Rect(PointF a, PointF b)
        {
            return RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        public static PointF Center(RectangleF r) { return new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f); }

        public static PointF SnapAngle(PointF from, PointF to, double stepDeg)
        {
            double dx = to.X - from.X, dy = to.Y - from.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double step = stepDeg * Math.PI / 180.0;
            double ang = Math.Round(Math.Atan2(dy, dx) / step) * step;
            return new PointF((float)(from.X + len * Math.Cos(ang)), (float)(from.Y + len * Math.Sin(ang)));
        }

        public static double AngleDeg(PointF a, PointF b)
        {
            double d = -Math.Atan2(b.Y - a.Y, b.X - a.X) * 180.0 / Math.PI;
            if (d < 0) d += 360;
            return d;
        }

        public static float Clamp(float v, float lo, float hi)
        {
            if (lo > hi) return (lo + hi) / 2f;
            return Math.Max(lo, Math.Min(hi, v));
        }

        /// <summary>Dimension geometry: measured points p1,p2 are shifted along the normal by the offset handle.</summary>
        public static void Dim(Shape s, out PointF a, out PointF b, out PointF n, out float d)
        {
            PointF p1 = s.Pts[0], p2 = s.Pts[1], o = s.Pts.Count > 2 ? s.Pts[2] : p1;
            float dx = p2.X - p1.X, dy = p2.Y - p1.Y, L = (float)Math.Sqrt(dx * dx + dy * dy);
            n = L < 1e-4f ? new PointF(0, -1) : new PointF(-dy / L, dx / L);
            d = (o.X - p1.X) * n.X + (o.Y - p1.Y) * n.Y;
            a = new PointF(p1.X + n.X * d, p1.Y + n.Y * d);
            b = new PointF(p2.X + n.X * d, p2.Y + n.Y * d);
        }

        /// <summary>Inside angle at vertex v between arms v→p1 and v→p2, 0..180 degrees.</summary>
        public static double AngleAt(PointF p1, PointF v, PointF p2)
        {
            double a1 = Math.Atan2(p1.Y - v.Y, p1.X - v.X), a2 = Math.Atan2(p2.Y - v.Y, p2.X - v.X);
            double d = Math.Abs(a2 - a1) * 180.0 / Math.PI;
            if (d > 180.0) d = 360.0 - d;
            return d;
        }

        /// <summary>Arrowhead size for a line width: length and half-width, never more than maxLen.</summary>
        public static void HeadSize(float w, float maxLen, out float len, out float half)
        {
            len = Math.Min(6f + 9f * w, Math.Max(2f, maxLen));
            half = len * 0.55f;
        }
    }

    public static class Renderer
    {
        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        static Graphics mg;
        static Bitmap grayFor, grayCache;

        public static Graphics MeasureG
        {
            get
            {
                if (mg == null) { mg = Graphics.FromImage(new Bitmap(1, 1)); mg.TextRenderingHint = TextRenderingHint.AntiAlias; }
                return mg;
            }
        }

        public static Font GetFont(float px, bool bold) { return GetFont(px, bold ? FontStyle.Bold : FontStyle.Regular); }

        public static Font GetFont(float px, FontStyle style)
        {
            px = Math.Max(4f, (float)Math.Round(px, 1));
            string key = px.ToString(CultureInfo.InvariantCulture) + "|" + (int)style;
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font("Segoe UI", px, style, GraphicsUnit.Pixel);
                fonts[key] = f;
            }
            return f;
        }

        public static SizeF TextSize(Shape s)
        {
            string t = string.IsNullOrEmpty(s.Text) ? " " : s.Text;
            SizeF sz = MeasureG.MeasureString(t, GetFont(s.FontSize, s.Style));
            return new SizeF(Math.Max(sz.Width, s.FontSize * 0.6f), sz.Height);
        }

        static StringAlignment HAlign(int a) { return a == 1 ? StringAlignment.Center : (a == 2 ? StringAlignment.Far : StringAlignment.Near); }

        public static Color A(Color c, int opacity)
        {
            int a = (int)Math.Round(Math.Max(0, Math.Min(100, opacity)) * 2.55);
            return Color.FromArgb(a, c.R, c.G, c.B);
        }

        /// <summary>Labels: D1.. for dimensions, A1.. for areas, and marker labels (auto-renumbered).</summary>
        public static Dictionary<Shape, string> Labels(List<Shape> shapes, ScaleInfo sc)
        {
            Dictionary<Shape, string> d = new Dictionary<Shape, string>();
            int nd = 0, na = 0, nm = 0, nl = 0, ng = 0;
            foreach (Shape s in shapes)
            {
                if (s.Kind == ShapeKind.Dimension) d[s] = "D" + (++nd);
                else if (s.Kind == ShapeKind.Area) d[s] = "A" + (++na);
                else if (s.Kind == ShapeKind.Angle) d[s] = "∠" + (++ng);
                else if (s.Kind == ShapeKind.Counter) d[s] = s.IsLetterMarker ? sc.MarkerLabel(nl++, true) : sc.MarkerLabel(nm++, false);
            }
            return d;
        }

        public static void DrawAll(Graphics g, List<Shape> shapes, Bitmap img, ScaleInfo sc, Shape skip)
        {
            DrawGlobalHighlights(g, shapes, img);
            Dictionary<Shape, string> names = Labels(shapes, sc);
            foreach (Shape s in shapes)
            {
                if (s == skip) continue;
                string n;
                names.TryGetValue(s, out n);
                // one bad shape must never stop the rest of the picture from drawing
                try
                {
                    if (s.Shadow && s.CanShadow) DrawShadow(g, s, img, sc, n);
                    Draw(g, s, img, sc, n);
                }
                catch (Exception) { }
            }
        }

        public static void DrawShadow(Graphics g, Shape s, Bitmap img, ScaleInfo sc, string name)
        {
            Shape sh = s.Clone();
            sh.Shadow = false;
            // a close, tight shadow (it used to sit too far away)
            float d = Math.Max(1.5f, Math.Min(4f, s.Width * 0.45f + 1f));
            if (s.Kind == ShapeKind.Text || s.Kind == ShapeKind.Counter || s.Kind == ShapeKind.Bubble) d = Math.Max(1.5f, Math.Min(3.5f, s.FontSize / 12f));
            sh.Offset(d, d);
            sh.Stroke = Color.Black;
            sh.Fill = Color.Black;
            sh.StrokeOpacity = s.StrokeOpacity * 35 / 100;
            sh.FillOpacity = s.FillOpacity * 35 / 100;
            Draw(g, sh, img, sc, name);
        }

        public static void Draw(Graphics g, Shape s, Bitmap img, ScaleInfo sc, string name)
        {
            if (s.Pts.Count == 0) return;
            GraphicsState st = null;
            if (s.Rotatable && s.Angle != 0f)
            {
                PointF c = ShapeOps.Center(s);
                st = g.Save();
                g.TranslateTransform(c.X, c.Y);
                g.RotateTransform(s.Angle);
                g.TranslateTransform(-c.X, -c.Y);
            }
            try { DrawCore(g, s, img, sc, name); }
            finally { if (st != null) g.Restore(st); }
        }

        static void DrawCore(Graphics g, Shape s, Bitmap img, ScaleInfo sc, string name)
        {
            Color sCol = A(s.Stroke, s.StrokeOpacity);
            Color fCol = A(s.Fill, s.FillOpacity);
            float w = Math.Max(0.5f, s.Width);
            bool stroke = s.Width > 0 && s.StrokeOpacity > 0, fill = s.FillOpacity > 0;

            switch (s.Kind)
            {
                case ShapeKind.Rect:
                case ShapeKind.Ellipse:
                    {
                        if (s.Pts.Count < 2) return;
                        RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
                        if (fill)
                            using (SolidBrush b = new SolidBrush(fCol)) { if (s.Kind == ShapeKind.Rect) g.FillRectangle(b, r); else g.FillEllipse(b, r); }
                        if (stroke)
                            using (Pen p = new Pen(sCol, w)) { if (s.Kind == ShapeKind.Rect) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height); else g.DrawEllipse(p, r); }
                        break;
                    }
                case ShapeKind.Line:
                case ShapeKind.Arrow:
                    {
                        if (s.Pts.Count < 2 || !stroke) return;
                        PointF a = s.Pts[0], b = s.Pts[1];
                        float L = Geo.Dist(a, b);
                        bool hs = (s.Heads & 2) != 0, he = (s.Heads & 1) != 0;   // start / end heads
                        using (Pen p = new Pen(sCol, w))
                        {
                            p.StartCap = LineCap.Round;
                            p.EndCap = LineCap.Round;
                            if ((hs || he) && L > 0.5f)
                            {
                                // solid heads that grow with the line width; the shaft stops inside each head
                                float hl, hw;
                                Geo.HeadSize(w, L * (hs && he ? 0.45f : 0.5f), out hl, out hw);
                                PointF u = new PointF((b.X - a.X) / L, (b.Y - a.Y) / L);
                                PointF s0 = a, s1 = b;
                                if (hs) { p.StartCap = LineCap.Flat; s0 = new PointF(a.X + u.X * hl * 0.85f, a.Y + u.Y * hl * 0.85f); }
                                if (he) { p.EndCap = LineCap.Flat; s1 = new PointF(b.X - u.X * hl * 0.85f, b.Y - u.Y * hl * 0.85f); }
                                g.DrawLine(p, s0, s1);
                                using (SolidBrush br = new SolidBrush(sCol))
                                {
                                    if (he) g.FillPolygon(br, Head(b, u, hl, hw));
                                    if (hs) g.FillPolygon(br, Head(a, new PointF(-u.X, -u.Y), hl, hw));
                                }
                            }
                            else g.DrawLine(p, a, b);
                        }
                        break;
                    }
                case ShapeKind.Freehand:
                    {
                        if (!stroke) return;
                        using (Pen p = new Pen(sCol, w))
                        {
                            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                            if (s.Pts.Count == 1) g.DrawLine(p, s.Pts[0], new PointF(s.Pts[0].X + 0.1f, s.Pts[0].Y));
                            else g.DrawLines(p, s.Pts.ToArray());
                        }
                        break;
                    }
                case ShapeKind.Text:
                    {
                        Font f = GetFont(s.FontSize, s.Style);
                        SizeF sz = TextSize(s);
                        if (fill)
                            using (SolidBrush b = new SolidBrush(fCol))
                                g.FillRectangle(b, s.Pts[0].X - 4, s.Pts[0].Y - 2, sz.Width + 8, sz.Height + 4);
                        using (SolidBrush b = new SolidBrush(sCol))
                        using (StringFormat sf = new StringFormat())
                        {
                            sf.Alignment = HAlign(s.Align);
                            g.DrawString(s.Text, f, b, new RectangleF(s.Pts[0].X, s.Pts[0].Y, sz.Width + 1f, sz.Height + 1f), sf);
                        }
                        break;
                    }
                case ShapeKind.Polygon:
                case ShapeKind.Area:
                    {
                        PointF[] pts = s.Pts.ToArray();
                        if (pts.Length >= 3 && fill)
                            using (SolidBrush b = new SolidBrush(fCol)) g.FillPolygon(b, pts);
                        if (stroke && pts.Length >= 2)
                            using (Pen p = new Pen(sCol, w))
                            {
                                p.LineJoin = LineJoin.Round;
                                if (pts.Length >= 3) g.DrawPolygon(p, pts); else g.DrawLines(p, pts);
                            }
                        if (s.Kind == ShapeKind.Area && pts.Length >= 3)
                        {
                            double area = Math.Abs(Geo.SignedArea(s.Pts));
                            double per = Geo.Perimeter(s.Pts, true);
                            string t = (string.IsNullOrEmpty(name) ? "" : name + (string.IsNullOrEmpty(s.Text) ? "" : "  " + s.Text) + "\n")
                                       + "A = " + sc.Area(area) + "\nP = " + sc.Len(per);
                            DrawBox(g, t, Geo.Centroid(s.Pts), GetFont(s.FontSize, false), A(s.Stroke, 100));
                        }
                        break;
                    }
                case ShapeKind.Dimension:
                    DrawDimension(g, s, sc, sCol, w);
                    break;
                case ShapeKind.Counter:
                    {
                        float r = Math.Max(8f, s.FontSize * 0.9f);
                        PointF c = s.Pts[0];
                        RectangleF rr = new RectangleF(c.X - r, c.Y - r, 2 * r, 2 * r);
                        if (fill) using (SolidBrush b = new SolidBrush(fCol)) g.FillEllipse(b, rr);
                        if (stroke) using (Pen p = new Pen(sCol, Math.Max(1.5f, Math.Min(w, r / 4f)))) g.DrawEllipse(p, rr);
                        string label = name ?? "?";
                        float fs = label.Length > 2 ? r * 0.8f : r * 1.05f;
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Max((int)sCol.A, (int)(s.StrokeOpacity * 2.55)), s.Stroke)))
                        using (StringFormat sf = new StringFormat())
                        {
                            sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                            g.DrawString(label, GetFont(fs, true), b, rr, sf);
                        }
                        break;
                    }
                case ShapeKind.Pixelate:
                    DrawPixelate(g, s, img);
                    break;
                case ShapeKind.Highlight:
                    {
                        if (s.Pts.Count < 2) return;
                        RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
                        if (s.Mode == (int)HighlightMode.Marker)
                        {
                            using (SolidBrush b = new SolidBrush(fCol)) g.FillRectangle(b, r);
                        }
                        else if (s.Mode == (int)HighlightMode.Magnify && img != null && r.Width > 1 && r.Height > 1)
                        {
                            const float factor = 2f;
                            PointF c = Geo.Center(r);
                            RectangleF src = new RectangleF(c.X - r.Width / factor / 2f, c.Y - r.Height / factor / 2f, r.Width / factor, r.Height / factor);
                            GraphicsState st = g.Save();
                            g.SetClip(r, CombineMode.Intersect);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.DrawImage(img, r, src, GraphicsUnit.Pixel);
                            g.Restore(st);
                            if (stroke) using (Pen p = new Pen(sCol, w)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                        }
                        break;
                    }
                case ShapeKind.Bubble:
                    {
                        if (s.Pts.Count < 2) return;
                        RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
                        PointF? tip = s.Pts.Count > 2 ? (PointF?)s.Pts[2] : null;
                        using (GraphicsPath path = BubblePath(r, tip))
                        {
                            if (fill) using (SolidBrush b = new SolidBrush(fCol)) g.FillPath(b, path);
                            if (stroke) using (Pen p = new Pen(sCol, w)) { p.LineJoin = LineJoin.Round; g.DrawPath(p, path); }
                        }
                        if (!string.IsNullOrEmpty(s.Text))
                        {
                            RectangleF tr = RectangleF.Inflate(r, -Math.Max(4f, w + 4f), -Math.Max(3f, w + 2f));
                            using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Max(60, (int)sCol.A), s.Stroke)))
                            using (StringFormat sf = new StringFormat())
                            {
                                sf.Alignment = HAlign(s.Align); sf.LineAlignment = StringAlignment.Center;
                                g.DrawString(s.Text, GetFont(s.FontSize, s.Style), b, tr, sf);
                            }
                        }
                        break;
                    }
                case ShapeKind.Angle:
                    DrawAngle(g, s, sCol, w, name);
                    break;
                case ShapeKind.Image:
                    {
                        if (s.Pts.Count < 2) return;
                        RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
                        if (s.Img != null && r.Width > 0 && r.Height > 0)
                        {
                            GraphicsState st = g.Save();
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            if (s.FillOpacity >= 100)
                                g.DrawImage(s.Img, r, new RectangleF(0, 0, s.Img.Width, s.Img.Height), GraphicsUnit.Pixel);
                            else
                                using (ImageAttributes ia = new ImageAttributes())
                                {
                                    ColorMatrix cm = new ColorMatrix();
                                    cm.Matrix33 = Math.Max(0, s.FillOpacity) / 100f;
                                    ia.SetColorMatrix(cm);
                                    g.DrawImage(s.Img, new Rectangle((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height)),
                                                0, 0, s.Img.Width, s.Img.Height, GraphicsUnit.Pixel, ia);
                                }
                            g.Restore(st);
                        }
                        if (s.Width > 0 && s.StrokeOpacity > 0)
                            using (Pen p = new Pen(sCol, w)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                        break;
                    }
            }
        }

        /// <summary>Filled arrowhead with its tip at 'tip', pointing along u.</summary>
        public static PointF[] Head(PointF tip, PointF u, float len, float half)
        {
            PointF n = new PointF(-u.Y, u.X);
            PointF bse = new PointF(tip.X - u.X * len, tip.Y - u.Y * len);
            return new PointF[] { tip, new PointF(bse.X + n.X * half, bse.Y + n.Y * half), new PointF(bse.X - n.X * half, bse.Y - n.Y * half) };
        }

        static void DrawAngle(Graphics g, Shape s, Color col, float w, string name)
        {
            if (s.Pts.Count < 2) return;
            PointF p1 = s.Pts[0], v = s.Pts[1];
            using (Pen p = new Pen(col, w))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                if (s.Pts.Count < 3) { g.DrawLine(p, p1, v); return; }
                PointF p2 = s.Pts[2];
                g.DrawLines(p, new PointF[] { p1, v, p2 });
                float L1 = Geo.Dist(p1, v), L2 = Geo.Dist(p2, v);
                if (L1 < 1f || L2 < 1f) return;
                double a1 = Math.Atan2(p1.Y - v.Y, p1.X - v.X) * 180.0 / Math.PI;
                double a2 = Math.Atan2(p2.Y - v.Y, p2.X - v.X) * 180.0 / Math.PI;
                double sweep = a2 - a1;
                while (sweep > 180) sweep -= 360;
                while (sweep <= -180) sweep += 360;
                float r = Math.Max(10f, Math.Min(60f, Math.Min(L1, L2) * 0.35f));
                using (Pen ap = new Pen(col, Math.Max(1f, w * 0.75f)))
                    if (Math.Abs(sweep) > 0.05) g.DrawArc(ap, v.X - r, v.Y - r, 2 * r, 2 * r, (float)a1, (float)sweep);
                double mid = (a1 + sweep / 2.0) * Math.PI / 180.0;
                double deg = Geo.AngleAt(p1, v, p2);
                string t = ScaleInfo.AngleText(deg);
                Font f = GetFont(s.FontSize, false);
                SizeF sz = g.MeasureString(t, f);
                float dist = r + 6f + Math.Max(sz.Width, sz.Height) * 0.55f;
                PointF at = new PointF(v.X + (float)Math.Cos(mid) * dist, v.Y + (float)Math.Sin(mid) * dist);
                DrawBox(g, t, at, f, Color.FromArgb(Math.Max(60, (int)col.A), col.R, col.G, col.B));
            }
        }

        public static GraphicsPath BubblePath(RectangleF r, PointF? tip)
        {
            float rad = Math.Max(1f, Math.Min(14f, Math.Min(r.Width, r.Height) / 3f)), d = rad * 2f;
            float tw = Math.Max(5f, Math.Min(Math.Min(r.Width, r.Height) * 0.16f, 20f));
            int side = -1;
            PointF t = tip.HasValue ? tip.Value : PointF.Empty;
            if (tip.HasValue && !r.Contains(t) && r.Width > 2 && r.Height > 2)
            {
                PointF c = Geo.Center(r);
                float dx = (t.X - c.X) / (r.Width / 2f), dy = (t.Y - c.Y) / (r.Height / 2f);
                side = Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? 1 : 3) : (dy > 0 ? 2 : 0);
            }
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            if (side == 0)
            {
                float bx = Geo.Clamp(t.X, r.X + rad + tw, r.Right - rad - tw);
                p.AddLine(bx - tw, r.Y, t.X, t.Y); p.AddLine(t.X, t.Y, bx + tw, r.Y);
            }
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            if (side == 1)
            {
                float by = Geo.Clamp(t.Y, r.Y + rad + tw, r.Bottom - rad - tw);
                p.AddLine(r.Right, by - tw, t.X, t.Y); p.AddLine(t.X, t.Y, r.Right, by + tw);
            }
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            if (side == 2)
            {
                float bx = Geo.Clamp(t.X, r.X + rad + tw, r.Right - rad - tw);
                p.AddLine(bx + tw, r.Bottom, t.X, t.Y); p.AddLine(t.X, t.Y, bx - tw, r.Bottom);
            }
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            if (side == 3)
            {
                float by = Geo.Clamp(t.Y, r.Y + rad + tw, r.Bottom - rad - tw);
                p.AddLine(r.X, by + tw, t.X, t.Y); p.AddLine(t.X, t.Y, r.X, by - tw);
            }
            p.CloseFigure();
            return p;
        }

        static void DrawGlobalHighlights(Graphics g, List<Shape> shapes, Bitmap img)
        {
            if (img == null) return;
            List<RectangleF> spot = new List<RectangleF>(), gray = new List<RectangleF>();
            int spotOp = 0;
            foreach (Shape s in shapes)
            {
                if (s.Kind != ShapeKind.Highlight || s.Pts.Count < 2) continue;
                if (s.Mode == (int)HighlightMode.Spotlight) { spot.Add(Geo.Rect(s.Pts[0], s.Pts[1])); spotOp = Math.Max(spotOp, s.FillOpacity); }
                else if (s.Mode == (int)HighlightMode.Grayscale) gray.Add(Geo.Rect(s.Pts[0], s.Pts[1]));
            }
            RectangleF all = new RectangleF(0, 0, img.Width, img.Height);
            if (gray.Count > 0)
            {
                if (grayFor != img) { grayCache = Effects.Grayscale(img); grayFor = img; }
                using (Region rg = new Region(all))
                {
                    foreach (RectangleF r in gray) rg.Exclude(r);
                    GraphicsState st = g.Save();
                    g.SetClip(rg, CombineMode.Intersect);
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(grayCache, all, all, GraphicsUnit.Pixel);
                    g.Restore(st);
                }
            }
            if (spot.Count > 0)
            {
                using (Region rg = new Region(all))
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(Math.Max(10, spotOp) * 2.55), 0, 0, 0)))
                {
                    foreach (RectangleF r in spot) rg.Exclude(r);
                    g.FillRegion(b, rg);
                }
            }
        }

        static void DrawDimension(Graphics g, Shape s, ScaleInfo sc, Color col, float w)
        {
            if (s.Pts.Count < 2) return;
            PointF p1 = s.Pts[0], p2 = s.Pts[1], a, b, n;
            float d;
            Geo.Dim(s, out a, out b, out n, out d);
            float L = Geo.Dist(p1, p2);
            float sign = d >= 0 ? 1f : -1f;
            string t = sc.Len(L);
            if (!string.IsNullOrEmpty(s.Text)) t = s.Text + " " + t;
            Font f = GetFont(s.FontSize, false);
            SizeF tsz = g.MeasureString(t, f);
            Color tcol = Color.FromArgb(Math.Max((int)col.A, 60), col);
            float ang = (float)(Math.Atan2(b.Y - a.Y, b.X - a.X) * 180.0 / Math.PI);
            if (ang > 90f) ang -= 180f; else if (ang <= -90f) ang += 180f;

            float hl = 0, hw = 0;
            Geo.HeadSize(w, L / 2.2f, out hl, out hw);
            float tick = hw * 1.15f;                       // end ticks cross the dimension line
            using (Pen pen = new Pen(col, w))
            {
                pen.StartCap = pen.EndCap = LineCap.Flat;
                // extension lines from the measured points out to (and a little past) the dimension line
                if (Math.Abs(d) > 3f)
                {
                    float gap = 3f, ext = tick;
                    g.DrawLine(pen, p1.X + n.X * sign * gap, p1.Y + n.Y * sign * gap, a.X + n.X * sign * ext, a.Y + n.Y * sign * ext);
                    g.DrawLine(pen, p2.X + n.X * sign * gap, p2.Y + n.Y * sign * gap, b.X + n.X * sign * ext, b.Y + n.Y * sign * ext);
                }
                if (L <= 0.5f) { g.DrawLine(pen, a, b); return; }
                PointF u = new PointF((b.X - a.X) / L, (b.Y - a.Y) / L);
                // end ticks
                g.DrawLine(pen, a.X - n.X * tick, a.Y - n.Y * tick, a.X + n.X * tick, a.Y + n.Y * tick);
                g.DrawLine(pen, b.X - n.X * tick, b.Y - n.Y * tick, b.X + n.X * tick, b.Y + n.Y * tick);
                // shaft between the heads, with a gap in the middle for the value when it fits
                float s0 = hl * 0.85f, s1 = L - hl * 0.85f;
                float gapHalf = tsz.Width / 2f + 4f + w;
                bool inline = s1 - s0 > 2f * gapHalf + 6f;
                if (s1 > s0)
                {
                    if (inline)
                    {
                        float m = L / 2f;
                        g.DrawLine(pen, a.X + u.X * s0, a.Y + u.Y * s0, a.X + u.X * (m - gapHalf), a.Y + u.Y * (m - gapHalf));
                        g.DrawLine(pen, a.X + u.X * (m + gapHalf), a.Y + u.Y * (m + gapHalf), a.X + u.X * s1, a.Y + u.Y * s1);
                    }
                    else g.DrawLine(pen, a.X + u.X * s0, a.Y + u.Y * s0, a.X + u.X * s1, a.Y + u.Y * s1);
                }
                using (SolidBrush br = new SolidBrush(col))
                {
                    g.FillPolygon(br, Head(a, new PointF(-u.X, -u.Y), hl, hw));
                    g.FillPolygon(br, Head(b, u, hl, hw));
                }
                if (inline) DrawRotLabel(g, t, Geo.Mid(a, b), ang, f, tcol, 0f, true);
                else DrawRotLabel(g, t, Geo.Mid(a, b), ang, f, tcol, hw + 2f, false);
            }
        }

        /// <summary>Text along a line. centred = in the line's gap; otherwise just above the line, off = distance.</summary>
        static void DrawRotLabel(Graphics g, string t, PointF at, float ang, Font f, Color col, float off, bool centred)
        {
            GraphicsState st = g.Save();
            g.TranslateTransform(at.X, at.Y);
            g.RotateTransform(ang);
            SizeF sz = g.MeasureString(t, f);
            RectangleF r = centred
                ? new RectangleF(-sz.Width / 2f - 2f, -sz.Height / 2f, sz.Width + 4f, sz.Height)
                : new RectangleF(-sz.Width / 2f - 2f, -sz.Height - off, sz.Width + 4f, sz.Height);
            if (!centred) using (SolidBrush bg = new SolidBrush(Color.FromArgb(170, 255, 255, 255))) g.FillRectangle(bg, r);
            using (SolidBrush b = new SolidBrush(col))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString(t, f, b, r, sf);
            }
            g.Restore(st);
        }

        public static void DrawBox(Graphics g, string t, PointF c, Font f, Color col)
        {
            SizeF sz = g.MeasureString(t, f);
            RectangleF r = new RectangleF(c.X - sz.Width / 2f - 4f, c.Y - sz.Height / 2f - 2f, sz.Width + 8f, sz.Height + 4f);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(215, 255, 255, 255))) g.FillRectangle(bg, r);
            using (Pen p = new Pen(col, 1f)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            using (SolidBrush b = new SolidBrush(col))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString(t, f, b, r, sf);
            }
        }

        static void DrawPixelate(Graphics g, Shape s, Bitmap img)
        {
            if (s.Pts.Count < 2 || img == null) return;
            Rectangle r = Rectangle.Round(Geo.Rect(s.Pts[0], s.Pts[1]));
            r.Intersect(new Rectangle(0, 0, img.Width, img.Height));
            if (r.Width < 1 || r.Height < 1) return;
            int block = Math.Max(3, (int)Math.Round(s.Width * 4));
            string key = r.ToString() + "|" + block + "|" + img.GetHashCode();
            if (s.Cache == null || s.CacheKey != key)
            {
                int sw = Math.Max(1, (r.Width + block - 1) / block), sh = Math.Max(1, (r.Height + block - 1) / block);
                Bitmap big = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
                using (Bitmap smallB = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g2 = Graphics.FromImage(smallB))
                    {
                        g2.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g2.DrawImage(img, new Rectangle(0, 0, sw, sh), r, GraphicsUnit.Pixel);
                    }
                    using (Graphics g3 = Graphics.FromImage(big))
                    {
                        g3.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g3.PixelOffsetMode = PixelOffsetMode.Half;
                        g3.DrawImage(smallB, new Rectangle(0, 0, sw * block, sh * block), new Rectangle(0, 0, sw, sh), GraphicsUnit.Pixel);
                    }
                }
                s.Cache = big;
                s.CacheKey = key;
            }
            GraphicsState st = g.Save();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(s.Cache, new Rectangle(r.X, r.Y, r.Width, r.Height), new Rectangle(0, 0, r.Width, r.Height), GraphicsUnit.Pixel);
            g.Restore(st);
        }

        /// <summary>Annotated image. background null keeps transparency (PNG); pass White for JPG/BMP/clipboard.</summary>
        public static Bitmap Flatten(Bitmap img, List<Shape> shapes, ScaleInfo sc, Color? background)
        {
            return Flatten(img, shapes, sc, background, null);
        }

        /// <summary>crop = the visible part of the image (adjustable crop); null = whole image.</summary>
        public static Bitmap Flatten(Bitmap img, List<Shape> shapes, ScaleInfo sc, Color? background, Rectangle? crop)
        {
            Rectangle cr = crop.HasValue ? crop.Value : new Rectangle(0, 0, img.Width, img.Height);
            Bitmap b = new Bitmap(cr.Width, cr.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(background.HasValue ? background.Value : Color.Transparent);
                g.TranslateTransform(-cr.X, -cr.Y);
                g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height), new Rectangle(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                DrawAll(g, shapes, img, sc, null);
            }
            return b;
        }
    }

    public enum HKind { Point, Box, Rotate, Center, DimOffset, Tail }

    public struct Handle
    {
        public PointF Pos;
        public HKind Kind;
        public int Index;
        public Handle(PointF pos, HKind kind, int index) { Pos = pos; Kind = kind; Index = index; }
    }

    public static class ShapeOps
    {
        public static RectangleF LocalRect(Shape s)
        {
            if (s.Kind == ShapeKind.Text) return new RectangleF(s.Pts[0], Renderer.TextSize(s));
            if (s.Pts.Count >= 2) return Geo.Rect(s.Pts[0], s.Pts[1]);
            return new RectangleF(s.Pts[0], SizeF.Empty);
        }

        public static PointF Center(Shape s)
        {
            if (s.Pts.Count == 0) return PointF.Empty;
            if (s.Kind == ShapeKind.Text || s.IsBox) return Geo.Center(LocalRect(s));
            if (s.Kind == ShapeKind.Counter) return s.Pts[0];
            if (s.IsPoly && s.Pts.Count >= 3) return Geo.Centroid(s.Pts);
            RectangleF b = RawBounds(s.Pts);
            return Geo.Center(b);
        }

        static RectangleF RawBounds(IList<PointF> pts)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (PointF p in pts) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
            return RectangleF.FromLTRB(x0, y0, x1, y1);
        }

        static PointF BoxPoint(RectangleF r, int i)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            switch (i)
            {
                case 0: return new PointF(r.X, r.Y);
                case 1: return new PointF(cx, r.Y);
                case 2: return new PointF(r.Right, r.Y);
                case 3: return new PointF(r.Right, cy);
                case 4: return new PointF(r.Right, r.Bottom);
                case 5: return new PointF(cx, r.Bottom);
                case 6: return new PointF(r.X, r.Bottom);
                default: return new PointF(r.X, cy);
            }
        }

        /// <summary>Corner points of the shape outline in image coordinates (rotation applied).</summary>
        public static PointF[] Outline(Shape s)
        {
            RectangleF r = LocalRect(s);
            PointF c = Center(s);
            float a = s.Rotatable ? s.Angle : 0f;
            return new PointF[] { Geo.Rot(BoxPoint(r, 0), c, a), Geo.Rot(BoxPoint(r, 2), c, a), Geo.Rot(BoxPoint(r, 4), c, a), Geo.Rot(BoxPoint(r, 6), c, a) };
        }

        /// <summary>Editable handles. rotDist = distance of the rotate knob above the shape, in image units.</summary>
        public static List<Handle> Handles(Shape s, float rotDist)
        {
            List<Handle> h = new List<Handle>();
            if (s.Pts.Count == 0) return h;
            switch (s.Kind)
            {
                case ShapeKind.Line:
                case ShapeKind.Arrow:
                    if (s.Pts.Count >= 2) { h.Add(new Handle(s.Pts[0], HKind.Point, 0)); h.Add(new Handle(s.Pts[1], HKind.Point, 1)); }
                    break;
                case ShapeKind.Angle:
                    for (int i = 0; i < s.Pts.Count; i++) h.Add(new Handle(s.Pts[i], HKind.Point, i));
                    break;
                case ShapeKind.Dimension:
                    if (s.Pts.Count >= 2)
                    {
                        PointF a, b, n; float d;
                        Geo.Dim(s, out a, out b, out n, out d);
                        h.Add(new Handle(s.Pts[0], HKind.Point, 0));
                        h.Add(new Handle(s.Pts[1], HKind.Point, 1));
                        h.Add(new Handle(Geo.Mid(a, b), HKind.DimOffset, 2));
                    }
                    break;
                case ShapeKind.Polygon:
                case ShapeKind.Area:
                case ShapeKind.Freehand:
                    {
                        if (s.Kind != ShapeKind.Freehand)
                            for (int i = 0; i < s.Pts.Count; i++) h.Add(new Handle(s.Pts[i], HKind.Point, i));
                        RectangleF b = RawBounds(s.Pts);
                        h.Add(new Handle(new PointF(b.X + b.Width / 2f, b.Y - rotDist), HKind.Rotate, 0));
                        break;
                    }
                case ShapeKind.Text:
                    {
                        RectangleF r = LocalRect(s);
                        PointF c = Center(s);
                        h.Add(new Handle(Geo.Rot(new PointF(c.X, r.Y - rotDist), c, s.Angle), HKind.Rotate, 0));
                        break;
                    }
                case ShapeKind.Counter:
                    break;
                default: // box shapes
                    {
                        RectangleF r = LocalRect(s);
                        PointF c = Center(s);
                        float a = s.Rotatable ? s.Angle : 0f;
                        for (int i = 0; i < 8; i++) h.Add(new Handle(Geo.Rot(BoxPoint(r, i), c, a), HKind.Box, i));
                        if (s.Rotatable) h.Add(new Handle(Geo.Rot(new PointF(c.X, r.Y - rotDist), c, a), HKind.Rotate, 0));
                        if (s.Kind == ShapeKind.Rect || s.Kind == ShapeKind.Ellipse || s.Kind == ShapeKind.Bubble) h.Add(new Handle(c, HKind.Center, 0));
                        if (s.Kind == ShapeKind.Bubble && s.Pts.Count > 2) h.Add(new Handle(s.Pts[2], HKind.Tail, 2));
                        break;
                    }
            }
            return h;
        }

        /// <summary>Apply a handle drag. orig = the shape as it was when the drag started.</summary>
        public static void DragHandle(Shape s, Shape orig, Handle h, PointF p, bool shift)
        {
            switch (h.Kind)
            {
                case HKind.Point:
                    if (h.Index < s.Pts.Count) s.Pts[h.Index] = p;
                    break;
                case HKind.DimOffset:
                case HKind.Tail:
                    if (s.Pts.Count < 3) s.Pts.Add(p); else s.Pts[2] = p;
                    break;
                case HKind.Center:
                    {
                        PointF c0 = Center(orig);
                        for (int i = 0; i < s.Pts.Count; i++) s.Pts[i] = new PointF(orig.Pts[i].X + p.X - c0.X, orig.Pts[i].Y + p.Y - c0.Y);
                        break;
                    }
                case HKind.Rotate:
                    {
                        PointF c = Center(orig);
                        float ang = (float)(Math.Atan2(p.Y - c.Y, p.X - c.X) * 180.0 / Math.PI) + 90f;
                        if (shift) ang = (float)(Math.Round(ang / 15.0) * 15.0);
                        ang = Geo.NormAngle(ang);
                        if (orig.Rotatable) s.Angle = ang;
                        else
                            for (int i = 0; i < s.Pts.Count; i++) s.Pts[i] = Geo.Rot(orig.Pts[i], c, ang);
                        break;
                    }
                case HKind.Box:
                    {
                        PointF c0 = Center(orig);
                        float a = orig.Rotatable ? orig.Angle : 0f;
                        PointF lp = Geo.Rot(p, c0, -a);
                        RectangleF r = LocalRect(orig);
                        float l = r.X, t = r.Y, rt = r.Right, bt = r.Bottom;
                        int i = h.Index;
                        if (i == 0 || i == 6 || i == 7) l = lp.X;
                        if (i == 2 || i == 3 || i == 4) rt = lp.X;
                        if (i == 0 || i == 1 || i == 2) t = lp.Y;
                        if (i == 4 || i == 5 || i == 6) bt = lp.Y;
                        // pictures keep their proportions on corners unless Shift is held; other boxes the other way round
                        bool keep = orig.Kind == ShapeKind.Image ? !shift : shift;
                        if (keep && (i % 2 == 0) && r.Width > 0 && r.Height > 0)
                        {
                            // keep proportions on corners
                            float ax = (i == 0 || i == 6) ? r.Right : r.X, ay = (i == 0 || i == 2) ? r.Bottom : r.Y;
                            float dx = lp.X - ax, dy = lp.Y - ay, ratio = r.Width / r.Height;
                            float nw = Math.Abs(dx), nh = Math.Abs(dy);
                            if (nw / ratio > nh) nh = nw / ratio; else nw = nh * ratio;
                            float ex = ax + (dx < 0 ? -nw : nw), ey = ay + (dy < 0 ? -nh : nh);
                            l = Math.Min(ax, ex); rt = Math.Max(ax, ex); t = Math.Min(ay, ey); bt = Math.Max(ay, ey);
                        }
                        RectangleF nr = RectangleF.FromLTRB(Math.Min(l, rt), Math.Min(t, bt), Math.Max(l, rt), Math.Max(t, bt));
                        PointF wc = Geo.Rot(Geo.Center(nr), c0, a);
                        s.Pts[0] = new PointF(wc.X - nr.Width / 2f, wc.Y - nr.Height / 2f);
                        s.Pts[1] = new PointF(wc.X + nr.Width / 2f, wc.Y + nr.Height / 2f);
                        break;
                    }
            }
        }

        public static RectangleF Bounds(Shape s)
        {
            if (s.Pts.Count == 0) return RectangleF.Empty;
            if (s.Kind == ShapeKind.Counter)
            {
                float r = Math.Max(8f, s.FontSize * 0.9f);
                return new RectangleF(s.Pts[0].X - r, s.Pts[0].Y - r, 2 * r, 2 * r);
            }
            List<PointF> pts = new List<PointF>();
            if (s.Kind == ShapeKind.Text || s.IsBox) pts.AddRange(Outline(s));
            else pts.AddRange(s.Pts);
            if (s.Kind == ShapeKind.Bubble && s.Pts.Count > 2) pts.Add(s.Pts[2]);
            if (s.Kind == ShapeKind.Dimension && s.Pts.Count >= 2)
            {
                PointF a, b, n; float d;
                Geo.Dim(s, out a, out b, out n, out d);
                pts.Add(a); pts.Add(b);
            }
            RectangleF rr = RawBounds(pts);
            rr.Inflate(s.Width / 2f + 1f, s.Width / 2f + 1f);
            return rr;
        }

        public static bool Hit(Shape s, PointF p, float tol)
        {
            if (s.Pts.Count == 0) return false;
            if (s.Rotatable && s.Angle != 0f) p = Geo.Rot(p, Center(s), -s.Angle);
            float t = tol + s.Width / 2f;
            switch (s.Kind)
            {
                case ShapeKind.Rect:
                    {
                        if (s.Pts.Count < 2) return false;
                        RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
                        RectangleF outer = r; outer.Inflate(t, t);
                        if (!outer.Contains(p)) return false;
                        if (s.FillOpacity > 0) return true;
                        RectangleF inner = r; inner.Inflate(-t, -t);
                        return !(inner.Width > 0 && inner.Height > 0 && inner.Contains(p));
                    }
                case ShapeKind.Pixelate:
                case ShapeKind.Highlight:
                case ShapeKind.Image:
                    {
                        if (s.Pts.Count < 2) return false;
                        RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
                        r.Inflate(tol, tol);
                        return r.Contains(p);
                    }
                case ShapeKind.Bubble:
                    {
                        if (s.Pts.Count < 2) return false;
                        using (GraphicsPath gp = Renderer.BubblePath(Geo.Rect(s.Pts[0], s.Pts[1]), s.Pts.Count > 2 ? (PointF?)s.Pts[2] : null))
                        using (Pen pen = new Pen(Color.Black, t * 2f))
                            return gp.IsVisible(p) || gp.IsOutlineVisible(p, pen);
                    }
                case ShapeKind.Ellipse:
                    {
                        if (s.Pts.Count < 2) return false;
                        RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
                        float rx = r.Width / 2f, ry = r.Height / 2f;
                        if (rx < 2 || ry < 2) { RectangleF o = r; o.Inflate(t, t); return o.Contains(p); }
                        double nx = (p.X - (r.X + rx)) / rx, ny = (p.Y - (r.Y + ry)) / ry;
                        double v = Math.Sqrt(nx * nx + ny * ny);
                        if (s.FillOpacity > 0 && v <= 1.0) return true;
                        return Math.Abs(v - 1.0) * Math.Min(rx, ry) <= t;
                    }
                case ShapeKind.Line:
                case ShapeKind.Arrow:
                    return s.Pts.Count >= 2 && Geo.SegDist(p, s.Pts[0], s.Pts[1]) <= t;
                case ShapeKind.Angle:
                    for (int i = 0; i + 1 < s.Pts.Count; i++) if (Geo.SegDist(p, s.Pts[i], s.Pts[i + 1]) <= t) return true;
                    return s.Pts.Count >= 2 && Geo.Dist(p, s.Pts[1]) <= s.FontSize * 2.5f;
                case ShapeKind.Freehand:
                    if (s.Pts.Count == 1) return Geo.Dist(p, s.Pts[0]) <= t;
                    for (int i = 0; i + 1 < s.Pts.Count; i++) if (Geo.SegDist(p, s.Pts[i], s.Pts[i + 1]) <= t) return true;
                    return false;
                case ShapeKind.Text:
                    {
                        RectangleF r = LocalRect(s); r.Inflate(tol, tol);
                        return r.Contains(p);
                    }
                case ShapeKind.Counter:
                    {
                        RectangleF r = Bounds(s); r.Inflate(tol, tol);
                        return r.Contains(p);
                    }
                case ShapeKind.Polygon:
                case ShapeKind.Area:
                    {
                        if (s.Pts.Count >= 3 && Geo.InPolygon(p, s.Pts)) return true;
                        for (int i = 0; i < s.Pts.Count; i++)
                            if (Geo.SegDist(p, s.Pts[i], s.Pts[(i + 1) % s.Pts.Count]) <= t) return true;
                        return false;
                    }
                case ShapeKind.Dimension:
                    {
                        if (s.Pts.Count < 2) return false;
                        PointF a, b, n; float d;
                        Geo.Dim(s, out a, out b, out n, out d);
                        if (Geo.SegDist(p, a, b) <= t + 2) return true;
                        if (Geo.SegDist(p, s.Pts[0], a) <= t || Geo.SegDist(p, s.Pts[1], b) <= t) return true;
                        return Geo.Dist(p, Geo.Mid(a, b)) <= s.FontSize + tol;
                    }
            }
            return false;
        }

        /// <summary>Move a shape along with an image rotate/flip/border operation.</summary>
        public static void Transform(Shape s, Func<PointF, PointF> map, float angleDelta, bool flip)
        {
            if (s.Rotatable)
            {
                RectangleF r = LocalRect(s);
                PointF c = map(Center(s));
                s.Angle = Geo.NormAngle(flip ? -s.Angle : s.Angle + angleDelta);
                s.Pts[0] = new PointF(c.X - r.Width / 2f, c.Y - r.Height / 2f);
                if (s.Pts.Count > 1) s.Pts[1] = new PointF(c.X + r.Width / 2f, c.Y + r.Height / 2f);
                return;
            }
            for (int i = 0; i < s.Pts.Count; i++) s.Pts[i] = map(s.Pts[i]);
            if (s.IsBox && s.Pts.Count >= 2)
            {
                RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
                s.Pts[0] = r.Location;
                s.Pts[1] = new PointF(r.Right, r.Bottom);
            }
        }
    }

    /// <summary>Whole-image effects. Each returns a new 32-bit bitmap (the original stays for undo).</summary>
    public static class Effects
    {
        public static Bitmap To32(Bitmap img)
        {
            Bitmap b = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
                g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height), new Rectangle(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
            return b;
        }

        static Bitmap Matrix(Bitmap img, ColorMatrix cm)
        {
            Bitmap b = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
            }
            return b;
        }

        public static Bitmap Grayscale(Bitmap img)
        {
            return Matrix(img, new ColorMatrix(new float[][] {
                new float[] { .299f, .299f, .299f, 0, 0 },
                new float[] { .587f, .587f, .587f, 0, 0 },
                new float[] { .114f, .114f, .114f, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 },
                new float[] { 0, 0, 0, 0, 1 } }));
        }

        public static Bitmap Invert(Bitmap img)
        {
            return Matrix(img, new ColorMatrix(new float[][] {
                new float[] { -1, 0, 0, 0, 0 },
                new float[] { 0, -1, 0, 0, 0 },
                new float[] { 0, 0, -1, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 },
                new float[] { 1, 1, 1, 0, 1 } }));
        }

        public static Bitmap Border(Bitmap img, int bw, Color c)
        {
            Bitmap b = new Bitmap(img.Width + 2 * bw, img.Height + 2 * bw, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(c);
                g.DrawImage(img, new Rectangle(bw, bw, img.Width, img.Height), new Rectangle(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
            }
            return b;
        }

        public const int ShadowOffset = 8, ShadowBlur = 10;

        public static Bitmap DropShadow(Bitmap img)
        {
            int off = ShadowOffset, blur = ShadowBlur;
            Bitmap b = new Bitmap(img.Width + off + blur, img.Height + off + blur, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                int step = Math.Max(1, 110 / (blur + 1));
                for (int i = 0; i <= blur; i++)
                {
                    Rectangle r = new Rectangle(off, off, img.Width, img.Height);
                    int inf = blur / 2 - i;
                    r.Inflate(inf, inf);
                    using (SolidBrush br = new SolidBrush(Color.FromArgb(step, 0, 0, 0))) g.FillRectangle(br, r);
                }
                g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height), new Rectangle(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
            }
            return b;
        }

        public static Bitmap TornEdge(Bitmap img)
        {
            int w = img.Width, h = img.Height, tooth = 14, depth = 9;
            Random rnd = new Random(w * 7919 + h);
            List<PointF> pts = new List<PointF>();
            for (int x = 0; x < w; x += tooth) pts.Add(new PointF(x, rnd.Next(0, depth)));
            for (int y = 0; y < h; y += tooth) pts.Add(new PointF(w - rnd.Next(0, depth), y));
            for (int x = w; x > 0; x -= tooth) pts.Add(new PointF(x, h - rnd.Next(0, depth)));
            for (int y = h; y > 0; y -= tooth) pts.Add(new PointF(rnd.Next(0, depth), y));
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            using (TextureBrush tb = new TextureBrush(img, WrapMode.Clamp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPolygon(tb, pts.ToArray());
            }
            return b;
        }

        public static Bitmap RotateFlip(Bitmap img, RotateFlipType t)
        {
            Bitmap b = To32(img);
            b.RotateFlip(t);
            return b;
        }
    }


    /// <summary>Everything a project holds besides the image and shapes.</summary>
    public class ProjectExtras
    {
        public Rectangle? Crop;
        public List<float> HGuides = new List<float>(), VGuides = new List<float>();
    }

    /// <summary>Editable project format (.snapm): the clean image plus every annotation, as XML.
    /// The same shape XML is used for copy / paste of shapes between editor windows.</summary>
    public static class ProjectFile
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const string ClipFormat = "SnapMeasure.Shapes.v1";
        static string C(Color c) { return c.ToArgb().ToString("X8"); }
        static Color PC(string s) { return Color.FromArgb(int.Parse(s, NumberStyles.HexNumber, Inv)); }
        static string F(double v) { return v.ToString("R", Inv); }
        static float PF(string s, float def) { float v; return float.TryParse(s, NumberStyles.Float, Inv, out v) ? v : def; }
        static int PI(string s, int def) { int v; return int.TryParse(s, NumberStyles.Integer, Inv, out v) ? v : def; }

        static void WriteShape(XmlWriter w, Shape s)
        {
            w.WriteStartElement("shape");
            w.WriteAttributeString("kind", s.Kind.ToString());
            w.WriteAttributeString("stroke", C(s.Stroke));
            w.WriteAttributeString("fill", C(s.Fill));
            w.WriteAttributeString("strokeOpacity", s.StrokeOpacity.ToString(Inv));
            w.WriteAttributeString("fillOpacity", s.FillOpacity.ToString(Inv));
            w.WriteAttributeString("width", F(s.Width));
            w.WriteAttributeString("font", F(s.FontSize));
            w.WriteAttributeString("angle", F(s.Angle));
            w.WriteAttributeString("shadow", s.Shadow ? "1" : "0");
            w.WriteAttributeString("mode", s.Mode.ToString(Inv));
            w.WriteAttributeString("bold", s.Bold ? "1" : "0");
            w.WriteAttributeString("italic", s.Italic ? "1" : "0");
            w.WriteAttributeString("align", s.Align.ToString(Inv));
            w.WriteAttributeString("heads", s.Heads.ToString(Inv));
            w.WriteAttributeString("text", s.Text ?? "");
            if (s.Kind == ShapeKind.Image && s.Img != null)
            {
                w.WriteStartElement("img");
                using (MemoryStream ms = new MemoryStream())
                {
                    s.Img.Save(ms, ImageFormat.Png);
                    w.WriteString(Convert.ToBase64String(ms.ToArray()));
                }
                w.WriteEndElement();
            }
            foreach (PointF p in s.Pts)
            {
                w.WriteStartElement("p");
                w.WriteAttributeString("x", F(p.X));
                w.WriteAttributeString("y", F(p.Y));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        // ---- input hardening: project files and clipboard data may come from anywhere ----
        public const int MaxImageSide = 30000;               // px
        public const long MaxImagePixels = 250L * 1000 * 1000;
        const float MaxCoord = 1000000f;
        const int MaxPoints = 200000;

        /// <summary>XML reader that refuses DTDs and never resolves external entities (no XXE).</summary>
        static XmlDocument SafeXml(TextReader src)
        {
            XmlReaderSettings rs = new XmlReaderSettings();
            rs.DtdProcessing = DtdProcessing.Prohibit;
            rs.XmlResolver = null;
            rs.MaxCharactersFromEntities = 1024;
            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;
            using (XmlReader r = XmlReader.Create(src, rs)) doc.Load(r);
            return doc;
        }

        /// <summary>Decodes a base64 PNG/JPG and checks its size before handing it to the app.</summary>
        public static Bitmap DecodeImage(string base64)
        {
            byte[] data = Convert.FromBase64String(base64);
            using (MemoryStream ms = new MemoryStream(data))
            using (Image tmp = Image.FromStream(ms, false, true))
            {
                CheckImageSize(tmp.Width, tmp.Height);
                return Effects.To32(new Bitmap(tmp));
            }
        }

        public static void CheckImageSize(int w, int h)
        {
            if (w < 1 || h < 1 || w > MaxImageSide || h > MaxImageSide || (long)w * h > MaxImagePixels)
                throw new InvalidDataException("The image is too large (" + w + " × " + h + " px).");
        }

        static float Finite(float v, float def, float lo, float hi)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return def;
            return Math.Max(lo, Math.Min(hi, v));
        }

        /// <summary>Repairs or rejects a loaded shape so bad data can't crash drawing. Returns false to drop it.</summary>
        public static bool Sanitize(Shape s)
        {
            if (!Enum.IsDefined(typeof(ShapeKind), s.Kind)) return false;
            if (s.Pts.Count > MaxPoints) return false;
            for (int i = 0; i < s.Pts.Count; i++)
            {
                PointF p = s.Pts[i];
                if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsInfinity(p.X) || float.IsInfinity(p.Y)) return false;
                s.Pts[i] = new PointF(Math.Max(-MaxCoord, Math.Min(MaxCoord, p.X)), Math.Max(-MaxCoord, Math.Min(MaxCoord, p.Y)));
            }
            int need;
            switch (s.Kind)
            {
                case ShapeKind.Text: case ShapeKind.Counter: case ShapeKind.Freehand: need = 1; break;
                case ShapeKind.Polygon: case ShapeKind.Area: case ShapeKind.Angle: need = 3; break;
                default: need = 2; break;
            }
            if (s.Pts.Count < need) return false;
            if (s.Kind == ShapeKind.Image && s.Img == null) return false;
            s.Width = Finite(s.Width, 2f, 0f, 200f);
            s.FontSize = Finite(s.FontSize, 16f, 4f, 1000f);
            s.Angle = Finite(s.Angle, 0f, -360f, 360f);
            s.StrokeOpacity = Math.Max(0, Math.Min(100, s.StrokeOpacity));
            s.FillOpacity = Math.Max(0, Math.Min(100, s.FillOpacity));
            s.Align = Math.Max(0, Math.Min(2, s.Align));
            s.Heads = Math.Max(0, Math.Min(3, s.Heads));
            if (s.Text != null && s.Text.Length > 100000) s.Text = s.Text.Substring(0, 100000);
            return true;
        }

        static Shape ReadShape(XmlElement e)
        {
            Shape s = new Shape();
            ShapeKind kind;
            string ks = e.GetAttribute("kind");
            // names only (no numbers), and only kinds this version knows
            if (ks.Length == 0 || char.IsDigit(ks[0]) || ks[0] == '-' || !Enum.TryParse(ks, false, out kind) || !Enum.IsDefined(typeof(ShapeKind), kind)) return null;
            s.Kind = kind;
            s.Stroke = PC(e.GetAttribute("stroke"));
            s.Fill = PC(e.GetAttribute("fill"));
            s.StrokeOpacity = PI(e.GetAttribute("strokeOpacity"), 100);
            s.FillOpacity = PI(e.GetAttribute("fillOpacity"), 0);
            s.Width = PF(e.GetAttribute("width"), 2f);
            s.FontSize = PF(e.GetAttribute("font"), 16f);
            s.Angle = PF(e.GetAttribute("angle"), 0f);
            s.Shadow = e.GetAttribute("shadow") == "1";
            s.Mode = PI(e.GetAttribute("mode"), 0);
            s.Bold = e.GetAttribute("bold") == "1";
            s.Italic = e.GetAttribute("italic") == "1";
            s.Align = PI(e.GetAttribute("align"), s.Kind == ShapeKind.Bubble ? 1 : 0);
            s.Heads = PI(e.GetAttribute("heads"), s.Kind == ShapeKind.Arrow ? 1 : 0);
            s.Text = e.GetAttribute("text");
            if (s.Kind == ShapeKind.Image)
            {
                XmlNode im = e.SelectSingleNode("img");
                if (im == null) return null;
                try { s.Img = DecodeImage(im.InnerText); } catch { return null; }
            }
            // v1 files: rectangles/ellipses/text only filled when the old "filled" flag was set
            if (e.HasAttribute("filled") && e.GetAttribute("filled") != "1" &&
                (s.Kind == ShapeKind.Rect || s.Kind == ShapeKind.Ellipse || s.Kind == ShapeKind.Text)) s.FillOpacity = 0;
            foreach (XmlElement p in e.SelectNodes("p"))
            {
                if (s.Pts.Count > MaxPoints) return null;
                s.Pts.Add(new PointF(PF(p.GetAttribute("x"), 0f), PF(p.GetAttribute("y"), 0f)));
            }
            try { s.Stroke = Color.FromArgb(255, s.Stroke); s.Fill = Color.FromArgb(255, s.Fill); } catch { }
            return Sanitize(s) ? s : null;
        }

        public static void Save(string path, Bitmap img, List<Shape> shapes, ScaleInfo sc)
        {
            Save(path, img, shapes, sc, new ProjectExtras());
        }

        public static void Save(string path, Bitmap img, List<Shape> shapes, ScaleInfo sc, ProjectExtras ex)
        {
            XmlWriterSettings set = new XmlWriterSettings();
            set.Indent = true;
            using (XmlWriter w = XmlWriter.Create(path, set))
            {
                w.WriteStartElement("snapmeasure");
                w.WriteAttributeString("version", "3");
                w.WriteAttributeString("unit", sc.Unit);
                w.WriteAttributeString("upp", F(sc.Upp));
                w.WriteAttributeString("decimals", sc.Decimals.ToString(Inv));
                w.WriteAttributeString("markerStart", sc.MarkerStart.ToString(Inv));
                w.WriteAttributeString("letterStart", sc.LetterStart.ToString(Inv));
                if (ex.Crop.HasValue)
                {
                    Rectangle r = ex.Crop.Value;
                    w.WriteAttributeString("crop", r.X + "," + r.Y + "," + r.Width + "," + r.Height);
                }
                w.WriteStartElement("image");
                using (MemoryStream ms = new MemoryStream())
                {
                    img.Save(ms, ImageFormat.Png);
                    w.WriteString(Convert.ToBase64String(ms.ToArray()));
                }
                w.WriteEndElement();
                foreach (Shape s in shapes) WriteShape(w, s);
                foreach (float y in ex.HGuides) { w.WriteStartElement("guide"); w.WriteAttributeString("axis", "h"); w.WriteAttributeString("v", F(y)); w.WriteEndElement(); }
                foreach (float x in ex.VGuides) { w.WriteStartElement("guide"); w.WriteAttributeString("axis", "v"); w.WriteAttributeString("v", F(x)); w.WriteEndElement(); }
                w.WriteEndElement();
            }
        }

        public static Bitmap Load(string path, List<Shape> shapes, ScaleInfo sc)
        {
            ProjectExtras ex;
            return Load(path, shapes, sc, out ex);
        }

        public static Bitmap Load(string path, List<Shape> shapes, ScaleInfo sc, out ProjectExtras ex)
        {
            ex = new ProjectExtras();
            XmlDocument doc;
            using (StreamReader sr = new StreamReader(path)) doc = SafeXml(sr);
            XmlElement root = doc.DocumentElement;
            if (root == null || root.Name != "snapmeasure") throw new InvalidDataException("Not a SnapMeasure project file.");
            sc.Unit = root.GetAttribute("unit");
            if (sc.Unit == "") sc.Unit = "px";
            double upp;
            sc.Upp = double.TryParse(root.GetAttribute("upp"), NumberStyles.Float, Inv, out upp) && upp > 0 ? upp : 1.0;
            sc.Decimals = PI(root.GetAttribute("decimals"), 2);
            sc.MarkerStart = PI(root.GetAttribute("markerStart"), 1);
            sc.LetterStart = PI(root.GetAttribute("letterStart"), 1);
            bool oldLetters = root.GetAttribute("markerLetters") == "1";     // v2: one series, either numbers or letters
            if (oldLetters) { sc.LetterStart = Math.Max(1, sc.MarkerStart); sc.MarkerStart = 1; }
            string[] cp = root.GetAttribute("crop").Split(',');
            int cx, cy, cw, ch;
            if (cp.Length == 4 && int.TryParse(cp[0], out cx) && int.TryParse(cp[1], out cy) && int.TryParse(cp[2], out cw) && int.TryParse(cp[3], out ch) && cw > 1 && ch > 1)
                ex.Crop = new Rectangle(cx, cy, cw, ch);
            if (sc.Upp <= 0 || double.IsNaN(sc.Upp) || double.IsInfinity(sc.Upp) || sc.Upp > 1e9) sc.Upp = 1.0;
            if (Array.IndexOf(ScaleInfo.Units, sc.Unit) < 0) sc.Unit = "px";
            sc.Decimals = Math.Max(0, Math.Min(6, sc.Decimals));
            sc.LetterStart = Math.Max(1, Math.Min(26, sc.LetterStart));
            XmlNode imgNode = root.SelectSingleNode("image");
            if (imgNode == null) throw new InvalidDataException("The project has no image.");
            Bitmap img = DecodeImage(imgNode.InnerText);
            foreach (XmlElement e in root.SelectNodes("shape"))
            {
                Shape s;
                try { s = ReadShape(e); } catch (Exception) { s = null; }
                if (s == null) continue;          // unknown or damaged shape: skip it, keep the rest
                if (oldLetters && s.Kind == ShapeKind.Counter) s.Mode = 1;
                shapes.Add(s);
            }
            foreach (XmlElement e in root.SelectNodes("guide"))
            {
                float v = PF(e.GetAttribute("v"), float.NaN);
                if (float.IsNaN(v)) continue;
                if (e.GetAttribute("axis") == "h") ex.HGuides.Add(v); else ex.VGuides.Add(v);
            }
            return img;
        }

        /// <summary>Shapes as XML text, for the clipboard.</summary>
        public static string ShapesToXml(IEnumerable<Shape> shapes)
        {
            StringBuilder sb = new StringBuilder();
            XmlWriterSettings set = new XmlWriterSettings();
            set.OmitXmlDeclaration = true;
            using (XmlWriter w = XmlWriter.Create(sb, set))
            {
                w.WriteStartElement("shapes");
                foreach (Shape s in shapes) WriteShape(w, s);
                w.WriteEndElement();
            }
            return sb.ToString();
        }

        public static List<Shape> ShapesFromXml(string xml)
        {
            List<Shape> list = new List<Shape>();
            if (string.IsNullOrEmpty(xml) || xml.Length > 200 * 1024 * 1024) return list;
            XmlDocument doc;
            using (StringReader sr = new StringReader(xml)) doc = SafeXml(sr);
            if (doc.DocumentElement == null) return list;
            foreach (XmlElement e in doc.DocumentElement.SelectNodes("shape"))
            {
                Shape s;
                try { s = ReadShape(e); } catch (Exception) { s = null; }
                if (s != null) list.Add(s);
            }
            return list;
        }
    }
}
