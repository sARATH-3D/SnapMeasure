using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SnapMeasure
{
    internal static class ScreenCapture
    {
        public static Rectangle VirtualBounds { get { return SystemInformation.VirtualScreen; } }

        /// <summary>Settings → Capture → "Capture the mouse pointer".</summary>
        public static bool IncludeCursor;

        /// <summary>The window that was active when the capture key was pressed (SnapMeasure may take the focus
        /// for a moment before grabbing, see App.HideCadCrosshair).</summary>
        public static IntPtr Target;
        public static IntPtr TargetOrForeground { get { return Target != IntPtr.Zero ? Target : Native.GetForegroundWindow(); } }

        /// <summary>CAD programs that draw their own crosshair into the drawing (not a Windows pointer): it is only
        /// removed when the program loses the focus.</summary>
        static readonly string[] CadApps = { "acad", "acadlt", "bricscad", "zwcad", "gcad", "draftsight", "revit", "ustation", "rhino" };

        public static bool IsCadApp(string processName)
        {
            return Array.IndexOf(CadApps, (processName ?? "").ToLowerInvariant()) >= 0;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public Point pt; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr h, out ICONINFO ii);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr h, int w, int hh, int step, IntPtr brush, int flags);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        public static Bitmap Grab(Rectangle r)
        {
            Bitmap bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
            // the pointer is kept apart (a picture on top in the editor, so it can be moved or deleted)
            if (IncludeCursor && pendingCursor == null) pendingCursor = SnapCursor();
            return bmp;
        }

        /// <summary>The mouse pointer at capture time: its picture (with transparency) and where its top-left was on screen.</summary>
        public class CursorShot
        {
            public Bitmap Image;
            public Point ScreenPos;
        }

        static CursorShot pendingCursor;

        /// <summary>The pointer recorded by the last capture (null if none); the caller owns its picture.</summary>
        public static CursorShot TakeCursor() { CursorShot c = pendingCursor; pendingCursor = null; return c; }

        public static void ForgetCursor() { if (pendingCursor != null) { pendingCursor.Image.Dispose(); pendingCursor = null; } }

        /// <summary>Draws the pointer on black and on white and works out its transparency from the difference.</summary>
        static CursorShot SnapCursor()
        {
            try
            {
                CURSORINFO ci = new CURSORINFO();
                ci.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(CURSORINFO));
                if (!GetCursorInfo(ref ci) || ci.flags == 0 || ci.hCursor == IntPtr.Zero) return null;
                ICONINFO ii;
                int hx = 0, hy = 0;
                if (GetIconInfo(ci.hCursor, out ii))
                {
                    hx = ii.xHotspot; hy = ii.yHotspot;
                    if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                }
                const int N = 128;
                using (Bitmap onBlack = new Bitmap(N, N, PixelFormat.Format24bppRgb))
                using (Bitmap onWhite = new Bitmap(N, N, PixelFormat.Format24bppRgb))
                {
                    foreach (Bitmap bg in new Bitmap[] { onBlack, onWhite })
                        using (Graphics g = Graphics.FromImage(bg))
                        {
                            g.Clear(bg == onBlack ? Color.Black : Color.White);
                            IntPtr hdc = g.GetHdc();
                            try { DrawIconEx(hdc, 0, 0, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3); }   // DI_NORMAL
                            finally { g.ReleaseHdc(hdc); }
                        }
                    int minX = N, minY = N, maxX = -1, maxY = -1;
                    Color[,] px = new Color[N, N];
                    for (int y = 0; y < N; y++)
                        for (int x = 0; x < N; x++)
                        {
                            Color b = onBlack.GetPixel(x, y), w = onWhite.GetPixel(x, y);
                            int a = 255 - Math.Min(255, Math.Max(0, ((w.R - b.R) + (w.G - b.G) + (w.B - b.B)) / 3));
                            if (a == 0) { px[x, y] = Color.Transparent; continue; }
                            px[x, y] = Color.FromArgb(a, Math.Min(255, b.R * 255 / a), Math.Min(255, b.G * 255 / a), Math.Min(255, b.B * 255 / a));
                            minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                        }
                    if (maxX < 0) return null;
                    Bitmap img = new Bitmap(maxX - minX + 1, maxY - minY + 1, PixelFormat.Format32bppArgb);
                    for (int y = minY; y <= maxY; y++)
                        for (int x = minX; x <= maxX; x++) img.SetPixel(x - minX, y - minY, px[x, y]);
                    CursorShot c = new CursorShot();
                    c.Image = img;
                    c.ScreenPos = new Point(ci.pt.X - hx + minX, ci.pt.Y - hy + minY);
                    return c;
                }
            }
            catch (Exception) { return null; }
        }

        public static Bitmap Crop(Bitmap src, Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, src.Width, src.Height));
            if (r.Width < 1 || r.Height < 1) return null;
            Bitmap b = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(b))
                g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
            return b;
        }

        /// <summary>Freeze all screens, let the user drag a region or click a window. Returns null on cancel.</summary>
        public static Bitmap SelectRegion(out Rectangle screenRect)
        {
            string title;
            return SelectRegion(out screenRect, out title);
        }

        /// <summary>As above; 'title' = the app / window that was captured (like Greenshot), for the file name.</summary>
        public static Bitmap SelectRegion(out Rectangle screenRect, out string title)
        {
            screenRect = Rectangle.Empty;
            title = "";
            Rectangle vs = VirtualBounds;
            string active = Native.WindowTitle(TargetOrForeground);   // the app you were in when you pressed the key
            List<string> titles = new List<string>();
            List<Rectangle> wins = Native.GetVisibleWindowRects(titles);
            using (Bitmap shot = Grab(vs))
            using (RegionSelector f = new RegionSelector(shot, vs, wins))
            {
                if (f.ShowDialog() != DialogResult.OK) return null;
                Rectangle r = f.Result;
                screenRect = new Rectangle(r.X + vs.X, r.Y + vs.Y, r.Width, r.Height);
                title = TitleFor(screenRect, wins, titles, active);
                return Crop(shot, r);
            }
        }

        /// <summary>Name of the window the area belongs to: the front-most titled window under its centre,
        /// or the app that was active when the capture started.</summary>
        public static string TitleFor(Rectangle area, List<Rectangle> wins, List<string> titles, string active)
        {
            Point c = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
            for (int i = 0; i < wins.Count && i < titles.Count; i++)
            {
                if (!wins[i].Contains(c)) continue;
                string t = titles[i];
                if (t.Length == 0 || t.StartsWith("SnapMeasure")) continue;
                return t;
            }
            return active != null && !active.StartsWith("SnapMeasure") ? active : "";
        }
    }

    /// <summary>Full-virtual-screen overlay: drag to select, hover+click to pick a window, magnifier loupe.</summary>
    internal class RegionSelector : Form
    {
        readonly Bitmap shot, dim;
        readonly Rectangle vs;
        readonly List<Rectangle> wins;
        bool dragging;
        Point start, cur;
        Rectangle sel, hover, lastDirty, hintRect;
        public Rectangle Result;
        readonly Font font, small;
        int Zoom { get { return Math.Max(8, (int)Math.Round(8 * k)); } }   // magnifier pixels per screen pixel
        readonly float k;            // UI scale of the monitor the capture starts on (2 on a 200 % screen)
        const string Hint = "Drag to select an area  ·  Click a window to capture it  ·  Arrow keys = 1 px nudge  ·  Enter = all screens  ·  Esc / right-click = cancel";

        public RegionSelector(Bitmap shot, Rectangle vs, List<Rectangle> wins)
        {
            this.shot = shot; this.vs = vs; this.wins = wins;
            k = Native.ScaleAt(Cursor.Position);
            font = new Font("Segoe UI", 13f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            small = new Font("Segoe UI", 12f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            Bounds = vs;

            dim = new Bitmap(shot.Width, shot.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dim))
            {
                Rectangle all = new Rectangle(0, 0, shot.Width, shot.Height);
                g.DrawImage(shot, all, all, GraphicsUnit.Pixel);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(115, 0, 0, 0))) g.FillRectangle(b, all);
            }
            Point cp = Cursor.Position;
            cur = new Point(cp.X - vs.X, cp.Y - vs.Y);

            Rectangle scr = Screen.FromPoint(cp).Bounds;
            Size hs = TextRenderer.MeasureText(Hint, small);
            hintRect = new Rectangle(scr.X - vs.X + (scr.Width - hs.Width) / 2 - (int)(12 * k), scr.Y - vs.Y + (int)(14 * k), hs.Width + (int)(24 * k), hs.Height + (int)(12 * k));
            UpdateHover();
        }

        protected override void WndProc(ref Message m)
        {
            // We cover monitors with different scaling; never let Windows resize us on DPI change.
            if (m.Msg == Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = vs;
            Activate();
            Focus();
        }

        Rectangle CurrentRect() { return dragging ? sel : hover; }

        static Rectangle Norm(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X) + 1, Math.Max(a.Y, b.Y) + 1);
        }

        void UpdateHover()
        {
            hover = Rectangle.Empty;
            Point sp = new Point(cur.X + vs.X, cur.Y + vs.Y);
            foreach (Rectangle w in wins)
            {
                if (!w.Contains(sp)) continue;
                Rectangle r = w;
                r.Offset(-vs.X, -vs.Y);
                r.Intersect(new Rectangle(0, 0, vs.Width, vs.Height));
                hover = r;
                break;
            }
        }

        Rectangle MagRect()
        {
            int z = Zoom, w = 15 * z, h = 15 * z + (int)(40 * k), gap = (int)(24 * k);
            // the overlay spans every monitor; keep the magnifier on the monitor under the pointer
            // (monitors of different sizes leave parts of the overlay that no screen shows)
            Rectangle area = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            try
            {
                Rectangle s = Screen.FromPoint(new Point(cur.X + vs.X, cur.Y + vs.Y)).Bounds;
                s.Offset(-vs.X, -vs.Y);
                s.Intersect(area);
                if (s.Width > 0 && s.Height > 0) area = s;
            }
            catch (Exception) { }
            int x = cur.X + gap, y = cur.Y + gap;
            if (x + w > area.Right) x = cur.X - gap - w;
            if (y + h > area.Bottom) y = cur.Y - gap - h;
            // keep it fully on that screen (e.g. a tiny screen or the cursor in a corner)
            x = Math.Max(area.X, Math.Min(x, Math.Max(area.X, area.Right - w)));
            y = Math.Max(area.Y, Math.Min(y, Math.Max(area.Y, area.Bottom - h)));
            return new Rectangle(x, y, w, h);
        }

        Rectangle LabelRect(Rectangle r)
        {
            int hh = (int)(26 * k), y = r.Y - hh - (int)(2 * k);
            if (y < 0) y = r.Y + (int)(4 * k);
            return new Rectangle(r.X, y, (int)(260 * k), hh);
        }

        void Dirty()
        {
            Rectangle r = CurrentRect();
            Rectangle d = showMag ? MagRect() : Rectangle.Empty;    // no magnifier: nothing to repaint around the cursor
            if (r.Width > 0)
            {
                Rectangle rr = r; rr.Inflate(4, 4);
                d = d.IsEmpty ? rr : Rectangle.Union(d, rr);
                d = Rectangle.Union(d, LabelRect(r));
            }
            Rectangle inv = lastDirty.IsEmpty ? d : Rectangle.Union(d, lastDirty);
            lastDirty = d;
            Invalidate(inv);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle clip = e.ClipRectangle;
            g.DrawImage(dim, clip, clip, GraphicsUnit.Pixel);

            Rectangle r = CurrentRect();
            if (r.Width > 0 && r.Height > 0)
            {
                Rectangle ir = Rectangle.Intersect(r, clip);
                if (ir.Width > 0 && ir.Height > 0) g.DrawImage(shot, ir, ir, GraphicsUnit.Pixel);
                using (Pen p = new Pen(Color.FromArgb(0, 162, 255), 2f)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                string t = r.Width + " × " + r.Height + " px" + (dragging ? "" : "   (click = window)");
                Rectangle lr = LabelRect(r);
                Size ts = TextRenderer.MeasureText(t, font);
                lr.Width = ts.Width + 12;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(220, 20, 20, 20))) g.FillRectangle(b, lr);
                TextRenderer.DrawText(g, t, font, lr, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            }

            if (clip.IntersectsWith(hintRect))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(225, 20, 20, 20))) g.FillRectangle(b, hintRect);
                TextRenderer.DrawText(g, Hint, small, hintRect, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            }
            if (showMag) DrawMag(g);
        }

        readonly bool showMag = Settings.GetBool("Magnifier", true);

        void DrawMag(Graphics g)
        {
            Rectangle m = MagRect();
            const int cells = 15, half = 7;
            int z = Zoom;
            Rectangle src = new Rectangle(cur.X - half, cur.Y - half, cells, cells);
            Rectangle dst = new Rectangle(m.X, m.Y, cells * z, cells * z);
            g.FillRectangle(Brushes.Black, m);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(shot, dst, src, GraphicsUnit.Pixel);
            g.PixelOffsetMode = PixelOffsetMode.Default;
            using (Pen p = new Pen(Color.FromArgb(140, 0, 162, 255), 1f))
            {
                g.DrawLine(p, dst.X, dst.Y + half * z + z / 2, dst.Right, dst.Y + half * z + z / 2);
                g.DrawLine(p, dst.X + half * z + z / 2, dst.Y, dst.X + half * z + z / 2, dst.Bottom);
            }
            g.DrawRectangle(Pens.Red, dst.X + half * z, dst.Y + half * z, z, z);
            g.DrawRectangle(Pens.White, m.X, m.Y, m.Width - 1, m.Height - 1);

            string hex = "";
            if (cur.X >= 0 && cur.Y >= 0 && cur.X < shot.Width && cur.Y < shot.Height)
            {
                Color c = shot.GetPixel(cur.X, cur.Y);
                hex = "  #" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
            }
            string t = (cur.X + vs.X) + ", " + (cur.Y + vs.Y) + hex;
            TextRenderer.DrawText(g, t, small, new Rectangle(m.X, dst.Bottom, m.Width, m.Bottom - dst.Bottom), Color.White,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (dragging) { dragging = false; Invalidate(); return; }
                DialogResult = DialogResult.Cancel; Close(); return;
            }
            if (e.Button != MouseButtons.Left) return;
            dragging = true;
            start = e.Location;
            sel = Norm(start, start);
            Dirty();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            cur = e.Location;
            if (dragging) sel = Norm(start, cur); else UpdateHover();
            Dirty();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !dragging) return;
            dragging = false;
            if (sel.Width >= 4 && sel.Height >= 4) Finish(sel);
            else if (hover.Width > 0) Finish(hover);
            else Invalidate();
        }

        void Finish(Rectangle r)
        {
            Result = r;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int dx = 0, dy = 0, step = e.Shift ? 10 : 1;
            switch (e.KeyCode)
            {
                case Keys.Escape: DialogResult = DialogResult.Cancel; Close(); return;
                case Keys.Enter: Finish(new Rectangle(0, 0, vs.Width, vs.Height)); return;
                case Keys.Left: dx = -step; break;
                case Keys.Right: dx = step; break;
                case Keys.Up: dy = -step; break;
                case Keys.Down: dy = step; break;
            }
            if (dx != 0 || dy != 0)
            {
                Point p = Cursor.Position;
                Cursor.Position = new Point(p.X + dx, p.Y + dy);
                e.Handled = true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { dim.Dispose(); font.Dispose(); small.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>Screen colour picker (like PowerToys): every screen is frozen, a magnifier follows the pointer with the
    /// colour under it as HEX and RGB. Left-click = take it as the line colour, right-click = as the fill colour,
    /// arrow keys = 1 px nudge, Enter = line colour, Esc = cancel.</summary>
    internal class ScreenColorPicker : Form
    {
        readonly Bitmap shot;
        readonly Rectangle vs;
        Point cur;
        Rectangle lastDirty;
        readonly float k;
        readonly Font font, small;
        public Color Picked;
        public bool ForFill;
        const int Cells = 13, Half = 6;
        int Zoom { get { return Math.Max(10, (int)Math.Round(210 * k / Cells)); } }    // magnifier pixels per screen pixel (about 210 px wide)

        /// <summary>Shows the picker; null when cancelled.</summary>
        public static Color? Pick(out bool forFill)
        {
            forFill = false;
            Rectangle vs = ScreenCapture.VirtualBounds;
            Bitmap shot = new Bitmap(Math.Max(1, vs.Width), Math.Max(1, vs.Height), PixelFormat.Format24bppRgb);
            try
            {
                using (Graphics g = Graphics.FromImage(shot)) g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
                using (ScreenColorPicker f = new ScreenColorPicker(shot, vs))
                {
                    if (f.ShowDialog() != DialogResult.OK) return null;
                    forFill = f.ForFill;
                    return f.Picked;
                }
            }
            finally { shot.Dispose(); }
        }

        ScreenColorPicker(Bitmap shot, Rectangle vs)
        {
            this.shot = shot; this.vs = vs;
            k = Native.ScaleAt(Cursor.Position);
            font = new Font("Segoe UI", 15f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            small = new Font(Lang.UiFamily, 12f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            Bounds = vs;
            Point cp = Cursor.Position;
            cur = new Point(cp.X - vs.X, cp.Y - vs.Y);
        }

        protected override void WndProc(ref Message m)
        {
            // spans monitors with different scaling: never let Windows resize it on a DPI change
            if (m.Msg == Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = vs;
            Activate();
            Focus();
        }

        Color At(Point p)
        {
            if (p.X < 0 || p.Y < 0 || p.X >= shot.Width || p.Y >= shot.Height) return Color.Black;
            return shot.GetPixel(p.X, p.Y);
        }

        static string Hex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        Rectangle PanelRect()
        {
            int z = Zoom, w = Math.Max(Cells * z, (int)(230 * k)), h = Cells * z + (int)(104 * k), gap = (int)(26 * k);
            Rectangle area = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            try
            {
                Rectangle s = Screen.FromPoint(new Point(cur.X + vs.X, cur.Y + vs.Y)).Bounds;
                s.Offset(-vs.X, -vs.Y);
                s.Intersect(area);
                if (s.Width > 0 && s.Height > 0) area = s;
            }
            catch (Exception) { }
            int x = cur.X + gap, y = cur.Y + gap;
            if (x + w > area.Right) x = cur.X - gap - w;
            if (y + h > area.Bottom) y = cur.Y - gap - h;
            x = Math.Max(area.X, Math.Min(x, Math.Max(area.X, area.Right - w)));
            y = Math.Max(area.Y, Math.Min(y, Math.Max(area.Y, area.Bottom - h)));
            return new Rectangle(x, y, w, h);
        }

        void Dirty()
        {
            Rectangle d = PanelRect();
            d.Inflate(2, 2);
            Invalidate(lastDirty.IsEmpty ? d : Rectangle.Union(d, lastDirty));
            lastDirty = d;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle clip = e.ClipRectangle;
            g.DrawImage(shot, clip, clip, GraphicsUnit.Pixel);

            Rectangle m = PanelRect();
            int z = Zoom;
            Rectangle src = new Rectangle(cur.X - Half, cur.Y - Half, Cells, Cells);
            Rectangle dst = new Rectangle(m.X + (m.Width - Cells * z) / 2, m.Y, Cells * z, Cells * z);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(32, 32, 36))) g.FillRectangle(bg, m);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(shot, dst, src, GraphicsUnit.Pixel);
            g.PixelOffsetMode = PixelOffsetMode.Default;
            Color c = At(cur);
            // the pixel being picked: framed in black and white so it shows on any colour
            Rectangle px = new Rectangle(dst.X + Half * z, dst.Y + Half * z, z, z);
            g.DrawRectangle(Pens.Black, px.X - 1, px.Y - 1, px.Width + 1, px.Height + 1);
            g.DrawRectangle(Pens.White, px);

            // colour swatch, HEX and RGB under the magnifier
            int pad = (int)(8 * k), sw = (int)(40 * k);
            Rectangle swatch = new Rectangle(m.X + pad, dst.Bottom + pad, sw, sw);
            using (SolidBrush b = new SolidBrush(c)) g.FillRectangle(b, swatch);
            g.DrawRectangle(Pens.White, swatch);
            Rectangle txt = new Rectangle(swatch.Right + pad, swatch.Y - (int)(2 * k), m.Right - swatch.Right - 2 * pad, (int)(24 * k));
            TextRenderer.DrawText(g, Hex(c), font, txt, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            txt.Y += (int)(22 * k);
            TextRenderer.DrawText(g, "RGB " + c.R + ", " + c.G + ", " + c.B, small, txt, Color.FromArgb(215, 215, 220), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            Rectangle hint = new Rectangle(m.X + pad, swatch.Bottom + (int)(4 * k), m.Width - 2 * pad, m.Bottom - swatch.Bottom - (int)(4 * k));
            TextRenderer.DrawText(g, Lang.T("Click = line · Right-click = fill · Esc"), small, hint, Color.FromArgb(170, 175, 185),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            g.DrawRectangle(Pens.White, m.X, m.Y, m.Width - 1, m.Height - 1);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            cur = e.Location;
            Dirty();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;
            cur = e.Location;
            Finish(e.Button == MouseButtons.Right);
        }

        void Finish(bool fill)
        {
            Picked = At(cur);
            ForFill = fill;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int dx = 0, dy = 0, step = e.Shift ? 10 : 1;
            switch (e.KeyCode)
            {
                case Keys.Escape: DialogResult = DialogResult.Cancel; Close(); return;
                case Keys.Enter: Finish(e.Shift); return;
                case Keys.Left: dx = -step; break;
                case Keys.Right: dx = step; break;
                case Keys.Up: dy = -step; break;
                case Keys.Down: dy = step; break;
            }
            if (dx != 0 || dy != 0)
            {
                Point p = Cursor.Position;
                Cursor.Position = new Point(p.X + dx, p.Y + dy);
                e.Handled = true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { font.Dispose(); small.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
