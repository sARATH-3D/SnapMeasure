using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SnapMeasure
{
    /// <summary>User-interface language. English is written in the code; other languages are a table of
    /// "English ⇥ translation" lines (lang\ko.txt, built into the exe). Windows are translated after they are
    /// built (Apply), and text set while the app runs goes through T(). Anything without a translation stays in English.
    /// Korean uses the Malgun Gothic font, which every Windows 10/11 has.</summary>
    internal static class Lang
    {
        public static readonly string[] Codes = { "en", "ko" };
        public static readonly string[] Names = { "English", "한국어 (Korean)" };

        /// <summary>The language chosen in Settings → General (read each time: new windows follow a change).</summary>
        public static string Code
        {
            get { string c = Settings.Get("Language", "en"); return Array.IndexOf(Codes, c) >= 0 ? c : "en"; }
        }

        public static bool Korean { get { return Code == "ko"; } }

        public static string UiFamily { get { return Korean ? "Malgun Gothic" : "Segoe UI"; } }

        static Dictionary<string, string> table;
        static string tableCode;

        /// <summary>For translators: SNAPMEASURE_LANG_DUMP=file collects every English text that had no translation.</summary>
        static readonly string dumpPath = Environment.GetEnvironmentVariable("SNAPMEASURE_LANG_DUMP");
        static readonly SortedSet<string> missing = new SortedSet<string>(StringComparer.Ordinal);

        static Dictionary<string, string> Table
        {
            get
            {
                string code = Code;
                if (table != null && tableCode == code) return table;
                table = new Dictionary<string, string>(StringComparer.Ordinal);
                tableCode = code;
                if (code == "en") return table;
                try
                {
                    using (Stream s = typeof(Lang).Assembly.GetManifestResourceStream("SnapMeasure." + code + ".txt"))
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                    {
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            if (line.Length == 0 || line[0] == '#') continue;
                            int tab = line.IndexOf('\t');
                            if (tab <= 0 || tab == line.Length - 1) continue;
                            table[Unescape(line.Substring(0, tab))] = Unescape(line.Substring(tab + 1).Trim());
                        }
                    }
                }
                catch (Exception ex) { ErrorLog.Write("language " + code, ex); }
                return table;
            }
        }

        static string Unescape(string s) { return s.Replace("\\n", "\n").Replace("\\t", "\t"); }

        static readonly Regex Tail = new Regex(@"^(?<head>.*?)(?<tail>\s*(\([^()]*\)|…|:|\.\.\.)\s*)$", RegexOptions.Singleline);

        /// <summary>The text in the chosen language (unchanged in English or when there is no translation).</summary>
        public static string T(string en)
        {
            if (string.IsNullOrEmpty(en) || (dumpPath == null && Code == "en")) return en;
            string tr = Find(en);
            if (tr != null) return tr;
            if (dumpPath != null && en.Trim().Length > 1) lock (missing) missing.Add(en.Replace("\r", "").Replace("\n", "\\n"));
            return en;
        }

        static string Find(string en)
        {
            Dictionary<string, string> t = Table;
            string tr;
            if (t.TryGetValue(en, out tr)) return tr;
            if (en.IndexOf('\n') >= 0)
            {
                // several lines: each one on its own
                string[] lines = en.Replace("\r\n", "\n").Split('\n');
                bool any = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    string l = Find(lines[i]);
                    if (l != null) { lines[i] = l; any = true; }
                }
                return any ? string.Join("\n", lines) : null;
            }
            // "Rectangle (R)", "Save as…", "Width:" – the words translated, the key or mark kept
            Match m = Tail.Match(en);
            if (m.Success && m.Groups["head"].Value.Length > 0)
            {
                string head = Find(m.Groups["head"].Value);
                if (head != null) return head + m.Groups["tail"].Value;
            }
            return null;
        }

        static readonly ConditionalWeakTable<ComboBox, object> formatted = new ConditionalWeakTable<ComboBox, object>();

        /// <summary>Translates a finished window: texts, tooltips, menus, list headers. Combo lists only change what
        /// they show (their items stay English, so code that compares them keeps working).</summary>
        public static void Apply(Control root, ToolTip tip)
        {
            if (dumpPath == null && Code == "en") return;
            Walk(root, tip);
        }

        static void Walk(Control c, ToolTip tip)
        {
            if (c is ComboBox)
            {
                ComboBox cb = (ComboBox)c;
                object done;
                if (!formatted.TryGetValue(cb, out done))
                {
                    formatted.Add(cb, true);
                    cb.Format += (s, e) => { string v = e.ListItem as string; if (v != null) e.Value = T(v); };
                    foreach (object o in cb.Items) if (o is string) T((string)o);      // dump mode: collect them now
                    cb.FormattingEnabled = false;
                    cb.FormattingEnabled = true;          // switching it on redraws the list with the handler above
                }
            }
            else if (c is ListView)
            {
                foreach (ColumnHeader h in ((ListView)c).Columns) h.Text = T(h.Text);
            }
            else if (c is ToolStrip)
            {
                Apply(((ToolStrip)c).Items);
            }
            else if (!(c is TextBoxBase) && !(c is UpDownBase) && !(c is Canvas))
            {
                string t = c.Text;
                if (t.Length > 0) { string tr = T(t); if (!ReferenceEquals(tr, t) && tr != t) c.Text = tr; }
            }
            if (tip != null)
            {
                string tt = tip.GetToolTip(c);
                if (tt.Length > 0) { string tr = T(tt); if (tr != tt) tip.SetToolTip(c, tr); }
            }
            if (c.ContextMenuStrip != null) Apply(c.ContextMenuStrip.Items);
            foreach (Control child in c.Controls) Walk(child, tip);
        }

        public static void Apply(ToolStripItemCollection items)
        {
            if (dumpPath == null && Code == "en") return;
            foreach (ToolStripItem i in items)
            {
                if (!string.IsNullOrEmpty(i.Text)) i.Text = T(i.Text);
                if (!string.IsNullOrEmpty(i.ToolTipText)) i.ToolTipText = T(i.ToolTipText);
                ToolStripDropDownItem dd = i as ToolStripDropDownItem;
                if (dd != null && dd.HasDropDownItems) Apply(dd.DropDownItems);
                ToolStripComboBox tc = i as ToolStripComboBox;
                if (tc != null) Walk(tc.ComboBox, null);
            }
        }

        /// <summary>Dump mode: writes the collected English texts (one per line, ready for "⇥ translation").</summary>
        public static void WriteDump()
        {
            if (dumpPath == null) return;
            lock (missing) File.WriteAllLines(dumpPath, missing, new UTF8Encoding(false));
        }
    }
}
