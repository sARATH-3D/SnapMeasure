using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace SnapMeasure
{
    /// <summary>Drawing-scale presets: 1:N at a known resolution or sheet size, plus scales the user saves.</summary>
    internal static class ScalePresets
    {
        public static readonly int[] Ratios = { 1, 2, 5, 10, 20, 25, 50, 75, 100, 125, 200, 250, 500, 1000, 1250, 2500 };
        public static readonly string[] Sheets = { "A0", "A1", "A2", "A3", "A4" };
        static readonly float[,] SheetMm = { { 841, 1189 }, { 594, 841 }, { 420, 594 }, { 297, 420 }, { 210, 297 } };

        /// <summary>Paper width in mm of a sheet (landscape = long side across).</summary>
        public static float SheetWidthMm(int sheet, bool landscape)
        {
            sheet = Math.Max(0, Math.Min(Sheets.Length - 1, sheet));
            return landscape ? SheetMm[sheet, 1] : SheetMm[sheet, 0];
        }

        static double MmPerUnit(string unit)
        {
            switch (unit)
            {
                case "mm": return 1;
                case "cm": return 10;
                case "m": return 1000;
                case "in": return 25.4;
                case "ft": return 304.8;
                default: return 1;
            }
        }

        /// <summary>Real units per image pixel for a drawing at 1:ratio, when one pixel is paperMmPerPx on paper.</summary>
        public static double Upp(double paperMmPerPx, double ratio, string unit)
        {
            return paperMmPerPx * ratio / MmPerUnit(unit);
        }

        public class Saved { public string Name, Unit; public double Upp; }

        public static List<Saved> Load()
        {
            List<Saved> list = new List<Saved>();
            foreach (string e in Settings.Get("ScalePresets", "").Split(';'))
            {
                string[] p = e.Split('|');
                double v;
                if (p.Length != 3 || p[0].Trim().Length == 0 || Array.IndexOf(ScaleInfo.Units, p[1]) < 0) continue;
                if (!double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v <= 0 || double.IsInfinity(v)) continue;
                Saved s = new Saved(); s.Name = p[0]; s.Unit = p[1]; s.Upp = v;
                list.Add(s);
            }
            return list;
        }

        public static void Store(List<Saved> list)
        {
            List<string> parts = new List<string>();
            foreach (Saved s in list)
                parts.Add(s.Name.Replace("|", "/").Replace(";", ",") + "|" + s.Unit + "|" + s.Upp.ToString("R", CultureInfo.InvariantCulture));
            Settings.Set("ScalePresets", string.Join(";", parts.ToArray()));
        }
    }

    /// <summary>A short camera-shutter click, made in memory (no sound file needed).</summary>
    internal static class Shutter
    {
        static byte[] wav;

        static byte[] Make()
        {
            const int rate = 22050;
            int n = rate * 16 / 100;                 // 0.16 s
            short[] s = new short[n];
            Random r = new Random(5);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate;
                // two quick clicks of filtered noise, like a shutter opening and closing
                double env = Math.Exp(-t * 90) + (t > 0.07 ? 0.8 * Math.Exp(-(t - 0.07) * 110) : 0);
                s[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, (r.NextDouble() * 2 - 1) * env * 14000));
            }
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(new char[] { 'R', 'I', 'F', 'F' }); w.Write(36 + n * 2); w.Write(new char[] { 'W', 'A', 'V', 'E' });
                w.Write(new char[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new char[] { 'd', 'a', 't', 'a' }); w.Write(n * 2);
                foreach (short v in s) w.Write(v);
                w.Flush();
                return ms.ToArray();
            }
        }

        static System.Media.SoundPlayer player;

        public static void Play()
        {
            try
            {
                // Play() is asynchronous, so the player must live on: keep one and reuse it
                if (player == null)
                {
                    if (wav == null) wav = Make();
                    System.Media.SoundPlayer p = new System.Media.SoundPlayer(new MemoryStream(wav));
                    p.Load();
                    player = p;
                }
                player.Play();
            }
            catch (Exception) { }
        }
    }

    /// <summary>"Check for updates": asks GitHub for the newest release. Only runs when the user clicks it.</summary>
    internal static class UpdateCheck
    {
        public const string ApiUrl = "https://api.github.com/repos/sARATH-3D/SnapMeasure/releases/latest";

        /// <summary>Version from a release tag like "v1.1" or "1.0.2"; null if it isn't one.</summary>
        public static Version ParseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            Match m = Regex.Match(tag.Trim(), @"^[vV]?(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), b = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int c = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            return new Version(a, b, c);
        }

        /// <summary>The tag_name field of GitHub's latest-release answer.</summary>
        public static string TagFromJson(string json)
        {
            Match m = Regex.Match(json ?? "", "\"tag_name\"\\s*:\\s*\"([^\"]{1,40})\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        public static bool IsNewer(Version latest, Version current)
        {
            if (latest == null) return false;
            Version c = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
            return latest > c;
        }

        /// <summary>Downloads the latest tag (blocking, ~1 s). Throws on network errors.</summary>
        public static string LatestTag()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;    // TLS 1.2
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(ApiUrl);
            rq.UserAgent = "SnapMeasure-update-check";
            rq.Accept = "application/vnd.github+json";
            rq.Timeout = 10000;
            rq.ReadWriteTimeout = 10000;
            rq.AllowAutoRedirect = true;
            using (HttpWebResponse rs = (HttpWebResponse)rq.GetResponse())
            using (StreamReader sr = new StreamReader(rs.GetResponseStream()))
            {
                char[] buf = new char[200000];
                int n = sr.ReadBlock(buf, 0, buf.Length);
                return TagFromJson(new string(buf, 0, n));
            }
        }

        /// <summary>Runs the check in the background and reports back on the UI thread.</summary>
        public static void Start(Control owner, Action<string> report)
        {
            Version cur = typeof(UpdateCheck).Assembly.GetName().Version;
            Thread t = new Thread(() =>
            {
                string msg;
                bool newer = false;
                try
                {
                    string tag = LatestTag();
                    Version v = ParseTag(tag);
                    if (v == null) msg = "Couldn't read the latest version from GitHub.";
                    else if (IsNewer(v, cur)) { msg = "SnapMeasure " + tag.TrimStart('v', 'V') + " is available."; newer = true; }
                    else msg = "You have the latest version (" + AppInfo.Version + ").";
                }
                catch (Exception ex) { msg = "Couldn't reach GitHub: " + ex.Message; }
                try
                {
                    if (!owner.IsDisposed)
                        owner.BeginInvoke((Action)(() =>
                        {
                            if (owner.IsDisposed) return;       // the window closed while GitHub was answering
                            report(msg);
                            if (newer && MessageBox.Show(owner, msg + "\n\nOpen the download page?", AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                                AppInfo.OpenUrl(AppInfo.ReleasesUrl);
                        }));
                }
                catch (InvalidOperationException) { }
            });
            t.IsBackground = true;
            t.Start();
        }
    }

    /// <summary>Scrolling capture: scrolls the window under a region and stitches the frames into one tall picture.</summary>
    internal static class ScrollCapture
    {
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        const uint MOUSEEVENTF_WHEEL = 0x0800;

        /// <summary>Makes the window under the point the active one: many apps ignore the wheel otherwise.</summary>
        static void Activate(Point at)
        {
            try
            {
                POINT p = new POINT { X = at.X, Y = at.Y };
                IntPtr h = WindowFromPoint(p);
                if (h == IntPtr.Zero) return;
                IntPtr root = GetAncestor(h, 2);    // GA_ROOT
                SetForegroundWindow(root != IntPtr.Zero ? root : h);
            }
            catch (Exception) { }
        }
        public const int MaxHeight = 30000;


        /// <summary>
        /// A brightness profile per pixel row (average of 48 strips across the row). Comparing profiles instead
        /// of exact pixels copes with apps that scroll by fractions of a pixel and redraw text slightly differently.
        /// </summary>
        public static float[][] RowHashes(Bitmap b)
        {
            int w = b.Width, h = b.Height;
            float[][] rows = new float[h][];
            int Bins = Math.Max(16, Math.Min(512, w / 4));     // one strip per ~4 px, so different digits still differ
            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] row = new int[w];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), row, 0, w);
                    float[] p = new float[Bins];
                    int[] n = new int[Bins];
                    for (int x = 0; x < w; x++)
                    {
                        int c = row[x], bin = x * Bins / w;
                        p[bin] += ((c >> 16) & 255) * 0.299f + ((c >> 8) & 255) * 0.587f + (c & 255) * 0.114f;
                        n[bin]++;
                    }
                    for (int i = 0; i < Bins; i++) if (n[i] > 0) p[i] /= n[i];
                    rows[y] = p;
                }
            }
            finally { b.UnlockBits(d); }
            return rows;
        }

        static float RowDiff(float[] a, float[] b)
        {
            float s = 0;
            for (int i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
            return s / a.Length;
        }

        /// <summary>
        /// How far the content moved up between two frames of the same size. header/footer = rows that stayed
        /// put (sticky bars). Returns 0 if nothing moved, -1 if the frames don't line up.
        /// </summary>
        public static string LastDiag = "";

        public static int FindShift(float[][] prev, float[][] cur, out int header, out int footer)
        {
            double be, st;
            int bd;
            int r = FindShift(prev, cur, out header, out footer, out bd, out be, out st);
            LastDiag = "best " + bd + " err " + be.ToString("F2") + " still " + st.ToString("F2");
            return r;
        }

        static int FindShift(float[][] prev, float[][] cur, out int header, out int footer, out int bestD, out double bestE, out double stillE)
        {
            bestD = -1; bestE = 0; stillE = 0;
            const float same = 0.6f;
            int h = Math.Min(prev.Length, cur.Length);
            header = 0;
            while (header < h && RowDiff(prev[header], cur[header]) < same) header++;
            footer = 0;
            while (footer < h - header && RowDiff(prev[h - 1 - footer], cur[h - 1 - footer]) < same) footer++;
            if (header + footer >= h - 2) return 0;
            // rows that carry content (not plain background) decide the match
            int best = -1;
            double bestErr = double.MaxValue;
            for (int d = 1; d < h - header - footer; d++)
            {
                int n = h - footer - d - header;
                if (n < 12) break;
                double err = 0;
                int cnt = 0;
                int step = n > 400 ? 2 : 1;
                for (int i = header; i < h - footer - d; i += step) { err += RowDiff(cur[i], prev[i + d]); cnt++; }
                err /= Math.Max(1, cnt);
                if (err < bestErr) { bestErr = err; best = d; }
            }
            // also check that the match is clearly better than no movement at all
            double still = 0;
            int c2 = 0;
            for (int i = header; i < h - footer; i += 2) { still += RowDiff(cur[i], prev[i]); c2++; }
            still /= Math.Max(1, c2);
            bestD = best; bestE = bestErr; stillE = still;
            if (still < 0.2) return 0;                                // nothing moved (end of the page)
            // a near-perfect match is always good; otherwise it must clearly beat "nothing moved"
            return bestErr < 0.8 || (bestErr < 3.0 && bestErr < still * 0.5) ? best : -1;
        }

        /// <summary>Stitches frames (all the same size). Used by the capture and by the self-test.</summary>
        public static Bitmap Stitch(List<Bitmap> frames)
        {
            if (frames.Count == 0) return null;
            int w = frames[0].Width, h = frames[0].Height;
            List<float[][]> hashes = frames.ConvertAll(RowHashes);
            // pieces: (frame index, first row, row count)
            List<int[]> pieces = new List<int[]>();
            int total = 0, lastEnd = -1, lastFrame = 0;     // output so far ends at row 'lastEnd' of frame 'lastFrame'
            for (int i = 1; i < frames.Count; i++)
            {
                int hd, ft;
                int d = FindShift(hashes[i - 1], hashes[i], out hd, out ft);
                if (d <= 0) break;
                if (lastEnd < 0)
                {
                    lastEnd = h - ft;                         // the first frame, without any fixed bottom bar
                    pieces.Add(new int[] { 0, 0, lastEnd });
                    total = lastEnd;
                }
                int from = lastEnd - d, to = h - ft;           // the same place in the new frame, then the new rows
                if (from < 0) from = 0;
                if (to <= from) { lastEnd = Math.Max(0, lastEnd - d); lastFrame = i; continue; }
                if (total + (to - from) > MaxHeight) break;
                pieces.Add(new int[] { i, from, to - from });
                total += to - from;
                lastEnd = to; lastFrame = i;
            }
            if (pieces.Count == 0) return Effects.To32(frames[0]);
            if (lastEnd < h) { pieces.Add(new int[] { lastFrame, lastEnd, h - lastEnd }); total += h - lastEnd; }    // fixed bottom bar, once
            Bitmap result = new Bitmap(w, total, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                int y = 0;
                foreach (int[] p in pieces)
                {
                    g.DrawImage(frames[p[0]], new Rectangle(0, y, w, p[2]), new Rectangle(0, p[1], w, p[2]), GraphicsUnit.Pixel);
                    y += p[2];
                }
            }
            return result;
        }

        /// <summary>Scrolls the window under 'r' with the mouse wheel and stitches. Esc stops early.</summary>
        public static Bitmap Capture(Rectangle r)
        {
            List<Bitmap> frames = new List<Bitmap>();
            Point oldCursor = Cursor.Position;
            try
            {
                SetCursorPos(r.X + r.Width / 2, r.Y + r.Height / 2);
                Activate(new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
                Thread.Sleep(350);                       // let the area picker fade away first
                frames.Add(ScreenCapture.Grab(r));
                float[][] prevHash = RowHashes(frames[0]);
                int height = r.Height, still = 0;
                for (int step = 0; step < 200 && height < MaxHeight; step++)
                {
                    if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) break;              // Esc
                    // newer apps only route the wheel to what's under a mouse that has moved, so nudge it first
                    SetCursorPos(r.X + r.Width / 2 + (step % 2 == 0 ? 2 : -2), r.Y + r.Height / 2 + 1);
                    Thread.Sleep(30);
                    SetCursorPos(r.X + r.Width / 2, r.Y + r.Height / 2);
                    Thread.Sleep(30);
                    mouse_event(MOUSEEVENTF_WHEEL, 0, 0, -120 * 3, UIntPtr.Zero);
                    Thread.Sleep(600);                   // smooth scrolling has to come to rest
                    Bitmap f = ScreenCapture.Grab(r);
                    float[][] hsh = RowHashes(f);
                    int hd, ft;
                    int d = FindShift(prevHash, hsh, out hd, out ft);
                    string dbg = Environment.GetEnvironmentVariable("SNAPMEASURE_SCROLLDEBUG");
                    if (!string.IsNullOrEmpty(dbg) && Directory.Exists(dbg))
                    {
                        f.Save(Path.Combine(dbg, "frame" + step.ToString("D2") + ".png"), ImageFormat.Png);
                        File.AppendAllText(Path.Combine(dbg, "log.txt"), step + ": shift " + d + " header " + hd + " footer " + ft + "  " + LastDiag + Environment.NewLine);
                    }
                    if (d == 0) { f.Dispose(); if (++still >= 2) break; continue; }    // bottom reached
                    if (d < 0) { f.Dispose(); break; }                                    // can't line up: stop here
                    still = 0;
                    frames.Add(f);
                    prevHash = hsh;
                    height += d;
                }
                return Stitch(frames);
            }
            finally
            {
                foreach (Bitmap b in frames) b.Dispose();
                SetCursorPos(oldCursor.X, oldCursor.Y);
            }
        }
    }
}
