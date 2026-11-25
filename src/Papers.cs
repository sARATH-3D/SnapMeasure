using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SnapMeasure
{
    /// <summary>Backgrounds and paper types for a new blank canvas (File → Blank canvas…).</summary>
    internal static class Papers
    {
        /// <summary>The list in the New blank canvas box: plain backgrounds first, then ruled and gridded paper.
        /// The name is what Settings remembers ("BlankPaper"), so the list can grow without breaking the saved choice.</summary>
        public static readonly string[] Names =
        {
            "White", "Cream", "Light gray", "Black", "Transparent",
            "Grid paper", "Graph paper (green)", "Dot grid", "Isometric grid", "Lined paper", "Blueprint", "Dark grid"
        };

        /// <summary>Remembered choice; the old number setting (0 White, 1 Grid paper, 2 Transparent, 3 Black) still works.</summary>
        public static string Remembered()
        {
            string n = Settings.Get("BlankPaper", null);
            if (n != null && Array.IndexOf(Names, n) >= 0) return n;
            switch (Settings.GetInt("BlankBack", 0)) { case 1: return "Grid paper"; case 2: return "Transparent"; case 3: return "Black"; default: return "White"; }
        }

        public static Color BackColor(string name)
        {
            switch (name)
            {
                case "Cream": return Color.FromArgb(250, 246, 233);
                case "Light gray": return Color.FromArgb(238, 238, 238);
                case "Black": return Color.Black;
                case "Transparent": return Color.Transparent;
                case "Graph paper (green)": return Color.FromArgb(252, 254, 252);
                case "Lined paper": return Color.FromArgb(255, 254, 247);
                case "Blueprint": return Color.FromArgb(22, 62, 120);
                case "Dark grid": return Color.FromArgb(30, 32, 36);
                default: return Color.White;
            }
        }

        /// <summary>Draws the rules, grid or dots of a paper type onto a canvas that already has its background colour.</summary>
        public static void Decorate(Bitmap b, string name)
        {
            int w = b.Width, h = b.Height;
            using (Graphics g = Graphics.FromImage(b))
            {
                switch (name)
                {
                    case "Grid paper": Lines(g, w, h, 10, 50, Color.FromArgb(232, 236, 242), Color.FromArgb(205, 214, 226)); break;
                    case "Graph paper (green)": Lines(g, w, h, 10, 50, Color.FromArgb(214, 234, 218), Color.FromArgb(160, 205, 170)); break;
                    case "Blueprint": Lines(g, w, h, 10, 50, Color.FromArgb(52, 96, 158), Color.FromArgb(108, 152, 208)); break;
                    case "Dark grid": Lines(g, w, h, 10, 50, Color.FromArgb(46, 50, 56), Color.FromArgb(68, 74, 84)); break;
                    case "Dot grid":
                        using (SolidBrush dot = new SolidBrush(Color.FromArgb(150, 156, 168)))
                            for (int y = 20; y < h; y += 20)
                                for (int x = 20; x < w; x += 20) g.FillRectangle(dot, x - 1, y - 1, 2, 2);
                        break;
                    case "Lined paper":
                        using (Pen rule = new Pen(Color.FromArgb(176, 196, 224)))
                        using (Pen margin = new Pen(Color.FromArgb(232, 130, 130)))
                        {
                            for (int y = 64; y < h; y += 32) g.DrawLine(rule, 0, y, w, y);
                            g.DrawLine(margin, 84, 0, 84, h);
                        }
                        break;
                    case "Isometric grid": Isometric(g, w, h); break;
                }
            }
        }

        static void Lines(Graphics g, int w, int h, int minor, int major, Color cMinor, Color cMajor)
        {
            using (Pen pMinor = new Pen(cMinor))
            using (Pen pMajor = new Pen(cMajor))
            {
                for (int x = 0; x <= w; x += minor) g.DrawLine(x % major == 0 ? pMajor : pMinor, x, 0, x, h);
                for (int y = 0; y <= h; y += minor) g.DrawLine(y % major == 0 ? pMajor : pMinor, 0, y, w, y);
            }
        }

        /// <summary>Triangular grid for isometric drawings: vertical lines and two families at ±30°, 24 px triangles.</summary>
        static void Isometric(Graphics g, int w, int h)
        {
            const float row = 24f, tan30 = 0.57735f;
            float dx = row * 0.866025f;                 // distance between the vertical lines
            using (Pen p = new Pen(Color.FromArgb(206, 216, 230)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                for (float x = 0; x <= w; x += dx) g.DrawLine(p, x, 0, x, h);
                float reach = w * tan30;
                // both families start on multiples of the row height, so every line meets the others at the vertical lines
                for (float c = -(float)Math.Ceiling(reach / row) * row; c <= h; c += row) g.DrawLine(p, 0, c, w, c + reach);     // sloping down to the right
                for (float c = 0; c <= h + reach; c += row) g.DrawLine(p, 0, c, w, c - reach);                                    // sloping up to the right
            }
        }
    }
}
