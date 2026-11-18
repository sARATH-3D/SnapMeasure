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

        public static Bitmap Grab(Rectangle r)
        {
            Bitmap bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
            return bmp;
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
            string active = Native.WindowTitle(Native.GetForegroundWindow());   // the app you were in when you pressed the key
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
            int x = cur.X + gap, y = cur.Y + gap;
            if (x + w > ClientSize.Width) x = cur.X - gap - w;
            if (y + h > ClientSize.Height) y = cur.Y - gap - h;
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
            Rectangle d = MagRect();
            if (r.Width > 0)
            {
                Rectangle rr = r; rr.Inflate(4, 4);
                d = Rectangle.Union(d, rr);
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
            DrawMag(g);
        }

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
}
