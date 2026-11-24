using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace SnapMeasure
{
    internal static class Native
    {
        public const int WM_HOTKEY = 0x0312;
        public const int WM_DPICHANGED = 0x02E0;
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 2;
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        /// <summary>Process name of a window's owner (e.g. "acadlt"), or "" if unknown.</summary>
        public static string ProcessNameOf(IntPtr hWnd)
        {
            try
            {
                uint pid;
                if (hWnd == IntPtr.Zero || GetWindowThreadProcessId(hWnd, out pid) == 0 || pid == 0) return "";
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById((int)pid)) return p.ProcessName;
            }
            catch { return ""; }
        }
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)] public struct POINTI { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINTI pt, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);

        /// <summary>UI scale of the main (system) DPI: 1 = 100 %.</summary>
        public static float SystemScale
        {
            get { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }
        }

        /// <summary>
        /// UI font sized in pixels for a monitor scale k (1 = 100 %). Point-sized fonts are converted with the
        /// DPI Windows had at sign-in, which is wrong when monitors with different scaling are mixed
        /// (text came out half or double size), so all UI fonts are given in pixels instead.
        /// </summary>
        public static Font UiFont(string family, float pt, float k, FontStyle style = FontStyle.Regular)
        {
            if (family == "Segoe UI") family = Lang.UiFamily;          // Korean: Malgun Gothic
            return new Font(family, pt * 96f / 72f * k, style, GraphicsUnit.Pixel);
        }

        /// <summary>UI scale of the monitor under a screen point (per-monitor DPI), e.g. 2 on a 200 % laptop screen.</summary>
        public static float ScaleAt(Point p)
        {
            try
            {
                POINTI pt = new POINTI { X = p.X, Y = p.Y };
                IntPtr mon = MonitorFromPoint(pt, 2);   // MONITOR_DEFAULTTONEAREST
                uint dx, dy;
                if (GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx >= 48) return dx / 96f;
            }
            catch (Exception) { }
            return SystemScale;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int max);

        /// <summary>Top-left corners of every visible SnapMeasure editor window (any process).</summary>
        public static List<Point> EditorWindowCorners(IntPtr except)
        {
            List<Point> list = new List<Point>();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (h == except || !IsWindowVisible(h) || IsIconic(h)) return true;
                System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
                GetWindowText(h, sb, 256);
                if (sb.ToString().StartsWith("SnapMeasure – "))
                {
                    RECT r;
                    if (GetWindowRect(h, out r)) list.Add(new Point(r.Left, r.Top));
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr rgn, uint flags);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hWnd);

        /// <summary>UI scale Windows uses for this window right now (0 if unknown, e.g. before Windows 10 1607).</summary>
        public static float WindowScale(IntPtr hWnd)
        {
            try { uint d = GetDpiForWindow(hWnd); return d >= 48 ? d / 96f : 0f; }
            catch (Exception) { return 0f; }
        }
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        /// <summary>Visible window bounds without the invisible resize border (DWM extended frame).</summary>
        public static Rectangle GetWindowBounds(IntPtr h)
        {
            RECT r;
            if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))) == 0) return r.ToRectangle();
            GetWindowRect(h, out r);
            return r.ToRectangle();
        }

        static bool IsCloaked(IntPtr h)
        {
            int c;
            return DwmGetWindowAttribute(h, 14, out c, 4) == 0 && c != 0;
        }

        /// <summary>Title bar text of a window ("" if none).</summary>
        public static string WindowTitle(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(512);
            GetWindowText(h, sb, 512);
            return sb.ToString().Trim();
        }

        /// <summary>Top-level windows in Z order (front first), for click-to-capture-window.</summary>
        public static List<Rectangle> GetVisibleWindowRects() { return GetVisibleWindowRects(null); }

        /// <summary>Same, also filling 'titles' (same order) with each window's title text.</summary>
        public static List<Rectangle> GetVisibleWindowRects(List<string> titles)
        {
            List<Rectangle> list = new List<Rectangle>();
            IntPtr shell = GetShellWindow();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (h == shell || !IsWindowVisible(h) || IsIconic(h) || IsCloaked(h)) return true;
                int ex = GetWindowLong(h, -20);
                if ((ex & 0x20) != 0) return true;                                  // WS_EX_TRANSPARENT
                if ((ex & 0x80) != 0 && GetWindowTextLength(h) == 0) return true;   // untitled tool windows (tooltips etc.)
                Rectangle r = GetWindowBounds(h);
                if (r.Width < 16 || r.Height < 16) return true;
                list.Add(r);
                if (titles != null) titles.Add(WindowTitle(h));
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }
}
