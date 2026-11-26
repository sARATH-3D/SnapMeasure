using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SnapMeasure
{
    /// <summary>1.4.2: which tools and commands fit the words typed in the top bar's search box. Find (Ctrl+F) is for
    /// text in the drawing; this is for what SnapMeasure can do. No windows here, so the self-test can check it.</summary>
    internal static class ToolSearch
    {
        public class Entry
        {
            public string Name = "";        // as shown
            public string Where = "";       // the tool group or menu it lives in
            public string Key = "";         // its shortcut
            public string More = "";        // also searched: the tooltip, the English name
            public Image Icon;
            public bool IsTool;
            public Action Run;
        }

        static string Fold(string s) { return EditorForm.StripMarks(s ?? "").ToLowerInvariant(); }

        static bool WordStart(string s, string word)
        {
            for (int i = s.IndexOf(word, StringComparison.Ordinal); i >= 0; i = s.IndexOf(word, i + 1, StringComparison.Ordinal))
                if (i == 0 || !char.IsLetterOrDigit(s[i - 1])) return true;
            return false;
        }

        /// <summary>0 = the name starts with the word, 1 = a word of the name does, 2 = the name contains it,
        /// 3 = a word of the description starts with it; -1 = no match.</summary>
        static int Rank(string name, string more, string word)
        {
            if (name.StartsWith(word, StringComparison.Ordinal)) return 0;
            if (WordStart(name, word)) return 1;
            if (name.IndexOf(word, StringComparison.Ordinal) >= 0) return 2;
            if (word.Length >= 3 && WordStart(more, word)) return 3;
            return -1;
        }

        /// <summary>The best 'max' entries for the query (every word must match); tools before commands on a tie.</summary>
        public static List<Entry> Match(IList<Entry> all, string query, int max)
        {
            List<Entry> found = new List<Entry>();
            string[] words = Fold(query).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return found;
            List<int[]> scored = new List<int[]>();      // rank, command?, position
            for (int i = 0; i < all.Count; i++)
            {
                string name = Fold(all[i].Name), more = Fold(all[i].More);
                int worst = 0;
                foreach (string w in words)
                {
                    int r = Rank(name, more, w);
                    if (r < 0) { worst = -1; break; }
                    worst = Math.Max(worst, r);
                }
                if (worst >= 0) scored.Add(new[] { worst, all[i].IsTool ? 0 : 1, i });
            }
            scored.Sort((a, b) => a[0] != b[0] ? a[0] - b[0] : a[1] != b[1] ? a[1] - b[1] : a[2] - b[2]);
            for (int i = 0; i < scored.Count && i < max; i++) found.Add(all[scored[i][2]]);
            return found;
        }

        static readonly Regex KeyHint = new Regex(@"(?:^|\()((?:(?:Ctrl|Shift|Alt)\+)*(?:F\d{1,2}|Plus|Minus|Home|[A-Z0-9]))(?=$|[),;])");

        /// <summary>The shortcut named in a tooltip: "Rectangle (R)" gives "R".</summary>
        public static string KeyOf(string tip)
        {
            Match m = KeyHint.Match(tip ?? "");
            return m.Success ? m.Groups[1].Value : "";
        }
    }

    public partial class EditorForm
    {
        // the box in the top bar; in a window too narrow for it, a button that opens the box under itself
        ToolStripSeparator sepSearch;
        ToolStripTextBox tsSearch;
        ToolStripButton bSearch;
        Panel searchDrop;
        TextBox searchOwn;          // the box in the drop-down, for narrow windows
        ListBox searchList;
        bool closingSearch;
        bool searchDark;            // the look this window was opened with (a new choice is for new windows)
        Color SearchText { get { return searchDark ? Theme.Label : SystemColors.ControlText; } }
        internal Color SearchFill()                                     // for the self-test
        {
            using (Bitmap b = new Bitmap(Math.Max(1, bar.Width), Math.Max(1, bar.Height)))
            using (Graphics g = Graphics.FromImage(b)) DrawSearchPill(bar, new PaintEventArgs(g, new Rectangle(Point.Empty, b.Size)));
            return tsSearch.BackColor;
        }
        readonly Dictionary<Tool, string> toolGroup = new Dictionary<Tool, string>();
        string toolHeading = "";

        void BuildSearch()
        {
            searchDark = Theme.Dark;
            sepSearch = new ToolStripSeparator { Overflow = ToolStripItemOverflow.Never };
            bar.Items.Add(sepSearch);
            tsSearch = new ToolStripTextBox { Name = "search", AutoSize = false, Overflow = ToolStripItemOverflow.Never };
            tsSearch.BorderStyle = BorderStyle.None;
            tsSearch.Font = Font;
            tsSearch.Size = new Size(S(220), tsSearch.TextBox.PreferredHeight);
            tsSearch.ToolTipText = "Search tools and commands (Ctrl+K)";
            SetCue(tsSearch.TextBox, Lang.T("Search tools (Ctrl+K)"));
            HookSearchBox(tsSearch.TextBox);
            bar.Items.Add(tsSearch);
            bSearch = IconOnly(AddBtn(bar, "Search tools", "Search tools and commands (Ctrl+K)", (s, e) => FocusSearch()), "search");
            bSearch.Overflow = ToolStripItemOverflow.Never;
            bSearch.Available = false;
            bar.SizeChanged += (s, e) => FitSearch();
            bar.Paint += DrawSearchPill;
            bar.MouseDown += (s, e) => { if (tsSearch.Available && SearchPill().Contains(e.Location)) FocusSearch(); };
            tsSearch.GotFocus += (s, e) => bar.Invalidate();
            tsSearch.LostFocus += (s, e) => bar.Invalidate();
            bar.ItemClicked += (s, e) => { if (e.ClickedItem != bSearch && e.ClickedItem != tsSearch) CloseSearch(); };
        }

        Rectangle SearchPill()
        {
            Rectangle b = tsSearch.Bounds;
            int top = Math.Max(1, b.Y - S(4)), bottom = Math.Min(bar.Height - 2, b.Bottom + S(4));
            return Rectangle.FromLTRB(b.X - S(24), top, b.Right + S(8), bottom);
        }

        void DrawSearchPill(object sender, PaintEventArgs e)
        {
            if (tsSearch == null || !tsSearch.Available) return;
            Color fill = searchDark ? Theme.Field : Color.FromArgb(232, 234, 238);
            if (tsSearch.BackColor != fill) tsSearch.BackColor = fill;
            Color ink = searchDark ? Theme.Label2 : Color.FromArgb(96, 102, 112);
            Rectangle r = SearchPill();
            Graphics g = e.Graphics;
            GraphicsState was = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = S(10);
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                if (tsSearch.Focused) using (Pen pen = new Pen(Color.FromArgb(0, 120, 215), Math.Max(1f, S(1)))) g.DrawPath(pen, p);
            }
            float k = S(100) / 100f, x = r.X + 8 * k, y = r.Y + r.Height / 2f - 6 * k;
            using (Pen pen = new Pen(ink, 1.4f * k))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawEllipse(pen, x, y, 8 * k, 8 * k);
                g.DrawLine(pen, x + 7 * k, y + 7 * k, x + 11 * k, y + 11 * k);
            }
            g.Restore(was);
        }

        /// <summary>The box when the bar has room for it, otherwise just its button.</summary>
        void FitSearch()
        {
            if (tsSearch == null || bar == null || bar.Width <= 0) return;
            int used = bar.Padding.Horizontal;
            foreach (ToolStripItem it in bar.Items)
                if (it != tsSearch && it != bSearch && it != sepSearch && it.Available)
                    used += it.GetPreferredSize(Size.Empty).Width + it.Margin.Horizontal;
            int free = bar.Width - used - S(28);
            bool wide = free >= S(172);
            if (wide)
            {
                tsSearch.Width = Math.Min(S(260), free - S(32));       // the pill is 32 wider than the box
                Padding m = tsSearch.Margin;
                int side = (free - S(32) - tsSearch.Width) / 2;        // in the middle of what is left
                tsSearch.Margin = new Padding(S(24) + side, m.Top, S(8), m.Bottom);
            }
            sepSearch.Available = tsSearch.Available = wide;
            bSearch.Available = !wide;
        }

        void HookSearchBox(TextBox box)
        {
            box.TextChanged += (s, e) => { if (!closingSearch) UpdateSearch(box); };
            // Esc, Enter and the arrows belong to the box, not to the toolbar
            box.PreviewKeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) e.IsInputKey = true;
            };
            box.KeyDown += (s, e) => SearchKey(box, e);
            box.LostFocus += (s, e) =>
            {
                if (!IsHandleCreated || closingSearch) return;
                // later: a click on the list takes the focus too
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed || (searchDrop != null && searchDrop.ContainsFocus) || (tsSearch != null && tsSearch.Focused)) return;
                    CloseSearch();
                }));
            };
        }

        void SearchKey(TextBox box, KeyEventArgs e)
        {
            int n = searchList == null || searchDrop == null || !searchDrop.Visible ? 0 : searchList.Items.Count;
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; CloseSearch(); cv.Focus(); }
            else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (n > 0) RunSearch(searchList.SelectedIndex); }
            else if (e.KeyCode == Keys.Down) { e.SuppressKeyPress = true; if (n > 0) searchList.SelectedIndex = (searchList.SelectedIndex + 1) % n; }
            else if (e.KeyCode == Keys.Up) { e.SuppressKeyPress = true; if (n > 0) searchList.SelectedIndex = (searchList.SelectedIndex + n - 1) % n; }
            else if (e.KeyData == (Keys.Control | Keys.F) || e.KeyData == (Keys.Control | Keys.H))
            {
                // SuppressKeyPress: the box would take Ctrl+H as Backspace
                e.SuppressKeyPress = true;
                FindInDrawing(box.Text.Trim(), e.KeyCode == Keys.H);
            }
        }

        /// <summary>Ctrl+K, or the button in a narrow window.</summary>
        void FocusSearch()
        {
            if (tsSearch == null) return;
            if (tsSearch.Available)
            {
                tsSearch.Focus();
                tsSearch.SelectAll();
                UpdateSearch(tsSearch.TextBox);
            }
            else
            {
                MakeSearchDrop();
                UpdateSearch(searchOwn);
                searchOwn.Focus();
                searchOwn.SelectAll();
            }
        }

        void MakeSearchDrop()
        {
            if (searchDrop != null) return;
            searchDrop = new Panel { Name = "searchDrop", Visible = false, Tag = Theme.KeepTag };
            searchDrop.BackColor = searchDark ? Theme.Border : SystemColors.ControlDark;      // shows as a thin frame
            searchOwn = new TextBox { Name = "searchOwn", BorderStyle = BorderStyle.FixedSingle, Font = Font };
            searchList = new ListBox { Name = "searchList", BorderStyle = BorderStyle.None, IntegralHeight = false, Font = Font };
            searchList.DrawMode = DrawMode.OwnerDrawFixed;
            searchList.BackColor = searchOwn.BackColor = searchDark ? Theme.Field : SystemColors.Window;
            searchList.ForeColor = searchOwn.ForeColor = SearchText;
            SetCue(searchOwn, Lang.T("Search tools (Ctrl+K)"));
            HookSearchBox(searchOwn);
            searchList.DrawItem += DrawSearchRow;
            searchList.MouseMove += (s, e) =>
            {
                int i = searchList.IndexFromPoint(e.Location);
                if (i >= 0 && i < searchList.Items.Count && searchList.SelectedIndex != i) searchList.SelectedIndex = i;
            };
            searchList.MouseClick += (s, e) => { int i = searchList.IndexFromPoint(e.Location); if (e.Button == MouseButtons.Left && i >= 0) RunSearch(i); };
            searchList.PreviewKeyDown += (s, e) => { if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter) e.IsInputKey = true; };
            searchList.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; CloseSearch(); cv.Focus(); }
                else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; RunSearch(searchList.SelectedIndex); }
            };
            searchDrop.Controls.Add(searchOwn);
            searchDrop.Controls.Add(searchList);
            Controls.Add(searchDrop);
        }

        void UpdateSearch(TextBox box)
        {
            string q = box.Text.Trim();
            bool narrow = !tsSearch.Available;
            if (q.Length == 0 && !narrow) { if (searchDrop != null) searchDrop.Visible = false; return; }
            MakeSearchDrop();
            searchList.BeginUpdate();
            searchList.Items.Clear();
            if (q.Length > 0)
            {
                foreach (ToolSearch.Entry en in ToolSearch.Match(SearchEntries(), q, 9)) searchList.Items.Add(en);
                // the last row hands the words to Find, for text in the drawing
                string words = q;
                searchList.Items.Add(new ToolSearch.Entry
                {
                    Name = string.Format(Lang.T("Find \"{0}\" in the drawing's text"), words), Key = "Ctrl+F",
                    Icon = Icons.Cmd("find", S(16)), Run = () => FindInDrawing(words, false)
                });
            }
            searchList.EndUpdate();
            PlaceSearchDrop(narrow);
            if (searchList.Items.Count > 0) searchList.SelectedIndex = 0;
        }

        void PlaceSearchDrop(bool narrow)
        {
            int row = Math.Min(255, Math.Max(S(24), TextRenderer.MeasureText("Ag가", Font).Height + S(8)));
            searchList.ItemHeight = row;
            int w = Math.Min(Math.Max(S(380), narrow ? 0 : tsSearch.Width), ClientSize.Width - S(8)), y = 1;
            searchOwn.Visible = narrow;
            if (narrow)
            {
                searchOwn.SetBounds(S(6), S(6), w - S(12), searchOwn.PreferredHeight);
                y = searchOwn.Bottom + S(6);
            }
            int rows = searchList.Items.Count;
            searchList.SetBounds(1, y, w - 2, row * rows);
            searchList.Visible = rows > 0;
            int x = narrow ? bSearch.Bounds.Left : tsSearch.Bounds.Left;
            Point at = PointToClient(bar.PointToScreen(new Point(x, bar.Height)));
            searchDrop.SetBounds(Math.Max(S(4), Math.Min(at.X, ClientSize.Width - w - S(4))), at.Y, w, y + row * rows + 1);
            searchDrop.Visible = true;
            searchDrop.BringToFront();
        }

        void DrawSearchRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= searchList.Items.Count) return;
            ToolSearch.Entry en = (ToolSearch.Entry)searchList.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            Color back = sel ? (searchDark ? Theme.AccentFill : Color.FromArgb(204, 228, 247)) : searchList.BackColor;
            using (SolidBrush br = new SolidBrush(back)) e.Graphics.FillRectangle(br, e.Bounds);
            Rectangle r = e.Bounds;
            int icon = S(16);
            if (en.Icon != null) e.Graphics.DrawImage(en.Icon, new Rectangle(r.X + S(8), r.Y + (r.Height - icon) / 2, icon, icon));
            string right = en.Where.Length > 0 && en.Key.Length > 0 ? en.Where + " · " + en.Key : en.Where + en.Key;
            TextFormatFlags f = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            // measured without the ellipsis flag: with it, an empty size gives only the width of "…"
            int rw = right.Length > 0 ? TextRenderer.MeasureText(right, searchList.Font, new Size(int.MaxValue, r.Height), TextFormatFlags.NoPrefix).Width + S(2) : 0;
            int left = r.X + S(8) + icon + S(8);
            TextRenderer.DrawText(e.Graphics, en.Name, searchList.Font, new Rectangle(left, r.Y, r.Right - left - rw - S(12), r.Height), SearchText, f);
            if (rw > 0)
                TextRenderer.DrawText(e.Graphics, right, searchList.Font, new Rectangle(r.Right - rw - S(8), r.Y, rw, r.Height),
                                      searchDark ? Theme.Label2 : SystemColors.GrayText, f);
        }

        void RunSearch(int index)
        {
            if (searchList == null || index < 0 || index >= searchList.Items.Count) return;
            Action run = ((ToolSearch.Entry)searchList.Items[index]).Run;
            CloseSearch();
            cv.Focus();
            BeginInvoke(run);       // after the key or click is done with: a command may open a window
        }

        void CloseSearch()
        {
            if (closingSearch) return;
            closingSearch = true;
            try
            {
                if (tsSearch != null && tsSearch.Text.Length > 0) tsSearch.Text = "";
                if (searchOwn != null && searchOwn.Text.Length > 0) searchOwn.Text = "";
                if (searchDrop != null && searchDrop.Visible)
                {
                    if (searchDrop.ContainsFocus) cv.Focus();
                    searchDrop.Visible = false;
                }
            }
            finally { closingSearch = false; }
        }

        /// <summary>The bars are rebuilt (another display scale): the drop-down goes with them.</summary>
        void DropSearch()
        {
            if (searchDrop == null) return;
            closingSearch = true;
            try { Controls.Remove(searchDrop); searchDrop.Dispose(); }
            finally { closingSearch = false; }
            searchDrop = null; searchOwn = null; searchList = null;
        }

        void FindInDrawing(string words, bool replace)
        {
            CloseSearch();
            ShowFind(replace);
            if (words.Length > 0 && findBox != null) { findBox.Text = words; findBox.SelectAll(); }
        }

        /// <summary>Everything the box can pick, read from the tool column and the top bar as they are now (their
        /// language, what is switched off).</summary>
        internal List<ToolSearch.Entry> SearchEntries()
        {
            List<ToolSearch.Entry> all = new List<ToolSearch.Entry>();
            foreach (KeyValuePair<Tool, ToolStripButton> kv in toolButtons)
            {
                Tool t = kv.Key;
                string tipText = kv.Value.ToolTipText ?? "", group;
                toolGroup.TryGetValue(t, out group);
                all.Add(new ToolSearch.Entry
                {
                    Name = Lang.T(SearchName(t)), Where = string.IsNullOrEmpty(group) ? Lang.T("Tool") : Lang.T(group),
                    Key = ToolSearch.KeyOf(tipText), More = SearchName(t) + " " + tipText, Icon = kv.Value.Image, IsTool = true,
                    Run = () => PickTool(t)
                });
            }
            if (bMatch != null)
                all.Add(new ToolSearch.Entry
                {
                    Name = Lang.T("Match properties"), Where = Lang.T("Tool"), Key = "Ctrl+Shift+M", More = "Match properties " + bMatch.ToolTipText,
                    Icon = bMatch.Image, IsTool = true, Run = () => { ToggleMatch(); cv.Focus(); }
                });
            foreach (ToolStripItem it in bar.Items)
            {
                if (it == bSearch || !it.Enabled) continue;
                ToolStripSplitButton split = it as ToolStripSplitButton;
                ToolStripDropDownItem menu = it as ToolStripDropDownItem;
                if (split != null) AddCommand(all, split, "", split.PerformButtonClick);
                else if (it is ToolStripButton) AddCommand(all, it, "", it.PerformClick);
                if (menu == null) continue;
                foreach (ToolStripItem sub in menu.DropDownItems)
                {
                    ToolStripMenuItem m = sub as ToolStripMenuItem;
                    if (m != null && m.Enabled) AddCommand(all, m, menu.Text, m.PerformClick);
                }
            }
            return all;
        }

        void AddCommand(List<ToolSearch.Entry> all, ToolStripItem it, string where, Action run)
        {
            string name = (it.Text ?? "").TrimEnd('…', '.', ' ');
            if (name.Length == 0) return;
            all.Add(new ToolSearch.Entry
            {
                Name = name, Where = where, Key = ToolSearch.KeyOf(it.ToolTipText), More = it.ToolTipText ?? "",
                Icon = it.Image, Run = run
            });
        }

        static string SearchName(Tool t)
        {
            switch (t)
            {
                case Tool.Calibrate: return "Calibrate scale";
                case Tool.Coord: return "Coordinates";
                case Tool.Counter: return "Number marker";
                case Tool.Letter: return "Letter marker";
                case Tool.Symbol: return "Symbol marker";
                case Tool.Ocr: return "Copy text from an area";
                default: return ToolName(t);
            }
        }

        /// <summary>For the self-test: types 'q' into the box and presses Enter; returns the first row's name.</summary>
        internal string TrySearch(string q)
        {
            FitSearch();
            TextBox box = tsSearch.TextBox;
            if (!tsSearch.Available) { MakeSearchDrop(); box = searchOwn; }
            box.Text = q;
            if (searchList == null || searchList.Items.Count == 0) return "";
            string first = ((ToolSearch.Entry)searchList.Items[0]).Name;
            RunSearch(0);
            Application.DoEvents();
            return first;
        }

        /// <summary>Selects a tool found by the search, and brings its button into view in the column.</summary>
        void PickTool(Tool t)
        {
            if (cv.MatchActive) cv.EndMatch();
            cv.CurrentTool = t;
            SyncToolButtons();
            ToolStripButton b;
            if (toolHost != null && toolHost.VerticalScroll.Visible && toolButtons.TryGetValue(t, out b))
            {
                int top = -toolHost.AutoScrollPosition.Y;
                if (b.Bounds.Top < top || b.Bounds.Bottom > top + toolHost.ClientSize.Height)
                    toolHost.AutoScrollPosition = new Point(0, Math.Max(0, b.Bounds.Top - S(20)));
            }
            cv.Focus();
        }
    }
}
