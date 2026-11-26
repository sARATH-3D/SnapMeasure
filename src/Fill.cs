using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SnapMeasure
{
    /// <summary>1.4.2: paint-bucket fill and background removal. Both work on a pixel grid of the picture:
    /// the fill grows from the clicked pixel and stops at annotation lines and at clear colour edges in the picture,
    /// closing small gaps; the result is traced into an outline (with holes) so it stays a vector shape.</summary>
    internal static class Regions
    {
        /// <summary>The picture's pixels as ARGB ints (row by row).</summary>
        public static int[] Pixels(Bitmap img)
        {
            int w = img.Width, h = img.Height;
            int[] px = new int[w * h];
            BitmapData d = img.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < h; y++) Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), px, y * w, w);
            }
            finally { img.UnlockBits(d); }
            return px;
        }

        static int Diff(int a, int b)
        {
            int d = Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255));
            d = Math.Max(d, Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)));
            d = Math.Max(d, Math.Abs((a & 255) - (b & 255)));
            d = Math.Max(d, Math.Abs(((a >> 24) & 255) - ((b >> 24) & 255)));
            return d;
        }

        /// <summary>Square dilation by r (a pixel is set when any pixel within r of it is set), in two passes.</summary>
        static bool[] Dilate(bool[] m, int w, int h, int r)
        {
            if (r <= 0) return (bool[])m.Clone();
            bool[] t = new bool[m.Length], o = new bool[m.Length];
            for (int y = 0; y < h; y++)
            {
                int row = y * w, last = -1000000;
                for (int x = 0; x < w; x++) { if (m[row + x]) last = x; t[row + x] = x - last <= r; }
                last = 1000000;
                for (int x = w - 1; x >= 0; x--) { if (m[row + x]) last = x; if (last - x <= r) t[row + x] = true; }
            }
            for (int x = 0; x < w; x++)
            {
                int last = -1000000;
                for (int y = 0; y < h; y++) { if (t[y * w + x]) last = y; o[y * w + x] = y - last <= r; }
                last = 1000000;
                for (int y = h - 1; y >= 0; y--) { if (t[y * w + x]) last = y; if (last - y <= r) o[y * w + x] = true; }
            }
            return o;
        }

        /// <summary>4-connected flood fill over the cells that are not blocked, from 'seed'.</summary>
        static bool[] Flood(bool[] blocked, int w, int h, int seed)
        {
            return Flood(blocked, w, h, new List<int> { seed });
        }

        static bool[] Flood(bool[] blocked, int w, int h, List<int> seeds)
        {
            bool[] reg = new bool[blocked.Length];
            int[] stack = new int[Math.Max(16, Math.Min(blocked.Length, Math.Max(1 << 20, seeds.Count + 16)))];
            int sp = 0;
            foreach (int seed in seeds)
            {
                if (blocked[seed] || reg[seed]) continue;
                reg[seed] = true;
                if (sp == stack.Length) Array.Resize(ref stack, stack.Length * 2);
                stack[sp++] = seed;
            }
            while (sp > 0)
            {
                int i = stack[--sp];
                int x = i % w, y = i / w;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = ny * w + nx;
                    if (reg[j] || blocked[j]) continue;
                    reg[j] = true;
                    if (sp == stack.Length) Array.Resize(ref stack, Math.Min(blocked.Length + 1, stack.Length * 2));
                    stack[sp++] = j;
                }
            }
            return reg;
        }

        /// <summary>
        /// The area to fill round 'seed': bounded by the given annotation outlines (each with its line width) and by
        /// colour edges in the picture (more than 'tol' away from the clicked colour). Gaps up to about 2 × gap pixels
        /// are closed. Returns the outer outline first, then the holes; null with 'why' when there is nothing to fill.
        /// </summary>
        public static List<List<PointF>> FillOutline(Bitmap img, List<KeyValuePair<List<PointF>, float>> lines, PointF seedW, int gap, int tol, out string why)
        {
            why = null;
            int iw = img.Width, ih = img.Height;
            // the grid: the picture plus every annotation outline round it, so areas drawn outside the picture fill too
            RectangleF all = new RectangleF(0, 0, iw, ih);
            float maxW = 1.5f;
            foreach (KeyValuePair<List<PointF>, float> l in lines)
            {
                maxW = Math.Max(maxW, l.Value);
                foreach (PointF p in l.Key)
                {
                    float x0 = Math.Min(all.Left, p.X), y0 = Math.Min(all.Top, p.Y), x1 = Math.Max(all.Right, p.X), y1 = Math.Max(all.Bottom, p.Y);
                    all = RectangleF.FromLTRB(x0, y0, x1, y1);
                }
            }
            float pad = Math.Min(64f, maxW) + 4f;
            all.Inflate(pad, pad);
            // keep it sane: at most about 12000 px a side round the picture
            all = RectangleF.Intersect(all, new RectangleF(-6000, -6000, iw + 12000, ih + 12000));
            int ox = (int)Math.Floor(all.X), oy = (int)Math.Floor(all.Y);
            int w = (int)Math.Ceiling(all.Right) - ox, h = (int)Math.Ceiling(all.Bottom) - oy;
            Point seed = new Point((int)Math.Floor(seedW.X) - ox, (int)Math.Floor(seedW.Y) - oy);
            if (seed.X < 0 || seed.Y < 0 || seed.X >= w || seed.Y >= h) { why = "Click inside a closed outline or on the picture"; return null; }
            bool onPicture = seedW.X >= 0 && seedW.Y >= 0 && seedW.X < iw && seedW.Y < ih;
            bool[] bar = new bool[w * h], lineMask = new bool[w * h];
            // annotation lines, drawn into a mask
            if (lines.Count > 0)
            {
                using (Bitmap mk = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(mk))
                    {
                        g.Clear(Color.Transparent);
                        g.SmoothingMode = SmoothingMode.None;
                        g.TranslateTransform(-ox, -oy);
                        foreach (KeyValuePair<List<PointF>, float> l in lines)
                        {
                            if (l.Key.Count < 2) continue;
                            using (Pen p = new Pen(Color.Black, Math.Max(1.5f, l.Value)))
                            {
                                p.LineJoin = LineJoin.Round; p.StartCap = p.EndCap = LineCap.Round;
                                g.DrawLines(p, l.Key.ToArray());
                            }
                        }
                    }
                    int[] mp = Pixels(mk);
                    for (int i = 0; i < mp.Length; i++) if (((mp[i] >> 24) & 255) > 40) bar[i] = lineMask[i] = true;
                }
            }
            int si = seed.Y * w + seed.X;
            if (bar[si]) { why = "Click inside an area, not on a line"; return null; }
            // colour edges in the picture (outside the picture counts as one plain "nothing" colour)
            int[] ipx = Pixels(img);
            int[] px = new int[w * h];
            for (int y = 0; y < ih; y++)
            {
                int gy = y - oy;
                if (gy < 0 || gy >= h) continue;
                for (int x = 0; x < iw; x++)
                {
                    int gx = x - ox;
                    if (gx >= 0 && gx < w) px[gy * w + gx] = ipx[y * iw + x];
                }
            }
            int sc = px[si];
            for (int i = 0; i < px.Length; i++) if (!bar[i] && Diff(px[i], sc) > tol) bar[i] = true;
            // close small gaps: grow the lines, fill, then grow the area back by the same amount (not into a line)
            bool[] grown = Dilate(bar, w, h, gap);
            int r = gap;
            if (grown[si]) { grown = bar; r = 0; }          // clicked right next to a line: no gap closing here
            bool[] reg = Flood(grown, w, h, si);
            if (r > 0)
            {
                bool[] back = Dilate(reg, w, h, r);
                for (int i = 0; i < reg.Length; i++) reg[i] = back[i] && !bar[i] || reg[i];
            }
            // off the picture, an area that runs to the edge of the grid is not closed
            if (!onPicture)
            {
                for (int x = 0; x < w; x++) if (reg[x] || reg[(h - 1) * w + x]) { why = "That area isn't closed – click inside a closed outline"; return null; }
                for (int y = 0; y < h; y++) if (reg[y * w] || reg[y * w + w - 1]) { why = "That area isn't closed – click inside a closed outline"; return null; }
            }
            IgnoreText(reg, lineMask, w, h);
            // like Paint: the fill reaches right up to the lines – into the soft (anti-aliased) edge of picture lines, and
            // under annotation lines (they are drawn on top of it), never past them
            bool[] one = Dilate(reg, w, h, 1);
            int ext = Math.Min(16, (int)Math.Ceiling(maxW / 2f) + 1);
            bool[] under = Dilate(reg, w, h, ext);
            for (int i = 0; i < reg.Length; i++)
                if (!reg[i] && ((one[i] && bar[i]) || (under[i] && lineMask[i]))) reg[i] = true;
            List<List<PointF>> loops = Trace(reg, w, h, out why);
            if (loops != null)
                foreach (List<PointF> lp in loops)
                    for (int i = 0; i < lp.Count; i++) lp[i] = new PointF(lp[i].X + ox, lp[i].Y + oy);
            return loops;
        }

        /// <summary>Text and small marks in the picture don't stop a fill: islands inside the area that hold no annotation
        /// line and are text-sized (at most about 40 px high or wide, or a few % of the area) are filled over.</summary>
        static void IgnoreText(bool[] reg, bool[] lineMask, int w, int h)
        {
            int area = 0;
            for (int i = 0; i < reg.Length; i++) if (reg[i]) area++;
            if (area == 0) return;
            bool[] seen = new bool[reg.Length];
            List<int> comp = new List<int>();
            Stack<int> st = new Stack<int>();
            for (int s = 0; s < reg.Length; s++)
            {
                if (reg[s] || seen[s]) continue;
                comp.Clear(); st.Push(s); seen[s] = true;
                bool border = false, hasLine = false;
                int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
                while (st.Count > 0)
                {
                    int i = st.Pop(); comp.Add(i);
                    int x = i % w, y = i / w;
                    if (x == 0 || y == 0 || x == w - 1 || y == h - 1) border = true;
                    if (lineMask[i]) hasLine = true;
                    if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
                    if (x > 0 && !reg[i - 1] && !seen[i - 1]) { seen[i - 1] = true; st.Push(i - 1); }
                    if (x < w - 1 && !reg[i + 1] && !seen[i + 1]) { seen[i + 1] = true; st.Push(i + 1); }
                    if (y > 0 && !reg[i - w] && !seen[i - w]) { seen[i - w] = true; st.Push(i - w); }
                    if (y < h - 1 && !reg[i + w] && !seen[i + w]) { seen[i + w] = true; st.Push(i + w); }
                }
                if (border || hasLine) continue;
                int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
                bool textSized = (Math.Min(bw, bh) <= 40 && comp.Count < area / 4) || comp.Count < area * 3 / 100;
                if (textSized) foreach (int i in comp) reg[i] = true;
            }
        }

        /// <summary>Outlines of the set cells (pixel corners), simplified: the largest first (the outside), then the holes.</summary>
        public static List<List<PointF>> Trace(bool[] reg, int w, int h, out string why)
        {
            why = null;
            long W1 = w + 1;
            Dictionary<long, long> first = new Dictionary<long, long>(), second = new Dictionary<long, long>();
            List<long> starts = new List<long>();
            Action<long, long> edge = (a, b) =>
            {
                if (first.ContainsKey(a)) second[a] = b; else { first[a] = b; starts.Add(a); }
            };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!reg[y * w + x]) continue;
                    long tl = y * W1 + x, tr = tl + 1, bl = tl + W1, br = bl + 1;
                    if (y == 0 || !reg[(y - 1) * w + x]) edge(tl, tr);
                    if (x == w - 1 || !reg[y * w + x + 1]) edge(tr, br);
                    if (y == h - 1 || !reg[(y + 1) * w + x]) edge(br, bl);
                    if (x == 0 || !reg[y * w + x - 1]) edge(bl, tl);
                    if (starts.Count > 4000000) { why = "That area is too complicated to fill"; return null; }
                }
            if (starts.Count == 0) { why = "Nothing to fill there"; return null; }
            List<List<PointF>> loops = new List<List<PointF>>();
            foreach (long st in starts)
            {
                while (first.ContainsKey(st))
                {
                    List<PointF> loop = new List<PointF>();
                    long cur = st;
                    int guard = 0;
                    while (true)
                    {
                        long nxt, n2;
                        if (!first.TryGetValue(cur, out nxt)) break;
                        if (second.TryGetValue(cur, out n2)) { first[cur] = n2; second.Remove(cur); }
                        else first.Remove(cur);
                        loop.Add(new PointF((float)(cur % W1), (float)(cur / W1)));
                        cur = nxt;
                        if (cur == st || ++guard > 50000000) break;
                    }
                    if (loop.Count >= 4) loops.Add(Simplify(loop, 0.8f));
                }
            }
            loops.RemoveAll(l => l.Count < 3 || Math.Abs(Geo.SignedArea(l)) < 2f);
            if (loops.Count == 0) { why = "Nothing to fill there"; return null; }
            loops.Sort((a, b) => Math.Abs(Geo.SignedArea(b)).CompareTo(Math.Abs(Geo.SignedArea(a))));
            return loops;
        }

        /// <summary>Closed outline: drop points on straight runs, then Douglas–Peucker with tolerance eps.</summary>
        static List<PointF> Simplify(List<PointF> loop, float eps)
        {
            List<PointF> p = new List<PointF>();
            int n = loop.Count;
            for (int i = 0; i < n; i++)
            {
                PointF a = loop[(i + n - 1) % n], b = loop[i], c = loop[(i + 1) % n];
                if (Math.Abs((b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X)) > 1e-6f) p.Add(b);
            }
            if (p.Count < 4) return p;
            // split at the point farthest from the first, then simplify both halves as open runs
            int far = 0; float fd = -1;
            for (int i = 1; i < p.Count; i++) { float d = Geo.Dist(p[0], p[i]); if (d > fd) { fd = d; far = i; } }
            List<PointF> a1 = p.GetRange(0, far + 1), a2 = p.GetRange(far, p.Count - far);
            a2.Add(p[0]);
            List<PointF> r = Dp(a1, eps);
            List<PointF> r2 = Dp(a2, eps);
            r.RemoveAt(r.Count - 1);
            r2.RemoveAt(r2.Count - 1);
            r.AddRange(r2);
            return r;
        }

        static List<PointF> Dp(List<PointF> pts, float eps)
        {
            int n = pts.Count;
            if (n < 3) return new List<PointF>(pts);
            bool[] keep = new bool[n];
            keep[0] = keep[n - 1] = true;
            Stack<int[]> st = new Stack<int[]>();
            st.Push(new[] { 0, n - 1 });
            while (st.Count > 0)
            {
                int[] s = st.Pop();
                int a = s[0], b = s[1];
                float best = -1; int bi = -1;
                for (int i = a + 1; i < b; i++)
                {
                    float d = Geo.SegDist(pts[i], pts[a], pts[b]);
                    if (d > best) { best = d; bi = i; }
                }
                if (bi >= 0 && best > eps) { keep[bi] = true; st.Push(new[] { a, bi }); st.Push(new[] { bi, b }); }
            }
            List<PointF> r = new List<PointF>();
            for (int i = 0; i < n; i++) if (keep[i]) r.Add(pts[i]);
            return r;
        }

        /// <summary>
        /// Remove background: the plain colour round the picture's edges (the commonest edge colour) and everything
        /// joined to it within 'tol' becomes transparent; pixels at the boundary that are close to it fade out. The
        /// subject in the middle stays. Returns a new picture (null when no plain background was found).
        /// </summary>
        public static Bitmap RemoveBackground(Bitmap img, int tol, out int removed)
        {
            removed = 0;
            int w = img.Width, h = img.Height;
            if (w < 3 || h < 3) return null;
            int[] px = Pixels(img);
            // the commonest colour along the edges (coarsely grouped), and how much of the edge it covers
            Dictionary<int, int> votes = new Dictionary<int, int>();
            List<int> edge = new List<int>();
            for (int x = 0; x < w; x++) { edge.Add(x); edge.Add((h - 1) * w + x); }
            for (int y = 1; y < h - 1; y++) { edge.Add(y * w); edge.Add(y * w + w - 1); }
            foreach (int i in edge)
            {
                int c = px[i];
                if (((c >> 24) & 255) < 16) continue;        // already transparent
                int k = ((c >> 20) & 15) << 8 | ((c >> 12) & 15) << 4 | ((c >> 4) & 15);
                int v; votes.TryGetValue(k, out v); votes[k] = v + 1;
            }
            int bestK = -1, bestV = 0;
            foreach (KeyValuePair<int, int> kv in votes) if (kv.Value > bestV) { bestV = kv.Value; bestK = kv.Key; }
            if (bestK < 0 || bestV < edge.Count / 4) return null;      // no plain background along the edges
            // the background colour: the average of the edge pixels in that group
            long sr = 0, sg = 0, sb = 0; int n = 0;
            foreach (int i in edge)
            {
                int c = px[i];
                int k = ((c >> 20) & 15) << 8 | ((c >> 12) & 15) << 4 | ((c >> 4) & 15);
                if (k != bestK || ((c >> 24) & 255) < 16) continue;
                sr += (c >> 16) & 255; sg += (c >> 8) & 255; sb += c & 255; n++;
            }
            int bg = unchecked((int)0xFF000000) | (int)(sr / n) << 16 | (int)(sg / n) << 8 | (int)(sb / n);
            bool[] blocked = new bool[px.Length];
            for (int i = 0; i < px.Length; i++) blocked[i] = Diff(px[i] | unchecked((int)0xFF000000), bg) > tol && ((px[i] >> 24) & 255) >= 16;
            // grow from every edge pixel of the background colour
            bool[] reg = Flood(blocked, w, h, edge);
            Bitmap nb = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            int[] o = (int[])px.Clone();
            for (int i = 0; i < o.Length; i++)
            {
                if (reg[i]) { o[i] = 0; removed++; continue; }
                // soft edge: a subject pixel touching the removed background and close to its colour fades partly
                int x = i % w, y = i / w;
                bool touch = (x > 0 && reg[i - 1]) || (x < w - 1 && reg[i + 1]) || (y > 0 && reg[i - w]) || (y < h - 1 && reg[i + w]);
                if (!touch) continue;
                int d = Diff(o[i] | unchecked((int)0xFF000000), bg);
                if (d < tol * 3)
                {
                    int a = (o[i] >> 24) & 255;
                    a = a * Math.Max(0, Math.Min(255, (d - tol) * 255 / Math.Max(1, tol * 2))) / 255;
                    o[i] = (o[i] & 0xFFFFFF) | (a << 24);
                }
            }
            if (removed == 0) { nb.Dispose(); return null; }
            BitmapData bd = nb.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < h; y++) Marshal.Copy(o, y * w, IntPtr.Add(bd.Scan0, y * bd.Stride), w); }
            finally { nb.UnlockBits(bd); }
            return nb;
        }
    }
}
