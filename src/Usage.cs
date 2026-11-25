using System;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace SnapMeasure
{
    /// <summary>How many captures were made on this PC, and the once-per-100 thank-you line.
    /// The numbers stay on this PC (%LOCALAPPDATA%\SnapMeasure\stats.txt) and are never sent anywhere.
    /// They are not kept in SnapMeasure.ini, so the user's own settings file is never touched.</summary>
    internal static class Usage
    {
        const int Every = 100;
        static int captures, noteMilestone;
        static bool noteOff, loaded;

        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapMeasure", "stats.txt"); }
        }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    int n;
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 0) continue;
                    if (k == "captures") captures = n;
                    else if (k == "noteMilestone") noteMilestone = n;
                    else if (k == "noteOff") noteOff = n != 0;
                }
            }
            catch (Exception) { }      // missing or damaged: start from zero, never an error
        }

        static void Save()
        {
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, "captures=" + captures.ToString(CultureInfo.InvariantCulture) +
                                       "\r\nnoteMilestone=" + noteMilestone.ToString(CultureInfo.InvariantCulture) +
                                       "\r\nnoteOff=" + (noteOff ? "1" : "0") + "\r\n");
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);      // a crash while saving never leaves a half-written file behind
            }
            catch (Exception) { }
        }

        public static int Captures { get { Load(); return captures; } }

        /// <summary>Settings → General → "Show capture count" (on by default).</summary>
        public static bool ShowCount { get { return Settings.GetBool("ShowCaptureCount", true); } }

        /// <summary>One more capture was made (any capture mode).</summary>
        public static void Bump()
        {
            Load();
            if (captures < int.MaxValue) captures++;
            Save();
        }

        /// <summary>True at capture 100, 200, 300 … once for each of them, unless the user closed the note for good.</summary>
        public static bool NoteDue
        {
            get { Load(); return !noteOff && captures >= Every && captures / Every > noteMilestone; }
        }

        /// <summary>The note was shown: not again until the next hundred.</summary>
        public static void NoteShown() { Load(); noteMilestone = captures / Every; Save(); }

        /// <summary>The ✕ or the link was clicked: the note never comes back.</summary>
        public static void NoteClose() { Load(); noteOff = true; noteMilestone = captures / Every; Save(); }

        public static string CountText
        {
            get { Load(); int n = DemoCount > 0 ? DemoCount : captures; return n.ToString("N0", CultureInfo.CurrentCulture) + " " + Lang.T(n == 1 ? "capture" : "captures"); }
        }

        /// <summary>Self-test only: show this count without touching the real stats file.</summary>
        internal static int DemoCount;
    }
}
