using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SnapMeasure
{
    /// <summary>Who made it and where it lives.</summary>
    internal static class AppInfo
    {
        public const string Name = "SnapMeasure";
        public const string Author = "sARATH";
        public const string RepoUrl = "https://github.com/sARATH-3D/SnapMeasure";
        public const string ReleasesUrl = RepoUrl + "/releases";
        public const string IssuesUrl = RepoUrl + "/issues";

        public static string Version
        {
            get { Version v = typeof(AppInfo).Assembly.GetName().Version; return v.Major + "." + v.Minor + (v.Build > 0 ? "." + v.Build : ""); }
        }

        /// <summary>Opens one of the fixed project web pages in the default browser.</summary>
        public static void OpenUrl(string url)
        {
            if (url != RepoUrl && url != ReleasesUrl && url != IssuesUrl) return;   // only our own pages
            try { Process.Start(url); }
            catch (Exception ex) { MessageBox.Show("Couldn't open the browser: " + ex.Message, Name); }
        }
    }

    /// <summary>Output settings (like Greenshot's): where Ctrl+S saves, the file name pattern, format and quality.</summary>
    internal static class Output
    {
        // like Greenshot: date + time + the window title of the app you captured
        public const string DefaultPattern = "${capturetime:yyyy-MM-dd HH_mm_ss}-${title}";
        public static readonly string[] Formats = { "png", "jpg", "bmp", "gif", "tif", "pdf", "ico" };
        public static readonly string[] FormatNames =
        {
            "PNG – best quality, keeps transparency", "JPG – smaller files", "BMP – uncompressed", "GIF – 256 colours",
            "TIFF – lossless, for print / archives", "PDF – one page, for documents", "ICO – Windows icon (max 256 px)"
        };

        /// <summary>Save-dialog filter for all formats plus the editable project.</summary>
        public static string SaveFilter
        {
            get
            {
                return "PNG image|*.png|JPEG image|*.jpg|Bitmap|*.bmp|GIF image|*.gif|TIFF image|*.tif;*.tiff|PDF document|*.pdf|Icon|*.ico" +
                       "|SnapMeasure project – stays editable|*.snapm";
            }
        }

        public static string Pattern { get { string p = Settings.Get("FilePattern", DefaultPattern); return string.IsNullOrWhiteSpace(p) ? DefaultPattern : p; } }
        public static string Format { get { string f = Settings.Get("ImageFormat", "png").ToLowerInvariant(); return Array.IndexOf(Formats, f) >= 0 ? f : "png"; } }
        public static int JpegQuality { get { return Math.Max(10, Math.Min(100, Settings.GetInt("JpegQuality", 90))); } }
        public static bool SaveProject { get { return Settings.GetBool("SaveProjectToo", true); } }
        public static bool CopyPath { get { return Settings.GetBool("CopyPathOnSave", false); } }

        /// <summary>
        /// File name (without folder or extension) from a pattern. Placeholders:
        /// ${capturetime:FORMAT}  e.g. ${capturetime:yyyy-MM-dd HH_mm_ss}
        /// ${date} = yyyy-MM-dd   ${time} = HH-mm-ss   ${title} = window / capture title   ${n} = running number
        /// </summary>
        public static string MakeName(string pattern, DateTime when, string title, int n)
        {
            string s = Regex.Replace(pattern ?? "", @"\$\{([a-zA-Z]+)(?::([^}]*))?\}", m =>
            {
                string key = m.Groups[1].Value.ToLowerInvariant(), fmt = m.Groups[2].Success ? m.Groups[2].Value : null;
                try
                {
                    switch (key)
                    {
                        case "capturetime": return when.ToString(string.IsNullOrEmpty(fmt) ? "yyyy-MM-dd_HH-mm-ss" : fmt, CultureInfo.InvariantCulture);
                        case "date": return when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        case "time": return when.ToString("HH-mm-ss", CultureInfo.InvariantCulture);
                        case "title": return title ?? "";
                        case "n": return n.ToString(string.IsNullOrEmpty(fmt) ? "D3" : fmt, CultureInfo.InvariantCulture);
                        default: return m.Value;
                    }
                }
                catch (FormatException) { return ""; }
            });
            return Clean(s);
        }

        /// <summary>Makes text safe as a single file name: no folders, no reserved characters or names.</summary>
        public static string Clean(string s)
        {
            StringBuilder b = new StringBuilder();
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char c in s ?? "") b.Append(Array.IndexOf(bad, c) >= 0 || c == '/' || c == '\\' ? '_' : c);
            string r = b.ToString().Trim().Trim('.', ' ');
            while (r.Contains("..")) r = r.Replace("..", ".");
            if (r.Length > 150) r = r.Substring(0, 150).Trim();
            string up = r.ToUpperInvariant();
            if (Regex.IsMatch(up, @"^(CON|PRN|AUX|NUL|COM\d|LPT\d)$")) r = "_" + r;
            return r.Length == 0 ? "SnapMeasure" : r;
        }

        /// <summary>Folder + name, with " (2)", " (3)" … added so no existing picture or project is overwritten.</summary>
        public static string UniqueBase(string folder, string name, string ext, bool withProject)
        {
            string b = Path.Combine(folder, name);
            for (int i = 2; File.Exists(b + "." + ext) || (withProject && File.Exists(b + ".snapm")); i++)
                b = Path.Combine(folder, name + " (" + i + ")");
            return b;
        }

        // ---------------- extra export formats ----------------

        /// <summary>GIF with an adaptive 256-colour palette (much better than the built-in web palette).</summary>
        /// <summary>Saves a plain picture in the format its extension names (used when a capture is saved without the editor).</summary>
        public static void SaveBitmap(Bitmap b, string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".jpg":
                case ".jpeg":
                    {
                        System.Drawing.Imaging.ImageCodecInfo jpg = Array.Find(System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders(), c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                        using (System.Drawing.Imaging.EncoderParameters ep = new System.Drawing.Imaging.EncoderParameters(1))
                        {
                            ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)JpegQuality);
                            b.Save(path, jpg, ep);
                        }
                        break;
                    }
                case ".bmp": b.Save(path, System.Drawing.Imaging.ImageFormat.Bmp); break;
                case ".gif": SaveGif(b, path); break;
                case ".tif":
                case ".tiff": b.Save(path, System.Drawing.Imaging.ImageFormat.Tiff); break;
                case ".pdf": SavePdf(b, path, JpegQuality); break;
                case ".ico": SaveIco(b, path); break;
                default: b.Save(path, System.Drawing.Imaging.ImageFormat.Png); break;
            }
        }

        public static void SaveGif(Bitmap src, string path)
        {
            int w = src.Width, h = src.Height;
            int[] px = new int[w * h];
            System.Drawing.Imaging.BitmapData bd = src.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), px, y * w, w); }
            finally { src.UnlockBits(bd); }

            // popularity palette on 5-bit-per-channel buckets
            int[] cnt = new int[32768]; long[] sr = new long[32768], sg = new long[32768], sb = new long[32768];
            foreach (int c in px)
            {
                int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255, k = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);
                cnt[k]++; sr[k] += r; sg[k] += g; sb[k] += b;
            }
            int[] order = new int[32768];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b2) => cnt[b2].CompareTo(cnt[a]));
            int n = 0;
            Color[] pal = new Color[256];
            for (int i = 0; i < order.Length && n < 256; i++)
            {
                int k = order[i];
                if (cnt[k] == 0) break;
                pal[n++] = Color.FromArgb((int)(sr[k] / cnt[k]), (int)(sg[k] / cnt[k]), (int)(sb[k] / cnt[k]));
            }
            if (n == 0) pal[n++] = Color.White;

            byte[] idx = new byte[w * h];
            short[] map = new short[32768];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            for (int i = 0; i < px.Length; i++)
            {
                int c = px[i], k = ((c >> 19) & 31) << 10 | ((c >> 11) & 31) << 5 | ((c >> 3) & 31);
                if (map[k] < 0)
                {
                    int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255, best = 0, bd2 = int.MaxValue;
                    for (int j = 0; j < n; j++)
                    {
                        int dr = pal[j].R - r, dg = pal[j].G - g, db = pal[j].B - b, d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                        if (d < bd2) { bd2 = d; best = j; }
                    }
                    map[k] = (short)best;
                }
                idx[i] = (byte)map[k];
            }
            using (Bitmap o = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format8bppIndexed))
            {
                System.Drawing.Imaging.ColorPalette cp = o.Palette;
                for (int i = 0; i < cp.Entries.Length; i++) cp.Entries[i] = i < n ? pal[i] : Color.Black;
                o.Palette = cp;
                System.Drawing.Imaging.BitmapData od = o.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format8bppIndexed);
                try { for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(idx, y * w, IntPtr.Add(od.Scan0, y * od.Stride), w); }
                finally { o.UnlockBits(od); }
                o.Save(path, System.Drawing.Imaging.ImageFormat.Gif);
            }
        }

        /// <summary>Windows icon: the picture fitted into squares of 16 … 256 px (PNG-compressed entries).</summary>
        public static void SaveIco(Bitmap src, string path)
        {
            int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
            System.Collections.Generic.List<byte[]> pngs = new System.Collections.Generic.List<byte[]>();
            foreach (int s in sizes)
                using (Bitmap b = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        float z = Math.Min((float)s / src.Width, (float)s / src.Height);
                        float w = src.Width * z, h = src.Height * z;
                        g.DrawImage(src, new RectangleF((s - w) / 2f, (s - h) / 2f, w, h));
                    }
                    using (MemoryStream ms = new MemoryStream()) { b.Save(ms, System.Drawing.Imaging.ImageFormat.Png); pngs.Add(ms.ToArray()); }
                }
            using (BinaryWriter w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) w.Write(p);
            }
        }

        /// <summary>One-page PDF with the picture at 96 dpi (JPEG inside, quality from the settings).</summary>
        public static void SavePdf(Bitmap src, string path, int quality)
        {
            byte[] jpg;
            using (MemoryStream ms = new MemoryStream())
            {
                System.Drawing.Imaging.ImageCodecInfo enc = Array.Find(System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders(), c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                using (System.Drawing.Imaging.EncoderParameters ep = new System.Drawing.Imaging.EncoderParameters(1))
                {
                    ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Max(60, quality));
                    src.Save(ms, enc, ep);
                }
                jpg = ms.ToArray();
            }
            CultureInfo inv = CultureInfo.InvariantCulture;
            string pw = (src.Width * 0.75).ToString("0.##", inv), ph = (src.Height * 0.75).ToString("0.##", inv);
            string content = "q " + pw + " 0 0 " + ph + " 0 0 cm /Im0 Do Q";
            using (FileStream fs = File.Create(path))
            {
                Encoding a = Encoding.ASCII;
                long[] off = new long[6];
                Action<string> put = t => { byte[] b = a.GetBytes(t); fs.Write(b, 0, b.Length); };
                put("%PDF-1.4\n");
                fs.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);   // binary marker line
                off[1] = fs.Position; put("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                off[2] = fs.Position; put("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
                off[3] = fs.Position; put("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + pw + " " + ph + "] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
                off[4] = fs.Position; put("4 0 obj\n<< /Type /XObject /Subtype /Image /Width " + src.Width + " /Height " + src.Height +
                                          " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpg.Length + " >>\nstream\n");
                fs.Write(jpg, 0, jpg.Length); put("\nendstream\nendobj\n");
                off[5] = fs.Position; put("5 0 obj\n<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream\nendobj\n");
                long xref = fs.Position;
                put("xref\n0 6\n0000000000 65535 f \n");
                for (int i = 1; i <= 5; i++) put(off[i].ToString("D10", inv) + " 00000 n \n");
                put("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
            }
        }

        public static int NextNumber()
        {
            int n = Settings.GetInt("FileCounter", 0) + 1;
            Settings.Set("FileCounter", n.ToString(CultureInfo.InvariantCulture));
            return n;
        }
    }
}
