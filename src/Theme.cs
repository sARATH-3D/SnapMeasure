using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapMeasure
{
    /// <summary>Light / dark appearance (Settings → General → Appearance: Automatic follows Windows).
    /// The dark look follows Apple's dark-mode design: a few layers of dark grey (window, bars and panels one step lighter,
    /// controls lighter again), white primary text with grey secondary text, hairline separators, rounded highlights and one
    /// blue accent for what is selected or switched on.
    /// WinForms has no dark theme of its own, so the windows are recoloured after they are built. The recolouring only turns
    /// light colours into dark ones (a colour that is already dark / light as wanted is left alone), so doing it twice is harmless.
    /// A control that must keep its exact colour (a colour swatch) is marked with Theme.Keep.</summary>
    internal static class Theme
    {
        public const string KeepTag = "keepcolor";

        // ---- the palette (Apple's dark system colours) ----
        public static readonly Color Base = Color.FromArgb(30, 30, 30);            // window
        public static readonly Color Elevated = Color.FromArgb(44, 44, 46);        // bars, side panel, menus
        public static readonly Color Fill = Color.FromArgb(58, 58, 60);            // buttons, selected tab
        public static readonly Color FillHover = Color.FromArgb(72, 72, 74);
        public static readonly Color FillPressed = Color.FromArgb(99, 99, 102);
        public static readonly Color Field = Color.FromArgb(28, 28, 30);           // text boxes and lists
        public static readonly Color Separator = Color.FromArgb(56, 56, 58);
        public static readonly Color Border = Color.FromArgb(84, 84, 88);
        public static readonly Color Label = Color.FromArgb(242, 242, 247);        // primary text
        public static readonly Color Label2 = Color.FromArgb(162, 162, 167);       // secondary text
        public static readonly Color Label3 = Color.FromArgb(110, 110, 115);       // disabled / hints
        public static readonly Color Accent = Color.FromArgb(10, 132, 255);        // system blue
        public static readonly Color AccentFill = Color.FromArgb(14, 56, 104);     // switched-on button background

        /// <summary>Set by the self-test to draw the windows dark whatever Windows is set to.</summary>
        public static bool ForceDark;
        static int dark = -1;
        static readonly ConditionalWeakTable<object, object> done = new ConditionalWeakTable<object, object>();
        static readonly ConditionalWeakTable<object, object> keep = new ConditionalWeakTable<object, object>();

        /// <summary>"Auto", "Light" or "Dark".</summary>
        public static string Mode
        {
            get { string m = Settings.Get("Theme", "Auto"); return m == "Light" || m == "Dark" ? m : "Auto"; }
        }

        public static bool Dark
        {
            get
            {
                if (ForceDark) return true;
                if (dark < 0)
                {
                    string m = Mode;
                    dark = m == "Dark" ? 1 : m == "Light" ? 0 : (WindowsIsDark() ? 1 : 0);
                }
                return dark == 1;
            }
        }

        static bool WindowsIsDark()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>The choice in Settings changed: menus follow at once, windows when they are next opened.</summary>
        public static void Reset()
        {
            dark = -1;
            Init();
        }

        /// <summary>Menus, tool bars and status bars of the whole program draw with the dark renderer (or the normal one).</summary>
        public static void Init()
        {
            try { ToolStripManager.Renderer = Dark ? (ToolStripRenderer)new DarkRenderer() : new ToolStripProfessionalRenderer(); }
            catch (Exception) { }
        }

        // ---------------- colours ----------------
        /// <summary>Ink for icons.</summary>
        public static Color Ink { get { return Dark ? Color.FromArgb(235, 235, 242) : Color.FromArgb(52, 56, 64); } }
        public static Color Text { get { return Dark ? Label : SystemColors.ControlText; } }
        public static Color Link { get { return Dark ? Color.FromArgb(100, 170, 255) : Color.FromArgb(0, 102, 204); } }

        static Color FromHsl(float h, float s, float l, int a)
        {
            if (s <= 0.0001f) { int v = (int)Math.Round(l * 255); return Color.FromArgb(a, v, v, v); }
            float q = l < 0.5f ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            Func<float, float> chan = t =>
            {
                if (t < 0) t += 1; if (t > 1) t -= 1;
                if (t < 1f / 6) return p + (q - p) * 6 * t;
                if (t < 0.5f) return q;
                if (t < 2f / 3) return p + (q - p) * (2f / 3 - t) * 6;
                return p;
            };
            float hh = h / 360f;
            return Color.FromArgb(a, (int)Math.Round(chan(hh + 1f / 3) * 255), (int)Math.Round(chan(hh) * 255), (int)Math.Round(chan(hh - 1f / 3) * 255));
        }

        /// <summary>A background colour for dark mode. Light greys become the layers of the dark palette (window, bar, control);
        /// pale tints become deep tints (blue ones the accent fill); colours that are already dark or strong stay.</summary>
        public static Color MapBack(Color c)
        {
            if (!Dark || c.A == 0 || c.IsEmpty) return c;
            float s = c.GetSaturation(), l = c.GetBrightness();
            if (l <= 0.36f) return c;                                         // already dark
            if (s >= 0.2f && l < 0.93f)                                       // (very light colours with a hint of blue, like 246,247,249, count as grey)
            {
                if (l <= 0.62f) return c;                                     // a strong colour (amber, red…) stays
                float h = c.GetHue();
                return h >= 190 && h <= 260 ? Color.FromArgb(c.A, AccentFill) : FromHsl(h, 0.5f, 0.22f, c.A);
            }
            Color m;
            if (l >= 0.985f) m = Base;                                        // white
            else if (l >= 0.95f) m = Elevated;                                // (246,247,249): the bars
            else if (l >= 0.90f) m = Base;                                    // (240,240,240): the window
            else if (l >= 0.75f) m = Fill;                                    // light grey faces
            else m = FillHover;
            return Color.FromArgb(c.A, m);
        }

        /// <summary>A text colour for dark mode: dark text becomes the primary (white) or secondary (grey) label colour,
        /// dark coloured text is lightened; light text stays.</summary>
        public static Color MapFore(Color c)
        {
            if (!Dark || c.A == 0 || c.IsEmpty) return c;
            float s = c.GetSaturation(), l = c.GetBrightness();
            if (l >= 0.62f) return c;                                         // already light
            if (s >= 0.2f) return l < 0.5f ? FromHsl(c.GetHue(), Math.Min(1f, s), 0.68f, c.A) : c;
            return Color.FromArgb(c.A, l < 0.30f ? Label : Label2);
        }

        // ---------------- applying it to a window ----------------
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string subApp, string subIdList);

        class Orig { public Color Back, Fore; }
        static Dictionary<Control, Orig> orig;
        static Dictionary<ToolStripItem, Color> itemFore;

        /// <summary>This control shows a colour the user chose (a swatch): dark mode must not recolour it.</summary>
        public static void Keep(Control c)
        {
            object o;
            if (!keep.TryGetValue(c, out o)) keep.Add(c, c);
        }

        /// <summary>Recolours a finished window and everything in it. Safe to call again after more controls were added.</summary>
        public static void Apply(Control root)
        {
            if (root == null) return;
            ScaleTicks(root);
            if (!Dark) return;
            try
            {
                Form f = root as Form;
                if (f != null) DarkTitleBar(f);
                // two passes: first note every control's own colours (a child that never had a colour of its own reports its
                // parent's), then recolour them all
                orig = new Dictionary<Control, Orig>();
                itemFore = new Dictionary<ToolStripItem, Color>();
                List<Control> fresh = new List<Control>();
                Collect(root, fresh);
                foreach (Control c in fresh) Recolor(c);
            }
            catch (Exception ex) { ErrorLog.Write("dark mode", ex); }
            finally { orig = null; itemFore = null; }
        }

        // ---------------- tick boxes at high display scaling ----------------
        static readonly ConditionalWeakTable<object, object> ticked = new ConditionalWeakTable<object, object>();

        /// <summary>.NET Framework draws the tick-box and option-button glyph 13 px square whatever the text size, so with
        /// large text it is half the height of its words. When the text is over about 115 % of normal they are drawn
        /// again at the matching size, over the small one (the text moves right by the difference, so nothing overlaps).</summary>
        static void ScaleTicks(Control root)
        {
            foreach (Control c in AllControls(root))
            {
                ButtonBase bb = c as ButtonBase;
                if (bb == null || !(c is CheckBox || c is RadioButton)) continue;
                CheckBox cbx = c as CheckBox;
                if (cbx != null && cbx.Appearance == Appearance.Button) continue;
                RadioButton rb = c as RadioButton;
                if (rb != null && rb.Appearance == Appearance.Button) continue;
                object seen;
                if (ticked.TryGetValue(c, out seen)) continue;
                ticked.Add(c, c);
                Control cc = c;
                Action setUp = () =>
                {
                    // scale by the text, not the screen: SnapMeasure also enlarges its windows itself (large text, big
                    // screens), and the 13 px glyph matches a 9 pt Segoe UI line of about 16 px
                    float k = cc.Font.Height / 16f;
                    if (k < 1.15f) return;
                    int size = (int)Math.Round(13f * k), native = 13;
                    // at 150 % / 200 % Windows already draws a bigger box: drawing over it left its right edge showing
                    try { using (Graphics g = cc.CreateGraphics()) native = CheckBoxRenderer.GetGlyphSize(g, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal).Width; }
                    catch (Exception) { }
                    if (size - native < 3) return;
                    int extra = size - native;
                    cc.Padding = new Padding(cc.Padding.Left + extra, cc.Padding.Top, cc.Padding.Right, cc.Padding.Bottom);
                    cc.Paint += (s, e) => DrawTick(cc, e.Graphics, size, extra, native);
                    cc.Invalidate();
                };
                if (c.IsHandleCreated) setUp(); else c.HandleCreated += (s, e) => setUp();
            }
        }

        static IEnumerable<Control> AllControls(Control c)
        {
            foreach (Control k in c.Controls)
            {
                yield return k;
                foreach (Control g in AllControls(k)) yield return g;
            }
        }

        static void DrawTick(Control c, Graphics g, int size, int extra, int native)
        {
            try
            {
                CheckBox cb = c as CheckBox;
                bool radio = c is RadioButton;
                bool on = cb != null ? cb.CheckState != CheckState.Unchecked : ((RadioButton)c).Checked;
                bool mixed = cb != null && cb.CheckState == CheckState.Indeterminate;
                ContentAlignment al = cb != null ? cb.CheckAlign : ((RadioButton)c).CheckAlign;
                bool top = al == ContentAlignment.TopLeft;
                int lineH = c.Font.Height;
                int y = top ? c.Padding.Top + Math.Max(0, (lineH - size) / 2) : (c.Height - size) / 2;
                Rectangle r = new Rectangle(0, y, size - 1, size - 1);
                // cover the small glyph Windows drew (it sits 'extra' px further right, centred the same way)
                Color bg = c.BackColor;
                for (Control p = c; bg.A < 255 && p.Parent != null; p = p.Parent) bg = p.Parent.BackColor;
                int sy = top ? c.Padding.Top + Math.Max(0, (lineH - native) / 2) : (c.Height - native) / 2;
                using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, 0, Math.Min(y, sy) - 1, extra + native + 2, size + 2);

                bool en = c.Enabled;
                Color accent = Dark ? Accent : Color.FromArgb(0, 103, 192);
                Color edge = !en ? (Dark ? Color.FromArgb(80, 80, 84) : Color.FromArgb(190, 190, 190))
                           : (Dark ? Color.FromArgb(160, 160, 166) : Color.FromArgb(118, 118, 118));
                Color box = Dark ? Field : Color.White;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float pw = Math.Max(1f, size / 13f);
                if (radio)
                {
                    using (SolidBrush b = new SolidBrush(box)) g.FillEllipse(b, r);
                    using (Pen p = new Pen(on && en ? accent : edge, pw)) g.DrawEllipse(p, r.X + pw / 2, r.Y + pw / 2, r.Width - pw, r.Height - pw);
                    if (on)
                    {
                        float d = size * 0.44f;
                        using (SolidBrush b = new SolidBrush(en ? accent : edge))
                            g.FillEllipse(b, r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
                    }
                    return;
                }
                float rad = size / 5f;
                using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    RectangleF rf = new RectangleF(r.X + pw / 2, r.Y + pw / 2, r.Width - pw, r.Height - pw);
                    path.AddArc(rf.X, rf.Y, rad * 2, rad * 2, 180, 90);
                    path.AddArc(rf.Right - rad * 2, rf.Y, rad * 2, rad * 2, 270, 90);
                    path.AddArc(rf.Right - rad * 2, rf.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
                    path.AddArc(rf.X, rf.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
                    path.CloseFigure();
                    using (SolidBrush b = new SolidBrush(on ? (en ? accent : edge) : box)) g.FillPath(b, path);
                    if (!on) using (Pen p = new Pen(edge, pw)) g.DrawPath(p, path);
                }
                if (on)
                    using (Pen p = new Pen(Color.White, Math.Max(1.5f, size / 8f)))
                    {
                        p.StartCap = p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        if (mixed) g.DrawLine(p, r.X + size * 0.28f, r.Y + size * 0.5f, r.X + size * 0.72f, r.Y + size * 0.5f);
                        else g.DrawLines(p, new PointF[] {
                            new PointF(r.X + size * 0.25f, r.Y + size * 0.52f),
                            new PointF(r.X + size * 0.43f, r.Y + size * 0.70f),
                            new PointF(r.X + size * 0.76f, r.Y + size * 0.32f) });
                    }
            }
            catch (Exception) { }
        }

        static void DarkTitleBar(Form f)
        {
            Action set = () => { try { int on = 1; if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4); } catch (Exception) { } };
            if (f.IsHandleCreated) set(); else f.HandleCreated += (s, e) => set();
        }

        static void Collect(Control c, List<Control> fresh)
        {
            object kept;
            if (c.Tag as string == KeepTag || keep.TryGetValue(c, out kept)) return;
            object seen;
            if (!done.TryGetValue(c, out seen))
            {
                done.Add(c, c);
                fresh.Add(c);
                Orig o = new Orig(); o.Back = c.BackColor; o.Fore = c.ForeColor;
                orig[c] = o;
                ToolStrip ts = c as ToolStrip;
                if (ts != null) foreach (ToolStripItem it in ts.Items) CollectItem(it, fresh);
            }
            if (c is Canvas) return;                       // the picture area paints itself (and has its own dark grey surround)
            foreach (Control child in c.Controls) Collect(child, fresh);
        }

        static void CollectItem(ToolStripItem it, List<Control> fresh)
        {
            itemFore[it] = it.ForeColor;
            ToolStripDropDownItem dd = it as ToolStripDropDownItem;
            if (dd != null && dd.HasDropDownItems) foreach (ToolStripItem sub in dd.DropDownItems) CollectItem(sub, fresh);
            ToolStripControlHost host = it as ToolStripControlHost;
            if (host != null && host.Control != null) Collect(host.Control, fresh);
        }

        static void WinTheme(Control c, string name, string list)
        {
            Action th = () => { try { SetWindowTheme(c.Handle, name, list); } catch (Exception) { } };
            if (c.IsHandleCreated) th(); else c.HandleCreated += (s, e) => th();
        }

        static void Recolor(Control c)
        {
            Orig o = orig[c];
            Color back = o.Back, fore = o.Fore;

            ToolStrip ts = c as ToolStrip;
            if (ts != null)
            {
                ts.BackColor = Elevated; ts.ForeColor = MapFore(fore);
                if (!(ts is ToolStripDropDown)) ts.Renderer = new DarkRenderer();       // the editor's bars use the system look: switch them to the dark one
                foreach (ToolStripItem it in ts.Items) RecolorItem(it);
                return;
            }
            if (c is Canvas) return;
            if (c is PictureBox) { if (back.A != 0) c.BackColor = MapBack(back); return; }

            TabControl tc = c as TabControl;
            if (tc != null) { DarkTabs(tc); return; }
            TabPage tp = c as TabPage;
            if (tp != null) { tp.UseVisualStyleBackColor = false; tp.BackColor = Base; tp.ForeColor = Label; return; }

            ComboBox cb = c as ComboBox;
            if (cb != null)
            {
                cb.BackColor = Field; cb.ForeColor = Label;
                WinTheme(cb, "DarkMode_CFD", "Combobox");      // Windows' own dark drop-down (its arrow button too)
                return;
            }

            Button b = c as Button;
            if (b != null)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = MapBack(back == SystemColors.Control ? Color.FromArgb(225, 225, 225) : back);
                b.ForeColor = MapFore(fore);
                b.FlatAppearance.BorderColor = Separator;
                b.FlatAppearance.MouseOverBackColor = FillHover;
                b.FlatAppearance.MouseDownBackColor = FillPressed;
                b.FlatAppearance.CheckedBackColor = AccentFill;
                return;
            }

            LinkLabel ll = c as LinkLabel;
            if (ll != null) { ll.LinkColor = Link; ll.ActiveLinkColor = Color.FromArgb(150, 200, 255); ll.VisitedLinkColor = Link; ll.ForeColor = MapFore(fore); if (back.A != 0) ll.BackColor = MapBack(back); return; }

            ListView lv = c as ListView;
            if (lv != null) { DarkList(lv); return; }

            if (c is TextBox || c is NumericUpDown || c is TreeView || c is RichTextBox || c is ListBox)
            {
                c.BackColor = Field;
                c.ForeColor = Label;
                // the sunken 3-D edge draws a thick white rim on a dark window: a single thin line instead
                TextBoxBase tb = c as TextBoxBase;
                if (tb != null && tb.BorderStyle == BorderStyle.Fixed3D) tb.BorderStyle = BorderStyle.FixedSingle;
                UpDownBase ud = c as UpDownBase;
                if (ud != null && ud.BorderStyle == BorderStyle.Fixed3D) ud.BorderStyle = BorderStyle.FixedSingle;
                WinTheme(c, "DarkMode_Explorer", null);         // dark scroll bars
                return;
            }

            if (c is CheckBox || c is RadioButton)
            {
                c.ForeColor = MapFore(fore);
                if (back.A != 0) c.BackColor = MapBack(back);
                CheckBox tg = c as CheckBox;
                if (tg != null && tg.Appearance == Appearance.Button) tg.FlatAppearance.CheckedBackColor = AccentFill;
                WinTheme(c, "DarkMode_Explorer", null);
                ReadableWhenOff(c);
                return;
            }

            if (c is Label || c is GroupBox)
            {
                Label sep = c as Label;
                if (sep != null && sep.BorderStyle == BorderStyle.Fixed3D && sep.Height <= 3 && sep.Text.Length == 0)
                {
                    // an etched divider line: one hairline in the separator colour
                    sep.BorderStyle = BorderStyle.None;
                    sep.Height = 1;
                    sep.BackColor = Separator;
                    return;
                }
                c.ForeColor = MapFore(fore);
                if (back.A != 0) c.BackColor = MapBack(back);
                if (sep != null) ReadableWhenOff(c);
                return;
            }

            // panels, flow panels, the form itself: surface colours
            c.BackColor = MapBack(back);
            c.ForeColor = MapFore(fore);
            ScrollableControl sc = c as ScrollableControl;
            if (sc != null && sc.AutoScroll) WinTheme(c, "DarkMode_Explorer", null);       // dark scroll bars
        }

        /// <summary>Colour for text that is switched off (greyed out): still easy to read, just quieter than the rest.</summary>
        public static readonly Color OffText = Color.FromArgb(138, 138, 143);

        /// <summary>Windows draws switched-off text as an "etched" pair of greys worked out from the background; on a dark
        /// background both are nearly black and the text vanishes (Line, Fill, Shadow, 0 % on the style bar). Draw it
        /// again in a readable grey.</summary>
        static void ReadableWhenOff(Control c)
        {
            c.Paint += (s, e) =>
            {
                if (c.Enabled || !Dark || string.IsNullOrEmpty(c.Text)) return;
                try
                {
                    Color bg = c.BackColor;
                    for (Control p = c; bg.A < 255 && p.Parent != null; p = p.Parent) bg = p.Parent.BackColor;
                    TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
                    Rectangle r;
                    Label l = c as Label;
                    if (l != null)
                    {
                        ContentAlignment a = l.TextAlign;
                        if (a == ContentAlignment.TopLeft || a == ContentAlignment.TopCenter || a == ContentAlignment.TopRight)
                            flags = (flags & ~TextFormatFlags.VerticalCenter) | TextFormatFlags.Top;
                        if (a == ContentAlignment.TopCenter || a == ContentAlignment.MiddleCenter || a == ContentAlignment.BottomCenter) flags |= TextFormatFlags.HorizontalCenter;
                        else if (a == ContentAlignment.TopRight || a == ContentAlignment.MiddleRight || a == ContentAlignment.BottomRight) flags |= TextFormatFlags.Right;
                        flags |= l.AutoEllipsis ? TextFormatFlags.EndEllipsis : TextFormatFlags.WordBreak;
                        r = new Rectangle(l.Padding.Left, l.Padding.Top, l.Width - l.Padding.Horizontal, l.Height - l.Padding.Vertical);
                        using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, c.ClientRectangle);
                    }
                    else
                    {
                        // a tick box: its text ends at the right edge when it sizes itself, else it follows the tick
                        Size ts = TextRenderer.MeasureText(c.Text, c.Font);
                        int x = c.AutoSize ? Math.Max(0, c.Width - ts.Width) : c.Padding.Left + (int)Math.Round(c.Font.Height * 1.25f);
                        r = new Rectangle(x, 0, c.Width - x, c.Height);
                        using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, x - 1, 0, c.Width - x + 1, c.Height);
                    }
                    TextRenderer.DrawText(e.Graphics, c.Text, c.Font, r, OffText, flags);
                }
                catch (Exception) { }
            };
            c.EnabledChanged += (s, e) => c.Invalidate();
        }

        static void RecolorItem(ToolStripItem it)
        {
            Color f;
            it.ForeColor = MapFore(itemFore.TryGetValue(it, out f) ? f : it.ForeColor);
            ToolStripDropDownItem dd = it as ToolStripDropDownItem;
            if (dd != null && dd.HasDropDownItems) foreach (ToolStripItem sub in dd.DropDownItems) RecolorItem(sub);
        }

        // ---------------- lists: a dark header like the rest ----------------
        static void DarkList(ListView lv)
        {
            lv.BackColor = Field; lv.ForeColor = Label;
            lv.OwnerDraw = true;
            lv.DrawColumnHeader += (s, e) =>
            {
                using (SolidBrush bg = new SolidBrush(Elevated)) e.Graphics.FillRectangle(bg, e.Bounds);
                using (Pen p = new Pen(Separator))
                {
                    e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                    e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
                }
                Rectangle t = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 10), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, lv.Font, t, Label2,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            // Windows writes a selected row in black, unreadable on dark: that row is drawn here instead
            lv.DrawItem += (s, e) => { e.DrawDefault = lv.View != View.Details || !e.Item.Selected; };
            lv.DrawSubItem += (s, e) =>
            {
                if (!e.Item.Selected) { e.DrawDefault = true; return; }
                DrawSelectedCell(e.Graphics, lv, e.Item, e.ColumnIndex, e.Bounds);
            };
            WinTheme(lv, "DarkMode_Explorer", null);
            // the header strip to the right of the last column is painted by Windows (white): paint it dark too
            Action hdr = () => { try { IntPtr h = SendMessage(lv.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero); if (h != IntPtr.Zero) new HeaderFiller(lv, h); } catch (Exception) { } };
            if (lv.IsHandleCreated) hdr(); else lv.HandleCreated += (s, e) => hdr();
        }

        static void DrawSelectedCell(Graphics g, ListView lv, ListViewItem item, int col, Rectangle r)
        {
            if (col == 0) r.Width = Math.Min(r.Width, lv.Columns[0].Width);     // Windows gives the first cell the whole row
            using (SolidBrush bg = new SolidBrush(AccentFill)) g.FillRectangle(bg, r);
            Font f = item.UseItemStyleForSubItems || col >= item.SubItems.Count ? item.Font : item.SubItems[col].Font;
            Rectangle t = new Rectangle(r.X, r.Y, Math.Max(0, r.Width - 2), r.Height);     // text where the other rows have it
            TextRenderer.DrawText(g, col < item.SubItems.Count ? item.SubItems[col].Text : "", f, t, Label,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out Rectangle r);

        class HeaderFiller : NativeWindow
        {
            readonly ListView lv;
            public HeaderFiller(ListView lv, IntPtr header)
            {
                this.lv = lv;
                AssignHandle(header);
                lv.HandleDestroyed += (s, e) => { if (Handle != IntPtr.Zero) ReleaseHandle(); };
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != 0x000F && m.Msg != 0x0014) return;          // WM_PAINT, WM_ERASEBKGND
                try
                {
                    int used = 0;
                    foreach (ColumnHeader ch in lv.Columns) used += ch.Width;
                    using (Graphics g = Graphics.FromHwnd(Handle))
                    using (SolidBrush b = new SolidBrush(Elevated))
                    using (Pen p = new Pen(Separator))
                    {
                        int w = lv.ClientSize.Width + 20, h = lv.Font.Height + 8;
                        if (used < w) g.FillRectangle(b, used, 0, w - used, h);
                        if (used < w) g.DrawLine(p, used, h - 1, w, h - 1);
                    }
                }
                catch (Exception) { }
            }
        }

        // ---------------- tabs ----------------
        static void DarkTabs(TabControl tc)
        {
            tc.DrawMode = TabDrawMode.OwnerDrawFixed;
            tc.SizeMode = TabSizeMode.Fixed;
            int w = 0;
            foreach (TabPage p in tc.TabPages) w = Math.Max(w, TextRenderer.MeasureText(p.Text, tc.Font).Width);
            tc.ItemSize = new Size(w + 28, Math.Max(tc.ItemSize.Height, tc.Font.Height + 10));
            tc.BackColor = Base;
            tc.DrawItem += (s, e) =>
            {
                bool sel = e.Index == tc.SelectedIndex;
                using (SolidBrush bg = new SolidBrush(sel ? Fill : Base)) e.Graphics.FillRectangle(bg, e.Bounds);
                if (sel) using (SolidBrush ac = new SolidBrush(Accent)) e.Graphics.FillRectangle(ac, e.Bounds.X, e.Bounds.Bottom - 2, e.Bounds.Width, 2);
                TextRenderer.DrawText(e.Graphics, tc.TabPages[e.Index].Text, tc.Font, e.Bounds, sel ? Label : Label2,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
            // the strip to the right of the tabs is painted by Windows itself (white): paint it dark after every redraw
            if (tc.IsHandleCreated) new TabStripPainter(tc); else tc.HandleCreated += (s, e) => new TabStripPainter(tc);
        }

        class TabStripPainter : NativeWindow
        {
            readonly TabControl tc;
            public TabStripPainter(TabControl tc)
            {
                this.tc = tc;
                AssignHandle(tc.Handle);
                tc.HandleDestroyed += (s, e) => ReleaseHandle();
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != 0x000F && m.Msg != 0x0014) return;          // WM_PAINT, WM_ERASEBKGND
                try
                {
                    if (tc.TabCount == 0) return;
                    // Windows draws white tab outlines and a white frame round the page: paint the whole tab row and the
                    // frame ourselves (the page itself is a child window and is left alone)
                    Rectangle d = tc.DisplayRectangle;
                    Rectangle page = Rectangle.Inflate(d, 1, 1);
                    using (Graphics g = Graphics.FromHwnd(tc.Handle))
                    using (SolidBrush b = new SolidBrush(Base))
                    using (SolidBrush sel = new SolidBrush(Fill))
                    using (SolidBrush ac = new SolidBrush(Accent))
                    using (Pen line = new Pen(Separator))
                    {
                        g.ExcludeClip(d);
                        g.FillRectangle(b, 0, 0, tc.Width, tc.Height);
                        for (int i = 0; i < tc.TabCount; i++)
                        {
                            Rectangle r = tc.GetTabRect(i);
                            bool on = i == tc.SelectedIndex;
                            r = new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, page.Top - r.Y - 1);
                            if (on)
                            {
                                g.FillRectangle(sel, r);
                                g.FillRectangle(ac, r.X, r.Bottom - 2, r.Width, 2);
                            }
                            TextRenderer.DrawText(g, tc.TabPages[i].Text, tc.Font, r, on ? Label : Label2,
                                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        }
                        g.DrawRectangle(line, page.X, page.Y, page.Width - 1, page.Height - 1);
                    }
                }
                catch (Exception) { }
            }
        }
    }

    /// <summary>Menu, tool bar and status bar colours for dark mode.</summary>
    internal class DarkColors : ProfessionalColorTable
    {
        public DarkColors() { UseSystemColors = false; }
        Color Bar { get { return Theme.Elevated; } }
        public override Color ToolStripDropDownBackground { get { return Theme.Elevated; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Elevated; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Elevated; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Elevated; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.Accent; } }
        public override Color MenuItemSelected { get { return Theme.Accent; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.Accent; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.Accent; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.Accent; } }
        public override Color MenuItemPressedGradientMiddle { get { return Theme.Accent; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.Accent; } }
        public override Color MenuStripGradientBegin { get { return Bar; } }
        public override Color MenuStripGradientEnd { get { return Bar; } }
        public override Color SeparatorDark { get { return Theme.Separator; } }
        public override Color SeparatorLight { get { return Theme.Separator; } }
        public override Color ToolStripBorder { get { return Theme.Separator; } }
        public override Color ToolStripGradientBegin { get { return Bar; } }
        public override Color ToolStripGradientMiddle { get { return Bar; } }
        public override Color ToolStripGradientEnd { get { return Bar; } }
        public override Color ToolStripContentPanelGradientBegin { get { return Bar; } }
        public override Color ToolStripContentPanelGradientEnd { get { return Bar; } }
        public override Color ToolStripPanelGradientBegin { get { return Bar; } }
        public override Color ToolStripPanelGradientEnd { get { return Bar; } }
        public override Color StatusStripGradientBegin { get { return Bar; } }
        public override Color StatusStripGradientEnd { get { return Bar; } }
        public override Color ButtonSelectedBorder { get { return Theme.Border; } }
        public override Color ButtonSelectedHighlight { get { return Theme.Fill; } }
        public override Color ButtonSelectedGradientBegin { get { return Theme.Fill; } }
        public override Color ButtonSelectedGradientMiddle { get { return Theme.Fill; } }
        public override Color ButtonSelectedGradientEnd { get { return Theme.Fill; } }
        public override Color ButtonPressedBorder { get { return Theme.Accent; } }
        public override Color ButtonPressedHighlight { get { return Theme.AccentFill; } }
        public override Color ButtonPressedGradientBegin { get { return Theme.AccentFill; } }
        public override Color ButtonPressedGradientMiddle { get { return Theme.AccentFill; } }
        public override Color ButtonPressedGradientEnd { get { return Theme.AccentFill; } }
        public override Color ButtonCheckedHighlight { get { return Theme.AccentFill; } }
        public override Color ButtonCheckedGradientBegin { get { return Theme.AccentFill; } }
        public override Color ButtonCheckedGradientMiddle { get { return Theme.AccentFill; } }
        public override Color ButtonCheckedGradientEnd { get { return Theme.AccentFill; } }
        public override Color CheckBackground { get { return Theme.AccentFill; } }
        public override Color CheckSelectedBackground { get { return Theme.AccentFill; } }
        public override Color CheckPressedBackground { get { return Theme.AccentFill; } }
        public override Color GripDark { get { return Theme.Border; } }
        public override Color GripLight { get { return Bar; } }
        public override Color OverflowButtonGradientBegin { get { return Bar; } }
        public override Color OverflowButtonGradientMiddle { get { return Bar; } }
        public override Color OverflowButtonGradientEnd { get { return Bar; } }
        public override Color RaftingContainerGradientBegin { get { return Bar; } }
        public override Color RaftingContainerGradientEnd { get { return Bar; } }
    }

    /// <summary>Menus and bars in the dark look: flat layers, rounded highlights, blue for what is selected.</summary>
    internal class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.Elevated)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            ToolStrip ts = e.ToolStrip;
            Rectangle r = new Rectangle(Point.Empty, ts.Size);
            using (Pen p = new Pen(ts is ToolStripDropDown ? Theme.Border : Theme.Separator))
            {
                if (ts is ToolStripDropDown) e.Graphics.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
                else if (ts.Dock == DockStyle.Top) e.Graphics.DrawLine(p, 0, r.Height - 1, r.Width, r.Height - 1);
                else if (ts.Dock == DockStyle.Bottom) e.Graphics.DrawLine(p, 0, 0, r.Width, 0);
                else if (ts.Dock == DockStyle.Left) e.Graphics.DrawLine(p, r.Width - 1, 0, r.Width - 1, r.Height);
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (Pen p = new Pen(Theme.Separator))
            {
                Rectangle r = new Rectangle(Point.Empty, e.Item.Size);
                if (e.Vertical) e.Graphics.DrawLine(p, r.Width / 2, 3, r.Width / 2, r.Height - 4);
                else e.Graphics.DrawLine(p, 8, r.Height / 2, r.Width - 8, r.Height / 2);
            }
        }

        void ButtonBox(ToolStripItemRenderEventArgs e, bool on)
        {
            if (!on && !e.Item.Selected && !e.Item.Pressed) return;
            // switched on (a tool in use, Panel, Lock): a raised grey pill, as in the macOS tool bars; no blue box round
            // the icon (that made the icons look boxed in)
            Color fill = e.Item.Pressed ? Theme.FillPressed : on ? (e.Item.Selected ? Color.FromArgb(92, 92, 96) : Color.FromArgb(78, 78, 82)) : Theme.Fill;
            Rectangle r = new Rectangle(1, 1, e.Item.Width - 3, e.Item.Height - 3);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath gp = Rounded(r, 5))
            using (SolidBrush br = new SolidBrush(fill)) e.Graphics.FillPath(br, gp);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            ToolStripButton b = e.Item as ToolStripButton;
            ButtonBox(e, b != null && b.Checked);
        }

        protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e) { ButtonBox(e, false); }

        protected override void OnRenderSplitButtonBackground(ToolStripItemRenderEventArgs e)
        {
            ButtonBox(e, false);
            ToolStripSplitButton sb = e.Item as ToolStripSplitButton;
            if (sb == null) return;
            if (e.Item.Selected || e.Item.Pressed)
                using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, sb.DropDownButtonBounds.Left, 5, sb.DropDownButtonBounds.Left, e.Item.Height - 6);
            // the ▾ of Save / Copy: the base renderer draws it here, and this override replaces the base, so draw it ourselves
            DrawArrow(new ToolStripArrowRenderEventArgs(e.Graphics, sb, sb.DropDownButtonBounds, Theme.Label2, ArrowDirection.Down));
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            // the highlighted row of a menu: the accent blue, inset and rounded (like the macOS menus)
            if (!e.Item.Selected || !e.Item.Enabled) return;
            if (!(e.ToolStrip is ToolStripDropDown)) { ButtonBox(e, false); return; }
            Rectangle r = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath gp = Rounded(r, 4)) using (SolidBrush br = new SolidBrush(Theme.Accent)) e.Graphics.FillPath(br, gp);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Color c = e.Item.ForeColor;
            bool plain = c == SystemColors.ControlText || c == Color.Black || c == SystemColors.MenuText;
            if (!e.Item.Enabled) e.TextColor = Theme.Label3;
            else if (e.Item.Selected && e.ToolStrip is ToolStripDropDown) e.TextColor = Color.White;
            else if (plain) e.TextColor = Theme.Label;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Selected && e.Item.Owner is ToolStripDropDown ? Color.White : Theme.Label2;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // a ticked menu item: just the check mark, in the accent colour (white on the highlighted row)
            Rectangle r = e.ImageRectangle;
            bool hot = e.Item.Selected && e.ToolStrip is ToolStripDropDown;
            using (Pen p = new Pen(hot ? Color.White : Theme.Accent, 1.8f))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = Math.Min(r.Width, r.Height) / 8f;
                e.Graphics.DrawLines(p, new PointF[] { new PointF(cx - 2.5f * u, cy), new PointF(cx - 0.8f * u, cy + 2f * u), new PointF(cx + 2.8f * u, cy - 2.2f * u) });
            }
        }
    }
}
