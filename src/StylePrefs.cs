using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace SnapMeasure
{
    /// <summary>
    /// Settings → Styles (1.4): "Remember my styles". The style of every tool (colours, widths, line ends, text, font,
    /// marker look) and the marker numbering (count down, start) are kept in a small file next to the settings file,
    /// and new editors start with them. Saved when an editor closes, or at once with "Save current styles".
    /// </summary>
    internal static class StylePrefs
    {
        /// <summary>Settings → Styles → "Remember my styles" (off unless switched on).</summary>
        public static bool On { get { return TestPath != null || Settings.GetBool("RememberStyles", false); } }

        /// <summary>Self-test only: use this file instead (and act as if switched on).</summary>
        internal static string TestPath;

        static string FilePath
        {
            get
            {
                if (TestPath != null) return TestPath;
                string dir = string.IsNullOrEmpty(Settings.FilePath) ? null : Path.GetDirectoryName(Settings.FilePath);
                if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapMeasure");
                return Path.Combine(dir, "SnapMeasure-styles.txt");
            }
        }

        public static bool HasSaved { get { try { return File.Exists(FilePath); } catch (Exception) { return false; } } }

        /// <summary>Writes the canvas's tool styles and numbering. Returns false (quietly) if the file can't be written.</summary>
        public static bool Save(Canvas cv)
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("# SnapMeasure styles – written by Settings → Styles. Delete this file to start from the defaults.");
                ScaleInfo sc = cv.Scale;
                lines.Add("numbering=" + (sc.LetterReverse ? "1" : "0") + "," + (sc.MarkerReverse ? "1" : "0") + "," +
                          sc.LetterStart.ToString(CultureInfo.InvariantCulture) + "," + sc.MarkerStart.ToString(CultureInfo.InvariantCulture));
                foreach (KeyValuePair<Tool, Shape> kv in cv.Styles)
                {
                    Shape s = kv.Value.Clone();
                    s.Pts.Clear(); s.Pts.Add(new PointF(0, 0)); s.Pts.Add(new PointF(10, 10));     // a shape needs points to be valid
                    s.Text = ""; s.Holes = null; s.Img = null;
                    lines.Add(kv.Key + "=" + ProjectFile.ShapesToXml(new Shape[] { s }));
                }
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception ex) { ErrorLog.Write("save styles", ex); return false; }
        }

        /// <summary>Puts the saved styles into a new canvas (only when "Remember my styles" is on). A damaged or missing
        /// file, or a tool this version doesn't know, simply keeps the default.</summary>
        public static void Apply(Canvas cv)
        {
            if (!On) return;
            try
            {
                string path = FilePath;
                if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return;
                foreach (string line in File.ReadAllLines(path))
                {
                    if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    string key = line.Substring(0, eq), val = line.Substring(eq + 1);
                    if (key == "numbering")
                    {
                        string[] p = val.Split(',');
                        int ls, ms;
                        if (p.Length == 4 && int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ls) &&
                            int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out ms))
                        {
                            cv.Scale.LetterReverse = p[0] == "1"; cv.Scale.MarkerReverse = p[1] == "1";
                            cv.Scale.LetterStart = Math.Max(1, Math.Min(26, ls)); cv.Scale.MarkerStart = Math.Max(1, Math.Min(99999, ms));
                        }
                        continue;
                    }
                    Tool t;
                    if (!Enum.TryParse(key, false, out t) || !cv.Styles.ContainsKey(t)) continue;
                    List<Shape> got;
                    try { got = ProjectFile.ShapesFromXml(val); } catch (Exception) { continue; }
                    if (got.Count != 1 || got[0].Kind != cv.Styles[t].Kind) continue;
                    Shape s = got[0];
                    s.Pts.Clear();
                    cv.Styles[t] = s;
                }
            }
            catch (Exception ex) { ErrorLog.Write("load styles", ex); }
        }

        /// <summary>Back to SnapMeasure's own styles for new editors (the saved file goes).</summary>
        public static void Reset()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (Exception ex) { ErrorLog.Write("reset styles", ex); }
        }
    }
}
