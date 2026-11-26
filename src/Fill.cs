using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SnapMeasure
{
    /// <summary>1.4.2: the paint-bucket fill. It is bounded by annotation outlines only; it
    /// closes small gaps and the result is an outline (with holes), so it stays a vector shape.</summary>
    internal static class Regions
    {
        const string NotClosed = "Click inside a closed outline";
        const string OnLine = "Click inside an area, not on a line";
        const string TooComplex = "That area is too complicated to fill";

        static bool IsClosed(List<PointF> p) { return p.Count >= 4 && Geo.Dist(p[0], p[p.Count - 1]) < 0.5f; }

        static bool Inside(List<PointF> p, PointF q)
        {
            bool inside = false;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                if ((p[i].Y > q.Y) != (p[j].Y > q.Y) && q.X < (p[j].X - p[i].X) * (q.Y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X) inside = !inside;
            return inside;
        }

        static double Area(List<PointF> p)
        {
            double a = 0;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += (double)p[j].X * p[i].Y - (double)p[i].X * p[j].Y;
            return a / 2;
        }

        /// <summary>One straight piece of an outline: which line it came from, its place in it, where it is cut.</summary>
        class Seg
        {
            public PointF A, B;
            public float Width;
            public int Line, At;
            public List<double> Cuts = new List<double>();
            public float Left { get { return Math.Min(A.X, B.X); } }
            public float Right { get { return Math.Max(A.X, B.X); } }
            public float Top { get { return Math.Min(A.Y, B.Y); } }
            public float Bottom { get { return Math.Max(A.Y, B.Y); } }
        }

        /// <summary>
        /// The area to fill round 'seed', bounded by the annotation outlines only (each with its line width); the
        /// picture does not count. It is worked out from the lines themselves: they are cut where they meet, ends that
        /// fall short by a few pixels are joined, lines that divide nothing (loose ends) are left out, and the smallest
        /// area round the click is taken. Returns the outer outline first, then the holes; null with 'why' when there
        /// is nothing to fill.
        /// </summary>
        public static List<List<PointF>> FillOutline(List<KeyValuePair<List<PointF>, float>> lines, PointF seed, int gap, out string why)
        {
            why = null;
            lines = new List<KeyValuePair<List<PointF>, float>>(lines);
            List<Seg> segs = new List<Seg>();
            for (int l = 0; l < lines.Count; l++)
            {
                // curves and sketches carry far more points than their shape needs: thinned, a sheet full of
                // revision clouds stays quick
                List<PointF> p = Thin(lines[l].Key, 0.25f);
                lines[l] = new KeyValuePair<List<PointF>, float>(p, lines[l].Value);
                for (int i = 1; i < p.Count; i++)
                    if (Geo.Dist(p[i - 1], p[i]) > 0.01f) segs.Add(new Seg { A = p[i - 1], B = p[i], Width = lines[l].Value, Line = l, At = i });
            }
            if (segs.Count == 0) { why = NotClosed; return null; }
            if (segs.Count > 150000) { why = TooComplex; return null; }
            foreach (Seg s in segs)
                if (Geo.SegDist(seed, s.A, s.B) <= Math.Max(1.5f, s.Width) / 2f) { why = OnLine; return null; }
            segs.AddRange(Bridges(lines, segs, gap));
            if (!Cut(segs)) { why = TooComplex; return null; }

            // the pieces between the cuts, as a net of points and edges
            List<PointF> node = new List<PointF>();
            List<List<int>> next = new List<List<int>>();
            Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
            Func<PointF, int> id = p =>
            {
                long gx = (long)Math.Floor(p.X * 5), gy = (long)Math.Floor(p.Y * 5);     // cells of 0.2 pixel
                List<int> at;
                for (long x = gx - 1; x <= gx + 1; x++)
                    for (long y = gy - 1; y <= gy + 1; y++)
                        if (grid.TryGetValue(x * 4000037L + y, out at))
                            foreach (int n in at) if (Geo.Dist(node[n], p) < 0.15f) return n;
                if (!grid.TryGetValue(gx * 4000037L + gy, out at)) grid[gx * 4000037L + gy] = at = new List<int>();
                at.Add(node.Count);
                node.Add(p);
                next.Add(new List<int>());
                return node.Count - 1;
            };
            foreach (Seg s in segs)
            {
                s.Cuts.Add(0); s.Cuts.Add(1);
                s.Cuts.Sort();
                int last = -1;
                foreach (double t in s.Cuts)
                {
                    int n = id(new PointF((float)(s.A.X + (s.B.X - s.A.X) * t), (float)(s.A.Y + (s.B.Y - s.A.Y) * t)));
                    if (last >= 0 && last != n && !next[last].Contains(n)) { next[last].Add(n); next[n].Add(last); }
                    last = n;
                }
            }
            DropBridges(next);

            // which points hang together
            int[] part = new int[node.Count];
            for (int i = 0; i < part.Length; i++) part[i] = -1;
            int parts = 0;
            for (int i = 0; i < node.Count; i++)
            {
                if (part[i] >= 0 || next[i].Count == 0) continue;
                Stack<int> st = new Stack<int>();
                st.Push(i); part[i] = parts;
                while (st.Count > 0) foreach (int n in next[st.Pop()]) if (part[n] < 0) { part[n] = parts; st.Push(n); }
                parts++;
            }
            if (parts == 0) { why = NotClosed; return null; }

            // walk round every face: neighbours in order of direction, always the one just before the way we came
            for (int i = 0; i < node.Count; i++)
            {
                PointF c = node[i];
                next[i].Sort((a, b) => Math.Atan2(node[a].Y - c.Y, node[a].X - c.X).CompareTo(Math.Atan2(node[b].Y - c.Y, node[b].X - c.X)));
            }
            HashSet<long> done = new HashSet<long>();
            List<PointF> best = null;
            double bestArea = double.MaxValue;
            int bestPart = -1;
            List<PointF>[] outline = new List<PointF>[parts];      // each group's outer boundary (walked the other way round)
            double[] outlineArea = new double[parts];
            for (int i = 0; i < node.Count; i++)
                foreach (int first in next[i])
                {
                    if (done.Contains((long)i * node.Count + first)) continue;
                    List<PointF> face = new List<PointF>();
                    int u = i, v = first;
                    while (done.Add((long)u * node.Count + v))
                    {
                        face.Add(node[u]);
                        List<int> at = next[v];
                        int w = at[(at.IndexOf(u) + at.Count - 1) % at.Count];
                        u = v; v = w;
                    }
                    double a = Area(face);
                    if (a > 0)
                    {
                        if (a < bestArea && Inside(face, seed)) { best = face; bestArea = a; bestPart = part[i]; }
                    }
                    else if (outline[part[i]] == null || -a > outlineArea[part[i]]) { outline[part[i]] = face; outlineArea[part[i]] = -a; }
                }
            if (best == null) { why = NotClosed; return null; }

            // outlines lying inside the area are holes (only the outermost of nested ones)
            List<int> inside = new List<int>();
            for (int k = 0; k < parts; k++)
                if (k != bestPart && outline[k] != null && outlineArea[k] > 2 && outlineArea[k] < bestArea && Inside(best, outline[k][0])) inside.Add(k);
            List<List<PointF>> loops = new List<List<PointF>> { Clean(best) };
            foreach (int k in inside)
            {
                bool nested = false;
                foreach (int m in inside) if (m != k && outlineArea[m] > outlineArea[k] && Inside(outline[m], outline[k][0])) nested = true;
                if (!nested) loops.Add(Clean(outline[k]));
            }
            loops.RemoveAll(l => l.Count < 3 || Math.Abs(Area(l)) < 2);
            if (loops.Count == 0 || bestArea < 4) { why = "Nothing to fill there"; return null; }
            return loops;
        }

        /// <summary>The same line with the points that move it by less than 'eps' left out (Douglas–Peucker); both ends stay.</summary>
        static List<PointF> Thin(List<PointF> p, float eps)
        {
            int n = p.Count;
            if (n < 3) return p;
            bool[] keep = new bool[n];
            keep[0] = keep[n - 1] = true;
            Stack<int[]> st = new Stack<int[]>();
            st.Push(new[] { 0, n - 1 });
            while (st.Count > 0)
            {
                int[] s = st.Pop();
                float far = -1; int at = -1;
                for (int i = s[0] + 1; i < s[1]; i++)
                {
                    float d = Geo.SegDist(p[i], p[s[0]], p[s[1]]);
                    if (d > far) { far = d; at = i; }
                }
                if (at >= 0 && far > eps) { keep[at] = true; st.Push(new[] { s[0], at }); st.Push(new[] { at, s[1] }); }
            }
            List<PointF> r = new List<PointF>();
            for (int i = 0; i < n; i++) if (keep[i]) r.Add(p[i]);
            return r;
        }

        /// <summary>Ends that stop a few pixels short of another line (twice 'gap', plus the line widths) are joined
        /// to it. An end whose last bit already meets a line is left alone.</summary>
        static List<Seg> Bridges(List<KeyValuePair<List<PointF>, float>> lines, List<Seg> segs, int gap)
        {
            List<Seg> add = new List<Seg>();
            float widest = 0;
            foreach (Seg s in segs) widest = Math.Max(widest, s.Width);
            for (int l = 0; l < lines.Count; l++)
            {
                List<PointF> p = lines[l].Key;
                if (p.Count < 2 || IsClosed(p)) continue;
                for (int end = 0; end < 2; end++)
                {
                    PointF tip = end == 0 ? p[0] : p[p.Count - 1];
                    float width = lines[l].Value, reach = gap * 2 + (width + widest) / 2f + 0.5f;
                    // the pieces of this line next to the tip (as far as 'reach' along it): not join targets
                    HashSet<int> own = new HashSet<int>();
                    float run = 0;
                    for (int k = 0; k + 1 < p.Count && run <= reach * 2; k++)
                    {
                        int at = end == 0 ? k + 1 : p.Count - 1 - k;
                        own.Add(at);
                        run += Geo.Dist(p[at - 1], p[at]);
                    }
                    bool met = false;
                    run = 0;
                    for (int k = 0; k + 1 < p.Count && run < reach && !met; k++)
                    {
                        int at = end == 0 ? k + 1 : p.Count - 1 - k;
                        PointF a = end == 0 ? p[at - 1] : p[at], b = end == 0 ? p[at] : p[at - 1];      // from the tip's side
                        float len = Geo.Dist(a, b);
                        if (len <= 0) continue;
                        float take = Math.Min(len, reach - run) / len;      // only the last bit counts, however long the piece
                        PointF stop = new PointF(a.X + (b.X - a.X) * take, a.Y + (b.Y - a.Y) * take);
                        foreach (Seg o in segs)
                        {
                            if (o.Line == l && (own.Contains(o.At) || Math.Abs(o.At - at) <= 1)) continue;
                            double t, u;
                            if (Meet(a, stop, o.A, o.B, out t, out u) || Geo.SegDist(tip, o.A, o.B) < 0.15f) { met = true; break; }
                        }
                        run += len;
                    }
                    if (met) continue;
                    float near = float.MaxValue;
                    PointF to = tip;
                    foreach (Seg o in segs)
                    {
                        if (o.Line == l && own.Contains(o.At)) continue;
                        float d = Geo.SegDist(tip, o.A, o.B);
                        if (d < near && d <= gap * 2 + (width + o.Width) / 2f + 0.5f)
                        {
                            near = d; to = Geo.Project(tip, o.A, o.B);
                            // a corner within reach: join to the corner itself
                            if (Geo.Dist(tip, o.A) <= d + 1.5f) to = o.A;
                            else if (Geo.Dist(tip, o.B) <= d + 1.5f) to = o.B;
                        }
                    }
                    if (near < float.MaxValue && Geo.Dist(tip, to) > 0.01f)
                        add.Add(new Seg { A = tip, B = to, Width = width, Line = -1, At = 0 });
                }
            }
            return add;
        }

        /// <summary>Whether a-b and c-d cross or touch end-on, and how far along each (0 to 1).</summary>
        static bool Meet(PointF a, PointF b, PointF c, PointF d, out double t, out double u)
        {
            double rx = (double)b.X - a.X, ry = (double)b.Y - a.Y, sx = (double)d.X - c.X, sy = (double)d.Y - c.Y;
            double cross = rx * sy - ry * sx, lr = Math.Sqrt(rx * rx + ry * ry), ls = Math.Sqrt(sx * sx + sy * sy);
            t = u = 0;
            if (lr == 0 || ls == 0 || Math.Abs(cross) < 1e-7 * lr * ls) return false;      // parallel: the ends decide
            t = ((c.X - (double)a.X) * sy - (c.Y - (double)a.Y) * sx) / cross;
            u = ((c.X - (double)a.X) * ry - (c.Y - (double)a.Y) * rx) / cross;
            double et = 0.1 / lr, eu = 0.1 / ls;
            if (t < -et || t > 1 + et || u < -eu || u > 1 + eu) return false;
            t = Math.Max(0, Math.Min(1, t)); u = Math.Max(0, Math.Min(1, u));
            return true;
        }

        /// <summary>Marks on every piece where others cross it or end on it. False: far too many crossings.</summary>
        static bool Cut(List<Seg> segs)
        {
            List<Seg> by = new List<Seg>(segs);
            by.Sort((a, b) => a.Left.CompareTo(b.Left));
            int cuts = 0;
            for (int i = 0; i < by.Count; i++)
            {
                Seg a = by[i];
                float right = a.Right + 0.2f;
                for (int j = i + 1; j < by.Count && by[j].Left <= right; j++)
                {
                    Seg b = by[j];
                    if (b.Top > a.Bottom + 0.2f || b.Bottom < a.Top - 0.2f) continue;
                    double t, u;
                    if (Meet(a.A, a.B, b.A, b.B, out t, out u)) { a.Cuts.Add(t); b.Cuts.Add(u); cuts += 2; }
                    cuts += OnPiece(a, b.A) + OnPiece(a, b.B) + OnPiece(b, a.A) + OnPiece(b, a.B);
                    if (cuts > 400000) return false;
                }
            }
            return true;
        }

        /// <summary>An end of another piece lying on this one (also where two lines run along each other).</summary>
        static int OnPiece(Seg s, PointF p)
        {
            if (Geo.SegDist(p, s.A, s.B) >= 0.15f || Geo.Dist(p, s.A) < 0.15f || Geo.Dist(p, s.B) < 0.15f) return 0;
            s.Cuts.Add(Geo.Dist(s.A, Geo.Project(p, s.A, s.B)) / Geo.Dist(s.A, s.B));
            return 1;
        }

        /// <summary>Removes the edges that are the only way between two points: loose ends, and links from one
        /// outline to another. They divide no area.</summary>
        static void DropBridges(List<List<int>> next)
        {
            int n = next.Count, clock = 0;
            int[] seen = new int[n], low = new int[n], from = new int[n], at = new int[n];
            List<KeyValuePair<int, int>> drop = new List<KeyValuePair<int, int>>();
            Stack<int> st = new Stack<int>();
            for (int root = 0; root < n; root++)
            {
                if (seen[root] != 0) continue;
                seen[root] = low[root] = ++clock; from[root] = -1;
                st.Push(root);
                while (st.Count > 0)
                {
                    int u = st.Peek();
                    if (at[u] < next[u].Count)
                    {
                        int v = next[u][at[u]++];
                        if (seen[v] == 0) { seen[v] = low[v] = ++clock; from[v] = u; st.Push(v); }
                        else if (v != from[u]) low[u] = Math.Min(low[u], seen[v]);
                    }
                    else
                    {
                        st.Pop();
                        int p = from[u];
                        if (p < 0) continue;
                        low[p] = Math.Min(low[p], low[u]);
                        if (low[u] > seen[p]) drop.Add(new KeyValuePair<int, int>(p, u));
                    }
                }
            }
            foreach (KeyValuePair<int, int> e in drop) { next[e.Key].Remove(e.Value); next[e.Value].Remove(e.Key); }
        }

        /// <summary>The corners only: points in the middle of a straight run go.</summary>
        static List<PointF> Clean(List<PointF> loop)
        {
            List<PointF> o = new List<PointF>(loop);
            for (bool cut = true; cut && o.Count > 3; )
            {
                cut = false;
                for (int i = 0; i < o.Count && o.Count > 3; i++)
                {
                    PointF a = o[(i + o.Count - 1) % o.Count], b = o[(i + 1) % o.Count];
                    if (Geo.SegDist(o[i], a, b) < 0.05f) { o.RemoveAt(i); cut = true; }
                }
            }
            return o;
        }
    }
}
