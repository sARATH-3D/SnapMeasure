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
            get { Version v = typeof(AppInfo).Assembly.GetName().Version; return v.Major + "." + v.Minor; }
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
        public const string DefaultPattern = "SnapMeasure_${capturetime:yyyy-MM-dd_HH-mm-ss}";
        public static readonly string[] Formats = { "png", "jpg", "bmp" };

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

        public static int NextNumber()
        {
            int n = Settings.GetInt("FileCounter", 0) + 1;
            Settings.Set("FileCounter", n.ToString(CultureInfo.InvariantCulture));
            return n;
        }
    }

    /// <summary>About window: version, author, GitHub link, licence and the main shortcuts.</summary>
    internal class AboutForm : Form
    {
        public AboutForm(string shortcuts)
        {
            float k = Native.ScaleAt(Cursor.Position), fs = k / Native.SystemScale;
            Func<float, int> S = v => (int)Math.Round(v * k);
            Text = "About " + AppInfo.Name;
            Icon = AppIcon.Get();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9f * fs);
            ClientSize = new Size(S(520), S(470));
            BackColor = Color.White;

            PictureBox logo = new PictureBox();
            logo.Image = AppIcon.Render(S(64));
            logo.SetBounds(S(20), S(20), S(64), S(64));
            Controls.Add(logo);

            Label name = new Label();
            name.Text = AppInfo.Name + "  " + AppInfo.Version;
            name.Font = new Font("Segoe UI", 16f * fs, FontStyle.Bold);
            name.AutoSize = true;
            name.Location = new Point(S(100), S(18));
            Controls.Add(name);

            Label sub = new Label();
            sub.Text = "Portable screenshot & measuring tool\nMade by " + AppInfo.Author + "  ·  free and open source (MIT licence)";
            sub.ForeColor = Color.FromArgb(90, 90, 90);
            sub.UseMnemonic = false;   // show "&" as a character
            sub.AutoSize = true;
            sub.Location = new Point(S(102), S(54));
            Controls.Add(sub);

            LinkLabel repo = new LinkLabel();
            repo.Text = AppInfo.RepoUrl.Replace("https://", "");
            repo.AutoSize = true;
            repo.Location = new Point(S(102), S(96));
            repo.LinkClicked += (s, e) => AppInfo.OpenUrl(AppInfo.RepoUrl);
            Controls.Add(repo);

            LinkLabel rel = new LinkLabel();
            rel.Text = "Check for updates";
            rel.AutoSize = true;
            rel.Location = new Point(S(102), S(120));
            rel.LinkClicked += (s, e) => AppInfo.OpenUrl(AppInfo.ReleasesUrl);
            Controls.Add(rel);

            LinkLabel iss = new LinkLabel();
            iss.Text = "Report a problem or ask for a feature";
            iss.AutoSize = true;
            iss.Location = new Point(S(240), S(120));
            iss.LinkClicked += (s, e) => AppInfo.OpenUrl(AppInfo.IssuesUrl);
            Controls.Add(iss);

            TextBox keys = new TextBox();
            keys.Multiline = true;
            keys.ReadOnly = true;
            keys.ScrollBars = ScrollBars.Vertical;
            keys.BackColor = Color.FromArgb(247, 248, 250);
            keys.Font = new Font("Consolas", 8.5f * fs);
            keys.Text = shortcuts.Replace("\r\n", "\n").Replace("\n", "\r\n");
            keys.SetBounds(S(20), S(152), S(480), S(260));
            Controls.Add(keys);

            Button ok = new Button();
            ok.Text = "Close";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(S(416), S(426), S(84), S(28));
            Controls.Add(ok);
            AcceptButton = CancelButton = ok;
            Shown += (s, e) => { ok.Focus(); keys.SelectionLength = 0; };
        }
    }
}
