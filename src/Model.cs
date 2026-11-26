using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;

namespace SnapMeasure
{
    // new kinds are only ever added at the end (names are stored in .snapm files)
    public enum ShapeKind { Rect, Ellipse, Line, Arrow, Freehand, Text, Polygon, Dimension, Area, Counter, Pixelate, Highlight, Bubble, Angle, Image,
                            Cloud, Stamp, Note, Markup, Polyline, Perimeter, Coord, Origin, Arc, Radius }

    public enum Tool { Select, Rect, Ellipse, Line, Arrow, Freehand, Text, Polygon, Dimension, Area, Calibrate, Counter, Pixelate, Crop, Highlight, Bubble, Letter, Angle,
                       Cloud, Stamp, Note, Markup, Polyline, Perimeter, Cutout, Ocr, Coord, Marker, Arc, Radius, PolyDraw, Symbol, Fill }

    /// <summary>Focus modes. Grayscale = gray outside the shape, Pixelate = pixelate inside it; 1.3 adds the other side of each.</summary>
    public enum HighlightMode { Marker = 0, Spotlight = 1, Grayscale = 2, Magnify = 3, Pixelate = 4, GrayscaleInside = 5, PixelateOutside = 6, Blur = 7, BlurOutside = 8 }

    /// <summary>Outline style for lines and shapes. Cloud = scalloped outline.</summary>
    public enum LineStyle { Solid = 0, Dashed = 1, Dotted = 2, DashDot = 3, LongDash = 4, Cloud = 5 }

    /// <summary>Shape of a highlight area.</summary>
    public enum HighlightForm { Rectangle = 0, Ellipse = 1, Triangle = 2, Diamond = 3, Rounded = 4, Perimeter = 5 }

    /// <summary>Revision cloud callout: none, a revision triangle with a number, or a text box.</summary>
    public enum CloudCallout { None = 0, Triangle = 1, TextBox = 2 }

    /// <summary>Line ends for arrows, lines and dimensions.</summary>
    /// <summary>Line ends. 0–9 are the ones from 1.0–1.3 (their numbers never change, so old files look the same).
    /// 10 and up match AutoCAD's arrowhead list: Closed filled, Closed blank, Closed, Oblique, Open 30, Dot small,
    /// Dot small blank, Box, Datum triangle, Datum triangle filled. Dot = Dot, Slash = Architectural tick,
    /// Circle = Dot blank, Square = Box filled, Open = Open.</summary>
    public enum HeadStyle
    {
        None = 0, Filled = 1, Open = 2, Hollow = 3, Slash = 4, Tick = 5, Dot = 6, Circle = 7, Diamond = 8, Square = 9,
        ClosedFilled = 10, ClosedBlank = 11, Closed = 12, Oblique = 13, Open30 = 14, DotSmall = 15, DotSmallBlank = 16,
        Box = 17, DatumTriangle = 18, DatumTriangleFilled = 19,
        HalfClosed = 20, HalfOpen = 21          // 1.4: one-sided (harpoon) arrows
    }

    /// <summary>Text markup: drag over a line of text in the picture.</summary>
    public enum MarkupMode { Underline = 0, StrikeOut = 1, HighlightText = 2 }

    public static class Stamps
    {
        // the first four keep their place (stamp type is stored by number); new ones go before "Custom…"
        public static readonly string[] Names = { "APPROVED", "REVISE", "FOR INFORMATION", "REJECTED",
            "DRAFT", "FINAL", "FOR COMMENT", "FOR CONSTRUCTION", "NOT APPROVED", "CHECKED", "AS BUILT", "CONFIDENTIAL", "VOID",
            "AS IS", "DEPARTMENTAL", "EXPERIMENTAL", "EXPIRED", "FOR PUBLIC RELEASE", "NOT FOR PUBLIC RELEASE", "SOLD", "TOP SECRET", "Custom…" };
        static readonly Color Green = Color.FromArgb(30, 140, 60), Orange = Color.FromArgb(225, 115, 0), Blue = Color.FromArgb(0, 100, 200),
                              Red = Color.FromArgb(210, 30, 30), Purple = Color.FromArgb(90, 40, 160);
        public static readonly Color[] Colors = { Green, Orange, Blue, Red, Red, Red, Green, Green, Red, Green, Blue, Red, Red,
                                                  Red, Blue, Blue, Red, Green, Red, Blue, Red, Purple };
        public static int Custom { get { return Names.Length - 1; } }

