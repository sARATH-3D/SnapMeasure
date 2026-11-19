using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapMeasure
{
    /// <summary>On-screen measuring tools that float over any app (PDF viewer, CAD, browser …).</summary>
    internal static class Overlays
    {
        static readonly List<Form> open = new List<Form>();

        static void Track(Form f)
        {
            open.Add(f);
            f.FormClosed += (s, e) => open.Remove(f);
            f.Show();
            f.Activate();
        }

        public static void ShowRuler() { Track(new RulerOverlay(Cursor.Position)); }
        public static void ShowProtractor() { Track(new ProtractorOverlay(Cursor.Position)); }

        public static void CloseAll()
        {
            foreach (Form f in open.ToArray()) f.Close();
        }
    }

    /// <summary>Borderless, always-on-top window drawn with per-pixel alpha (UpdateLayeredWindow).
    /// Fully transparent pixels let clicks through to the app underneath.</summary>
    internal abstract class LayeredOverlay : Form
    {
        protected readonly float k;          // DPI factor
        protected bool shiftDown { get { return (ModifierKeys & Keys.Shift) != 0; } }

        protected LayeredOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            k = Native.ScaleAt(Cursor.Position);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW (no Alt+Tab entry)
                return cp;
            }
        }

        /// <summary>Screen bounds of the window for the current state.</summary>
        protected abstract Rectangle ComputeBounds();
        /// <summary>Draws the overlay; g is translated so screen coordinates can be used directly.</summary>
        protected abstract void Render(Graphics g);

        protected void Redraw()
        {
            Rectangle r = ComputeBounds();
            if (r.Width < 1 || r.Height < 1) return;
            using (Bitmap b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.TranslateTransform(-r.X, -r.Y);
                    Render(g);
                }
                Layered.Apply(this, b, r.Location);
            }
            if (Bounds != r) Bounds = r;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // start fully on the screen that has the mouse
            Rectangle r = ComputeBounds(), wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int dx = 0, dy = 0;
            if (r.Right > wa.Right) dx = wa.Right - r.Right;
            if (r.Left + dx < wa.Left) dx = wa.Left - r.Left;
            if (r.Bottom > wa.Bottom) dy = wa.Bottom - r.Bottom;
            if (r.Top + dy < wa.Top) dy = wa.Top - r.Top;
            if (dx != 0 || dy != 0) MoveBy(dx, dy);
            Redraw();
        }

        /// <summary>Moves the overlay's anchor point by a screen offset.</summary>
        protected abstract void MoveBy(float dx, float dy);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            if (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift))
            {
                if (ContextMenuStrip != null) ContextMenuStrip.Show(this, PointToClient(Cursor.Position));
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void WndProc(ref Message m)
        {
            // Show the menu ourselves on right-button up. Relying on WM_CONTEXTMENU failed after the
            // overlay had been dragged (the menu simply did not open), so that message is ignored.
            const int WM_CONTEXTMENU = 0x007B, WM_RBUTTONUP = 0x0205;
            if (m.Msg == WM_CONTEXTMENU) return;
            base.WndProc(ref m);
            if (m.Msg == WM_RBUTTONUP && ContextMenuStrip != null)
                ContextMenuStrip.Show(Cursor.Position);
        }

        protected Point ScreenPt(MouseEventArgs e) { return PointToScreen(e.Location); }

        protected static Font F(float px, bool bold) { return Renderer.GetFont(px, bold); }

        protected static void Pill(Graphics g, string t, PointF c, Font f, Color back, Color fore)
        {
            SizeF sz = g.MeasureString(t, f);
            RectangleF r = new RectangleF(c.X - sz.Width / 2f - 6, c.Y - sz.Height / 2f - 2, sz.Width + 12, sz.Height + 4);
            using (GraphicsPath gp = new GraphicsPath())
            {
                float d = r.Height;
                gp.AddArc(r.X, r.Y, d, d, 90, 180);
                gp.AddArc(r.Right - d, r.Y, d, d, 270, 180);
                gp.CloseFigure();
                using (SolidBrush b = new SolidBrush(back)) g.FillPath(b, gp);
            }
            using (SolidBrush b = new SolidBrush(fore))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString(t, f, b, r, sf);
            }
        }

        protected ContextMenuStrip BuildMenu(params object[] items)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = Native.UiFont("Segoe UI", 9f, k);
            for (int i = 0; i + 1 < items.Length; i += 2)
            {
                string text = (string)items[i];
                Action a = (Action)items[i + 1];
                if (text == "-") { m.Items.Add(new ToolStripSeparator()); continue; }
                m.Items.Add(text, null, (s, e) => a());
            }
            return m;
        }
    }

    internal static class Layered
    {
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SZ { public int W, H; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref PT dst, ref SZ size, IntPtr hdcSrc, ref PT src, int key, ref BLEND blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        public static void Apply(Form f, Bitmap b, Point at)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hb = b.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(mem, hb);
                PT dst = new PT { X = at.X, Y = at.Y }, src = new PT();
                SZ size = new SZ { W = b.Width, H = b.Height };
                BLEND bl = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };   // AC_SRC_OVER, AC_SRC_ALPHA
                UpdateLayeredWindow(f.Handle, screen, ref dst, ref size, mem, ref src, 0, ref bl, 2);   // ULW_ALPHA
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
                if (hb != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(hb); }
                DeleteDC(mem);
            }
        }
    }

    /// <summary>A straight ruler in screen pixels. Drag to move, drag the green knob to rotate about the
    /// red pivot (left end), mouse wheel = 1° (Ctrl = 0.1°), Shift = 15° steps, right-click for options.</summary>
    internal class RulerOverlay : LayeredOverlay
    {
        PointF pivot;           // left end of the measuring edge (screen px)
        double ang;             // degrees, screen (clockwise positive)
        float len, thick;
        int drag;               // 0 none, 1 move, 2 rotate, 3 length, 4 red measuring marker
        PointF dragOff;
        float mark;             // red marker: distance from the pivot along the edge (screen px)
        readonly ScaleInfo sc = new ScaleInfo();   // ruler scale: real units per screen pixel (remembered)
        static readonly CultureInfo Cc = CultureInfo.CurrentCulture;

        public RulerOverlay(Point at)
        {
            Text = "SnapMeasure ruler";
            len = 600 * k; thick = 64 * k;
            mark = len / 2f;
            pivot = new PointF(at.X - len / 2f, at.Y - thick / 2f);
            sc.Unit = Settings.Get("RulerUnit", "px");
            sc.Upp = Settings.GetDouble("RulerUpp", 1.0);
            if (Array.IndexOf(ScaleInfo.Units, sc.Unit) < 0) { sc.Unit = "px"; sc.Upp = 1.0; }
            sc.Decimals = 1;
            ContextMenuStrip = BuildMenu(
                "Calibrate: red marker = real length…", (Action)Calibrate,
                "Set scale (1 px = …)…", (Action)AskScale,
                "Reset to pixels", (Action)(() => { sc.Unit = "px"; sc.Upp = 1.0; SaveScale(); Redraw(); }),
                "-", null,
                "Horizontal (0°)", (Action)(() => SetAngle(0)),
                "Vertical (90°)", (Action)(() => SetAngle(-90)),
                "Rotate 90°", (Action)(() => SetAngle(ang - 90)),
                "Set angle…", (Action)AskAngle,
                "-", null,
                "Length 300 px", (Action)(() => SetLen(300)),
                "Length 600 px", (Action)(() => SetLen(600)),
                "Length 1000 px", (Action)(() => SetLen(1000)),
                "Length 1600 px", (Action)(() => SetLen(1600)),
                "-", null,
                "New protractor", (Action)Overlays.ShowProtractor,
                "Close  (Esc)", (Action)Close);
        }

        PointF Dir { get { double r = ang * Math.PI / 180; return new PointF((float)Math.Cos(r), (float)Math.Sin(r)); } }
        PointF Nrm { get { PointF d = Dir; return new PointF(-d.Y, d.X); } }
        PointF At(float along, float across) { PointF d = Dir, n = Nrm; return new PointF(pivot.X + d.X * along + n.X * across, pivot.Y + d.Y * along + n.Y * across); }
        PointF Knob { get { return At(len - 16 * k, thick / 2f); } }
        PointF EndGrip { get { return At(len, thick * 0.82f); } }
        float KnobR { get { return 9 * k; } }

        /// <summary>Angle shown to the user: counter-clockwise from horizontal, 0..180.</summary>
        double ShownAngle { get { double a = -ang % 180; if (a < 0) a += 180; if (a >= 179.95) a = 0; return a; } }

        protected override void MoveBy(float dx, float dy) { pivot = new PointF(pivot.X + dx, pivot.Y + dy); }

        void SetAngle(double a) { ang = a; Redraw(); }
        void SetLen(float px) { len = px * k; mark = Math.Min(mark, len); Redraw(); }

        PointF MarkPt { get { return At(mark, 0); } }
        bool OnMark(PointF m)
        {
            PointF d = Dir, n = Nrm;
            float along = (m.X - pivot.X) * d.X + (m.Y - pivot.Y) * d.Y, across = (m.X - pivot.X) * n.X + (m.Y - pivot.Y) * n.Y;
            return Math.Abs(along - mark) <= 6 * k && across >= -8 * k && across <= thick * 0.5f;
        }

        void SaveScale()
        {
            Settings.Set("RulerUnit", sc.Unit);
            Settings.Set("RulerUpp", sc.Upp.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>Put the pivot on one end of a known dimension, the red marker on the other, then type its real length.</summary>
        void Calibrate()
        {
            if (mark < 5) { MessageBox.Show(this, "Drag the red marker to the other end of a known length first.", "Calibrate ruler"); return; }
            if (Dialogs.Calibrate(this, mark, sc, "The distance from the red circle to the red marker")) { SaveScale(); Redraw(); }
        }

        void AskScale()
        {
            string t = Dialogs.Ask(this, "1 screen pixel = how many " + (sc.Unit == "px" ? "mm" : sc.Unit) + "?  (e.g. 0.5)", sc.Calibrated ? sc.Upp.ToString("G6", Cc) : "", false);
            double v;
            if (t != null && (double.TryParse(t, NumberStyles.Float, Cc, out v) || double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) && v > 0 && v < 1e9)
            {
                if (sc.Unit == "px") sc.Unit = "mm";
                sc.Upp = v; SaveScale(); Redraw();
            }
        }

        string Len(double px) { return sc.Calibrated ? (px * sc.Upp).ToString("N" + LenDecimals(), Cc) + " " + sc.Unit : Math.Round(px).ToString(Cc) + " px"; }
        int LenDecimals() { double u = 100 * sc.Upp; return u >= 100 ? 0 : (u >= 10 ? 1 : 2); }

        static double Nice(double v)
        {
            if (v <= 0 || double.IsNaN(v) || double.IsInfinity(v)) return 1;
            double p = Math.Pow(10, Math.Floor(Math.Log10(v))), m = v / p;
            return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * p;
        }

        void AskAngle()
        {
            string t = Dialogs.Ask(this, "Ruler angle in degrees (counter-clockwise from horizontal)", ShownAngle.ToString("0.##", Cc), false);
            double v;
            if (t != null && (double.TryParse(t, NumberStyles.Float, Cc, out v) || double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v)))
                SetAngle(-v);
        }

        protected override Rectangle ComputeBounds()
        {
            PointF[] c = { At(0, 0), At(len, 0), At(len, thick), At(0, thick) };
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (PointF p in c) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
            int m = (int)(14 * k);
            return Rectangle.FromLTRB((int)Math.Floor(x0) - m, (int)Math.Floor(y0) - m, (int)Math.Ceiling(x1) + m, (int)Math.Ceiling(y1) + m);
        }

        protected override void Render(Graphics g)
        {
            GraphicsState st = g.Save();
            g.TranslateTransform(pivot.X, pivot.Y);
            g.RotateTransform((float)ang);
            RectangleF body = new RectangleF(0, 0, len, thick);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(215, 255, 236, 150))) g.FillRectangle(b, body);
            using (Pen p = new Pen(Color.FromArgb(230, 120, 95, 20), 1.2f)) g.DrawRectangle(p, 0, 0, len, thick);
            using (Pen tp = new Pen(Color.FromArgb(240, 40, 40, 40), 1f))
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(250, 40, 40, 40)))
            {
                Font f = F(10f * k, false);
                if (!sc.Calibrated)
                {
                    // ticks every 2 px (when there is room), longer every 10 / 50 / 100 px; numbers every 100 px
                    for (int i = 0; i <= (int)len; i += 2)
                    {
                        float L;
                        if (i % 100 == 0) L = thick * 0.42f;
                        else if (i % 50 == 0) L = thick * 0.3f;
                        else if (i % 10 == 0) L = thick * 0.2f;
                        else if (k < 1.4f) continue;   // 2-px ticks only on high-DPI screens
                        else L = thick * 0.08f;
                        g.DrawLine(tp, i, 0, i, L);
                        if (i % 100 == 0 && i > 0 && i < len - 30 * k) g.DrawString(i.ToString(Cc), f, tb, i + 1, thick * 0.42f - 4 * k);
                    }
                }
                else
                {
                    // calibrated: ticks and numbers in real units (mm, cm, m, in, ft)
                    double major = Nice(90 * k * sc.Upp);          // units between numbers, about 90 px apart
                    int sub = 10;
                    if (major / sub / sc.Upp < 5 * k) sub = 5;
                    if (major / sub / sc.Upp < 5 * k) sub = 2;
                    double minor = major / sub;
                    int dec = major >= 1 ? 0 : (int)Math.Ceiling(-Math.Log10(major) - 1e-9);
                    long n = (long)Math.Floor(len * sc.Upp / minor);
                    for (long i = 0; i <= n && i < 5000; i++)
                    {
                        float x = (float)(i * minor / sc.Upp);
                        bool mj = i % sub == 0, half = sub % 2 == 0 && i % (sub / 2) == 0;
                        float L = mj ? thick * 0.42f : (half ? thick * 0.28f : thick * 0.16f);
                        g.DrawLine(tp, x, 0, x, L);
                        if (mj && i > 0 && x < len - 30 * k) g.DrawString((i * minor).ToString("F" + dec, Cc), f, tb, x + 1, thick * 0.42f - 4 * k);
                    }
                    g.DrawString(sc.Unit, F(9f * k, true), tb, 3 * k, thick * 0.42f - 4 * k);
                }
                // red measuring marker
                using (Pen mp = new Pen(Color.FromArgb(230, 30, 30), 1.6f)) g.DrawLine(mp, mark, 0, mark, thick * 0.5f);
                using (SolidBrush mb = new SolidBrush(Color.FromArgb(230, 30, 30)))
                    g.FillPolygon(mb, new PointF[] { new PointF(mark - 6 * k, -1), new PointF(mark + 6 * k, -1), new PointF(mark, 8 * k) });
            }
            g.Restore(st);

            // readout in the middle: angle, red-marker distance, ruler length
            string t = "∠ " + ShownAngle.ToString("0.0", Cc) + "°    ▼ " + Len(mark) + "    L " + Len(len);
            Pill(g, t, At(len / 2f, thick * 0.74f), F(12f * k, true), Color.FromArgb(225, 35, 35, 35), Color.White);

            // pivot (red) and rotate knob (green)
            PointF pv = At(0, 0);
            using (Pen rp = new Pen(Color.FromArgb(230, 30, 30), 2f)) g.DrawEllipse(rp, pv.X - 5 * k, pv.Y - 5 * k, 10 * k, 10 * k);
            PointF kn = Knob;
            float r = KnobR;
            using (SolidBrush gb = new SolidBrush(Color.FromArgb(40, 175, 70))) g.FillEllipse(gb, kn.X - r, kn.Y - r, 2 * r, 2 * r);
            using (Pen wp = new Pen(Color.White, 1.8f)) g.DrawEllipse(wp, kn.X - r, kn.Y - r, 2 * r, 2 * r);
            using (Pen ap = new Pen(Color.White, 1.6f)) g.DrawArc(ap, kn.X - r * 0.5f, kn.Y - r * 0.5f, r, r, 30, 260);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            PointF m = ScreenPt(e);
            if (Geo.Dist(m, Knob) <= KnobR * 1.5f) drag = 2;
            else if (Geo.Dist(m, EndGrip) <= 10 * k) drag = 3;
            else if (OnMark(m)) drag = 4;
            else { drag = 1; dragOff = new PointF(m.X - pivot.X, m.Y - pivot.Y); }
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            PointF m = ScreenPt(e);
            if (drag == 0)
            {
                Cursor = Geo.Dist(m, Knob) <= KnobR * 1.5f ? Cursors.Hand : (Geo.Dist(m, EndGrip) <= 10 * k || OnMark(m) ? Cursors.SizeWE : Cursors.SizeAll);
                return;
            }
            if (drag == 4)
            {
                PointF d = Dir;
                float along = (m.X - pivot.X) * d.X + (m.Y - pivot.Y) * d.Y;
                mark = (float)Math.Round(Math.Max(0, Math.Min(len, along)));
                Redraw();
                return;
            }
            if (drag == 1) pivot = new PointF(m.X - dragOff.X, m.Y - dragOff.Y);
            else if (drag == 2)
            {
                double a = Math.Atan2(m.Y - pivot.Y, m.X - pivot.X) * 180 / Math.PI;
                // the knob sits below the edge; correct for its offset so the edge follows the mouse
                a -= Math.Atan2(thick / 2f, len - 16 * k) * 180 / Math.PI;
                if (shiftDown) a = Math.Round(a / 15) * 15;
                ang = a;
            }
            else if (drag == 3)
            {
                PointF d = Dir;
                float along = (m.X - pivot.X) * d.X + (m.Y - pivot.Y) * d.Y;
                len = Math.Max(120 * k, Math.Min(4000, along));
                mark = Math.Min(mark, len);
            }
            Redraw();
        }

        protected override void OnMouseUp(MouseEventArgs e) { drag = 0; Capture = false; }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            double step = (ModifierKeys & Keys.Control) != 0 ? 0.1 : ((ModifierKeys & Keys.Shift) != 0 ? 15 : 1);
            ang -= Math.Sign(e.Delta) * step;
            if (step == 15) ang = Math.Round(ang / 15) * 15;
            Redraw();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int s = e.Shift ? 10 : 1;
            switch (e.KeyCode)
            {
                case Keys.Left: pivot.X -= s; break;
                case Keys.Right: pivot.X += s; break;
                case Keys.Up: pivot.Y -= s; break;
                case Keys.Down: pivot.Y += s; break;
                case Keys.H: ang = 0; break;
                case Keys.V: ang = -90; break;
                default: base.OnKeyDown(e); return;
            }
            Redraw();
        }
    }

    /// <summary>A 360° protractor with two arms. Drag the arm knobs to line them up with the edges you want to
    /// measure; drag anywhere else to move it; mouse wheel = size; right-click for options.</summary>
    internal class ProtractorOverlay : LayeredOverlay
    {
        PointF c;               // centre (screen px)
        float radius;
        double baseAng, measAng;   // screen degrees (clockwise positive)
        int drag;               // 0 none, 1 move, 2 base arm, 3 measuring arm
        PointF dragOff;
        static readonly CultureInfo Cc = CultureInfo.CurrentCulture;

        public ProtractorOverlay(Point at)
        {
            Text = "SnapMeasure protractor";
            c = at;
            radius = 150 * k;
            baseAng = 0; measAng = -45;
            ContextMenuStrip = BuildMenu(
                "Reset arms (0° / 45°)", (Action)(() => { baseAng = 0; measAng = -45; Redraw(); }),
                "Base arm horizontal", (Action)(() => { baseAng = 0; Redraw(); }),
                "Swap arms", (Action)(() => { double t = baseAng; baseAng = measAng; measAng = t; Redraw(); }),
                "-", null,
                "Small", (Action)(() => { radius = 100 * k; Redraw(); }),
                "Medium", (Action)(() => { radius = 150 * k; Redraw(); }),
                "Large", (Action)(() => { radius = 240 * k; Redraw(); }),
                "-", null,
                "New ruler", (Action)Overlays.ShowRuler,
                "Close  (Esc)", (Action)Close);
        }

        protected override void MoveBy(float dx, float dy) { c = new PointF(c.X + dx, c.Y + dy); }

        float ArmLen { get { return radius * 1.55f; } }
        float KnobR { get { return 9 * k; } }
        PointF ArmEnd(double a) { double r = a * Math.PI / 180; return new PointF(c.X + (float)Math.Cos(r) * ArmLen, c.Y + (float)Math.Sin(r) * ArmLen); }

        /// <summary>Angle from the base arm to the measuring arm, counter-clockwise, 0..360.</summary>
        double Sweep { get { double d = -(measAng - baseAng) % 360; if (d < 0) d += 360; return d; } }

        protected override Rectangle ComputeBounds()
        {
            int m = (int)(ArmLen + KnobR + 6 * k);
            return new Rectangle((int)c.X - m, (int)c.Y - m, 2 * m, 2 * m);
        }

        protected override void Render(Graphics g)
        {
            RectangleF disk = new RectangleF(c.X - radius, c.Y - radius, 2 * radius, 2 * radius);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 235, 245, 255))) g.FillEllipse(b, disk);
            using (Pen p = new Pen(Color.FromArgb(230, 0, 90, 170), 1.5f)) g.DrawEllipse(p, disk);

            // swept angle
            double sw = Sweep;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 230, 40, 40)))
                if (sw > 0.05) g.FillPie(b, c.X - radius * 0.45f, c.Y - radius * 0.45f, radius * 0.9f, radius * 0.9f, (float)baseAng, (float)-sw);

            // degree ticks, numbered from the base arm counter-clockwise
            Font f = F(9.5f * k, false);
            using (Pen tp = new Pen(Color.FromArgb(235, 30, 50, 80), 1f))
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(245, 30, 50, 80)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                for (int d = 0; d < 360; d++)
                {
                    float L = d % 10 == 0 ? 14 * k : (d % 5 == 0 ? 9 * k : 5 * k);
                    if (d % 10 != 0 && radius < 120 * k && d % 5 != 0) continue;
                    double a = (baseAng - d) * Math.PI / 180;
                    float cs = (float)Math.Cos(a), sn = (float)Math.Sin(a);
                    g.DrawLine(tp, c.X + cs * radius, c.Y + sn * radius, c.X + cs * (radius - L), c.Y + sn * (radius - L));
                    if (d % (radius < 120 * k ? 30 : 10) == 0)
                    {
                        float tr = radius - 14 * k - 9 * k;
                        g.DrawString(d.ToString(Cc), f, tb, new RectangleF(c.X + cs * tr - 14 * k, c.Y + sn * tr - 8 * k, 28 * k, 16 * k), sf);
                    }
                }
            }

            // arms
            DrawArm(g, baseAng, Color.FromArgb(0, 110, 210));
            DrawArm(g, measAng, Color.FromArgb(220, 35, 35));

            // centre cross
            using (Pen p = new Pen(Color.FromArgb(240, 20, 20, 20), 1.2f))
            {
                g.DrawLine(p, c.X - 8 * k, c.Y, c.X + 8 * k, c.Y);
                g.DrawLine(p, c.X, c.Y - 8 * k, c.X, c.Y + 8 * k);
            }

            // readout
            double inner = sw > 180 ? 360 - sw : sw;
            string t = sw.ToString("0.0", Cc) + "°";
            Pill(g, t, new PointF(c.X, c.Y + radius * 0.38f), F(16f * k, true), Color.FromArgb(230, 35, 35, 35), Color.White);
            string t2 = "inside " + inner.ToString("0.0", Cc) + "°   ·   other side " + (360 - sw).ToString("0.0", Cc) + "°";
            Pill(g, t2, new PointF(c.X, c.Y + radius * 0.58f), F(9.5f * k, false), Color.FromArgb(200, 255, 255, 255), Color.FromArgb(40, 40, 40));
        }

        void DrawArm(Graphics g, double a, Color col)
        {
            PointF e = ArmEnd(a);
            using (Pen p = new Pen(Color.FromArgb(235, col), 1.6f)) g.DrawLine(p, c, e);
            float r = KnobR;
            using (SolidBrush b = new SolidBrush(col)) g.FillEllipse(b, e.X - r, e.Y - r, 2 * r, 2 * r);
            using (Pen w = new Pen(Color.White, 1.8f)) g.DrawEllipse(w, e.X - r, e.Y - r, 2 * r, 2 * r);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            PointF m = ScreenPt(e);
            if (Geo.Dist(m, ArmEnd(measAng)) <= KnobR * 1.6f) drag = 3;
            else if (Geo.Dist(m, ArmEnd(baseAng)) <= KnobR * 1.6f) drag = 2;
            else { drag = 1; dragOff = new PointF(m.X - c.X, m.Y - c.Y); }
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            PointF m = ScreenPt(e);
            if (drag == 0)
            {
                bool knob = Geo.Dist(m, ArmEnd(measAng)) <= KnobR * 1.6f || Geo.Dist(m, ArmEnd(baseAng)) <= KnobR * 1.6f;
                Cursor = knob ? Cursors.Hand : Cursors.SizeAll;
                return;
            }
            if (drag == 1) c = new PointF(m.X - dragOff.X, m.Y - dragOff.Y);
            else
            {
                double a = Math.Atan2(m.Y - c.Y, m.X - c.X) * 180 / Math.PI;
                if (shiftDown)
                {
                    double rel = a - (drag == 3 ? baseAng : 0);
                    a = Math.Round(rel / 15) * 15 + (drag == 3 ? baseAng : 0);
                }
                if (drag == 2) baseAng = a; else measAng = a;
            }
            Redraw();
        }

        protected override void OnMouseUp(MouseEventArgs e) { drag = 0; Capture = false; }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            radius = Math.Max(80 * k, Math.Min(420 * k, radius + Math.Sign(e.Delta) * 12 * k));
            Redraw();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int s = e.Shift ? 10 : 1;
            switch (e.KeyCode)
            {
                case Keys.Left: c.X -= s; break;
                case Keys.Right: c.X += s; break;
                case Keys.Up: c.Y -= s; break;
                case Keys.Down: c.Y += s; break;
                default: base.OnKeyDown(e); return;
            }
            Redraw();
        }
    }
}