        // the date line on a stamp
        public static readonly string[] DateFormats = { "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "ddd, d MMMM yyyy", "" };
        public static readonly string[] DateNames = { "2026-11-19", "19/11/2026", "11/19/2026", "Thu, 19 November 2026", "No date" };

        public static string FormatDate(DateTime d, int fmt)
        {
            fmt = Math.Max(0, Math.Min(DateFormats.Length - 1, fmt));
            return DateFormats[fmt].Length == 0 ? "" : d.ToString(DateFormats[fmt], CultureInfo.InvariantCulture);
        }

        /// <summary>Reads a stamp's date line back in any of the formats (so the format can be changed later).</summary>
        public static bool TryParseDate(string s, out DateTime d)
        {
            foreach (string f in DateFormats)
                if (f.Length > 0 && DateTime.TryParseExact((s ?? "").Trim(), f, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return true;
            d = DateTime.MinValue;
            return false;
        }
    }

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
        public bool Underline, Strikeout;   // Text and speech bubble (1.3)
        public string FontName;      // Text and speech bubble: font family; null = Segoe UI (1.4)
        public string FontFamilyName { get { return string.IsNullOrEmpty(FontName) ? Renderer.DefaultFont : FontName; } }
        public PointF LabelOffset;          // Area: where its label sits, from the middle of the area (1.3)
        public RectangleF LabelRect;        // Area: the label box as last drawn (for its handles; not saved)
        public int Align;            // 0 left, 1 centre, 2 right (Text, speech bubble)
        public int LabelAt;          // 1.4.2 Dimension: 0 value on the line, 1 above, 2 below (not Align: that is text alignment)
        public int VAlign;           // 1.4.2: 0 top, 1 middle, 2 bottom – where the text sits in a taller text box
        public int HeadStart;        // Line / Arrow / Dimension: HeadStyle at the first point
        public int HeadEnd = 1;      // … and at the second point
        public Bitmap Img;           // Image shapes: the pasted picture (shared between undo copies, never modified)
        public List<List<PointF>> Holes;   // Area: cut-outs (openings) subtracted from the area; null = none
        public int Dash;             // LineStyle of the outline
        public int Form;             // HighlightForm for Highlight shapes
        public bool Frame;           // Text: draw a frame round the text in the line colour and width
        public int Strength;         // Focus magnify / pixelate: 1..10; 0 = older files (pixel size from the line width, magnify ×2)
        public int FromArea;         // open length made with the Area tool (Closed unticked): its Show setting + 1, so ticking Closed gives the area back

        /// <summary>Pixelate block size in picture pixels.</summary>
        /// <summary>Blur radius in picture pixels: strength 1 = 3 px … 10 = 30 px (0 = older shapes: 9 px).</summary>
        public int BlurRadius { get { return Strength > 0 ? Strength * 3 : 9; } }
        public int PixelBlock { get { return Strength > 0 ? Math.Max(3, Strength * 4) : Math.Max(3, (int)Math.Round(Width * 4)); } }
        /// <summary>Magnify factor: strength 1 = ×1.5 … 10 = ×6 (2 = ×2).</summary>
        public float MagnifyFactor { get { return Strength > 0 ? 1f + Strength * 0.5f : 2f; } }
        /// <summary>The strength the slider shows (older shapes: worked out from what they draw).</summary>
        public int ShownStrength(bool magnify)
        {
            if (Strength > 0) return Math.Min(10, Strength);
            return magnify ? 2 : Math.Max(1, Math.Min(10, (int)Math.Round(PixelBlock / 4.0)));
        }

        /// <summary>Old on/off heads (1 = end, 2 = start), kept for files from 1.0.</summary>
        public int Heads
        {
            get { return (HeadEnd > 0 ? 1 : 0) | (HeadStart > 0 ? 2 : 0); }
            set
            {
                HeadEnd = (value & 1) != 0 ? (HeadEnd > 0 ? HeadEnd : (int)HeadStyle.Filled) : 0;
                HeadStart = (value & 2) != 0 ? (HeadStart > 0 ? HeadStart : (int)HeadStyle.Filled) : 0;
            }
        }

        public FontStyle Style
        {
            get
            {
                return (Bold ? FontStyle.Bold : FontStyle.Regular) | (Italic ? FontStyle.Italic : FontStyle.Regular) |
                       (Underline ? FontStyle.Underline : FontStyle.Regular) | (Strikeout ? FontStyle.Strikeout : FontStyle.Regular);
            }
        }
        public bool HasTextStyle { get { return Kind == ShapeKind.Text || Kind == ShapeKind.Bubble; } }
        public bool IsLetterMarker { get { return Kind == ShapeKind.Counter && Mode == 1; } }
        /// <summary>1.4.2: a marker showing any character the user typed (in Text); it does not count up.</summary>
        public bool IsSymbolMarker { get { return Kind == ShapeKind.Counter && Mode == 2; } }
        /// <summary>A marker that counts: numbers or letters (not a symbol marker).</summary>
        public bool IsCountingMarker { get { return Kind == ShapeKind.Counter && Mode != 2; } }

        // render cache (pixelate); not serialized
        public Bitmap Cache;
        public string CacheKey;

        public Shape Clone()
        {
            Shape s = (Shape)MemberwiseClone();
            s.Pts = new List<PointF>(Pts);
            if (Holes != null) s.Holes = Holes.ConvertAll(h => new List<PointF>(h));
            s.Cache = null; s.CacheKey = null;     // each copy builds its own render cache (never share a bitmap)
            return s;
        }

        public void Offset(float dx, float dy)
        {
            for (int i = 0; i < Pts.Count; i++) Pts[i] = new PointF(Pts[i].X + dx, Pts[i].Y + dy);
            if (Holes != null)
                foreach (List<PointF> h in Holes)
                    for (int i = 0; i < h.Count; i++) h[i] = new PointF(h[i].X + dx, h[i].Y + dy);
        }

        /// <summary>Applies a point mapping to the outline and any cut-outs.</summary>
        public void MapPoints(Func<PointF, PointF> map)
        {
            for (int i = 0; i < Pts.Count; i++) Pts[i] = map(Pts[i]);
            if (Holes != null)
                foreach (List<PointF> h in Holes)
                    for (int i = 0; i < h.Count; i++) h[i] = map(h[i]);
        }

        /// <summary>Shapes built by clicking corners one after the other.</summary>
        public bool IsPoly { get { return Kind == ShapeKind.Polygon || Kind == ShapeKind.Area || Kind == ShapeKind.Polyline || Kind == ShapeKind.Perimeter || IsFocusPoly; } }
        /// <summary>Focus drawn as a perimeter: its corners clicked one by one, like an area (Form = Perimeter).</summary>
        public bool IsFocusPoly { get { return Kind == ShapeKind.Highlight && Form == (int)HighlightForm.Perimeter; } }
        /// <summary>Open path: path length, or a perimeter with "Closed" switched off (Mode 1).</summary>
        /// <summary>Area shape: Mode 0 shows area and perimeter, 1 area only, 2 perimeter only.</summary>
        public bool ShowsArea { get { return Kind == ShapeKind.Area && Mode != 2; } }
        public bool ShowsPerimeter { get { return Kind == ShapeKind.Area ? Mode != 1 : Kind == ShapeKind.Perimeter; } }
        public bool IsOpenPath { get { return Kind == ShapeKind.Polyline || ((Kind == ShapeKind.Perimeter || Kind == ShapeKind.Polygon) && Mode == 1); } }
        public bool HasOutlineStyle
        {
            get
            {
                return Kind == ShapeKind.Rect || Kind == ShapeKind.Ellipse || Kind == ShapeKind.Line || Kind == ShapeKind.Arrow || Kind == ShapeKind.Freehand ||
                       Kind == ShapeKind.Polygon || Kind == ShapeKind.Area || Kind == ShapeKind.Polyline || Kind == ShapeKind.Perimeter || Kind == ShapeKind.Arc ||
                       Kind == ShapeKind.Text;          // the text's frame (line width above 0)
            }
        }
        /// <summary>Cloud (scalloped) outlines only make sense round a closed shape.</summary>
        public bool CanBeCloud
        {
            get
            {
                return Kind == ShapeKind.Rect || Kind == ShapeKind.Ellipse || Kind == ShapeKind.Area || Kind == ShapeKind.Text ||
                       ((Kind == ShapeKind.Polygon || Kind == ShapeKind.Perimeter) && !IsOpenPath);
            }
        }

        public bool IsBox
        {
            get
            {
                return Kind == ShapeKind.Rect || Kind == ShapeKind.Ellipse || Kind == ShapeKind.Pixelate || (Kind == ShapeKind.Highlight && !IsFocusPoly) || Kind == ShapeKind.Bubble ||
                       Kind == ShapeKind.Image || Kind == ShapeKind.Cloud || Kind == ShapeKind.Stamp || Kind == ShapeKind.Markup;
            }
        }
        /// <summary>Shapes with a green rotate knob. A revision cloud turns when it has no callout (a callout's leader stays put).</summary>
        public bool Rotatable
        {
            get
            {
                return Kind == ShapeKind.Rect || Kind == ShapeKind.Ellipse || Kind == ShapeKind.Text || Kind == ShapeKind.Image || Kind == ShapeKind.Stamp ||
                       (Kind == ShapeKind.Cloud && Mode == 0) || (Kind == ShapeKind.Highlight && !IsFocusPoly);
            }
        }
        public bool HasHeads
        {
            get
            {
                return Kind == ShapeKind.Line || Kind == ShapeKind.Arrow || Kind == ShapeKind.Dimension || (Kind == ShapeKind.Freehand && !IsMarkerPen) ||
                       (Kind == ShapeKind.Polygon && Mode == 1);       // an open polyline drawn with the Polygon tool
            }
        }
        /// <summary>Highlighter pen: a freehand stroke drawn as a see-through marker (Mode 1).</summary>
        public bool IsMarkerPen { get { return Kind == ShapeKind.Freehand && Mode == 1; } }
        public bool IsMeasure
        {
            get
            {
                return Kind == ShapeKind.Dimension || Kind == ShapeKind.Area || Kind == ShapeKind.Angle || Kind == ShapeKind.Polyline || Kind == ShapeKind.Perimeter ||
                       Kind == ShapeKind.Coord || Kind == ShapeKind.Radius;
            }
        }
        public bool CanShadow
        {
            get
            {
                return Kind != ShapeKind.Dimension && Kind != ShapeKind.Area && Kind != ShapeKind.Pixelate && Kind != ShapeKind.Highlight &&
                       Kind != ShapeKind.Note && Kind != ShapeKind.Markup && Kind != ShapeKind.Polyline && Kind != ShapeKind.Perimeter &&
                       Kind != ShapeKind.Coord && Kind != ShapeKind.Origin;
            }
        }
    }

    /// <summary>Document-level settings: measuring scale and marker numbering.</summary>
    public class ScaleInfo
    {
        public double Upp = 1.0;      // real units per image pixel
        public string Unit = "px";
        public int Decimals = 2;
        public int MarkerStart = 1;   // first number of the number-marker series
        public int LetterStart = 1;   // first letter of the letter-marker series (1 = A)
        public string NumberLabel = "", LetterLabel = "";   // what each marker series counts, e.g. "Blind panel"
        public int Rounding;          // 0 = nearest, 1 = always up (ceiling), 2 = always down (floor), at 'Decimals'
        public bool LetterReverse, MarkerReverse;   // marker series counting down (Z, Y, X … / 10, 9, 8 …)
        public double OriginX, OriginY;             // 1.4.2: the origin's own coordinates (real units), e.g. 1250 ; 300 to match a drawing grid

        public ScaleInfo Clone() { return (ScaleInfo)MemberwiseClone(); }
        public bool Calibrated { get { return !(Unit == "px" && Math.Abs(Upp - 1.0) < 1e-12); } }

        /// <summary>A real value rounded the chosen way at 'Decimals' (up / down); nearest is left to the number format.</summary>
        public double Rnd(double v) { return RoundTo(v, Decimals, Rounding); }

        public static double RoundTo(double v, int decimals, int mode)
        {
            if (mode == 0 || double.IsNaN(v) || double.IsInfinity(v)) return v;
            double f = Math.Pow(10, Math.Max(0, Math.Min(10, decimals)));
            double x = v * f;
            // floating-point noise: 2.30 kept as 2.3000000001 must not round up to 2.31 (nor 2.2999999 down to 2.29)
            double c = Math.Round(x);
            if (Math.Abs(x - c) < 1e-7 * Math.Max(1.0, Math.Abs(x))) x = c;
            x = mode == 1 ? Math.Ceiling(x) : Math.Floor(x);
            double r = x / f;
            return r == 0 ? 0 : r;     // no "-0.00"
        }

        public string Fmt(double real) { return Rnd(real).ToString("N" + Decimals, CultureInfo.CurrentCulture); }
        public string Len(double px) { return Fmt(px * Upp) + " " + Unit; }
        public string Num(double px) { return Fmt(px * Upp); }

        /// <summary>Coordinates of p from the origin o, Y up (like CAD).</summary>
        public string Coord(PointF p, PointF o) { return "X " + Fmt(CoordX(p, o)) + "   Y " + Fmt(CoordY(p, o)) + (Unit == "px" ? " px" : " " + Unit); }
        /// <summary>Real X / Y of p: from the origin o (Y up, like CAD), plus the origin's own coordinates.</summary>
        public double CoordX(PointF p, PointF o) { return (p.X - o.X) * Upp + OriginX; }
        public double CoordY(PointF p, PointF o) { return (o.Y - p.Y) * Upp + OriginY; }
        public string Area(double px2) { return Fmt(px2 * Upp * Upp) + " " + Unit + "²"; }
        public string Describe()
        {
            if (!Calibrated) return "Scale: not set (pixels)";
            return "Scale: 1 px = " + PixelSize();
        }

        /// <summary>One pixel's size, readable: in the next smaller unit when tiny (0.00996667 m → 9.97 mm),
        /// three significant digits (the exact value stays in the panel's box).</summary>
        public string PixelSize()
        {
            double v = Upp;
            string u = Unit;
            if (u == "m" && v < 0.1) { v *= 1000; u = "mm"; }
            else if (u == "cm" && v < 1) { v *= 10; u = "mm"; }
            else if (u == "ft" && v < 0.1) { v *= 12; u = "in"; }
            CultureInfo cc = CultureInfo.CurrentCulture;
            string n = v >= 1000 ? v.ToString("N0", cc) : v >= 0.001 ? v.ToString("G3", cc) : v.ToString("G2", cc);
            return n + " " + u;
        }

        public string MarkerLabel(int index, bool letters)
        {
            if (letters)
            {
                int first = Math.Max(1, LetterStart);
                if (!LetterReverse) return LetterName(first - 1 + index);     // … Y, Z, AA, AB … AZ, BA … like spreadsheet columns
                int n = first - index;                                        // counting down: Z, Y, X … A, then round again from Z
                if (n < 1) n = ((n - 1) % 26 + 26) % 26 + 1;
                return LetterName(n - 1);
            }
            if (MarkerReverse) return Math.Max(1, MarkerStart - index).ToString(CultureInfo.InvariantCulture);   // 10, 9, 8 … 1
            return (MarkerStart + index).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>0 = A, 25 = Z, 26 = AA, 27 = AB … 701 = ZZ, 702 = AAA.</summary>
        public static string LetterName(int n)
        {
            if (n < 0) n = 0;
            string s = "";
            n++;
            while (n > 0) { int r = (n - 1) % 26; s = (char)('A' + r) + s; n = (n - 1) / 26; }
            return s;
        }

        /// <summary>The number of a letter label (A = 1, Z = 26, AA = 27); 0 if it is not letters.</summary>
        public static int LetterNumber(string s)
        {
            s = (s ?? "").Trim().ToUpperInvariant();
            if (s.Length == 0 || s.Length > 4) return 0;
            int n = 0;
            foreach (char ch in s) { if (ch < 'A' || ch > 'Z') return 0; n = n * 26 + (ch - 'A' + 1); }
            return n;
        }


        public static string AngleText(double deg) { return deg.ToString("N1", CultureInfo.CurrentCulture) + "°"; }

        /// <summary>An angle shown with the panel's Decimals and Round (ceil / floor) settings, like the lengths.</summary>
        public string Angle(double deg) { return Fmt(deg) + "°"; }

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

        /// <summary>The circle through three points. False when they lie (almost) on one line.</summary>
        public static bool Circle3(PointF a, PointF b, PointF c, out PointF centre, out float radius)
        {
            double d = 2.0 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
            centre = PointF.Empty; radius = 0;
            double scale = Math.Max(1.0, Math.Max(Dist(a, b), Math.Max(Dist(b, c), Dist(a, c))));
            if (Math.Abs(d) < 1e-6 * scale * scale) return false;
            double a2 = a.X * a.X + a.Y * a.Y, b2 = b.X * b.X + b.Y * b.Y, c2 = c.X * c.X + c.Y * c.Y;
            double ux = (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d;
            double uy = (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d;
            centre = new PointF((float)ux, (float)uy);
            radius = Dist(centre, a);
            return radius < 1e6f;
        }

        /// <summary>Start angle and sweep (radians) of the arc from a through b to c around 'centre'.</summary>
        static void ArcAngles(PointF a, PointF b, PointF c, PointF centre, out double start, out double sweep)
        {
            start = Math.Atan2(a.Y - centre.Y, a.X - centre.X);
            double tb = Math.Atan2(b.Y - centre.Y, b.X - centre.X), tc = Math.Atan2(c.Y - centre.Y, c.X - centre.X);
            Func<double, double> norm = v => { while (v < 0) v += 2 * Math.PI; while (v >= 2 * Math.PI) v -= 2 * Math.PI; return v; };
            double toC = norm(tc - start), toB = norm(tb - start);
            sweep = toB <= toC ? toC : toC - 2 * Math.PI;     // go the way that passes through b
        }

        /// <summary>Points along the arc from a through b to c (a straight line a–c when the three are in line).</summary>
        public static List<PointF> ArcPoints(PointF a, PointF b, PointF c, int n)
        {
            List<PointF> r = new List<PointF>();
            PointF o; float rad;
            if (!Circle3(a, b, c, out o, out rad)) { r.Add(a); r.Add(c); return r; }
            double start, sweep;
            ArcAngles(a, b, c, o, out start, out sweep);
            for (int i = 0; i <= n; i++)
            {
                double t = start + sweep * i / n;
                r.Add(new PointF(o.X + rad * (float)Math.Cos(t), o.Y + rad * (float)Math.Sin(t)));
            }
            return r;
        }

        /// <summary>Length along the arc from a through b to c.</summary>
        public static double ArcLength(PointF a, PointF b, PointF c)
        {
            PointF o; float rad;
            if (!Circle3(a, b, c, out o, out rad)) return Dist(a, c);
            double start, sweep;
            ArcAngles(a, b, c, o, out start, out sweep);
            return Math.Abs(sweep) * rad;
        }

        /// <summary>Unit vector from a to b (x axis if they coincide).</summary>
        public static PointF Unit(PointF a, PointF b)
        {
            float L = Dist(a, b);
            return L < 0.0001f ? new PointF(1, 0) : new PointF((b.X - a.X) / L, (b.Y - a.Y) / L);
        }

        /// <summary>The point 'd' along a path from its start (or its end).</summary>
        public static PointF PointAlong(List<PointF> p, bool fromStart, float d)
        {
            int n = p.Count;
            if (n == 0) return PointF.Empty;
            PointF prev = fromStart ? p[0] : p[n - 1];
            float run = 0;
            for (int i = 1; i < n; i++)
            {
                PointF q = fromStart ? p[i] : p[n - 1 - i];
                float seg = Dist(prev, q);
                if (run + seg >= d && seg > 0)
                {
                    float t = (d - run) / seg;
                    return new PointF(prev.X + (q.X - prev.X) * t, prev.Y + (q.Y - prev.Y) * t);
                }
                run += seg; prev = q;
            }
            return prev;
        }

        /// <summary>The path with 'cutStart' cut off its start and 'cutEnd' off its end (both measured along it).</summary>
        public static List<PointF> TrimPath(List<PointF> p, float cutStart, float cutEnd)
        {
            List<PointF> r = new List<PointF>();
            if (p.Count < 2) { r.AddRange(p); return r; }
            float total = (float)Perimeter(p, false);
            float a = Math.Max(0, cutStart), b = Math.Max(a, total - Math.Max(0, cutEnd));
            r.Add(PointAlong(p, true, a));
            float run = 0;
            for (int i = 1; i < p.Count; i++)
            {
                run += Dist(p[i - 1], p[i]);
                if (run > a && run < b) r.Add(p[i]);
            }
            r.Add(PointAlong(p, true, b));
            return r;
        }

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

    /// <summary>One line of the measurements list / Excel export.</summary>
    public class MeasureRow
    {
        public Shape Shape;          // null for marker counts
        public string Name, Type, Unit, Label;
        public double Value;         // in real units (or degrees / count)
        public double? Perimeter, Gross, Cutouts, X, Y;
        public string Display, Extra;
    }

    /// <summary>Quantities: net areas with cut-outs, path lengths, marker counts, and the rows for the panel and Excel.</summary>
    public static class Measure
    {
        public static double HolesArea(Shape s)
        {
            double a = 0;
            if (s.Holes != null) foreach (List<PointF> h in s.Holes) if (h.Count >= 3) a += Math.Abs(Geo.SignedArea(h));
            return a;
        }

        public static double GrossArea(Shape s) { return Math.Abs(Geo.SignedArea(s.Pts)); }
        public static double NetArea(Shape s) { return Math.Max(0, GrossArea(s) - HolesArea(s)); }

        public static int Count(List<Shape> shapes, bool letters)
        {
            int n = 0;
            foreach (Shape s in shapes) if (s.IsCountingMarker && s.IsLetterMarker == letters) n++;
            return n;
        }

        public static List<MeasureRow> Rows(List<Shape> shapes, ScaleInfo sc, bool withNotes)
        {
            List<MeasureRow> rows = new List<MeasureRow>();
            Dictionary<Shape, string> names = Renderer.Labels(shapes, sc);
            double u = sc.Upp;
            Shape origin = Renderer.Origin(shapes);
            foreach (Shape s in shapes)
            {
                MeasureRow r = new MeasureRow();
                r.Shape = s;
                r.Label = s.Text ?? "";
                string n;
                names.TryGetValue(s, out n);
                r.Name = n ?? "";
                switch (s.Kind)
                {
                    case ShapeKind.Dimension:
                        if (s.Pts.Count < 2) continue;
                        r.Type = "Length"; r.Unit = sc.Unit;
                        r.Value = Geo.Dist(s.Pts[0], s.Pts[1]) * u;
                        r.Display = sc.Len(Geo.Dist(s.Pts[0], s.Pts[1]));
                        break;
                    case ShapeKind.Polyline:
                    case ShapeKind.Perimeter:
                        if (s.IsOpenPath)
                        {
                            if (s.Pts.Count < 2) continue;
                            r.Type = "Path length"; r.Unit = sc.Unit;
                            r.Value = Geo.Perimeter(s.Pts, false) * u;
                            r.Display = sc.Len(Geo.Perimeter(s.Pts, false));
                            r.Extra = (s.Pts.Count - 1) + " segments";
                            break;
                        }
                        if (s.Pts.Count < 3) continue;
                        r.Type = "Perimeter"; r.Unit = sc.Unit;
                        r.Value = Geo.Perimeter(s.Pts, true) * u;
                        r.Display = sc.Len(Geo.Perimeter(s.Pts, true));
                        break;
                    case ShapeKind.Radius:
                        {
                            if (s.Pts.Count < 3) continue;
                            PointF o; float rad;
                            if (!Geo.Circle3(s.Pts[0], s.Pts[1], s.Pts[2], out o, out rad)) continue;
                            r.Type = "Radius"; r.Unit = sc.Unit;
                            r.Value = rad * u;
                            r.Display = sc.Len(rad);
                            r.Perimeter = Geo.ArcLength(s.Pts[0], s.Pts[1], s.Pts[2]) * u;     // the arc's length
                            r.Extra = "Ø " + sc.Len(2 * rad) + " · arc " + sc.Len((float)Geo.ArcLength(s.Pts[0], s.Pts[1], s.Pts[2]));
                            if (s.Mode == 2 || s.Mode == 3)
                            {
                                // the label shows the diameter: list the diameter
                                r.Type = "Diameter"; r.Value = 2 * rad * u; r.Display = sc.Len(2 * rad);
                                r.Extra = "R " + sc.Len(rad) + " · arc " + sc.Len((float)Geo.ArcLength(s.Pts[0], s.Pts[1], s.Pts[2]));
                            }
                            else if (s.Mode == 6)
                            {
                                // the label shows only the arc length: list the arc length
                                double arc = Geo.ArcLength(s.Pts[0], s.Pts[1], s.Pts[2]);
                                r.Type = "Arc length"; r.Value = arc * u; r.Display = sc.Len((float)arc);
                                r.Extra = "R " + sc.Len(rad) + " · Ø " + sc.Len(2 * rad);
                            }
                            break;
                        }
                    case ShapeKind.Angle:
                        if (s.Pts.Count < 3) continue;
                        r.Type = "Angle"; r.Unit = "°";
                        r.Value = Geo.AngleAt(s.Pts[0], s.Pts[1], s.Pts[2]);
                        r.Display = sc.Angle(r.Value);
                        break;
                    case ShapeKind.Area:
                        {
                            if (s.Pts.Count < 3) continue;
                            if (!s.ShowsArea)
                            {
                                r.Type = "Perimeter"; r.Unit = sc.Unit;
                                r.Value = Geo.Perimeter(s.Pts, true) * u;
                                r.Display = sc.Len(Geo.Perimeter(s.Pts, true));
                                break;
                            }
                            r.Type = "Area"; r.Unit = sc.Unit + "²";
                            double net = NetArea(s), holes = HolesArea(s);
                            r.Value = net * u * u;
                            r.Display = sc.Area(net);
                            r.Perimeter = Geo.Perimeter(s.Pts, true) * u;
                            r.Extra = "P " + sc.Len(Geo.Perimeter(s.Pts, true));
                            if (holes > 0)
                            {
                                r.Gross = GrossArea(s) * u * u;
                                r.Cutouts = holes * u * u;
                                r.Type = "Area (net)";
                                r.Extra = "gross " + sc.Area(GrossArea(s));
                            }
                            break;
                        }
                    case ShapeKind.Coord:
                        {
                            r.Type = "Point"; r.Unit = sc.Unit;
                            if (origin == null) { r.Display = "no origin"; break; }
                            PointF p = s.Pts[0], o = origin.Pts[0];
                            r.X = sc.CoordX(p, o); r.Y = sc.CoordY(p, o);
                            r.Display = sc.Fmt(r.X.Value) + " ; " + sc.Fmt(r.Y.Value);
                            r.Extra = "X ; Y";
                            break;
                        }
                    case ShapeKind.Note:
                        if (!withNotes) continue;
                        r.Type = "Note"; r.Unit = ""; r.Name = "Note";
                        r.Display = "";
                        break;
                    default:
                        continue;
                }
                rows.Add(r);
            }
            // marker series totals
            for (int pass = 0; pass < 2; pass++)
            {
                bool letters = pass == 1;
                int c = Count(shapes, letters);
                if (c == 0) continue;
                MeasureRow r = new MeasureRow();
                r.Name = letters ? "Letters" : "Numbers";
                r.Type = "Count"; r.Unit = "nr";
                r.Value = c;
                r.Display = c + " nr";
                r.Label = letters ? sc.LetterLabel : sc.NumberLabel;
                r.Extra = letters ? sc.MarkerLabel(0, true) + "–" + sc.MarkerLabel(c - 1, true)
                                  : sc.MarkerLabel(0, false) + "–" + sc.MarkerLabel(c - 1, false);
                rows.Add(r);
            }
            // 1.4.2 symbol markers: one row per symbol, with how many there are
            Dictionary<string, int> sym = new Dictionary<string, int>();
            List<string> order = new List<string>();
            foreach (Shape s in shapes)
                if (s.IsSymbolMarker)
                {
                    string k = string.IsNullOrEmpty(s.Text) ? "?" : s.Text;
                    if (!sym.ContainsKey(k)) { sym[k] = 0; order.Add(k); }
                    sym[k]++;
                }
            foreach (string k in order)
            {
                MeasureRow r = new MeasureRow();
                r.Name = k;
                r.Type = "Symbol marker"; r.Unit = "nr";
                r.Value = sym[k];
                r.Display = sym[k] + " nr";
                r.Label = "";
                rows.Add(r);
            }
            return rows;
        }

        /// <summary>Tab-separated text for Excel: one row per measurement, count and (optionally) note.</summary>
        public static string ExcelText(List<Shape> shapes, ScaleInfo sc)
        {
            CultureInfo cc = CultureInfo.CurrentCulture;
            string fmt = "F" + sc.Decimals;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Name\tType\tValue\tUnit\tPerimeter\tGross\tCut-outs\tLabel\tX\tY");
            foreach (MeasureRow r in Rows(shapes, sc, true))
            {
                string val = r.Type == "Note" || r.Type == "Point" ? "" : (r.Type == "Count" || r.Type == "Symbol marker" ? r.Value.ToString("F0", cc) : sc.Rnd(r.Value).ToString(fmt, cc));
                Func<double?, string> o = v => v.HasValue ? sc.Rnd(v.Value).ToString(fmt, cc) : "";
                string label = (r.Label ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
                sb.AppendLine(r.Name + "\t" + r.Type + "\t" + val + "\t" + r.Unit + "\t" + o(r.Perimeter) + "\t" + o(r.Gross) + "\t" + o(r.Cutouts) + "\t" + label +
                              "\t" + o(r.X) + "\t" + o(r.Y));
            }
            return sb.ToString();
        }
    }

    public static class Renderer
    {
        /// <summary>The radius label. Mode: 0 = radius + diameter (files before 1.3.2), 1 = radius, 2 = diameter,
        /// 3 = diameter + arc length, 4 = radius + arc length, 5 = none, 6 = arc length, 7 = all three.</summary>
        public static string RadiusText(int mode, ScaleInfo sc, float rad, double arcPx)
        {
            string r = "R = " + sc.Len(rad), d = "Ø = " + sc.Len(2 * rad), a = "Arc = " + sc.Len(arcPx);
            switch (mode)
            {
                case 0: return r + "\n" + d;
                case 7: return r + "\n" + d + "\n" + a;
                case 1: return r;
                case 3: return d + "\n" + a;
                case 4: return r + "\n" + a;
                case 5: return "";
                case 6: return a;
                default: return d;
            }
        }

        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        static Graphics mg;
        /// <summary>A grayscale or pixelated copy of a picture. A few are kept: the screenshot's own and the one with
        /// the pasted pictures are both used in one redraw, and must not push each other out.</summary>
        sealed class FxCopy { public Bitmap For; public int Block; public Bitmap Img; }
        static readonly List<FxCopy> grayCopies = new List<FxCopy>(), pixCopies = new List<FxCopy>();
        const int FxKeep = 3;

        static Bitmap FxGet(List<FxCopy> list, Bitmap img, int block, Func<Bitmap> make)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i].For, img) && list[i].Block == block)
                {
                    FxCopy hit = list[i];
                    if (i > 0) { list.RemoveAt(i); list.Insert(0, hit); }
                    return hit.Img;
                }
            FxCopy c = new FxCopy { For = img, Block = block, Img = make() };
            list.Insert(0, c);
            while (list.Count > FxKeep) { list[list.Count - 1].Img.Dispose(); list.RemoveAt(list.Count - 1); }
            return c.Img;
        }

        /// <summary>Frees the grayscale / pixelated copies made for 'img' (called when that picture is disposed).</summary>
        public static void ReleaseCache(Bitmap img)
        {
            if (img == null) return;
            foreach (List<FxCopy> list in new[] { grayCopies, pixCopies, blurCopies })
                for (int i = list.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(list[i].For, img)) { list[i].Img.Dispose(); list.RemoveAt(i); }
        }

        /// <summary>A grayscale copy of the picture, kept while the picture is the same.</summary>
        static Bitmap GrayOf(Bitmap img)
        {
            return FxGet(grayCopies, img, 0, () => Effects.Grayscale(img));
        }

        /// <summary>
        /// The part r of img in solid blocks of 'block' pixels, each the exact average of the pixels under it (the
        /// blocks start at r's corner). Counted pixel by pixel: shrinking the picture with smoothing let GDI+ blend the
        /// edge blocks with transparency, and text under the outer row and column of blocks stayed readable.
        /// Opaque pictures give fully opaque blocks.
        /// </summary>
        internal static Bitmap Blocks(Bitmap img, Rectangle r, int block)
        {
            return Blocks(img, r, block, 0, 0);
        }

        /// <summary>As above, with the block grid shifted by (ox, oy) pixels: the first blocks are cut short, so a part
        /// of a larger picture gets the same blocks the whole picture would.</summary>
        internal static Bitmap Blocks(Bitmap img, Rectangle r, int block, int ox, int oy)
        {
            block = Math.Max(1, block);
            ox = ((ox % block) + block) % block; oy = ((oy % block) + block) % block;
            int w = r.Width, h = r.Height;
            Bitmap res = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData sd = img.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData dd = res.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] row = new int[w];
                int bw = (w + ox + block - 1) / block;
                long[] sa = new long[bw], sr = new long[bw], sg = new long[bw], sb = new long[bw];
                int[] outRow = new int[w];
                for (int by = -oy; by < h; by += block)
                {
                    int y0 = Math.Max(0, by), y1 = Math.Min(h, by + block), rows = y1 - y0;
                    Array.Clear(sa, 0, bw); Array.Clear(sr, 0, bw); Array.Clear(sg, 0, bw); Array.Clear(sb, 0, bw);
                    for (int y = y0; y < y1; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(sd.Scan0 + y * sd.Stride, row, 0, w);
                        for (int x = 0; x < w; x++)
                        {
                            int p = row[x], a = (p >> 24) & 255, i = (x + ox) / block;
                            // colours weighted by their alpha: a see-through pixel adds little colour
                            sa[i] += a; sr[i] += ((p >> 16) & 255) * a; sg[i] += ((p >> 8) & 255) * a; sb[i] += (p & 255) * a;
                        }
                    }
                    for (int i = 0; i < bw; i++)
                    {
                        int x0 = Math.Max(0, i * block - ox), x1 = Math.Min(w, (i + 1) * block - ox), cols = x1 - x0;
                        if (cols <= 0) continue;
                        long n = (long)cols * rows;
                        int a = (int)(sa[i] / n);
                        int cr = sa[i] > 0 ? (int)(sr[i] / sa[i]) : 0, cg = sa[i] > 0 ? (int)(sg[i] / sa[i]) : 0, cb = sa[i] > 0 ? (int)(sb[i] / sa[i]) : 0;
                        int px = (a << 24) | (cr << 16) | (cg << 8) | cb;
                        for (int x = x0; x < x1; x++) outRow[x] = px;
                    }
                    for (int y = y0; y < y1; y++)
                        System.Runtime.InteropServices.Marshal.Copy(outRow, 0, dd.Scan0 + y * dd.Stride, w);
                }
            }
            finally
            {
                img.UnlockBits(sd);
                res.UnlockBits(dd);
            }
            return res;
        }

        /// <summary>A pixelated copy of the whole picture (blocks of 'block' pixels), kept while nothing changes.</summary>
        static readonly List<FxCopy> blurCopies = new List<FxCopy>();

        /// <summary>A blurred copy of the picture (Focus → Blur), kept while the picture and the strength stay the same.</summary>
        static Bitmap BlurredOf(Bitmap img, int radius)
        {
            return FxGet(blurCopies, img, radius, () => Blurred(img, radius));
        }

        /// <summary>
        /// A soft blur of about 'radius' pixels: the picture is shrunk (fast; it is going to be blurred anyway), blurred
        /// with three box passes each way (close to a Gaussian blur) and enlarged again with smoothing. Worked on
        /// premultiplied colours, so see-through parts don't leave dark fringes.
        /// </summary>
        internal static Bitmap Blurred(Bitmap img, int radius)
        {
            radius = Math.Max(1, radius);
            int w = img.Width, h = img.Height;
            int d = Math.Max(1, radius / 3);
            int sw = Math.Max(1, (w + d - 1) / d), sh = Math.Max(1, (h + d - 1) / d);
            int rr = Math.Max(1, (int)Math.Round(radius / (float)d / 1.7f));
            using (Bitmap small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.InterpolationMode = d > 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(img, new Rectangle(0, 0, sw, sh), 0, 0, w, h, GraphicsUnit.Pixel, ia);
                }
                BitmapData bd = small.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
                try
                {
                    int[] px = new int[sw * sh];
                    System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, px, 0, px.Length);
                    int[] tmp = new int[px.Length];
                    for (int pass = 0; pass < 3; pass++)
                    {
                        BoxPass(px, tmp, sw, sh, rr, true);
                        BoxPass(tmp, px, sw, sh, rr, false);
                    }
                    System.Runtime.InteropServices.Marshal.Copy(px, 0, bd.Scan0, px.Length);
                }
                finally { small.UnlockBits(bd); }
                Bitmap res = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(res))
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(small, new Rectangle(0, 0, w, h), 0, 0, sw, sh, GraphicsUnit.Pixel, ia);
                }
                return res;
            }
        }

        /// <summary>One box-blur pass along the rows (or the columns) of src into dst; the edge pixels repeat outwards.</summary>
        static void BoxPass(int[] src, int[] dst, int w, int h, int r, bool rows)
        {
            int lines = rows ? h : w, len = rows ? w : h, step = rows ? 1 : w;
            int win = 2 * r + 1;
            for (int ln = 0; ln < lines; ln++)
            {
                int start = rows ? ln * w : ln;
                long sa = 0, sr = 0, sg = 0, sb = 0;
                for (int k = -r; k <= r; k++)
                {
                    int v = src[start + Math.Max(0, Math.Min(len - 1, k)) * step];
                    sa += (v >> 24) & 255; sr += (v >> 16) & 255; sg += (v >> 8) & 255; sb += v & 255;
                }
                for (int i = 0; i < len; i++)
                {
                    dst[start + i * step] = (int)((sa / win) << 24 | (sr / win) << 16 | (sg / win) << 8 | (sb / win));
                    int vOut = src[start + Math.Max(0, i - r) * step];
                    int vIn = src[start + Math.Min(len - 1, i + r + 1) * step];
                    sa += ((vIn >> 24) & 255) - ((vOut >> 24) & 255);
                    sr += ((vIn >> 16) & 255) - ((vOut >> 16) & 255);
                    sg += ((vIn >> 8) & 255) - ((vOut >> 8) & 255);
                    sb += (vIn & 255) - (vOut & 255);
                }
            }
        }

        static Bitmap PixelatedOf(Bitmap img, int block)
        {
            // the picture with the pasted pictures starts somewhere on the drawing: its blocks line up with the
            // screenshot's own, so a pasted picture's blocks meet the ones around it
            RectangleF w = WorldOf(img);
            return FxGet(pixCopies, img, block, () => Blocks(img, new Rectangle(0, 0, img.Width, img.Height), block, (int)w.X, (int)w.Y));
        }

        public static Graphics MeasureG
        {
            get
            {
                if (mg == null) { mg = Graphics.FromImage(new Bitmap(1, 1)); mg.TextRenderingHint = TextRenderingHint.AntiAlias; }
                return mg;
            }
        }

        /// <summary>A marker's radius (the disc's half height).</summary>
        public static float MarkerRadius(Shape s) { return Math.Max(8f, s.FontSize * 0.9f); }

        /// <summary>1.4.2: half the marker's width. Number and letter markers are discs; a symbol marker with a longer
        /// text (more than 2 characters, or wider than the disc) becomes a pill that fits its text on one line.</summary>
        public static float MarkerHalfWidth(Shape s)
        {
            float r = MarkerRadius(s);
            if (!s.IsSymbolMarker || string.IsNullOrEmpty(s.Text) || s.Text.Length <= 2) return r;
            SizeF tsz;
            Graphics g = MeasureG;
            lock (g) tsz = g.MeasureString(s.Text, GetFont(r * 1.05f, true), PointF.Empty, StringFormat.GenericTypographic);
            return Math.Max(r, tsz.Width / 2f + r * 0.55f);
        }

        /// <summary>The marker's outline: a circle, or a pill (round ends) for a wide symbol marker.</summary>
        public static GraphicsPath MarkerPath(Shape s)
        {
            float r = MarkerRadius(s), hw = MarkerHalfWidth(s);
            PointF c = s.Pts[0];
            GraphicsPath gp = new GraphicsPath();
            if (hw <= r + 0.01f) { gp.AddEllipse(c.X - r, c.Y - r, 2 * r, 2 * r); return gp; }
            float l = c.X - hw, t = c.Y - r, d = 2 * r;
            gp.AddArc(l, t, d, d, 90, 180);
            gp.AddLine(l + r, t, c.X + hw - r, t);
            gp.AddArc(c.X + hw - d, t, d, d, 270, 180);
            gp.AddLine(c.X + hw - r, t + d, l + r, t + d);
            gp.CloseFigure();
            return gp;
        }

        public static Font GetFont(float px, bool bold) { return GetFont(px, bold ? FontStyle.Bold : FontStyle.Regular); }

        public const string DefaultFont = "Segoe UI";

        public static Font GetFont(float px, FontStyle style) { return GetFont(px, style, DefaultFont); }

        /// <summary>A font of the family the text uses (Segoe UI if that family isn't installed on this PC).</summary>
        public static Font GetFont(float px, FontStyle style, string family)
        {
            px = Math.Max(4f, (float)Math.Round(px, 1));
            if (string.IsNullOrEmpty(family)) family = DefaultFont;
            string key = px.ToString(CultureInfo.InvariantCulture) + "|" + (int)style + "|" + family;
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                // keep the cache small: fonts still in use stay valid (not disposed here; the finalizer frees them)
                if (fonts.Count > 300) fonts.Clear();
                try
                {
                    f = new Font(family, px, style, GraphicsUnit.Pixel);
                    // a family that isn't installed comes back as Microsoft Sans Serif; one without this style throws
                    if (family != DefaultFont && !string.Equals(f.FontFamily.Name, family, StringComparison.OrdinalIgnoreCase)) { f.Dispose(); f = new Font(DefaultFont, px, style, GraphicsUnit.Pixel); }
                }
                catch (Exception) { f = new Font(DefaultFont, px, style, GraphicsUnit.Pixel); }
                fonts[key] = f;
            }
            return f;
        }

        /// <summary>Text with its style. Strike-out is drawn here instead of by the font: the font's own line is a
        /// hairline, this one is as heavy as the letters (heavier for bold).</summary>
        public static void DrawStyledText(Graphics g, string text, float px, FontStyle st, Brush b, RectangleF r, StringFormat sf)
        {
            DrawStyledText(g, text, px, st, b, r, sf, DefaultFont);
        }

        public static void DrawStyledText(Graphics g, string text, float px, FontStyle st, Brush b, RectangleF r, StringFormat sf, string family)
        {
            bool strike = (st & FontStyle.Strikeout) != 0;
            Font f = GetFont(px, st & ~FontStyle.Strikeout, family);
            g.DrawString(text ?? "", f, b, r, sf);
            if (!strike || string.IsNullOrEmpty(text)) return;
            FontFamily ff = f.FontFamily;
            float em = f.Size;                                         // pixel size
            float asc = em * ff.GetCellAscent(f.Style) / ff.GetEmHeight(f.Style);
            float th = Math.Max(1f, em * ((st & FontStyle.Bold) != 0 ? 0.095f : 0.055f));
            foreach (RectangleF ln in TextLines(g, text, f, r, sf))
            {
                float y = ln.Y + asc - em * 0.28f;                     // through the middle of the small letters
                g.FillRectangle(b, ln.X, y - th / 2f, ln.Width, th);
            }
        }

        /// <summary>Where each drawn line of text sits (left, top, width of its letters), as DrawString lays it out.</summary>
        static List<RectangleF> TextLines(Graphics g, string text, Font f, RectangleF r, StringFormat sf)
        {
            List<RectangleF> lines = new List<RectangleF>();
            using (StringFormat mf = new StringFormat(sf))
            {
                for (int start = 0; start < text.Length; start += 32)
                {
                    int n = Math.Min(32, text.Length - start);
                    CharacterRange[] cr = new CharacterRange[n];
                    for (int i = 0; i < n; i++) cr[i] = new CharacterRange(start + i, 1);
                    mf.SetMeasurableCharacterRanges(cr);
                    Region[] rg = g.MeasureCharacterRanges(text, f, r, mf);
                    for (int i = 0; i < rg.Length; i++)
                    {
                        char ch = text[start + i];
                        RectangleF cb = rg[i].GetBounds(g);
                        rg[i].Dispose();
                        if (char.IsWhiteSpace(ch) || cb.Width <= 0 || cb.Height <= 0) continue;
                        int at = lines.FindIndex(l => Math.Abs(l.Y - cb.Y) < cb.Height * 0.5f);
                        if (at < 0) lines.Add(new RectangleF(cb.X, cb.Y, cb.Width, cb.Height));
                        else lines[at] = RectangleF.Union(lines[at], cb);
                    }
                }
            }
            return lines;
        }

        /// <summary>Width of a text box the user dragged out (0 = grows with the text).</summary>
        public static float WrapWidth(Shape s)
        {
            if (s.Kind != ShapeKind.Text || s.Pts.Count < 2) return 0f;
            float w = s.Pts[1].X - s.Pts[0].X;
            return w > 8f ? w : 0f;
        }

        public static SizeF TextSize(Shape s)
        {
            string t = string.IsNullOrEmpty(s.Text) ? " " : s.Text;
            float wrap = WrapWidth(s);
            if (wrap > 0)
            {
                SizeF wz = MeasureG.MeasureString(t, GetFont(s.FontSize, s.Style, s.FontFamilyName), (int)Math.Ceiling(wrap));
                // the box is at least as tall as dragged (Pts[1] = bottom-right); it grows when the text needs more room
                float boxH = s.Pts[1].Y - s.Pts[0].Y;
                return new SizeF(wrap, Math.Max(Math.Max(wz.Height, s.FontSize * 1.2f), boxH));
            }
            SizeF sz = MeasureG.MeasureString(t, GetFont(s.FontSize, s.Style, s.FontFamilyName));
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
            int nd = 0, na = 0, nm = 0, nl = 0, ng = 0, npl = 0, npm = 0, nc = 0, nr = 0;
            foreach (Shape s in shapes)
            {
                if (s.Kind == ShapeKind.Dimension) d[s] = "D" + (++nd);
                else if (s.Kind == ShapeKind.Area && !s.ShowsArea) d[s] = "P" + (++npm);   // perimeter only
                else if (s.Kind == ShapeKind.Area) d[s] = "A" + (++na);
                else if (s.Kind == ShapeKind.Angle) d[s] = "∠" + (++ng);
                else if (s.Kind == ShapeKind.Polyline || (s.Kind == ShapeKind.Perimeter && s.IsOpenPath)) d[s] = "L" + (++npl);
                else if (s.Kind == ShapeKind.Perimeter) d[s] = "P" + (++npm);
                else if (s.Kind == ShapeKind.Coord) d[s] = "C" + (++nc);
                else if (s.Kind == ShapeKind.Radius) d[s] = "R" + (++nr);
                else if (s.IsSymbolMarker) d[s] = string.IsNullOrEmpty(s.Text) ? "?" : s.Text;
                else if (s.Kind == ShapeKind.Counter) d[s] = s.IsLetterMarker ? sc.MarkerLabel(nl++, true) : sc.MarkerLabel(nm++, false);
            }
            return d;
        }

        public static void DrawAll(Graphics g, List<Shape> shapes, Bitmap img, ScaleInfo sc, Shape skip)
        {
            DrawAll(g, shapes, img, sc, skip, false);
        }

        /// <summary>export = true for saved / copied / pinned pictures: non-printing notes are left out.</summary>
        public static void DrawAll(Graphics g, List<Shape> shapes, Bitmap img, ScaleInfo sc, Shape skip, bool export)
        {
            // pasted pictures are part of the picture for the focus effects: pixelate, grayscale, magnify and cut
            // change them too, not only the captured picture under them
            // The screenshot's own grayscale / pixelated copies are kept as they are; the pasted pictures get a small
            // picture of their own (only where they lie), so moving one rebuilds just that, not the whole screenshot.
            bool globals = shapes.Exists(IsGlobalFocus);
            Bitmap src = img;
            if (img != null && shapes.Exists(UsesPicture)) src = WithPictures(img, shapes, sc, skip);
            RectangleF picsAt = src != img ? WorldOf(src) : RectangleF.Empty;
            if (img != null) DrawGlobalHighlights(g, shapes, img, new RectangleF(0, 0, img.Width, img.Height));
            Dictionary<Shape, string> names = Labels(shapes, sc);
            Shape origin = Origin(shapes);
            foreach (Shape s in shapes)
            {
                if (s == skip) continue;
                if (export && s.Kind == ShapeKind.Note) continue;
                string n;
                names.TryGetValue(s, out n);
                if (s.Kind == ShapeKind.Coord && s.Pts.Count > 0)
                    n = (n ?? "") + (string.IsNullOrEmpty(s.Text) ? "" : "  " + s.Text) + "\n" +
                        (origin != null ? sc.Coord(s.Pts[0], origin.Pts[0]) : "no origin – place one with the coordinate tool");
                // one bad shape must never stop the rest of the picture from drawing
                try
                {
                    if (s.Shadow && s.CanShadow) DrawShadow(g, s, img, sc, n);
                    Draw(g, s, img, sc, n);
                    if (!export) DrawGuideOutline(g, s);
                    if (UsesPicture(s) && !picsAt.IsEmpty && ShapeOps.Bounds(s).IntersectsWith(picsAt))
                    {
                        // where it covers pasted pictures: the effect once more, from the picture with them on it
                        // (the same pixels elsewhere; see-through where there is no picture) – its border only once
                        GraphicsState st = g.Save();
                        g.SetClip(picsAt, CombineMode.Intersect);
                        noBorder = true;
                        try { Draw(g, s, src, sc, n); }
                        finally { noBorder = false; g.Restore(st); }
                    }
                    if (globals && s.Kind == ShapeKind.Image && s.Pts.Count >= 2)
                    {
                        // a pasted picture lies on top of the outside effects (inside or outside the captured
                        // picture): give it the same treatment, only where the picture is
                        using (GraphicsPath op = ImageOutline(s))
                        {
                            GraphicsState st = g.Save();
                            g.SetClip(op, CombineMode.Intersect);
                            DrawGlobalHighlights(g, shapes, src, op.GetBounds());
                            g.Restore(st);
                        }
                    }
                }
                catch (Exception) { }
            }
        }

        /// <summary>Editor only (never in a saved, copied, printed or pinned picture): a focus / highlight box with no
        /// border gets a thin magenta line round it, so it can still be found and clicked.</summary>
        public static readonly Color GuideMagenta = Color.FromArgb(230, 0, 200);
        static void DrawGuideOutline(Graphics g, Shape s)
        {
            if (s.Kind != ShapeKind.Highlight || (s.Width > 0 && s.StrokeOpacity > 0)) return;
            if (s.Pts.Count < (s.IsFocusPoly ? 3 : 2)) return;
            using (GraphicsPath hp = HighlightPath(s))
            using (Pen p = new Pen(GuideMagenta, 0f))         // 0 = one screen pixel at any zoom (the "0.5" hairline)
            {
                p.DashStyle = DashStyle.Solid;
                g.DrawPath(p, hp);
            }
        }

        /// <summary>The coordinate origin (0,0) of the drawing: the last origin marker placed, or null.</summary>
        public static Shape Origin(List<Shape> shapes)
        {
            for (int i = shapes.Count - 1; i >= 0; i--) if (shapes[i].Kind == ShapeKind.Origin && shapes[i].Pts.Count > 0) return shapes[i];
            return null;
        }

        /// <summary>Pen for a shape's outline, with its line style (dashes scale with the width).</summary>
        public static Pen OutlinePen(Shape s, Color c, float w)
        {
            Pen p = new Pen(c, w);
            switch ((LineStyle)s.Dash)
            {
                case LineStyle.Dashed: p.DashPattern = new float[] { 4f, 2.5f }; break;
                case LineStyle.Dotted: p.DashPattern = new float[] { 1f, 1.8f }; break;
                case LineStyle.DashDot: p.DashPattern = new float[] { 6f, 2f, 1f, 2f }; break;
                case LineStyle.LongDash: p.DashPattern = new float[] { 10f, 3f }; break;
            }
            return p;
        }

        static bool IsCloudStyle(Shape s) { return s.Dash == (int)LineStyle.Cloud; }

        /// <summary>The scallops of a revision cloud, or of a shape drawn with the cloud line style, as they are drawn
        /// (turned, closed ones ending where they start). Null for everything else.</summary>
        public static List<PointF> CloudOutline(Shape s)
        {
            if (s.Pts.Count < 2) return null;
            bool cloud = s.Kind == ShapeKind.Cloud;
            if (!cloud && !(IsCloudStyle(s) && (s.Kind == ShapeKind.Rect || s.Kind == ShapeKind.Ellipse ||
                (s.Kind == ShapeKind.Polygon && !s.IsOpenPath && s.Pts.Count >= 3) || (s.Kind == ShapeKind.Area && s.Pts.Count >= 3)))) return null;
            float w = Math.Max(0.5f, s.Width);
            bool boxed = cloud || s.Kind == ShapeKind.Rect || s.Kind == ShapeKind.Ellipse;
            RectangleF r = boxed ? Geo.Rect(s.Pts[0], s.Pts[1]) : RectangleF.Empty;
            PointF[] pts;
            using (GraphicsPath gp = cloud ? CloudPath(r, w)
                : s.Kind == ShapeKind.Rect ? CloudPolyPath(new PointF[] { new PointF(r.X, r.Y), new PointF(r.Right, r.Y), new PointF(r.Right, r.Bottom), new PointF(r.X, r.Bottom) }, true, w)
                : s.Kind == ShapeKind.Ellipse ? CloudPolyPath(EllipsePoints(r, 64), true, w)
                : CloudPolyPath(s.Pts, true, w))
            {
                if (gp.PointCount < 3) return null;
                if (boxed && Math.Abs(s.Angle) > 0.01f)
                    using (Matrix m = new Matrix()) { m.RotateAt(s.Angle, Geo.Center(r)); gp.Transform(m); }
                pts = gp.PathPoints;
            }
            List<PointF> o = new List<PointF>(pts);
            if (Geo.Dist(o[0], o[o.Count - 1]) > 0.01f) o.Add(o[0]);
            return o;
        }

        static PointF[] EllipsePoints(RectangleF r, int n)
        {
            PointF[] p = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                p[i] = new PointF((float)(r.X + r.Width / 2 + Math.Cos(a) * r.Width / 2), (float)(r.Y + r.Height / 2 + Math.Sin(a) * r.Height / 2));
            }
            return p;
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
            // the shadow falls under the lines only: value boxes and labels (A = …, R = …, 4.00 m) get none
            shadowPass = true;
            try { Draw(g, sh, img, sc, name); }
            finally { shadowPass = false; }
        }

        [ThreadStatic] static bool shadowPass;

        public static void Draw(Graphics g, Shape s, Bitmap img, ScaleInfo sc, string name)
        {
            if (s.Pts.Count == 0) return;
            GraphicsState st = null;
            // a focus shape turns only its outline (HighlightPath): the picture seen through it stays upright
            if (s.Rotatable && s.Angle != 0f && s.Kind != ShapeKind.Highlight)
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
                        if (IsCloudStyle(s))
                        {
                            PointF[] outline = s.Kind == ShapeKind.Rect
                                ? new PointF[] { new PointF(r.X, r.Y), new PointF(r.Right, r.Y), new PointF(r.Right, r.Bottom), new PointF(r.X, r.Bottom) }
                                : EllipsePoints(r, 64);
                            using (GraphicsPath gp = CloudPolyPath(outline, true, w))
                            {
                                if (fill) using (SolidBrush b = new SolidBrush(fCol)) g.FillPath(b, gp);
                                if (stroke) using (Pen p = new Pen(sCol, w)) { p.LineJoin = LineJoin.Round; g.DrawPath(p, gp); }
                            }
                            break;
                        }
                        if (fill)
                            using (SolidBrush b = new SolidBrush(fCol)) { if (s.Kind == ShapeKind.Rect) g.FillRectangle(b, r); else g.FillEllipse(b, r); }
                        if (stroke)
                            using (Pen p = OutlinePen(s, sCol, w)) { if (s.Kind == ShapeKind.Rect) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height); else g.DrawEllipse(p, r); }
                        break;
                    }
                case ShapeKind.Line:
                case ShapeKind.Arrow:
                    {
                        if (s.Pts.Count < 2 || !stroke) return;
                        PointF a = s.Pts[0], b = s.Pts[1];
                        float L = Geo.Dist(a, b);
                        int hs = s.HeadStart, he = s.HeadEnd;
                        using (Pen p = OutlinePen(s, sCol, w))
                        {
                            // an arrow's tail is cut square (flat), not rounded; a plain line keeps round ends
                            LineCap end = s.Kind == ShapeKind.Arrow || hs > 0 || he > 0 ? LineCap.Flat : LineCap.Round;
                            p.StartCap = end;
                            p.EndCap = end;
                            if ((hs > 0 || he > 0) && L > 0.5f)
                            {
                                // heads grow with the line width; the shaft stops where each head needs it to
                                float hl, hw;
                                Geo.HeadSize(w, L * (hs > 0 && he > 0 ? 0.45f : 0.5f), out hl, out hw);
                                PointF u = new PointF((b.X - a.X) / L, (b.Y - a.Y) / L), nu = new PointF(-u.X, -u.Y);
                                float b0 = HeadBack(hs, w, hl, hw), b1 = HeadBack(he, w, hl, hw);
                                if (hs > 0) p.StartCap = LineCap.Flat;
                                if (he > 0) p.EndCap = LineCap.Flat;
                                PointF s0 = new PointF(a.X + u.X * b0, a.Y + u.Y * b0), s1 = new PointF(b.X - u.X * b1, b.Y - u.Y * b1);
                                if (Geo.Dist(a, s0) + Geo.Dist(b, s1) < L) g.DrawLine(p, s0, s1);
                                DrawHead(g, a, nu, hs, sCol, w, hl, hw);
                                DrawHead(g, b, u, he, sCol, w, hl, hw);
                            }
                            else g.DrawLine(p, a, b);
                        }
                        break;
                    }
                case ShapeKind.Freehand:
                    {
                        if (!stroke) return;
                        bool heads = !s.IsMarkerPen && s.Pts.Count >= 2 && (s.HeadStart > 0 || s.HeadEnd > 0);
                        float hl = 0, hw = 0, cut0 = 0, cut1 = 0;
                        if (heads)
                        {
                            float len = (float)Geo.Perimeter(s.Pts, false);
                            Geo.HeadSize(w, Math.Max(4f, len * 0.4f), out hl, out hw);
                            // like a straight arrow: the stroke stops where each head begins, so it runs straight into the head
                            if (s.HeadStart > 0) cut0 = Math.Min(len * 0.45f, Math.Max(HeadBack(s.HeadStart, w, hl, hw), hl * 0.6f));
                            if (s.HeadEnd > 0) cut1 = Math.Min(len * 0.45f, Math.Max(HeadBack(s.HeadEnd, w, hl, hw), hl * 0.6f));
                        }
                        List<PointF> body = heads ? Geo.TrimPath(s.Pts, cut0, cut1) : s.Pts;
                        using (Pen p = OutlinePen(s, sCol, w))
                        {
                            // highlighter pen: square ends like a chisel marker; one stroke, so overlaps don't darken
                            // with an arrowhead the tail is cut flat too, like the straight arrow
                            LineCap cap = s.IsMarkerPen ? LineCap.Square : heads ? LineCap.Flat : LineCap.Round;
                            p.StartCap = cap; p.EndCap = cap; p.LineJoin = LineJoin.Round;
                            if (s.HeadStart > 0 && heads) p.StartCap = LineCap.Flat;
                            if (s.HeadEnd > 0 && heads) p.EndCap = LineCap.Flat;
                            if (body.Count == 1) g.DrawLine(p, body[0], new PointF(body[0].X + 0.1f, body[0].Y));
                            else if (body.Count > 1) g.DrawLines(p, body.ToArray());
                        }
                        if (heads)
                        {
                            // each head points from where the stroke stops to the tip: in line with the stroke's last stretch
                            float over = Math.Max(hl, w * 3f);
                            if (s.HeadStart > 0) DrawHead(g, s.Pts[0], Geo.Unit(Geo.PointAlong(s.Pts, true, over), s.Pts[0]), s.HeadStart, sCol, w, hl, hw);
                            if (s.HeadEnd > 0) DrawHead(g, s.Pts[s.Pts.Count - 1], Geo.Unit(Geo.PointAlong(s.Pts, false, over), s.Pts[s.Pts.Count - 1]), s.HeadEnd, sCol, w, hl, hw);
                        }
                        break;
                    }
                case ShapeKind.Text:
                    {
                        Font f = GetFont(s.FontSize, s.Style, s.FontFamilyName);
                        SizeF sz = TextSize(s);
                        if (fill)
                            using (SolidBrush b = new SolidBrush(fCol))
                            {
                                if (IsCloudStyle(s) && s.Width > 0)
                                {
                                    // a cloud frame: the fill reaches into the scallops too
                                    RectangleF cr = new RectangleF(s.Pts[0].X - 4 - w / 2f, s.Pts[0].Y - 2 - w / 2f, sz.Width + 8 + w, sz.Height + 4 + w);
                                    using (GraphicsPath cp = CloudPath(cr, w)) g.FillPath(b, cp);
                                }
                                else g.FillRectangle(b, s.Pts[0].X - 4, s.Pts[0].Y - 2, sz.Width + 8, sz.Height + 4);
                            }
                        if (s.Width > 0)          // line width 0 = no frame; any width draws the frame
                            using (Pen fp = OutlinePen(s, sCol, w))
                            {
                                RectangleF fr = new RectangleF(s.Pts[0].X - 4 - w / 2f, s.Pts[0].Y - 2 - w / 2f, sz.Width + 8 + w, sz.Height + 4 + w);
                                if (IsCloudStyle(s)) using (GraphicsPath cp = CloudPath(fr, w)) g.DrawPath(fp, cp);      // a revision-cloud frame
                                else g.DrawRectangle(fp, fr.X, fr.Y, fr.Width, fr.Height);
                            }
                        using (SolidBrush b = new SolidBrush(sCol))
                        using (StringFormat sf = new StringFormat())
                        {
                            sf.Alignment = HAlign(s.Align);
                            // 1.4.2: top, middle or bottom of a box dragged taller than its text
                            sf.LineAlignment = s.VAlign == 1 ? StringAlignment.Center : s.VAlign == 2 ? StringAlignment.Far : StringAlignment.Near;
                            DrawStyledText(g, s.Text, s.FontSize, s.Style, b, new RectangleF(s.Pts[0].X, s.Pts[0].Y, sz.Width + 1f, sz.Height + 1f), sf, s.FontFamilyName);
                        }
                        break;
                    }
                case ShapeKind.Polygon:
                case ShapeKind.Area:
                    {
                        if (s.Kind == ShapeKind.Polygon && s.IsOpenPath) goto case ShapeKind.Freehand;   // open polyline: drawn like a stroke, heads optional
                        PointF[] pts = s.Pts.ToArray();
                        bool holes = s.Holes != null && s.Holes.Count > 0;
                        if (pts.Length >= 3 && fill)
                            using (SolidBrush b = new SolidBrush(fCol))
                            {
                                bool cloudFill = IsCloudStyle(s);
                                if (!holes && !cloudFill) g.FillPolygon(b, pts);
                                else if (!holes)
                                    // a cloud outline is filled right out to its scallops, not just the straight polygon
                                    using (GraphicsPath cp = CloudPolyPath(pts, true, w)) { cp.FillMode = FillMode.Winding; g.FillPath(b, cp); }
                                else
                                    using (GraphicsPath gp = new GraphicsPath(FillMode.Alternate))
                                    {
                                        if (cloudFill)
                                            using (GraphicsPath cp = CloudPolyPath(pts, true, w))
                                            {
                                              cp.FillMode = FillMode.Winding;
                                              using (Region rg = new Region(cp))
                                              {
                                                foreach (List<PointF> h in s.Holes)
                                                    if (h.Count >= 3) using (GraphicsPath hp = new GraphicsPath()) { hp.AddPolygon(h.ToArray()); rg.Exclude(hp); }
                                                g.FillRegion(b, rg);
                                              }
                                            }
                                        else
                                        {
                                            gp.AddPolygon(pts);
                                            foreach (List<PointF> h in s.Holes) if (h.Count >= 3) gp.AddPolygon(h.ToArray());
                                            g.FillPath(b, gp);
                                        }
                                    }
                            }
                        if (stroke && pts.Length >= 2)
                            using (Pen p = OutlinePen(s, sCol, w))
                            {
                                p.LineJoin = LineJoin.Round;
                                if (pts.Length >= 3 && IsCloudStyle(s)) using (GraphicsPath cp = CloudPolyPath(pts, true, w)) g.DrawPath(p, cp);
                                else if (pts.Length >= 3) g.DrawPolygon(p, pts); else g.DrawLines(p, pts);
                                if (holes && s.Kind == ShapeKind.Polygon)      // a hollow section: its inner edge is a real edge
                                    using (Pen hp = OutlinePen(s, sCol, w))
                                    {
                                        hp.LineJoin = LineJoin.Round;
                                        foreach (List<PointF> h in s.Holes) if (h.Count >= 3) g.DrawPolygon(hp, h.ToArray());
                                    }
                                else if (holes)
                                    using (Pen hp = new Pen(sCol, Math.Max(1f, w * 0.8f)))
                                    {
                                        hp.DashStyle = DashStyle.Dash;
                                        foreach (List<PointF> h in s.Holes) if (h.Count >= 3) g.DrawPolygon(hp, h.ToArray());
                                    }
                            }
                        if (s.Kind == ShapeKind.Area && s.Mode == 3) s.LabelRect = RectangleF.Empty;     // Show: None – no label on the picture
                        else if (s.Kind == ShapeKind.Area && pts.Length >= 3)
                        {
                            double per = Geo.Perimeter(s.Pts, true);
                            string head = string.IsNullOrEmpty(name) ? "" : name + (string.IsNullOrEmpty(s.Text) ? "" : "  " + s.Text) + "\n";
                            // Mode: 0 = area and perimeter, 1 = area only, 2 = perimeter only, 3 = no label (still listed as an area)
                            string at = !s.ShowsArea ? "" : holes
                                ? "Gross " + sc.Area(Measure.GrossArea(s)) + "\nCut-outs −" + sc.Area(Measure.HolesArea(s)) + "\nNet A = " + sc.Area(Measure.NetArea(s))
                                : "A = " + sc.Area(Measure.GrossArea(s));
                            string pt = s.ShowsPerimeter ? "P = " + sc.Len(per) : "";
                            string t = head + at + (at.Length > 0 && pt.Length > 0 ? "\n" : "") + pt;
                            // the label can be dragged away from the middle (LabelOffset) and resized (text size); a thin line joins it back
                            PointF mid = Geo.Centroid(s.Pts);
                            PointF lc = new PointF(mid.X + s.LabelOffset.X, mid.Y + s.LabelOffset.Y);
                            if (Geo.Dist(mid, lc) > s.FontSize * 1.5f)
                                using (Pen lp = new Pen(A(s.Stroke, 80), 1f)) { lp.DashStyle = DashStyle.Dot; g.DrawLine(lp, mid, lc); g.FillEllipse(lp.Brush, mid.X - 2, mid.Y - 2, 4, 4); }
                            s.LabelRect = DrawBoxR(g, t, lc, GetFont(s.FontSize, false), A(s.Stroke, 100));
                        }
                        break;
                    }
                case ShapeKind.Polyline:
                    if (s.Pts.Count >= 2) DrawOpenPath(g, s, sc, name, sCol, w, stroke);
                    break;
                case ShapeKind.Perimeter:
                    {
                        if (s.Pts.Count < 2) return;
                        if (s.IsOpenPath) { DrawOpenPath(g, s, sc, name, sCol, w, stroke); break; }
                        if (stroke)
                            using (Pen p = OutlinePen(s, sCol, w))
                            {
                                p.LineJoin = LineJoin.Round;
                                if (s.Pts.Count >= 3 && IsCloudStyle(s)) using (GraphicsPath cp = CloudPolyPath(s.Pts, true, w)) g.DrawPath(p, cp);
                                else if (s.Pts.Count >= 3) g.DrawPolygon(p, s.Pts.ToArray()); else g.DrawLines(p, s.Pts.ToArray());
                            }
                        if (s.Pts.Count >= 3)
                        {
                            string t = (string.IsNullOrEmpty(name) ? "" : name + (string.IsNullOrEmpty(s.Text) ? "" : "  " + s.Text) + "\n") + "P = " + sc.Len(Geo.Perimeter(s.Pts, true));
                            PointF mid = Geo.Centroid(s.Pts);
                            PointF lc = new PointF(mid.X + s.LabelOffset.X, mid.Y + s.LabelOffset.Y);
                            if (Geo.Dist(mid, lc) > s.FontSize * 1.5f)
                                using (Pen lp = new Pen(A(s.Stroke, 80), 1f)) { lp.DashStyle = DashStyle.Dot; g.DrawLine(lp, mid, lc); }
                            s.LabelRect = DrawBoxR(g, t, lc, GetFont(s.FontSize, false), A(s.Stroke, 100));
                        }
                        break;
                    }
                case ShapeKind.Cloud:
                    {
                        if (s.Pts.Count < 2) return;
                        RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
                        using (GraphicsPath gp = CloudPath(r, w))
                        {
                            if (fill) using (SolidBrush b = new SolidBrush(fCol)) g.FillPath(b, gp);
                            if (stroke) using (Pen p = new Pen(sCol, w)) { p.LineJoin = LineJoin.Round; g.DrawPath(p, gp); }
                        }
                        if (s.Mode != (int)CloudCallout.None && s.Pts.Count >= 3) DrawCallout(g, s, r, sCol, w);
                        break;
                    }
                case ShapeKind.Origin:
                    {
                        PointF c = s.Pts[0];
                        float r = Math.Max(6f, s.FontSize * 0.9f);
                        using (Pen p = new Pen(sCol, Math.Max(1.5f, w)))
                        {
                            g.DrawEllipse(p, c.X - r * 0.55f, c.Y - r * 0.55f, r * 1.1f, r * 1.1f);
                            g.DrawLine(p, c.X - r * 1.6f, c.Y, c.X + r * 1.6f, c.Y);
                            g.DrawLine(p, c.X, c.Y - r * 1.6f, c.X, c.Y + r * 1.6f);
                        }
                        // small X / Y arrows, Y pointing up like CAD
                        using (SolidBrush b = new SolidBrush(sCol))
                        {
                            g.FillPolygon(b, Head(new PointF(c.X + r * 2.4f, c.Y), new PointF(1, 0), r * 0.8f, r * 0.4f));
                            g.FillPolygon(b, Head(new PointF(c.X, c.Y - r * 2.4f), new PointF(0, -1), r * 0.8f, r * 0.4f));
                            using (Pen p = new Pen(sCol, Math.Max(1f, w * 0.7f)))
                            {
                                g.DrawLine(p, c.X + r * 1.6f, c.Y, c.X + r * 1.7f, c.Y);
                                g.DrawLine(p, c.X, c.Y - r * 1.6f, c.X, c.Y - r * 1.7f);
                            }
                            Font f = GetFont(Math.Max(6f, s.FontSize * 0.8f), true);
                            g.DrawString("X", f, b, c.X + r * 2.5f, c.Y - f.Height * 0.55f);
                            g.DrawString("Y", f, b, c.X + r * 0.3f, c.Y - r * 2.6f - f.Height * 0.6f);
                            string ot = sc != null && (sc.OriginX != 0 || sc.OriginY != 0) ? sc.Fmt(sc.OriginX) + " ; " + sc.Fmt(sc.OriginY) : "0,0";
                            g.DrawString(ot, GetFont(Math.Max(6f, s.FontSize * 0.85f), false), b, c.X + r * 0.6f, c.Y + r * 0.5f);
                        }
                        break;
                    }
                case ShapeKind.Coord:
                    {
                        PointF c = s.Pts[0];
                        float r = Math.Max(4f, s.FontSize * 0.5f);
                        PointF lab = s.Pts.Count > 1 ? s.Pts[1] : new PointF(c.X + r * 4, c.Y - r * 4);
                        using (Pen p = new Pen(sCol, Math.Max(1.2f, w)))
                        {
                            g.DrawLine(p, c.X - r, c.Y - r, c.X + r, c.Y + r);
                            g.DrawLine(p, c.X - r, c.Y + r, c.X + r, c.Y - r);
                            using (Pen lp = new Pen(sCol, Math.Max(1f, w * 0.6f))) g.DrawLine(lp, c, lab);
                        }
                        DrawBox(g, name ?? "", lab, GetFont(s.FontSize, false), A(s.Stroke, 100));
                        break;
                    }
                case ShapeKind.Stamp:
                    DrawStamp(g, s, sCol, fCol, fill, w);
                    break;
                case ShapeKind.Note:
                    DrawNoteIcon(g, s);
                    break;
                case ShapeKind.Markup:
                    {
                        if (s.Pts.Count < 2) return;
                        RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
                        if (s.Mode == (int)MarkupMode.HighlightText)
                        {
                            using (SolidBrush b = new SolidBrush(A(s.Fill, Math.Max(15, s.FillOpacity)))) g.FillRectangle(b, r);
                        }
                        else if (stroke)
                            using (Pen p = new Pen(sCol, w))
                            {
                                float y = s.Mode == (int)MarkupMode.StrikeOut ? r.Y + r.Height * 0.55f : r.Bottom - w / 2f;
                                g.DrawLine(p, r.X, y, r.Right, y);
                            }
                        break;
                    }
                case ShapeKind.Dimension:
                    DrawDimension(g, s, sc, sCol, w);
                    break;
                case ShapeKind.Counter:
                    {
                        float r = MarkerRadius(s), hw = MarkerHalfWidth(s);
                        PointF c = s.Pts[0];
                        RectangleF rr = new RectangleF(c.X - hw, c.Y - r, 2 * hw, 2 * r);
                        using (GraphicsPath mp = MarkerPath(s))
                        {
                            if (fill) using (SolidBrush b = new SolidBrush(fCol)) g.FillPath(b, mp);
                            if (stroke) using (Pen p = new Pen(sCol, Math.Max(1.5f, Math.Min(w, r / 4f)))) g.DrawPath(p, mp);
                        }
                        string label = name ?? "?";
                        // one line, always: the text shrinks to fit the circle (BM, 1012 … never wrap); a pill fits its text
                        float fs = r * 1.05f;
                        SizeF tsz = g.MeasureString(label, GetFont(fs, true), PointF.Empty, StringFormat.GenericTypographic);
                        float maxW = hw > r ? 2 * hw - r * 0.9f : r * 1.55f;
                        if (tsz.Width > maxW && tsz.Width > 0) fs = Math.Max(4f, fs * maxW / tsz.Width);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Max((int)sCol.A, (int)(s.StrokeOpacity * 2.55)), s.Stroke)))
                        using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
                        {
                            sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                            RectangleF tr = RectangleF.Inflate(rr, r, 0);     // wide enough that nothing is clipped
                            g.DrawString(label, GetFont(fs, true), b, tr, sf);
                        }
                        break;
                    }
                case ShapeKind.Pixelate:
                    DrawPixelate(g, s, img);
                    break;
                case ShapeKind.Highlight:
                    {
                        if (s.Pts.Count < (s.IsFocusPoly ? 3 : 2)) return;
                        using (GraphicsPath hp = HighlightPath(s))
                        {
                            // the box (a perimeter: the box round its corners)
                            RectangleF r = s.IsFocusPoly ? hp.GetBounds() : Geo.Rect(s.Pts[0], s.Pts[1]);
                            if (s.Mode == (int)HighlightMode.Marker)
                            {
                                using (SolidBrush b = new SolidBrush(fCol)) g.FillPath(b, hp);
                            }
                            else if (s.Mode == (int)HighlightMode.Magnify && img != null && r.Width > 1 && r.Height > 1)
                            {
                                float factor = s.MagnifyFactor;
                                PointF c = Geo.Center(r);
                                RectangleF dst = s.Angle != 0f ? hp.GetBounds() : r;     // turned: the whole turned outline gets the enlarged picture
                                RectangleF world = WorldOf(img);
                                RectangleF src = new RectangleF(c.X - dst.Width / factor / 2f - world.X, c.Y - dst.Height / factor / 2f - world.Y, dst.Width / factor, dst.Height / factor);
                                GraphicsState st = g.Save();
                                g.SetClip(hp, CombineMode.Intersect);
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.PixelOffsetMode = PixelOffsetMode.Half;
                                g.DrawImage(img, dst, src, GraphicsUnit.Pixel);
                                g.Restore(st);
                            }
                            else if (s.Mode == (int)HighlightMode.Pixelate)
                            {
                                GraphicsState st = g.Save();
                                g.SetClip(hp, CombineMode.Intersect);
                                if ((s.Angle != 0f || s.IsFocusPoly) && img != null)
                                {
                                    // turned or a perimeter: the box's own pixelation would miss parts of the outline
                                    RectangleF all = WorldOf(img);
                                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                                    g.PixelOffsetMode = PixelOffsetMode.Half;
                                    g.DrawImage(PixelatedOf(img, s.PixelBlock), all, new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
                                }
                                else DrawPixelate(g, s, img);
                                g.Restore(st);
                            }
                            else if (s.Mode == (int)HighlightMode.Blur && img != null)
                            {
                                GraphicsState st = g.Save();
                                g.SetClip(hp, CombineMode.Intersect);
                                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                                g.PixelOffsetMode = PixelOffsetMode.Half;
                                g.DrawImage(BlurredOf(img, s.BlurRadius), WorldOf(img), new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
                                g.Restore(st);
                            }
                            else if (s.Mode == (int)HighlightMode.GrayscaleInside && img != null)
                            {
                                GraphicsState st = g.Save();
                                g.SetClip(hp, CombineMode.Intersect);
                                g.PixelOffsetMode = PixelOffsetMode.Half;
                                g.DrawImage(GrayOf(img), WorldOf(img), new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
                                g.Restore(st);
                            }
                            // PixelateOutside and Grayscale (outside) are drawn for the whole picture in DrawGlobalHighlights
                            // every mode and shape: a border in the line colour (its cut-outs get one too)
                            if (stroke && !noBorder) using (Pen p = OutlinePen(s, sCol, w)) g.DrawPath(p, hp);
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
                                DrawStyledText(g, s.Text, s.FontSize, s.Style, b, tr, sf, s.FontFamilyName);
                            }
                        }
                        break;
                    }
                case ShapeKind.Angle:
                    DrawAngle(g, s, sCol, w, name, sc);
                    break;
                case ShapeKind.Arc:
                    {
                        if (!stroke || s.Pts.Count < 2) return;
                        List<PointF> ap = s.Pts.Count >= 3 ? Geo.ArcPoints(s.Pts[0], s.Pts[1], s.Pts[2], 96) : s.Pts;
                        using (Pen p = OutlinePen(s, sCol, w)) { p.StartCap = p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round; g.DrawLines(p, ap.ToArray()); }
                        break;
                    }
                case ShapeKind.Radius:
                    {
                        if (s.Pts.Count < 2) return;
                        if (s.Pts.Count < 3) { if (stroke) using (Pen p = new Pen(sCol, w)) g.DrawLine(p, s.Pts[0], s.Pts[1]); return; }
                        PointF o; float rad;
                        bool round = Geo.Circle3(s.Pts[0], s.Pts[1], s.Pts[2], out o, out rad);
                        List<PointF> ap = Geo.ArcPoints(s.Pts[0], s.Pts[1], s.Pts[2], 96);
                        if (stroke) using (Pen p = new Pen(sCol, w)) { p.LineJoin = LineJoin.Round; g.DrawLines(p, ap.ToArray()); }
                        if (!round) break;
                        // centre mark and a dashed radius line to the middle point
                        float cm = Math.Max(4f, s.FontSize * 0.45f);
                        using (Pen cp = new Pen(sCol, Math.Max(1f, w * 0.8f)))
                        {
                            g.DrawLine(cp, o.X - cm, o.Y, o.X + cm, o.Y);
                            g.DrawLine(cp, o.X, o.Y - cm, o.X, o.Y + cm);
                            cp.DashStyle = DashStyle.Dash;
                            g.DrawLine(cp, o, s.Pts[1]);
                        }
                        foreach (PointF q in s.Pts) using (SolidBrush b = new SolidBrush(sCol)) g.FillEllipse(b, q.X - w * 1.3f, q.Y - w * 1.3f, w * 2.6f, w * 2.6f);
                        if (s.Mode == 5) { s.LabelRect = RectangleF.Empty; break; }     // Show → None: no label, still listed
                        string t = (string.IsNullOrEmpty(name) ? "" : name + (string.IsNullOrEmpty(s.Text) ? "" : "  " + s.Text) + "\n") +
                                   RadiusText(s.Mode, sc, rad, Geo.ArcLength(s.Pts[0], s.Pts[1], s.Pts[2]));
                        PointF mid = Geo.Mid(o, s.Pts[1]);
                        PointF lc = new PointF(mid.X + s.LabelOffset.X, mid.Y + s.LabelOffset.Y);
                        if (Geo.Dist(mid, lc) > s.FontSize * 1.5f)
                            using (Pen lp = new Pen(A(s.Stroke, 80), 1f)) { lp.DashStyle = DashStyle.Dot; g.DrawLine(lp, mid, lc); }
                        s.LabelRect = DrawBoxR(g, t, lc, GetFont(s.FontSize, false), A(s.Stroke, 100));
                        break;
                    }
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

        static float DotRadius(float w, float half) { return Math.Max(w * 1.4f, half * 0.55f); }
        static float SmallDotRadius(float w, float half) { return Math.Max(w * 0.8f, DotRadius(w, half) * 0.45f); }
        static float SmallBlankRadius(float w, float half) { return Math.Max(w * 1.1f, DotRadius(w, half) * 0.6f); }
        static float BoxHalf(float w, float half) { return DotRadius(w, half) * 0.95f; }

        /// <summary>AutoCAD's closed arrow: a slender triangle, three times as long as it is wide.</summary>
        static PointF[] Slender(PointF tip, PointF u, float len, float w)
        {
            return Head(tip, u, len, Math.Max(len / 6f, w * 0.9f));
        }

        /// <summary>The datum triangle: a plain triangle, about as wide as it is long.</summary>
        static PointF[] Datum(PointF tip, PointF u, float len) { return Head(tip, u, len * 0.9f, len * 0.45f); }

        /// <summary>How far back from the tip the line has to stop for this head style.</summary>
        public static float HeadBack(int style, float w, float len, float half)
        {
            switch ((HeadStyle)style)
            {
                case HeadStyle.Filled: return len * 0.85f;
                case HeadStyle.Hollow: return len;
                case HeadStyle.Circle: return DotRadius(w, half);
                case HeadStyle.ClosedFilled: return len * 0.92f;
                case HeadStyle.ClosedBlank: return len;
                case HeadStyle.DotSmallBlank: return SmallBlankRadius(w, half);
                case HeadStyle.Box: return BoxHalf(w, half);
                case HeadStyle.DatumTriangle: return len * 0.9f;
                case HeadStyle.DatumTriangleFilled: return len * 0.85f;
                case HeadStyle.HalfClosed: return len;          // the line stops at the head's base: a thick line can't poke out past its slant
                default: return 0f;           // Closed, Open, Open 30, ticks and solid dots: the line runs to the tip
            }
        }

        /// <summary>Draws one line end. tip = end point, u = direction pointing out of the line.</summary>
        public static void DrawHead(Graphics g, PointF tip, PointF u, int style, Color col, float w, float len, float half)
        {
            if (style <= 0) return;
            PointF n = new PointF(-u.Y, u.X);
            using (SolidBrush br = new SolidBrush(col))
            using (Pen p = new Pen(col, Math.Max(1f, w)))
            {
                p.LineJoin = LineJoin.Miter; p.StartCap = p.EndCap = LineCap.Round;
                switch ((HeadStyle)style)
                {
                    case HeadStyle.Filled:
                        g.FillPolygon(br, Head(tip, u, len, half));
                        break;
                    case HeadStyle.Open:
                        {
                            PointF[] h = Head(tip, u, len * 0.95f, half);
                            g.DrawLines(p, new PointF[] { h[1], tip, h[2] });
                            break;
                        }
                    case HeadStyle.Hollow:
                        using (Pen hp = new Pen(col, Math.Max(1f, w * 0.85f))) { hp.LineJoin = LineJoin.Miter; g.DrawPolygon(hp, Head(tip, u, len, half)); }
                        break;
                    case HeadStyle.Slash:
                        {
                            // architectural tick: a short stroke at 45° through the end point
                            float k = half * 0.85f;
                            PointF d = new PointF((u.X + n.X) * 0.7071f * k, (u.Y + n.Y) * 0.7071f * k);
                            using (Pen sp = new Pen(col, Math.Max(1.5f, w * 1.4f))) g.DrawLine(sp, tip.X - d.X, tip.Y - d.Y, tip.X + d.X, tip.Y + d.Y);
                            break;
                        }
                    case HeadStyle.Tick:
                        using (Pen tp = new Pen(col, Math.Max(1.5f, w * 1.2f)))
                            g.DrawLine(tp, tip.X - n.X * half, tip.Y - n.Y * half, tip.X + n.X * half, tip.Y + n.Y * half);
                        break;
                    case HeadStyle.Dot:
                        {
                            float r = DotRadius(w, half);
                            g.FillEllipse(br, tip.X - r, tip.Y - r, 2 * r, 2 * r);
                            break;
                        }
                    case HeadStyle.Circle:
                        {
                            float r = DotRadius(w, half);
                            using (Pen cp = new Pen(col, Math.Max(1f, w * 0.85f))) g.DrawEllipse(cp, tip.X - r, tip.Y - r, 2 * r, 2 * r);
                            break;
                        }
                    case HeadStyle.Diamond:
                        {
                            float r = DotRadius(w, half) * 1.35f;
                            g.FillPolygon(br, new PointF[] {
                                new PointF(tip.X + u.X * r, tip.Y + u.Y * r), new PointF(tip.X + n.X * r * 0.7f, tip.Y + n.Y * r * 0.7f),
                                new PointF(tip.X - u.X * r, tip.Y - u.Y * r), new PointF(tip.X - n.X * r * 0.7f, tip.Y - n.Y * r * 0.7f) });
                            break;
                        }
                    case HeadStyle.Square:
                        {
                            float r = DotRadius(w, half) * 0.95f;
                            g.FillPolygon(br, new PointF[] {
                                new PointF(tip.X + (u.X + n.X) * r, tip.Y + (u.Y + n.Y) * r), new PointF(tip.X + (-u.X + n.X) * r, tip.Y + (-u.Y + n.Y) * r),
                                new PointF(tip.X + (-u.X - n.X) * r, tip.Y + (-u.Y - n.Y) * r), new PointF(tip.X + (u.X - n.X) * r, tip.Y + (u.Y - n.Y) * r) });
                            break;
                        }
                    // ---- AutoCAD's own list (10 and up) ----
                    case HeadStyle.ClosedFilled:
                        g.FillPolygon(br, Slender(tip, u, len, w));
                        break;
                    case HeadStyle.ClosedBlank:
                        using (Pen hp = new Pen(col, Math.Max(1f, w * 0.7f))) { hp.LineJoin = LineJoin.Miter; g.DrawPolygon(hp, Slender(tip, u, len, w)); }
                        break;
                    case HeadStyle.Closed:       // the outline with the line running on to the tip
                        using (Pen hp = new Pen(col, Math.Max(1f, w * 0.7f))) { hp.LineJoin = LineJoin.Miter; g.DrawPolygon(hp, Slender(tip, u, len, w)); }
                        break;
                    case HeadStyle.Oblique:      // a thin 45° stroke, as wide as the line
                        {
                            float k = half * 0.85f;
                            PointF d = new PointF((u.X + n.X) * 0.7071f * k, (u.Y + n.Y) * 0.7071f * k);
                            using (Pen sp = new Pen(col, Math.Max(1f, w))) g.DrawLine(sp, tip.X - d.X, tip.Y - d.Y, tip.X + d.X, tip.Y + d.Y);
                            break;
                        }
                    case HeadStyle.Open30:       // two thin strokes, 30° apart
                        {
                            float hl2 = len * 1.15f;
                            PointF[] h = Head(tip, u, hl2, hl2 * 0.2679f);     // tan 15°
                            g.DrawLines(p, new PointF[] { h[1], tip, h[2] });
                            break;
                        }
                    case HeadStyle.DotSmall:
                        {
                            float r = SmallDotRadius(w, half);
                            g.FillEllipse(br, tip.X - r, tip.Y - r, 2 * r, 2 * r);
                            break;
                        }
                    case HeadStyle.DotSmallBlank:
                        {
                            float r = SmallBlankRadius(w, half);
                            using (Pen cp = new Pen(col, Math.Max(1f, w * 0.85f))) g.DrawEllipse(cp, tip.X - r, tip.Y - r, 2 * r, 2 * r);
                            break;
                        }
                    case HeadStyle.Box:
                        {
                            float r = BoxHalf(w, half);
                            using (Pen bp = new Pen(col, Math.Max(1f, w * 0.85f)))
                            {
                                bp.LineJoin = LineJoin.Miter;
                                g.DrawPolygon(bp, new PointF[] {
                                    new PointF(tip.X + (u.X + n.X) * r, tip.Y + (u.Y + n.Y) * r), new PointF(tip.X + (-u.X + n.X) * r, tip.Y + (-u.Y + n.Y) * r),
                                    new PointF(tip.X + (-u.X - n.X) * r, tip.Y + (-u.Y - n.Y) * r), new PointF(tip.X + (u.X - n.X) * r, tip.Y + (u.Y - n.Y) * r) });
                            }
                            break;
                        }
                    case HeadStyle.DatumTriangle:
                        using (Pen hp = new Pen(col, Math.Max(1f, w * 0.85f))) { hp.LineJoin = LineJoin.Miter; g.DrawPolygon(hp, Datum(tip, u, len)); }
                        break;
                    case HeadStyle.DatumTriangleFilled:
                        g.FillPolygon(br, Datum(tip, u, len));
                        break;
                    case HeadStyle.HalfClosed:   // a filled half arrow (harpoon): flush with one edge of the line, flaring out on the other
                        {
                            float e = w / 2f, flare = Math.Max(len * 0.32f, w * 1.2f);
                            PointF a = new PointF(tip.X - n.X * e, tip.Y - n.Y * e);                                 // tip, on the line's far edge
                            PointF b = new PointF(a.X - u.X * len, a.Y - u.Y * len);                                 // straight back along that edge
                            PointF c = new PointF(tip.X - u.X * len + n.X * (e + flare), tip.Y - u.Y * len + n.Y * (e + flare));   // the barb
                            g.FillPolygon(br, new PointF[] { a, b, c });
                            break;
                        }
                    case HeadStyle.HalfOpen:     // one barb, from the tip out past the line's near edge
                        {
                            float e = w / 2f, flare = Math.Max(len * 0.32f, w * 1.2f);
                            PointF c = new PointF(tip.X - u.X * len * 0.95f + n.X * (e + flare), tip.Y - u.Y * len * 0.95f + n.Y * (e + flare));
                            g.DrawLine(p, tip, c);
                            break;
                        }
                }
            }
        }

        /// <summary>The point halfway along an open path (for its label).</summary>
        public static PointF PathMiddle(IList<PointF> p)
        {
            double total = Geo.Perimeter(p, false), half = total / 2, run = 0;
            for (int i = 0; i + 1 < p.Count; i++)
            {
                double d = Geo.Dist(p[i], p[i + 1]);
                if (run + d >= half && d > 0)
                {
                    float t = (float)((half - run) / d);
                    return new PointF(p[i].X + (p[i + 1].X - p[i].X) * t, p[i].Y + (p[i + 1].Y - p[i].Y) * t);
                }
                run += d;
            }
            return p.Count > 0 ? p[p.Count - 1] : PointF.Empty;
        }

        /// <summary>Revision cloud: scallops all round the rectangle, bulging outwards.</summary>
        public static GraphicsPath CloudPath(RectangleF r, float w)
        {
            if (r.Width < 2 || r.Height < 2) { GraphicsPath gp = new GraphicsPath(); gp.AddRectangle(r); return gp; }
            return CloudPolyPath(new PointF[] { new PointF(r.X, r.Y), new PointF(r.Right, r.Y), new PointF(r.Right, r.Bottom), new PointF(r.X, r.Bottom) }, true, w);
        }

        /// <summary>Scalloped outline along any polygon or path: half-circles bulging away from the middle of the shape.</summary>
        public static GraphicsPath CloudPolyPath(IList<PointF> pts, bool closed, float w)
        {
            GraphicsPath gp = new GraphicsPath();
            if (pts.Count < 2) return gp;
            float target = Math.Max(10f, Math.Min(60f, 9f + w * 5f));
            PointF mid = closed && pts.Count >= 3 ? Geo.Centroid(pts) : Geo.Average(pts);
            List<PointF> line = new List<PointF>();
            int edges = closed ? pts.Count : pts.Count - 1;
            for (int e = 0; e < edges; e++)
            {
                PointF a = pts[e], b = pts[(e + 1) % pts.Count];
                float len = Geo.Dist(a, b);
                if (len < 0.5f) continue;
                PointF u = new PointF((b.X - a.X) / len, (b.Y - a.Y) / len);
                PointF n = new PointF(u.Y, -u.X);
                PointF em = Geo.Mid(a, b);
                if ((em.X - mid.X) * n.X + (em.Y - mid.Y) * n.Y < 0) n = new PointF(-n.X, -n.Y);   // bulge away from the middle
                int arcs = Math.Max(1, (int)Math.Round(len / target));
                float d = len / arcs, rad = d / 2f;
                for (int i = 0; i < arcs; i++)
                {
                    PointF c = new PointF(a.X + u.X * (d * i + rad), a.Y + u.Y * (d * i + rad));
                    for (int k = 0; k <= 10; k++)
                    {
                        double t = Math.PI * k / 10.0;
                        float cx = (float)-Math.Cos(t) * rad, sy = (float)Math.Sin(t) * rad;
                        line.Add(new PointF(c.X + u.X * cx + n.X * sy, c.Y + u.Y * cx + n.Y * sy));
                    }
                }
            }
            if (line.Count >= 2) gp.AddLines(line.ToArray());
            if (closed) gp.CloseFigure();
            return gp;
        }

        /// <summary>A focus shape's outline in picture coordinates, turned by its angle.</summary>
        public static GraphicsPath HighlightPath(Shape s)
        {
            GraphicsPath p;
            if (s.IsFocusPoly)
            {
                // perimeter: the clicked corners
                p = new GraphicsPath();
                if (s.Pts.Count >= 3) p.AddPolygon(s.Pts.ToArray());
            }
            else
            {
                RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
                p = HighlightPath(r, s.Form);
                if (s.Angle != 0f)
                    using (Matrix m = new Matrix())
                    {
                        m.RotateAt(s.Angle, Geo.Center(r));
                        p.Transform(m);
                    }
            }
            // cut-outs (X): holes in the focus, like openings in an area (the path fills alternately, so they stay out)
            if (s.Holes != null)
                foreach (List<PointF> h in s.Holes) if (h.Count >= 3) p.AddPolygon(h.ToArray());
            return p;
        }

        /// <summary>Outline of a highlight area in the chosen form.</summary>
        public static GraphicsPath HighlightPath(RectangleF r, int form)
        {
            GraphicsPath p = new GraphicsPath();
            switch ((HighlightForm)form)
            {
                case HighlightForm.Ellipse: p.AddEllipse(r); break;
                case HighlightForm.Triangle:
                    p.AddPolygon(new PointF[] { new PointF(r.X + r.Width / 2f, r.Y), new PointF(r.Right, r.Bottom), new PointF(r.X, r.Bottom) });
                    break;
                case HighlightForm.Diamond:
                    p.AddPolygon(new PointF[] { new PointF(r.X + r.Width / 2f, r.Y), new PointF(r.Right, r.Y + r.Height / 2f),
                                                new PointF(r.X + r.Width / 2f, r.Bottom), new PointF(r.X, r.Y + r.Height / 2f) });
                    break;
                case HighlightForm.Rounded:
                    p.Dispose();
                    return RoundRect(r, Math.Min(r.Width, r.Height) * 0.2f);
                default: p.AddRectangle(r); break;
            }
            return p;
        }

        static void DrawOpenPath(Graphics g, Shape s, ScaleInfo sc, string name, Color sCol, float w, bool stroke)
        {
            if (stroke)
                using (Pen p = OutlinePen(s, sCol, w))
                {
                    p.LineJoin = LineJoin.Round; p.StartCap = p.EndCap = LineCap.Round;
                    if (IsCloudStyle(s)) using (GraphicsPath cp = CloudPolyPath(s.Pts, false, w)) g.DrawPath(p, cp);
                    else g.DrawLines(p, s.Pts.ToArray());
                }
            using (SolidBrush b = new SolidBrush(sCol))
                foreach (PointF q in s.Pts) g.FillEllipse(b, q.X - w * 1.3f, q.Y - w * 1.3f, w * 2.6f, w * 2.6f);
            string t = (string.IsNullOrEmpty(name) ? "" : name + (string.IsNullOrEmpty(s.Text) ? "" : "  " + s.Text) + "\n") + "L = " + sc.Len(Geo.Perimeter(s.Pts, false));
            DrawBox(g, t, PathMiddle(s.Pts), GetFont(s.FontSize, false), A(s.Stroke, 100));
        }

        /// <summary>Box of a cloud's callout (revision triangle or text box), centred on its tag point.</summary>
        public static RectangleF CalloutRect(Shape s)
        {
            if (s.Pts.Count < 3) return RectangleF.Empty;
            PointF t = s.Pts[2];
            Font f = GetFont(s.FontSize, true);
            string txt = string.IsNullOrEmpty(s.Text) ? " " : s.Text;
            SizeF sz = MeasureG.MeasureString(txt, f);
            if (s.Mode == (int)CloudCallout.Triangle)
            {
                float side = Math.Max(s.FontSize * 2.6f, sz.Width * 2.1f);
                float h = side * 0.866f;
                return new RectangleF(t.X - side / 2f, t.Y - h * 0.62f, side, h);
            }
            float bw = Math.Max(sz.Width + s.FontSize, s.FontSize * 3f), bh = sz.Height + s.FontSize * 0.5f;
            return new RectangleF(t.X - bw / 2f, t.Y - bh / 2f, bw, bh);
        }

        static void DrawCallout(Graphics g, Shape s, RectangleF cloud, Color col, float w)
        {
            RectangleF tr = CalloutRect(s);
            PointF t = s.Pts[2];
            // leader: from the cloud edge nearest the tag, with a short level tail into the tag (like a drawing callout)
            PointF from = new PointF(Geo.Clamp(t.X, cloud.X, cloud.Right), Geo.Clamp(t.Y, cloud.Y, cloud.Bottom));
            if (cloud.Contains(t)) from = new PointF(cloud.Right, Geo.Center(cloud).Y);
            float tailX = t.X < cloud.X ? tr.Right + s.FontSize * 0.6f : tr.X - s.FontSize * 0.6f;
            PointF elbow = new PointF(tailX, t.Y), into = new PointF(t.X < cloud.X ? tr.Right - tr.Width * 0.25f : tr.X + tr.Width * 0.25f, t.Y);
            using (Pen p = new Pen(col, Math.Max(1f, w * 0.8f)))
            {
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new PointF[] { from, elbow, into });
            }
            using (Pen p = new Pen(col, Math.Max(1.2f, w * 0.9f)))
            using (SolidBrush white = new SolidBrush(Color.White))
            using (SolidBrush ink = new SolidBrush(col))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                Font f = GetFont(s.FontSize, true);
                if (s.Mode == (int)CloudCallout.Triangle)
                {
                    PointF[] tri = { new PointF(tr.X + tr.Width / 2f, tr.Y), new PointF(tr.Right, tr.Bottom), new PointF(tr.X, tr.Bottom) };
                    g.FillPolygon(white, tri);
                    g.DrawPolygon(p, tri);
                    g.DrawString(s.Text ?? "", f, ink, new RectangleF(tr.X, tr.Y + tr.Height * 0.3f, tr.Width, tr.Height * 0.7f), sf);
                }
                else
                {
                    g.FillRectangle(white, tr);
                    g.DrawRectangle(p, tr.X, tr.Y, tr.Width, tr.Height);
                    g.DrawString(s.Text ?? "", f, ink, tr, sf);
                }
            }
        }

        static void DrawStamp(Graphics g, Shape s, Color col, Color fCol, bool fill, float w)
        {
            if (s.Pts.Count < 2) return;
            RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
            if (r.Width < 4 || r.Height < 4) return;
            string[] lines = (s.Text ?? "").Replace("\r", "").Split('\n');
            string main = lines.Length > 0 ? lines[0] : "";
            string sub = lines.Length > 1 ? string.Join("\n", lines, 1, lines.Length - 1) : "";
            float bw = Math.Max(1.5f, w);
            float rad = Math.Min(r.Height * 0.18f, 14f);
            using (GraphicsPath gp = RoundRect(r, rad))
            {
                if (fill) using (SolidBrush b = new SolidBrush(fCol)) g.FillPath(b, gp);
                using (Pen p = new Pen(col, bw)) g.DrawPath(p, gp);
            }
            RectangleF inner = RectangleF.Inflate(r, -bw * 2.2f, -bw * 2.2f);
            if (inner.Width > 4 && inner.Height > 4)
                using (GraphicsPath gp = RoundRect(inner, Math.Max(1f, rad - bw * 2f)))
                using (Pen p = new Pen(col, Math.Max(1f, bw * 0.5f))) g.DrawPath(p, gp);
            RectangleF tr = RectangleF.Inflate(inner, -bw * 2f, -bw);
            bool hasSub = sub.Trim().Length > 0;
            RectangleF mainR = hasSub ? new RectangleF(tr.X, tr.Y, tr.Width, tr.Height * 0.62f) : tr;
            RectangleF subR = new RectangleF(tr.X, tr.Y + tr.Height * 0.6f, tr.Width, tr.Height * 0.4f);
            using (SolidBrush b = new SolidBrush(col))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                // the main word fills the stamp: shrink until it fits the width
                float fs = Math.Max(6f, mainR.Height * 0.62f);
                Font f = GetFont(fs, true);
                SizeF sz = g.MeasureString(main.Length > 0 ? main : " ", f);
                if (sz.Width > mainR.Width) { fs = Math.Max(6f, fs * mainR.Width / sz.Width); f = GetFont(fs, true); }
                g.DrawString(main, f, b, mainR, sf);
                if (hasSub)
                {
                    float ss = Math.Max(5f, Math.Min(fs * 0.55f, subR.Height * 0.85f));
                    Font f2 = GetFont(ss, false);
                    SizeF s2 = g.MeasureString(sub, f2);
                    if (s2.Width > subR.Width) f2 = GetFont(Math.Max(5f, ss * subR.Width / s2.Width), false);
                    g.DrawString(sub, f2, b, subR, sf);
                }
            }
        }

        public static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Max(0.5f, Math.Min(rad * 2f, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Size of a non-printing note's icon, in image pixels.</summary>
        public static float NoteSize(Shape s) { return Math.Max(14f, s.FontSize * 1.5f); }

        public static RectangleF NoteRect(Shape s)
        {
            float n = NoteSize(s);
            return new RectangleF(s.Pts[0].X, s.Pts[0].Y, n, n);
        }

        static void DrawNoteIcon(Graphics g, Shape s)
        {
            RectangleF r = NoteRect(s);
            float fold = r.Width * 0.3f;
            PointF[] body = { new PointF(r.X, r.Y), new PointF(r.Right, r.Y), new PointF(r.Right, r.Bottom - fold), new PointF(r.Right - fold, r.Bottom), new PointF(r.X, r.Bottom) };
            using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 255, 214, 64))) g.FillPolygon(b, body);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 214, 160, 20)))
                g.FillPolygon(b, new PointF[] { new PointF(r.Right, r.Bottom - fold), new PointF(r.Right - fold, r.Bottom - fold), new PointF(r.Right - fold, r.Bottom) });
            using (Pen p = new Pen(Color.FromArgb(150, 110, 0), Math.Max(1f, r.Width / 18f))) { p.LineJoin = LineJoin.Round; g.DrawPolygon(p, body); }
            using (Pen p = new Pen(Color.FromArgb(150, 110, 0), Math.Max(0.8f, r.Width / 22f)))
                for (int i = 1; i <= 3; i++)
                {
                    float y = r.Y + r.Height * (0.22f + i * 0.17f);
                    g.DrawLine(p, r.X + r.Width * 0.2f, y, r.Right - r.Width * (i == 3 ? 0.45f : 0.2f), y);
                }
        }

        static void DrawAngle(Graphics g, Shape s, Color col, float w, string name, ScaleInfo sc)
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
                string t = sc != null ? sc.Angle(deg) : ScaleInfo.AngleText(deg);
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

        /// <summary>area: where the spotlight darkens (the captured picture, or a pasted picture's box).</summary>
        static void DrawGlobalHighlights(Graphics g, List<Shape> shapes, Bitmap img, RectangleF area)
        {
            if (img == null) return;
            List<Shape> spot = new List<Shape>(), gray = new List<Shape>(), pixOut = new List<Shape>(), blurOut = new List<Shape>();
            int spotOp = 0;
            foreach (Shape s in shapes)
            {
                if (!IsGlobalFocus(s)) continue;
                if (s.Mode == (int)HighlightMode.Spotlight) { spot.Add(s); spotOp = Math.Max(spotOp, s.FillOpacity); }
                else if (s.Mode == (int)HighlightMode.Grayscale) gray.Add(s);
                else if (s.Mode == (int)HighlightMode.PixelateOutside) pixOut.Add(s);
                else if (s.Mode == (int)HighlightMode.BlurOutside) blurOut.Add(s);
            }
            // the whole picture, pasted pictures lying outside it included
            RectangleF all = WorldOf(img), px = new RectangleF(0, 0, img.Width, img.Height);
            if (pixOut.Count > 0)
            {
                // everything outside the shapes is pixelated (block size from the first one's strength)
                int block = pixOut[0].PixelBlock;
                using (Region rg = new Region(area))
                {
                    foreach (Shape s in pixOut) using (GraphicsPath hp = HighlightPath(s)) rg.Exclude(hp);
                    GraphicsState st = g.Save();
                    g.SetClip(rg, CombineMode.Intersect);
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(PixelatedOf(img, block), all, px, GraphicsUnit.Pixel);
                    g.Restore(st);
                }
            }
            if (blurOut.Count > 0)
            {
                // everything outside the shapes is blurred (strength from the first one)
                using (Region rg = new Region(area))
                {
                    foreach (Shape s in blurOut) using (GraphicsPath hp = HighlightPath(s)) rg.Exclude(hp);
                    GraphicsState st = g.Save();
                    g.SetClip(rg, CombineMode.Intersect);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(BlurredOf(img, blurOut[0].BlurRadius), all, px, GraphicsUnit.Pixel);
                    g.Restore(st);
                }
            }
            if (gray.Count > 0)
            {
                using (Region rg = new Region(area))
                {
                    foreach (Shape s in gray) using (GraphicsPath hp = HighlightPath(s)) rg.Exclude(hp);
                    GraphicsState st = g.Save();
                    g.SetClip(rg, CombineMode.Intersect);
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(GrayOf(img), all, px, GraphicsUnit.Pixel);
                    g.Restore(st);
                }
            }
            if (spot.Count > 0)
            {
                using (Region rg = new Region(area))
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(Math.Max(10, spotOp) * 2.55), 0, 0, 0)))
                {
                    foreach (Shape s in spot) using (GraphicsPath hp = HighlightPath(s)) rg.Exclude(hp);
                    g.FillRegion(b, rg);
                }
            }
        }

        /// <summary>Focus modes drawn for the whole picture (outside the shapes) before the other shapes.</summary>
        static bool IsGlobalFocus(Shape s)
        {
            return s.Kind == ShapeKind.Highlight && s.Pts.Count >= (s.IsFocusPoly ? 3 : 2) &&
                   (s.Mode == (int)HighlightMode.Spotlight || s.Mode == (int)HighlightMode.Grayscale ||
                    s.Mode == (int)HighlightMode.PixelateOutside || s.Mode == (int)HighlightMode.BlurOutside);
        }

        /// <summary>Focus modes that show the picture changed (gray, pixelated, magnified): they need the picture as it looks.</summary>
        static bool UsesPicture(Shape s)
        {
            if (s.Kind == ShapeKind.Pixelate) return true;
            return s.Kind == ShapeKind.Highlight && s.Mode != (int)HighlightMode.Marker && s.Mode != (int)HighlightMode.Spotlight;
        }

        /// <summary>The outline a pasted picture covers (turned with it).</summary>
        static GraphicsPath ImageOutline(Shape s)
        {
            RectangleF r = Geo.Rect(s.Pts[0], s.Pts[1]);
            GraphicsPath p = new GraphicsPath();
            p.AddRectangle(r);
            if (s.Angle != 0f)
                using (Matrix m = new Matrix()) { m.RotateAt(s.Angle, Geo.Center(r)); p.Transform(m); }
            return p;
        }

        /// <summary>Where a picture lies on the drawing: the picture itself at 0,0, or the picture with the pasted
        /// pictures, which may reach past its edges.</summary>
        static RectangleF WorldOf(Bitmap b)
        {
            if (b != null && ReferenceEquals(b, picsCache)) return picsWorld;
            return b == null ? RectangleF.Empty : new RectangleF(0, 0, b.Width, b.Height);
        }

        /// <summary>Set while a focus effect is drawn a second time over pasted pictures: its border is drawn once.</summary>
        [ThreadStatic] static bool noBorder;

        static Bitmap picsCache;
        static Bitmap picsFor;
        static string picsKey;
        static RectangleF picsWorld;

        /// <summary>The picture with the pasted pictures on it, so focus effects (pixelate, grayscale, magnify) also
        /// change what was pasted. Kept until the picture or a pasted picture changes.</summary>
        static Bitmap WithPictures(Bitmap img, List<Shape> shapes, ScaleInfo sc, Shape skip)
        {
            StringBuilder key = new StringBuilder();
            foreach (Shape s in shapes)
            {
                if (s == skip || s.Kind != ShapeKind.Image || s.Img == null || s.Pts.Count < 2) continue;
                key.Append(RuntimeHelpers.GetHashCode(s.Img)).Append(':');
                foreach (PointF p in s.Pts) key.Append(p.X).Append(',').Append(p.Y).Append(';');
                key.Append(s.FillOpacity).Append('|').Append(s.Angle).Append('|').Append(s.Width).Append('|').Append(s.Stroke.ToArgb()).Append('|')
                   .Append(s.StrokeOpacity).Append('|').Append(s.Shadow).Append('/');
            }
            if (key.Length == 0) return img;
            string k = key.ToString();
            if (picsCache != null && picsFor == img && picsKey == k) return picsCache;
            // only where the pasted pictures lie (they may reach past the captured picture): the screenshot's own
            // grayscale / pixelated copies stay valid while a picture is moved, and this stays small
            RectangleF world = RectangleF.Empty;
            foreach (Shape s in shapes)
            {
                if (s == skip || s.Kind != ShapeKind.Image || s.Img == null || s.Pts.Count < 2) continue;
                RectangleF b = ShapeOps.Bounds(s);
                b.Inflate(s.Width + 12f, s.Width + 12f);          // frame and shadow
                world = world.IsEmpty ? b : RectangleF.Union(world, b);
            }
            Rectangle wr = Rectangle.FromLTRB((int)Math.Floor(world.Left), (int)Math.Floor(world.Top), (int)Math.Ceiling(world.Right), (int)Math.Ceiling(world.Bottom));
            // keep it sane: pictures pasted miles apart must not ask for a giant bitmap
            if (wr.Width < 1 || wr.Height < 1 || (long)wr.Width * wr.Height > 120000000L) return img;
            Bitmap res = new Bitmap(wr.Width, wr.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(res))
            {
                g.TranslateTransform(-wr.X, -wr.Y);
                g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height), new Rectangle(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (Shape s in shapes)
                {
                    if (s == skip || s.Kind != ShapeKind.Image || s.Img == null || s.Pts.Count < 2) continue;
                    try
                    {
                        if (s.Shadow && s.CanShadow) DrawShadow(g, s, img, sc, null);
                        Draw(g, s, img, sc, null);
                    }
                    catch (Exception) { }
                }
            }
            Bitmap old = picsCache;
            picsCache = res; picsFor = img; picsKey = k; picsWorld = wr;
            if (old != null) { ReleaseCache(old); old.Dispose(); }
            return res;
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
                float s0 = HeadBack(s.HeadStart, w, hl, hw), s1 = L - HeadBack(s.HeadEnd, w, hl, hw);
                float gapHalf = tsz.Width / 2f + 4f + w;
                // 1.4.2 label position (LabelAt): 0 = on the line (in a gap, above when it doesn't fit), 1 = above, 2 = below
                int place = s.LabelAt;
                bool inline = place == 0 && s1 - s0 > 2f * gapHalf + 6f;
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
                DrawHead(g, a, new PointF(-u.X, -u.Y), s.HeadStart, col, w, hl, hw);
                DrawHead(g, b, u, s.HeadEnd, col, w, hl, hw);
                if (inline) DrawRotLabel(g, t, Geo.Mid(a, b), ang, f, tcol, 0f, true);
                else DrawRotLabel(g, t, Geo.Mid(a, b), ang, f, tcol, hw + 2f, false, place == 2);
            }
        }

        static void DrawRotLabel(Graphics g, string t, PointF at, float ang, Font f, Color col, float off, bool centred)
        {
            DrawRotLabel(g, t, at, ang, f, col, off, centred, false);
        }

        /// <summary>Text along a line. centred = in the line's gap; otherwise just above (or below) the line, off = distance.</summary>
        static void DrawRotLabel(Graphics g, string t, PointF at, float ang, Font f, Color col, float off, bool centred, bool below)
        {
            if (shadowPass) return;
            GraphicsState st = g.Save();
            g.TranslateTransform(at.X, at.Y);
            g.RotateTransform(ang);
            SizeF sz = g.MeasureString(t, f);
            RectangleF r = centred
                ? new RectangleF(-sz.Width / 2f - 2f, -sz.Height / 2f, sz.Width + 4f, sz.Height)
                : below ? new RectangleF(-sz.Width / 2f - 2f, off, sz.Width + 4f, sz.Height)
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

        public static void DrawBox(Graphics g, string t, PointF c, Font f, Color col) { DrawBoxR(g, t, c, f, col); }

        /// <summary>Same, and returns the box it drew.</summary>
        public static RectangleF DrawBoxR(Graphics g, string t, PointF c, Font f, Color col)
        {
            SizeF sz = g.MeasureString(t, f);
            RectangleF r = new RectangleF(c.X - sz.Width / 2f - 4f, c.Y - sz.Height / 2f - 2f, sz.Width + 8f, sz.Height + 4f);
            DrawBoxAt(g, t, r, f, col);
            return r;
        }

        static void DrawBoxAt(Graphics g, string t, RectangleF r, Font f, Color col)
        {
            if (shadowPass) return;
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
            Rectangle world = Rectangle.Round(WorldOf(img));         // the picture may reach past 0,0 (pasted pictures)
            r.Intersect(world);
            if (r.Width < 1 || r.Height < 1) return;
            int block = s.PixelBlock;
            string key = r.ToString() + "|" + block + "|" + img.GetHashCode() + "|" + world.ToString();
            if (s.Cache == null || s.CacheKey != key)
            {
                Bitmap big = Blocks(img, new Rectangle(r.X - world.X, r.Y - world.Y, r.Width, r.Height), block);
                if (s.Cache != null) s.Cache.Dispose();
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
                DrawAll(g, shapes, img, sc, null, true);
            }
            return b;
        }
    }

    public enum HKind { Point, Box, Rotate, Center, DimOffset, Tail, Label, LabelSize, HolePoint }

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
            if (s.Kind == ShapeKind.Counter || s.Kind == ShapeKind.Coord || s.Kind == ShapeKind.Origin) return s.Pts[0];
            if (s.Kind == ShapeKind.Note) return Geo.Center(Renderer.NoteRect(s));
            if (s.IsPoly && !s.IsOpenPath && s.Pts.Count >= 3) return Geo.Centroid(s.Pts);
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

        /// <summary>A cut-out corner's handle index is cut-out × HoleStride + corner.</summary>
        public const int HoleStride = 100000;

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
                case ShapeKind.Highlight:
                    if (!s.IsFocusPoly) goto default;
                    goto case ShapeKind.Polygon;                       // a focus perimeter: its corners, like an area
                case ShapeKind.Polygon:
                case ShapeKind.Area:
                case ShapeKind.Polyline:
                case ShapeKind.Perimeter:
                case ShapeKind.Freehand:
                    {
                        // a traced fill (many corners, or holes) is colour, not geometry: no corner handles
                        bool traced = Canvas.IsFillShape(s) && (s.Holes != null || s.Pts.Count > 12);
                        if (s.Kind != ShapeKind.Freehand && !traced)
                            for (int i = 0; i < s.Pts.Count; i++) h.Add(new Handle(s.Pts[i], HKind.Point, i));
                        // the corners of its cut-outs: drag to reshape a cut-out (Ctrl+click removes a corner)
                        if (s.Holes != null && !traced)
                            for (int k = 0; k < s.Holes.Count; k++)
                                for (int i = 0; i < s.Holes[k].Count && i < HoleStride; i++) h.Add(new Handle(s.Holes[k][i], HKind.HolePoint, k * HoleStride + i));
                        // area / perimeter label: yellow knob in its middle = move it, corner = size of its text
                        if ((s.Kind == ShapeKind.Area || (s.Kind == ShapeKind.Perimeter && !s.IsOpenPath)) && !s.LabelRect.IsEmpty)
                        {
                            h.Add(new Handle(Geo.Center(s.LabelRect), HKind.Label, 0));
                            h.Add(new Handle(new PointF(s.LabelRect.Right, s.LabelRect.Bottom), HKind.LabelSize, 0));
                        }
                        RectangleF b = RawBounds(s.Pts);
                        h.Add(new Handle(new PointF(b.X + b.Width / 2f, b.Y - rotDist), HKind.Rotate, 0));
                        break;
                    }
                case ShapeKind.Text:
                    {
                        RectangleF r = LocalRect(s);
                        PointF c = Center(s);
                        h.Add(new Handle(Geo.Rot(new PointF(c.X, r.Y - rotDist - 2), c, s.Angle), HKind.Rotate, 0));
                        // 8 handles on the text box (as drawn, with its padding): corners and sides resize it, the text wraps inside
                        RectangleF fr = RectangleF.FromLTRB(r.X - 4, r.Y - 2, r.Right + 4, r.Bottom + 2);
                        for (int i = 0; i < 8; i++) h.Add(new Handle(Geo.Rot(BoxPoint(fr, i), c, s.Angle), HKind.Box, i));
                        break;
                    }
                case ShapeKind.Coord:
                    h.Add(new Handle(s.Pts[0], HKind.Point, 0));
                    if (s.Pts.Count > 1) h.Add(new Handle(s.Pts[1], HKind.Tail, 1));
                    break;
                case ShapeKind.Arc:
                case ShapeKind.Radius:
                    for (int i = 0; i < s.Pts.Count; i++) h.Add(new Handle(s.Pts[i], HKind.Point, i));   // start, middle, end
                    if (s.Kind == ShapeKind.Radius && !s.LabelRect.IsEmpty)
                    {
                        h.Add(new Handle(Geo.Center(s.LabelRect), HKind.Label, 0));
                        h.Add(new Handle(new PointF(s.LabelRect.Right, s.LabelRect.Bottom), HKind.LabelSize, 0));
                    }
                    break;
                case ShapeKind.Origin:
                    h.Add(new Handle(s.Pts[0], HKind.Point, 0));
                    break;
                case ShapeKind.Counter:
                    {
                        // number / letter marker: drag a corner to make the disc (and its text) bigger or smaller
                        float r = Renderer.MarkerRadius(s), hw = Renderer.MarkerHalfWidth(s);
                        PointF c = s.Pts[0];
                        h.Add(new Handle(new PointF(c.X - hw, c.Y - r), HKind.Box, 0));
                        h.Add(new Handle(new PointF(c.X + hw, c.Y - r), HKind.Box, 2));
                        h.Add(new Handle(new PointF(c.X + hw, c.Y + r), HKind.Box, 4));
                        h.Add(new Handle(new PointF(c.X - hw, c.Y + r), HKind.Box, 6));
                        break;
                    }
                case ShapeKind.Note:
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
                        if (s.Kind == ShapeKind.Cloud && s.Mode != 0 && s.Pts.Count > 2) h.Add(new Handle(s.Pts[2], HKind.Tail, 2));
                        break;
                    }
            }
            return h;
        }

        /// <summary>Where a movable label sits before it is moved: the middle of an area, or halfway along a radius.</summary>
        public static PointF LabelAnchor(Shape s)
        {
            PointF o; float rad;
            if (s.Kind == ShapeKind.Radius && s.Pts.Count >= 3 && Geo.Circle3(s.Pts[0], s.Pts[1], s.Pts[2], out o, out rad)) return Geo.Mid(o, s.Pts[1]);
            return Geo.Centroid(s.Pts);
        }

        /// <summary>Apply a handle drag. orig = the shape as it was when the drag started.</summary>
        public static void DragHandle(Shape s, Shape orig, Handle h, PointF p, bool shift)
        {
            switch (h.Kind)
            {
                case HKind.Label:
                    {
                        PointF mid = LabelAnchor(orig);
                        s.LabelOffset = new PointF(p.X - mid.X, p.Y - mid.Y);
                        break;
                    }
                case HKind.LabelSize:
                    {
                        // drag the label's corner out or in: its text grows or shrinks
                        PointF lc = Geo.Center(orig.LabelRect);
                        PointF c0 = new PointF(orig.LabelRect.Right, orig.LabelRect.Bottom);
                        float d0 = Geo.Dist(lc, c0), d1 = Geo.Dist(lc, p);
                        if (d0 > 1f) s.FontSize = Math.Max(6f, Math.Min(300f, orig.FontSize * d1 / d0));
                        break;
                    }
                case HKind.Point:
                    if (h.Index < s.Pts.Count)
                    {
                        // a coordinate point drags its label along
                        if (s.Kind == ShapeKind.Coord && h.Index == 0 && s.Pts.Count > 1 && orig.Pts.Count > 1)
                            s.Pts[1] = new PointF(orig.Pts[1].X + p.X - orig.Pts[0].X, orig.Pts[1].Y + p.Y - orig.Pts[0].Y);
                        s.Pts[h.Index] = p;
                    }
                    break;
                case HKind.HolePoint:
                    {
                        int hi = h.Index / HoleStride, pi = h.Index % HoleStride;
                        if (s.Holes != null && hi < s.Holes.Count && pi < s.Holes[hi].Count) { s.Holes[hi][pi] = p; s.Cache = null; }
                        break;
                    }
                case HKind.DimOffset:
                case HKind.Tail:
                    {
                        int idx = Math.Max(1, h.Index);
                        while (s.Pts.Count <= idx) s.Pts.Add(p);
                        s.Pts[idx] = p;
                        break;
                    }
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
                        {
                            for (int i = 0; i < s.Pts.Count; i++) s.Pts[i] = Geo.Rot(orig.Pts[i], c, ang);
                            if (orig.Holes != null && s.Holes != null)
                                for (int h2 = 0; h2 < orig.Holes.Count && h2 < s.Holes.Count; h2++)
                                    for (int i = 0; i < orig.Holes[h2].Count && i < s.Holes[h2].Count; i++) s.Holes[h2][i] = Geo.Rot(orig.Holes[h2][i], c, ang);
                        }
                        break;
                    }
                case HKind.Box:
                    {
                        if (s.Kind == ShapeKind.Counter)
                        {
                            // marker: the corner's distance from the centre sets the disc's size (it stays round)
                            PointF c = orig.Pts[0];
                            // a pill: its width follows the text, so the corner's height (and the width scaled back) sets the size
                            float r0 = Renderer.MarkerRadius(orig), hw0 = Renderer.MarkerHalfWidth(orig);
                            float half = Math.Max(Math.Abs(p.X - c.X) * r0 / hw0, Math.Abs(p.Y - c.Y));
                            s.FontSize = Math.Max(6f, Math.Min(300f, half / 0.9f));
                            s.Cache = null;
                            break;
                        }
                        if (s.Kind == ShapeKind.Text)
                        {
                            // text box: corners and sides resize the box; the text wraps inside and keeps its size.
                            // The box never gets shorter than its text (TextSize grows it back).
                            PointF c1 = Center(orig);
                            PointF lp1 = Geo.Rot(p, c1, -orig.Angle);
                            RectangleF r1 = LocalRect(orig);
                            float l1 = r1.X, t1 = r1.Y, rt1 = r1.Right, bt1 = r1.Bottom;
                            int hi = h.Index;
                            float minW = Math.Max(12f, orig.FontSize * 0.8f), minH = orig.FontSize * 1.2f;
                            if (hi == 0 || hi == 6 || hi == 7) l1 = Math.Min(lp1.X + 4f, rt1 - minW);
                            if (hi == 2 || hi == 3 || hi == 4) rt1 = Math.Max(lp1.X - 4f, l1 + minW);
                            if (hi == 0 || hi == 1 || hi == 2) t1 = Math.Min(lp1.Y + 2f, bt1 - minH);
                            if (hi == 4 || hi == 5 || hi == 6) bt1 = Math.Max(lp1.Y - 2f, t1 + minH);
                            while (s.Pts.Count < 2) s.Pts.Add(new PointF(l1, t1));
                            s.Pts[0] = new PointF(l1, t1);
                            s.Pts[1] = new PointF(rt1, bt1);
                            break;
                        }
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
                        // a corner stretches width and height freely; Shift keeps the proportions (uniform scale) – pictures too
                        bool keep = shift;
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
                float r = Renderer.MarkerRadius(s), hw = Renderer.MarkerHalfWidth(s);
                return new RectangleF(s.Pts[0].X - hw, s.Pts[0].Y - r, 2 * hw, 2 * r);
            }
            if (s.Kind == ShapeKind.Note) return Renderer.NoteRect(s);
            if ((s.Kind == ShapeKind.Arc || s.Kind == ShapeKind.Radius) && s.Pts.Count >= 3)
            {
                RectangleF ab = RawBounds(Geo.ArcPoints(s.Pts[0], s.Pts[1], s.Pts[2], 48));
                PointF o; float rad;
                if (s.Kind == ShapeKind.Radius && Geo.Circle3(s.Pts[0], s.Pts[1], s.Pts[2], out o, out rad))
                    ab = RectangleF.Union(ab, new RectangleF(o.X - 4, o.Y - 4, 8, 8));
                ab.Inflate(s.Width, s.Width);
                return ab;
            }
            if (s.Kind == ShapeKind.Origin)
            {
                float r = Math.Max(6f, s.FontSize * 0.9f) * 2.8f;
                return new RectangleF(s.Pts[0].X - r, s.Pts[0].Y - r, 2 * r, 2 * r);
            }
            if (s.Kind == ShapeKind.Coord)
            {
                RectangleF a = new RectangleF(s.Pts[0].X - s.FontSize, s.Pts[0].Y - s.FontSize, 2 * s.FontSize, 2 * s.FontSize);
                if (s.Pts.Count > 1)
                {
                    PointF l = s.Pts[1];
                    RectangleF lb = new RectangleF(l.X - s.FontSize * 5, l.Y - s.FontSize * 1.4f, s.FontSize * 10, s.FontSize * 2.8f);
                    a = RectangleF.Union(a, lb);
                }
                return a;
            }
            if (s.Kind == ShapeKind.Cloud && s.Mode != 0 && s.Pts.Count > 2)
            {
                RectangleF cr = RawBounds(new PointF[] { s.Pts[0], s.Pts[1] });
                cr = RectangleF.Union(cr, Renderer.CalloutRect(s));
                cr.Inflate(s.Width * 3f + 4f, s.Width * 3f + 4f);
                return cr;
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
            // a label moved off its area still selects the area
            if ((s.Kind == ShapeKind.Area || s.Kind == ShapeKind.Perimeter || s.Kind == ShapeKind.Radius) && !s.LabelRect.IsEmpty && s.LabelRect.Contains(p)) return true;
            if (s.Rotatable && s.Angle != 0f) p = Geo.Rot(p, Center(s), -s.Angle);
            float t = tol + s.Width / 2f;
            if ((s.Kind == ShapeKind.Cloud || s.Kind == ShapeKind.Rect || s.Kind == ShapeKind.Ellipse) && s.Pts.Count >= 2)
            {
                if (s.Kind == ShapeKind.Cloud && s.Mode != 0 && s.Pts.Count > 2)
                {
                    RectangleF cr = Renderer.CalloutRect(s); cr.Inflate(tol, tol);
                    if (cr.Contains(p)) return true;
                }
                float turn = s.Angle; s.Angle = 0;                  // p is already turned back
                List<PointF> o;
                try { o = Renderer.CloudOutline(s); } finally { s.Angle = turn; }
                if (o != null)
                {
                    if (s.FillOpacity > 0 && Geo.InPolygon(p, o)) return true;
                    for (int i = 1; i < o.Count; i++) if (Geo.SegDist(p, o[i - 1], o[i]) <= t) return true;
                    return false;
                }
            }
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
                case ShapeKind.Coord:
                    if (Geo.Dist(p, s.Pts[0]) <= s.FontSize * 0.8f + tol) return true;
                    if (s.Pts.Count > 1)
                    {
                        RectangleF lb = new RectangleF(s.Pts[1].X - s.FontSize * 4, s.Pts[1].Y - s.FontSize * 1.2f, s.FontSize * 8, s.FontSize * 2.4f);
                        return lb.Contains(p);
                    }
                    return false;
                case ShapeKind.Origin:
                    return Geo.Dist(p, s.Pts[0]) <= Math.Max(6f, s.FontSize * 0.9f) * 1.8f + tol;
                case ShapeKind.Pixelate:
                case ShapeKind.Highlight:
                case ShapeKind.Image:
                case ShapeKind.Stamp:
                case ShapeKind.Markup:
                    {
                        if (s.IsFocusPoly)
                        {
                            // a focus perimeter: inside it, or near one of its sides
                            if (Geo.InPolygon(p, s.Pts)) return true;
                            for (int i = 0; i < s.Pts.Count; i++) if (Geo.SegDist(p, s.Pts[i], s.Pts[(i + 1) % s.Pts.Count]) <= t) return true;
                            return false;
                        }
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
                case ShapeKind.Note:
                    {
                        RectangleF r = Bounds(s); r.Inflate(tol, tol);
                        return r.Contains(p);
                    }
                case ShapeKind.Polyline:
                    for (int i = 0; i + 1 < s.Pts.Count; i++) if (Geo.SegDist(p, s.Pts[i], s.Pts[i + 1]) <= t) return true;
                    return s.Pts.Count >= 2 && Geo.Dist(p, Renderer.PathMiddle(s.Pts)) <= s.FontSize * 1.5f + tol;
                case ShapeKind.Perimeter:
                    {
                        int segs = s.IsOpenPath ? s.Pts.Count - 1 : s.Pts.Count;
                        for (int i = 0; i < segs; i++) if (Geo.SegDist(p, s.Pts[i], s.Pts[(i + 1) % s.Pts.Count]) <= t) return true;
                        PointF lab = s.IsOpenPath ? Renderer.PathMiddle(s.Pts) : (s.Pts.Count >= 3 ? Geo.Centroid(s.Pts) : s.Pts[0]);
                        return Geo.Dist(p, lab) <= s.FontSize * 1.5f + tol;
                    }
                case ShapeKind.Arc:
                case ShapeKind.Radius:
                    {
                        if (s.Pts.Count < 3) return false;
                        List<PointF> ap = Geo.ArcPoints(s.Pts[0], s.Pts[1], s.Pts[2], 64);
                        for (int i = 0; i + 1 < ap.Count; i++) if (Geo.SegDist(p, ap[i], ap[i + 1]) <= t) return true;
                        PointF o; float rad;
                        if (s.Kind == ShapeKind.Radius && Geo.Circle3(s.Pts[0], s.Pts[1], s.Pts[2], out o, out rad))
                            return Geo.SegDist(p, o, s.Pts[1]) <= t || Geo.Dist(p, Geo.Mid(o, s.Pts[1])) <= s.FontSize * 1.5f + tol;
                        return false;
                    }
                case ShapeKind.Polygon:
                case ShapeKind.Area:
                    {
                        if (s.IsOpenPath)
                        {
                            for (int i = 0; i + 1 < s.Pts.Count; i++) if (Geo.SegDist(p, s.Pts[i], s.Pts[i + 1]) <= t) return true;
                            return false;
                        }
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
            s.MapPoints(map);
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
        public static Bitmap To32(Image img)
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
        public int[] AddedRoom;     // left, top, right, bottom added by Extend canvas / Ctrl+Plus
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
            if (s.Underline) w.WriteAttributeString("underline", "1");
            if (s.Strikeout) w.WriteAttributeString("strikeout", "1");
            if (!string.IsNullOrEmpty(s.FontName)) w.WriteAttributeString("fontName", s.FontName);
            w.WriteAttributeString("align", s.Align.ToString(Inv));
            if (s.VAlign != 0) w.WriteAttributeString("valign", s.VAlign.ToString(Inv));
            if (s.LabelAt != 0) w.WriteAttributeString("labelAt", s.LabelAt.ToString(Inv));
            if (s.Kind == ShapeKind.Freehand)
            {
                // own names: older files stored a default "arrow at the end" on every shape, which must not turn into freehand arrows
                w.WriteAttributeString("arrowStart", s.HeadStart.ToString(Inv));
                w.WriteAttributeString("arrowEnd", s.HeadEnd.ToString(Inv));
            }
            else
            {
                w.WriteAttributeString("heads", s.Heads.ToString(Inv));          // for SnapMeasure 1.0
                w.WriteAttributeString("headStart", s.HeadStart.ToString(Inv));
                w.WriteAttributeString("headEnd", s.HeadEnd.ToString(Inv));
            }
            if (s.Dash != 0) w.WriteAttributeString("dash", s.Dash.ToString(Inv));
            if (s.Form != 0) w.WriteAttributeString("form", s.Form.ToString(Inv));
            if (s.Strength != 0) w.WriteAttributeString("strength", s.Strength.ToString(Inv));
            if (s.FromArea != 0) w.WriteAttributeString("fromarea", s.FromArea.ToString(Inv));
            if (s.LabelOffset.X != 0 || s.LabelOffset.Y != 0)
            {
                w.WriteAttributeString("lx", F(s.LabelOffset.X));
                w.WriteAttributeString("ly", F(s.LabelOffset.Y));
            }
            if (s.Kind == ShapeKind.Text ? s.Width > 0 : s.Frame) w.WriteAttributeString("frame", "1");   // 1.2 reads the frame from this
            w.WriteAttributeString("text", s.Text ?? "");
            if (s.Holes != null)
                foreach (List<PointF> h in s.Holes)
                {
                    w.WriteStartElement("hole");
                    foreach (PointF p in h)
                    {
                        w.WriteStartElement("p");
                        w.WriteAttributeString("x", F(p.X));
                        w.WriteAttributeString("y", F(p.Y));
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                }
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
                return Effects.To32(tmp);
            }
        }

        public static void CheckImageSize(int w, int h)
        {
            if (w < 1 || h < 1 || w > MaxImageSide || h > MaxImageSide || (long)w * h > MaxImagePixels)
                throw new InvalidDataException("The image is too large (" + w + " × " + h + " px).");
        }

        static string Trunc(string s, int max) { s = s ?? ""; return s.Length > max ? s.Substring(0, max) : s; }

        static float Finite(float v, float def, float lo, float hi)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return def;
            return Math.Max(lo, Math.Min(hi, v));
        }

        /// <summary>Repairs or rejects a loaded shape so bad data can't crash drawing. Returns false to drop it.</summary>
        static bool CleanPoints(List<PointF> pts)
        {
            if (pts.Count > MaxPoints) return false;
            for (int i = 0; i < pts.Count; i++)
            {
                PointF p = pts[i];
                if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsInfinity(p.X) || float.IsInfinity(p.Y)) return false;
                pts[i] = new PointF(Math.Max(-MaxCoord, Math.Min(MaxCoord, p.X)), Math.Max(-MaxCoord, Math.Min(MaxCoord, p.Y)));
            }
            return true;
        }

        public static bool Sanitize(Shape s)
        {
            if (!Enum.IsDefined(typeof(ShapeKind), s.Kind)) return false;
            if (!CleanPoints(s.Pts)) return false;
            if (s.Holes != null)
            {
                if ((s.Kind != ShapeKind.Area && s.Kind != ShapeKind.Polygon && s.Kind != ShapeKind.Highlight) || s.Holes.Count > 1000) s.Holes = null;
                else
                {
                    s.Holes.RemoveAll(h => h == null || h.Count < 3 || !CleanPoints(h));
                    if (s.Holes.Count == 0) s.Holes = null;
                }
            }
            int need;
            switch (s.Kind)
            {
                case ShapeKind.Text: case ShapeKind.Counter: case ShapeKind.Freehand: case ShapeKind.Note:
                case ShapeKind.Coord: case ShapeKind.Origin: need = 1; break;
                case ShapeKind.Polygon: need = s.Mode == 1 ? 2 : 3; break;         // an open polyline needs only 2 points
                case ShapeKind.Area: case ShapeKind.Angle: case ShapeKind.Arc: case ShapeKind.Radius: need = 3; break;
                case ShapeKind.Perimeter: need = s.Mode == 1 ? 2 : 3; break;      // an open path needs only 2 points
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
            s.HeadStart = Math.Max(0, Math.Min(21, s.HeadStart));
            s.HeadEnd = Math.Max(0, Math.Min(21, s.HeadEnd));
            s.Mode = Math.Max(0, Math.Min(Math.Max(20, Stamps.Custom), s.Mode));   // stamps go up to Custom
            s.Dash = Math.Max(0, Math.Min(5, s.Dash));
            s.Form = Math.Max(0, Math.Min((int)HighlightForm.Perimeter, s.Form));
            if (s.Kind == ShapeKind.Highlight)
            {
                if (!Enum.IsDefined(typeof(HighlightMode), s.Mode)) s.Mode = (int)HighlightMode.Marker;
                // a perimeter needs three corners; a box form uses only its two corners
                if (s.IsFocusPoly && s.Pts.Count < 3) s.Form = (int)HighlightForm.Rectangle;
                if (!s.IsFocusPoly && s.Pts.Count > 2) s.Pts.RemoveRange(2, s.Pts.Count - 2);
            }
            s.Strength = Math.Max(0, Math.Min(10, s.Strength));
            s.FromArea = s.Kind == ShapeKind.Perimeter ? Math.Max(0, Math.Min(3, s.FromArea)) : 0;
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
            s.Underline = e.GetAttribute("underline") == "1";
            s.Strikeout = e.GetAttribute("strikeout") == "1";
            { string fn = e.GetAttribute("fontName"); s.FontName = string.IsNullOrEmpty(fn) || fn.Length > 64 ? null : fn; }
            s.Align = PI(e.GetAttribute("align"), s.Kind == ShapeKind.Bubble ? 1 : 0);
            s.VAlign = Math.Max(0, Math.Min(2, PI(e.GetAttribute("valign"), 0)));
            s.LabelAt = Math.Max(0, Math.Min(2, PI(e.GetAttribute("labelAt"), 0)));
            if (s.Kind == ShapeKind.Freehand)
            {
                s.HeadStart = PI(e.GetAttribute("arrowStart"), 0);
                s.HeadEnd = PI(e.GetAttribute("arrowEnd"), 0);
            }
            else if (e.HasAttribute("headEnd"))
            {
                s.HeadStart = PI(e.GetAttribute("headStart"), 0);
                s.HeadEnd = PI(e.GetAttribute("headEnd"), 0);
            }
            else if (s.Kind == ShapeKind.Dimension) { s.HeadStart = s.HeadEnd = (int)HeadStyle.Filled; }   // 1.0 dimensions
            else
            {
                s.HeadStart = 0; s.HeadEnd = 0;
                s.Heads = PI(e.GetAttribute("heads"), s.Kind == ShapeKind.Arrow ? 1 : 0);
            }
            s.Text = e.GetAttribute("text");
            if (s.Kind == ShapeKind.Counter && (s.Mode > 2 || (s.Mode == 2 && s.Text.Length == 0))) s.Mode = 0;
            s.Dash = PI(e.GetAttribute("dash"), 0);
            s.Form = PI(e.GetAttribute("form"), 0);
            s.Strength = PI(e.GetAttribute("strength"), 0);
            s.FromArea = PI(e.GetAttribute("fromarea"), 0);
            if (e.HasAttribute("lx")) s.LabelOffset = new PointF(Finite(PF(e.GetAttribute("lx"), 0f), 0f, -100000f, 100000f), Finite(PF(e.GetAttribute("ly"), 0f), 0f, -100000f, 100000f));
            s.Frame = e.GetAttribute("frame") == "1";
            // since 1.3 a text's frame is its line width: older text without a frame gets width 0
            if (s.Kind == ShapeKind.Text && !s.Frame) s.Width = 0f;
            XmlNodeList holes = e.SelectNodes("hole");
            if (holes.Count > 0)
            {
                if (holes.Count > 1000) return null;
                s.Holes = new List<List<PointF>>();
                foreach (XmlElement he in holes)
                {
                    List<PointF> h = new List<PointF>();
                    foreach (XmlElement p in he.SelectNodes("p"))
                    {
                        if (h.Count > MaxPoints) return null;
                        h.Add(new PointF(PF(p.GetAttribute("x"), 0f), PF(p.GetAttribute("y"), 0f)));
                    }
                    s.Holes.Add(h);
                }
            }
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
            // written next to it first and swapped in: a failed save never spoils the old file
            Output.WriteSafely(path, p => WriteProject(p, img, shapes, sc, ex));
            ShellPreview.WarmThumbnail(path);           // File Explorer shows it with its picture straight away
        }

        static void WriteProject(string path, Bitmap img, List<Shape> shapes, ScaleInfo sc, ProjectExtras ex)
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
                w.WriteAttributeString("numberLabel", sc.NumberLabel ?? "");
                w.WriteAttributeString("letterLabel", sc.LetterLabel ?? "");
                if (sc.Rounding != 0) w.WriteAttributeString("rounding", sc.Rounding.ToString(Inv));
                if (sc.LetterReverse) w.WriteAttributeString("letterReverse", "1");
                if (sc.MarkerReverse) w.WriteAttributeString("markerReverse", "1");
                if (sc.OriginX != 0) w.WriteAttributeString("originX", F(sc.OriginX));
                if (sc.OriginY != 0) w.WriteAttributeString("originY", F(sc.OriginY));
                if (ex.AddedRoom != null && ex.AddedRoom.Length == 4 && Array.Exists(ex.AddedRoom, v => v > 0))
                    w.WriteAttributeString("addedRoom", ex.AddedRoom[0] + "," + ex.AddedRoom[1] + "," + ex.AddedRoom[2] + "," + ex.AddedRoom[3]);
                if (ex.Crop.HasValue)
                {
                    Rectangle r = ex.Crop.Value;
                    w.WriteAttributeString("crop", r.X + "," + r.Y + "," + r.Width + "," + r.Height);
                }
                // a small picture with the markups, first in the file: File Explorer's thumbnail and preview pane read only this
                byte[] preview = PreviewImage(img, shapes, sc, ex.Crop);
                if (preview != null)
                {
                    w.WriteStartElement("preview");
                    w.WriteString(Convert.ToBase64String(preview));
                    w.WriteEndElement();
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

        /// <summary>Longest side of the preview stored in a project (Explorer's largest thumbnails are 256 px; the
        /// preview pane is often wider).</summary>
        public const int PreviewSize = 640;

        /// <summary>The finished picture (markups drawn, crop applied) scaled to fit PreviewSize, as JPEG (quality 80):
        /// a scaled-down screenshot as PNG made small projects 4× bigger and saving 2× slower. Null if it fails.</summary>
        static byte[] PreviewImage(Bitmap img, List<Shape> shapes, ScaleInfo sc, Rectangle? crop)
        {
            try
            {
                // drawn straight at the small size (one pass, no full-size copy): saving stays quick on 4K pictures
                Rectangle cr = crop.HasValue ? crop.Value : new Rectangle(0, 0, img.Width, img.Height);
                {
                    float f = Math.Min(1f, (float)PreviewSize / Math.Max(cr.Width, cr.Height));
                    int w = Math.Max(1, (int)Math.Round(cr.Width * f)), h = Math.Max(1, (int)Math.Round(cr.Height * f));
                    using (Bitmap small = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (Graphics g = Graphics.FromImage(small))
                        {
                            g.Clear(Color.White);
                            g.ScaleTransform(f, f);
                            g.TranslateTransform(-cr.X, -cr.Y);
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height), new Rectangle(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
                            g.PixelOffsetMode = PixelOffsetMode.Default;
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                            Renderer.DrawAll(g, shapes, img, sc, null, true);
                        }
                        ImageCodecInfo jpg = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
                        using (EncoderParameters ep = new EncoderParameters(1))
                        {
                            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
                            small.Save(ms, jpg, ep);
                        }
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>For File Explorer: the stored preview of a project, read from the start of the file without
        /// loading the rest. Older projects have none: then the clean picture is used.</summary>
        public static Bitmap ReadPreview(Stream s)
        {
            XmlReaderSettings set = new XmlReaderSettings();
            set.DtdProcessing = DtdProcessing.Prohibit;
            set.XmlResolver = null;
            set.IgnoreWhitespace = true;
            using (XmlReader r = XmlReader.Create(s, set))
            {
                if (!r.ReadToFollowing("snapmeasure")) return null;
                r.Read();
                while (!r.EOF)
                {
                    if (r.NodeType != XmlNodeType.Element) { r.Read(); continue; }
                    if (r.Name == "preview" || r.Name == "image")
                        return DecodeImage(r.ReadElementContentAsString());
                    r.Skip();
                }
            }
            return null;
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
            sc.NumberLabel = Trunc(root.GetAttribute("numberLabel"), 500);
            sc.LetterLabel = Trunc(root.GetAttribute("letterLabel"), 500);
            sc.Rounding = Math.Max(0, Math.Min(2, PI(root.GetAttribute("rounding"), 0)));
            sc.LetterReverse = root.GetAttribute("letterReverse") == "1";
            sc.MarkerReverse = root.GetAttribute("markerReverse") == "1";
            double ox, oy;
            if (double.TryParse(root.GetAttribute("originX"), NumberStyles.Float, Inv, out ox) && !double.IsNaN(ox) && !double.IsInfinity(ox)) sc.OriginX = ox;
            if (double.TryParse(root.GetAttribute("originY"), NumberStyles.Float, Inv, out oy) && !double.IsNaN(oy) && !double.IsInfinity(oy)) sc.OriginY = oy;
            bool oldLetters = root.GetAttribute("markerLetters") == "1";     // v2: one series, either numbers or letters
            if (oldLetters) { sc.LetterStart = Math.Max(1, sc.MarkerStart); sc.MarkerStart = 1; }
            string[] cp = root.GetAttribute("crop").Split(',');
            int cx, cy, cw, ch;
            if (cp.Length == 4 && int.TryParse(cp[0], out cx) && int.TryParse(cp[1], out cy) && int.TryParse(cp[2], out cw) && int.TryParse(cp[3], out ch) && cw > 1 && ch > 1)
                ex.Crop = new Rectangle(cx, cy, cw, ch);
            if (sc.Upp <= 0 || double.IsNaN(sc.Upp) || double.IsInfinity(sc.Upp) || sc.Upp > 1e9) sc.Upp = 1.0;
            if (Array.IndexOf(ScaleInfo.Units, sc.Unit) < 0) sc.Unit = "px";
            sc.Decimals = Math.Max(0, Math.Min(6, sc.Decimals));
            sc.LetterStart = Math.Max(1, Math.Min(18278, sc.LetterStart));
            XmlNode imgNode = root.SelectSingleNode("image");
            if (imgNode == null) throw new InvalidDataException("The project has no image.");
            Bitmap img = DecodeImage(imgNode.InnerText);
            string[] ar = root.GetAttribute("addedRoom").Split(',');
            if (ar.Length == 4)
            {
                int[] room = new int[4];
                for (int i = 0; i < 4; i++) room[i] = Math.Max(0, Math.Min(i % 2 == 0 ? img.Width : img.Height, PI(ar[i], 0)));
                ex.AddedRoom = room;
            }
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
