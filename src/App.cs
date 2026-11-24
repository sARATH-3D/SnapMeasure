using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

[assembly: System.Reflection.AssemblyTitle("SnapMeasure")]
[assembly: System.Reflection.AssemblyProduct("SnapMeasure")]
[assembly: System.Reflection.AssemblyDescription("Screenshot tool with dimensions, area and scale")]
[assembly: System.Reflection.AssemblyVersion("1.3.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.3.2.0")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 sARATH – MIT licence – github.com/sARATH-3D/SnapMeasure")]
// P/Invoke DLLs (user32, gdi32, dwmapi, kernel32) only from System32, never from the exe's folder
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace SnapMeasure
{
    internal static class Program
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern bool SetDllDirectory(string path);

        [STAThread]
        static void Main(string[] args)
        {
            // DLL-planting protection: never load DLLs from the current folder (e.g. Downloads)
            try { SetDllDirectory(""); } catch { }
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Exception ex = e.ExceptionObject as Exception;
                ErrorLog.Write("fatal", ex);
                try { MessageBox.Show("SnapMeasure hit an unexpected problem and has to close:\n\n" + (ex != null ? ex.Message : "unknown error") + "\n\nDetails: " + ErrorLog.FilePath, "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            };
            if (args.Length == 2 && args[0] == "--write-icon") { AppIcon.WriteIco(args[1]); return; }
            // Settings → Output → preview pane: the part that needs administrator rights (registers for the machine)
            if (args.Length >= 2 && args[0] == ShellPreview.ElevatedArg) { Environment.ExitCode = ShellPreview.MachineRegister(args); return; }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--selftest")
            {
                Settings.Load();
                try { SelfTest.Run(args[1]); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(args[1], "selftest_error.txt"), SelfTest.log + Environment.NewLine + ex.ToString()); }
                return;
            }
            Application.ThreadException += (s, e) =>
            {
                ErrorLog.Write("error", e.Exception);
                if (ErrorLog.Showing) return;          // never stack error boxes on top of each other
                ErrorLog.Showing = true;
                try
                {
                    MessageBox.Show("Something went wrong, but your work is still open:\n\n" + e.Exception.Message +
                        "\n\nSave your work (Ctrl+S). Details were written to:\n" + ErrorLog.FilePath, "SnapMeasure – unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally { ErrorLog.Showing = false; }
            };
            Settings.Load();
            try { StartJitProfile(); } catch { }

            // started at sign-in ("Start with Windows"): stay quiet in the tray
            bool atStartup = Array.IndexOf(args, Startup.Arg) >= 0;
            if (atStartup) args = Array.FindAll(args, a => a != Startup.Arg);
            if (!atStartup && AppInfo.IsStoreApp) atStartup = StartedByStartupTask();
            App.QuietStart = atStartup;

            bool first;
            using (Mutex m = new Mutex(true, "SnapMeasure.SingleInstance.v1", out first))
            {
                if (!first)
                {
                    if (args.Length > 0)
                    {
                        Form f = App.LoadEditor(args[0]);
                        if (f != null) Application.Run(f);
                    }
                    else if (!atStartup)
                        MessageBox.Show("SnapMeasure is already running.\n\nLook for its icon in the system tray (bottom-right, maybe under the ^ arrow), or press Print Screen.",
                            "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                TrayContext tray = new TrayContext();
                foreach (string a in args) App.OpenFile(a);
                Application.Run(tray);
            }
        }

        /// <summary>Multicore JIT: Windows remembers which code the app needed last time and compiles it on a
        /// spare core while starting, so the tray and the first editor are ready sooner. The profile is a small
        /// file in the temp folder; if it is missing it is simply made again.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void StartJitProfile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "SnapMeasure");
            Directory.CreateDirectory(dir);
            System.Runtime.ProfileOptimization.SetProfileRoot(dir);
            System.Runtime.ProfileOptimization.StartProfile("SnapMeasure.jitprofile");
        }

        /// <summary>Store package: true when Windows started us through the StartupTask at sign-in.</summary>
        static bool StartedByStartupTask()
        {
            try
            {
                return WinRt.Run(() =>
                {
                    var a = Windows.ApplicationModel.AppInstance.GetActivatedEventArgs();
                    return a != null && a.Kind == Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
                });
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Unexpected errors go to %TEMP%\SnapMeasure-error.log (kept small) so a problem can be reported.</summary>
    internal static class ErrorLog
    {
        public static bool Showing;
        public static string FilePath { get { return Path.Combine(Path.GetTempPath(), "SnapMeasure-error.log"); } }

        public static void Write(string what, Exception ex)
        {
            try
            {
                string p = FilePath;
                if (File.Exists(p) && new FileInfo(p).Length > 256 * 1024) File.Delete(p);
                File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + what + " (SnapMeasure " +
                    Application.ProductVersion + ")" + Environment.NewLine + (ex != null ? ex.ToString() : "(no details)") + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }

    /// <summary>Headless check: builds a known drawing, measures it, renders, round-trips the project file.</summary>
    internal static class SelfTest
    {
        static Shape Mk(Canvas cv, Tool t, params float[] xy)
        {
            Shape s = cv.Styles[t].Clone();
            s.Pts.Clear();
            for (int i = 0; i + 1 < xy.Length; i += 2) s.Pts.Add(new PointF(xy[i], xy[i + 1]));
            cv.Shapes.Add(s);
            return s;
        }

        public static StringBuilder log = new StringBuilder();

        /// <summary>Checks for the 1.2 features: coordinates, line styles, cloud callouts, highlight shapes / pixelate,
        /// text boxes and frames, open / closed perimeter, freehand arrows, saving captures without the editor.</summary>
        /// <summary>Checks for 1.3: highlighter pen, text frame = line width, 8-handle text box, text list edits, capture key names.</summary>
        static void V13(Canvas cv, string dir, Action<string, string, string> check)
        {
            List<Shape> saved = new List<Shape>(cv.Shapes);
            cv.Shapes.Clear();

            // highlighter pen: a see-through freehand stroke that survives save / paste and has no arrowheads
            Shape mk = Mk(cv, Tool.Marker, 100, 520, 180, 522, 300, 519);
            List<Shape> rt = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { mk }));
            check("Highlighter pen round-trip", rt.Count + "|" + (rt.Count > 0 ? rt[0].IsMarkerPen + "/" + rt[0].StrokeOpacity + "/" + rt[0].HasHeads + "/" + Canvas.ToolFor(rt[0]) : "-"),
                  "1|True/45/False/Marker");

            // text: line width is the frame; a 1.2 text without a frame loads with width 0, one with a frame keeps its width
            List<Shape> old = ProjectFile.ShapesFromXml("<shapes><shape kind=\"Text\" stroke=\"FFFF0000\" fill=\"FFFFFFFF\" width=\"2\" text=\"plain\"><p x=\"10\" y=\"10\"/></shape>" +
                                                        "<shape kind=\"Text\" stroke=\"FFFF0000\" fill=\"FFFFFFFF\" width=\"3\" frame=\"1\" text=\"framed\"><p x=\"10\" y=\"60\"/></shape></shapes>");
            check("1.2 text frame becomes line width", old.Count + "|" + (old.Count == 2 ? old[0].Width.ToString("F0") + "/" + old[1].Width.ToString("F0") : "-"), "2|0/3");
            check("New text has no frame", cv.Styles[Tool.Text].Width.ToString("F0"), "0");

            // text box: the bottom-right corner sets width and height; the text wraps and keeps its size
            Shape tb = Mk(cv, Tool.Text, 100, 100, 260, 100); tb.Text = "Box"; tb.FontSize = 20;
            Shape tb2 = tb.Clone();
            ShapeOps.DragHandle(tb2, tb, new Handle(PointF.Empty, HKind.Box, 4), new PointF(364, 202), false);
            SizeF sz = Renderer.TextSize(tb2);
            check("Text box corner handle", Renderer.WrapWidth(tb2).ToString("F0") + "x" + sz.Height.ToString("F0") + "|" + tb2.FontSize.ToString("F0"), "260x100|20");
            check("Text box has 8 handles", ShapeOps.Handles(tb2, 30f).FindAll(h => h.Kind == HKind.Box).Count.ToString(), "8");

            // the editor's text list changes a text as one undo step
            Shape bub = Mk(cv, Tool.Bubble, 400, 100, 560, 170); bub.Text = "Old";
            cv.SetShapeText(bub, "New\nline");
            string after = bub.Text;
            cv.Undo();
            check("Text list edit + undo", after.Replace("\n", "/") + "|" + cv.Shapes.Find(x => x.Kind == ShapeKind.Bubble).Text, "New/line|Old");

            // capture keys: shown in words, and keys that would block typing are refused
            check("Capture key names", Hotkeys.Text(Keys.PrintScreen) + "|" + Hotkeys.Text(Keys.Control | Keys.Shift | Keys.D1) + "|" + Hotkeys.Text(Keys.Alt | Keys.F9),
                  "PrtSc|Ctrl+Shift+1|Alt+F9");
            check("Keys that block typing are refused", Hotkeys.Usable(Keys.A) + "|" + Hotkeys.Usable(Keys.Shift | Keys.A) + "|" + Hotkeys.Usable(Keys.Control | Keys.A) + "|" + Hotkeys.Usable(Keys.F8),
                  "False|False|True|True");

            // radius / diameter: 3 points on a circle round (300,300) with radius 100 px = 1 m
            ScaleInfo sc = cv.Scale;
            Shape rad = Mk(cv, Tool.Radius, 200, 300, 300, 200, 400, 300);
            MeasureRow rr = Measure.Rows(cv.Shapes, sc, false).Find(r => r.Shape == rad);
            CultureInfo cc = CultureInfo.CurrentCulture;
            // new radius shapes show the diameter (1.3.2 default); the list row follows the label
            check("Radius Ø / arc length", rr == null ? "-" : rr.Name + "|" + rr.Display + "|" + (rr.Perimeter ?? 0).ToString("F2", CultureInfo.InvariantCulture),
                  "R1|" + (2.0).ToString("N2", cc) + " m|3.14");
            rad.Mode = 1;
            MeasureRow rr1 = Measure.Rows(cv.Shapes, sc, false).Find(r => r.Shape == rad);
            check("Radius R when set to Radius", rr1 == null ? "-" : rr1.Display, (1.0).ToString("N2", cc) + " m");
            rad.Mode = 2;
            Shape arc = Mk(cv, Tool.Arc, 500, 300, 600, 200, 700, 300);
            List<Shape> rt5 = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { rad, arc }));
            check("Arc and radius round-trip", rt5.Count + "|" + (rt5.Count == 2 ? rt5[0].Kind + "/" + rt5[1].Kind + "/" + rt5[1].Pts.Count : "-"), "2|Radius/Arc/3");
            check("Arc hit test", ShapeOps.Hit(arc, new PointF(600, 201), 3) + "|" + ShapeOps.Hit(arc, new PointF(600, 280), 3), "True|False");

            // polygon with Closed unticked = open polyline, with arrowheads possible
            Shape pl = Mk(cv, Tool.Polygon, 100, 600, 200, 650, 300, 600); pl.Mode = 1; pl.HeadEnd = (int)HeadStyle.Filled;
            List<Shape> rt6 = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { pl }));
            check("Open polygon (polyline)", pl.IsOpenPath + "|" + pl.HasHeads + "|" + pl.CanBeCloud + "|" + (rt6.Count == 1 ? rt6[0].Mode + "/" + rt6[0].HeadEnd : "-"), "True|True|False|1/1");

            // text underline / strike out, area label position, cloud only for closed shapes
            Shape tu = Mk(cv, Tool.Text, 100, 700); tu.Text = "U"; tu.Underline = true; tu.Strikeout = true;
            Shape ar = Mk(cv, Tool.Area, 600, 500, 800, 500, 800, 650, 600, 650); ar.LabelOffset = new PointF(150, -40);
            List<Shape> rt7 = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { tu, ar }));
            check("Text U/S and moved area label round-trip", rt7.Count == 2 ? rt7[0].Underline + "/" + rt7[0].Strikeout + "|" + P(rt7[1].LabelOffset) : "-", "True/True|150.00,-40.00");
            check("Cloud only round closed shapes", cv.Styles[Tool.Line].CanBeCloud + "|" + cv.Styles[Tool.Ellipse].CanBeCloud + "|" + cv.Styles[Tool.Freehand].CanBeCloud, "False|True|False");

            // a freehand arrow stops where its head begins
            List<PointF> line = new List<PointF> { new PointF(0, 0), new PointF(50, 0), new PointF(100, 0) };
            List<PointF> cut = Geo.TrimPath(line, 10, 20);
            check("Freehand stroke trimmed for its heads", P(cut[0]) + " " + P(cut[cut.Count - 1]), "10.00,0.00 80.00,0.00");

            // the captured mouse pointer is a separate picture that can be deleted
            using (Bitmap ptr = new Bitmap(12, 19))
            {
                int before = cv.Shapes.Count;
                cv.AddPointerPicture(new Bitmap(ptr), new Point(40, 40));
                Shape ps = cv.Shapes[cv.Shapes.Count - 1];
                check("Pointer is its own picture", (cv.Shapes.Count - before) + "|" + ps.Kind + "|" + ps.Text, "1|Image|Mouse pointer");
            }

            // area tool: show area + perimeter / area only / perimeter only (Q); Excel rows follow
            Shape aP = Mk(cv, Tool.Area, 1000, 100, 1200, 100, 1200, 200, 1000, 200); aP.Mode = 2;
            Shape aA = Mk(cv, Tool.Area, 1000, 300, 1200, 300, 1200, 400, 1000, 400); aA.Mode = 1;
            List<MeasureRow> rows13 = Measure.Rows(cv.Shapes, sc, false);
            MeasureRow rP = rows13.Find(r => r.Shape == aP), rA = rows13.Find(r => r.Shape == aA);
            check("Area tool: perimeter only", rP == null ? "-" : rP.Name + "|" + rP.Type + "|" + rP.Display + "|" + aP.ShowsArea + "/" + aP.ShowsPerimeter,
                  "P1|Perimeter|" + (6.0).ToString("N2", cc) + " m|False/True");
            check("Area tool: area only", rA == null ? "-" : rA.Type + "|" + aA.ShowsArea + "/" + aA.ShowsPerimeter, "Area|True/False");
            List<Shape> rt8 = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { aP }));
            check("Perimeter-only area round-trip", rt8.Count == 1 ? rt8[0].Kind + "/" + rt8[0].Mode : "-", "Area/2");

            // radius: diameter only, and its label moves from halfway along the radius line
            Shape rd = rad.Clone(); rd.Mode = 2;
            MeasureRow rdr = Measure.Rows(new List<Shape> { rd }, sc, false).Find(r => r.Shape == rd);
            check("Radius: diameter only", rdr == null ? "-" : rdr.Type + "|" + rdr.Display, "Diameter|" + (2.0).ToString("N2", cc) + " m");
            Shape rd2 = rd.Clone();
            ShapeOps.DragHandle(rd2, rd, new Handle(PointF.Empty, HKind.Label, 0), new PointF(400, 250), false);
            check("Radius label moves", P(ShapeOps.LabelAnchor(rd)) + "|" + P(rd2.LabelOffset), "300.00,250.00|100.00,0.00");

            // match properties: the look is copied, the text and points are not
            Shape mSrc = Mk(cv, Tool.Line, 0, 0, 10, 10); mSrc.Stroke = Color.FromArgb(255, 0, 128, 0); mSrc.Width = 7; mSrc.Dash = 1; mSrc.FontSize = 33; mSrc.Bold = true; mSrc.Shadow = true;
            Shape mT = Mk(cv, Tool.Bubble, 50, 50, 150, 100); mT.Text = "keep";
            Canvas.MatchProps(mSrc, mT);
            check("Match properties", mT.Stroke.G + "|" + mT.Width + "|" + mT.FontSize + "|" + mT.Bold + "|" + mT.Text + "|" + P(mT.Pts[0]), "128|7|33|True|keep|50.00,50.00");

            // focus strength: magnify factor and pixel size, round-trip, and older files keep their look
            Shape fz = Mk(cv, Tool.Highlight, 50, 700, 150, 760); fz.Mode = (int)HighlightMode.Magnify; fz.Strength = 6;
            Shape fp = Mk(cv, Tool.Highlight, 200, 700, 300, 760); fp.Mode = (int)HighlightMode.PixelateOutside; fp.Strength = 5;
            Shape fold = fp.Clone(); fold.Strength = 0; fold.Width = 3;
            List<Shape> rt9 = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { fz, fp, fold }));
            check("Focus strength", fz.MagnifyFactor.ToString("0.0", CultureInfo.InvariantCulture) + "|" + fp.PixelBlock + "|" + fold.PixelBlock + "|" +
                  fold.ShownStrength(false) + "|" + cv.Styles[Tool.Highlight].MagnifyFactor.ToString("0.0", CultureInfo.InvariantCulture) + "|" +
                  (rt9.Count == 3 ? rt9[0].Strength + "/" + rt9[1].Strength + "/" + rt9[2].Strength : "-"), "4.0|20|12|3|2.0|6/5/0");
            cv.Shapes.Remove(fz); cv.Shapes.Remove(fp);

            // strike-out as heavy as the letters (picture for a visual check), and a filled cloud polygon
            using (Bitmap sk = new Bitmap(700, 330))
            using (Graphics sg = Graphics.FromImage(sk))
            {
                sg.Clear(Color.Black);
                sg.SmoothingMode = SmoothingMode.AntiAlias; sg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                Shape st1 = cv.Styles[Tool.Text].Clone(); st1.Pts.Clear(); st1.Pts.Add(new PointF(10, 10)); st1.Text = "fvxzsdgsedxfxhgf";
                st1.FontSize = 60; st1.Bold = true; st1.Italic = true; st1.Strikeout = true; st1.Width = 0; st1.Stroke = Color.FromArgb(230, 30, 30);
                Shape st2 = st1.Clone(); st2.Pts[0] = new PointF(10, 100); st2.Bold = false; st2.Italic = false; st2.Text = "Regular struck text";
                Shape pc = cv.Styles[Tool.Polygon].Clone(); pc.Pts.Clear();
                pc.Pts.AddRange(new PointF[] { new PointF(20, 200), new PointF(330, 250), new PointF(560, 180), new PointF(640, 300), new PointF(30, 320) });
                pc.Dash = (int)LineStyle.Cloud; pc.Stroke = Color.Gold; pc.Fill = Color.Olive; pc.FillOpacity = 100;
                Renderer.Draw(sg, st1, sk, cv.Scale, null); Renderer.Draw(sg, st2, sk, cv.Scale, null); Renderer.Draw(sg, pc, sk, cv.Scale, null);
                sk.Save(Path.Combine(dir, "selftest_strike.png"), ImageFormat.Png);
            }

            // a plain revision cloud turns; one with a callout stays level
            Shape cl = cv.Styles[Tool.Cloud].Clone(); cl.Mode = 0;
            Shape clc = cl.Clone(); clc.Mode = 1;
            check("Cloud rotates (no callout)", cl.Rotatable + "|" + clc.Rotatable, "True|False");

            // lock: nothing is moved; a shape wholly outside is hidden (and cannot be picked), one sticking out stays
            if (cv.Image != null)
            {
                Shape lk = Mk(cv, Tool.Rect, cv.Image.Width - 40, 10, cv.Image.Width + 60, 50);
                Shape lo = Mk(cv, Tool.Rect, cv.Image.Width + 100, 10, cv.Image.Width + 200, 50);
                RectangleF lk0 = ShapeOps.Bounds(lk);
                cv.SetLock(true);
                cv.SelectAll();
                string r1 = (ShapeOps.Bounds(lk) == lk0) + "|" + cv.HiddenByLock(lk) + "|" + cv.HiddenByLock(lo) + "|" + cv.Sel.Contains(lo);
                cv.SetLock(false);
                cv.SelectAll();
                string r2 = cv.HiddenByLock(lo) + "|" + cv.Sel.Contains(lo);
                check("Lock hides outside shapes, moves nothing", r1 + "|" + r2, "True|False|True|False|False|True");
                cv.Sel.Clear(); cv.Shapes.Remove(lk); cv.Shapes.Remove(lo);
            }

            // lock: zoomed in, the view cannot be scrolled past the picture's edge
            if (cv.Image != null && cv.Width > 100 && cv.Height > 100)
            {
                cv.SetLock(true);
                cv.ZoomCenter(8f);
                cv.PanBy(100000, 100000);       // try to drag the picture far right / down
                PointF tl = cv.ToScr(new PointF(0, 0));
                bool edge = Math.Abs(tl.X - cv.RulerSize) < 0.5f && Math.Abs(tl.Y - cv.RulerSize) < 0.5f;
                cv.PanBy(-200000, -200000);     // and far left / up
                PointF br = cv.ToScr(new PointF(cv.Image.Width, cv.Image.Height));
                edge = edge && Math.Abs(br.X - cv.Width) < 0.5f && Math.Abs(br.Y - cv.Height) < 0.5f;
                cv.SetLock(false);
                cv.Fit();
                check("Lock keeps the view on the picture", edge.ToString(), "True");
            }

            // area tool, Closed unticked: the area becomes an open length (kept through saving) and comes back when ticked
            {
                Shape ao = Mk(cv, Tool.Area, 600, 500, 800, 500, 800, 650, 600, 650); ao.Mode = 2;
                cv.Sel.Clear(); cv.Sel.Add(ao);
                cv.SetAreaClosed(false);
                List<Shape> rto = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { ao }));
                string o1 = ao.Kind + "|" + ao.IsOpenPath + "|" + (rto.Count == 1 ? rto[0].Kind + "/" + rto[0].FromArea : "-");
                cv.SetAreaClosed(true);
                check("Area tool open / closed", o1 + "|" + ao.Kind + "/" + ao.Mode + "/" + ao.FromArea, "Perimeter|True|Perimeter/3|Area/2/0");
                cv.Sel.Clear(); cv.Shapes.Remove(ao);
            }

            // pin: rotate and mirror carry the crop along (a marked pixel ends up where it should)
            {
                Bitmap pb = new Bitmap(40, 20);
                using (Graphics pg = Graphics.FromImage(pb)) pg.Clear(Color.White);
                pb.SetPixel(31, 2, Color.Red);
                using (PinForm pf = new PinForm(pb))
                {
                    pf.ShownPart = new Rectangle(30, 0, 10, 8);
                    bool a = pf.ShownPixel(1, 2).ToArgb() == Color.Red.ToArgb();
                    pf.Turn(RotateFlipType.Rotate90FlipNone);
                    string d1 = pf.Describe();
                    bool b = pf.ShownPixel(5, 1).ToArgb() == Color.Red.ToArgb();
                    pf.Turn(RotateFlipType.RotateNoneFlipX);
                    bool c = pf.ShownPixel(2, 1).ToArgb() == Color.Red.ToArgb();
                    pf.Turn(RotateFlipType.RotateNoneFlipX);
                    pf.Turn(RotateFlipType.Rotate270FlipNone);
                    string d2 = pf.Describe();
                    check("Pin rotates and mirrors", a + "|" + d1 + "|" + b + "|" + c + "|" + d2, "True|20x40 12,30,8,10|True|True|40x20 30,0,10,8");
                }
            }

            // saving over an existing file swaps a finished file in (no half-written file, no temp file left)
            string safeP = Path.Combine(dir, "selftest_safe.png");
            using (Bitmap sb1 = new Bitmap(30, 20)) Output.SaveBitmap(sb1, safeP);
            using (Bitmap sb2 = new Bitmap(40, 25)) Output.SaveBitmap(sb2, safeP);
            string safeR;
            using (Bitmap back = new Bitmap(safeP)) safeR = back.Width + "x" + back.Height;
            check("Safe save over a file", safeR + "|" + File.Exists(safeP + ".saving"), "40x25|False");

            // zoom to area: the middle of the chosen part comes to the middle of the window
            if (cv.Image != null && cv.Width > 100 && cv.Height > 100)
            {
                cv.ZoomToRect(new RectangleF(100, 100, 200, 100));
                PointF mid = cv.ToScr(new PointF(200, 150));
                check("Zoom to area", (Math.Abs(mid.X - (cv.Width + cv.RulerSize) / 2f) < 2 && Math.Abs(mid.Y - (cv.Height + cv.RulerSize) / 2f) < 2).ToString(), "True");
                cv.Fit();
            }

            string err = "ok";
            try { using (Bitmap fl = cv.Flatten(null)) fl.Save(Path.Combine(dir, "selftest_v13.png"), ImageFormat.Png); } catch (Exception ex) { err = ex.Message; }
            check("1.3 shapes draw", err, "ok");

            cv.Shapes.Clear();
            cv.Shapes.AddRange(saved);
            cv.Selected = null;
        }

        static void V12(Canvas cv, string dir, Action<string, string, string> check)
        {
            CultureInfo cc = CultureInfo.CurrentCulture;
            List<Shape> saved = new List<Shape>(cv.Shapes);
            ScaleInfo sc = cv.Scale;      // 100 px = 1 m
            cv.Shapes.Clear();

            // coordinates: origin at (100,350), Y counts upwards
            Shape org = Mk(cv, Tool.Coord, 100, 350); org.Kind = ShapeKind.Origin;
            Shape c1 = Mk(cv, Tool.Coord, 500, 100, 540, 70);
            Shape c2 = Mk(cv, Tool.Coord, 50, 400, 90, 370);
            check("Coordinate X / Y (Y up)", sc.Coord(c1.Pts[0], org.Pts[0]) + " | " + sc.Coord(c2.Pts[0], org.Pts[0]),
                  "X " + (4.0).ToString("N2", cc) + "   Y " + (2.5).ToString("N2", cc) + " m | X " + (-0.5).ToString("N2", cc) + "   Y " + (-0.5).ToString("N2", cc) + " m");
            cv.MakeOrigin(c2);
            check("New origin moves every point", sc.Coord(c1.Pts[0], Renderer.Origin(cv.Shapes).Pts[0]),
                  "X " + (4.5).ToString("N2", cc) + "   Y " + (3.0).ToString("N2", cc) + " m");
            string xl = Measure.ExcelText(cv.Shapes, sc);
            check("Coordinates in Excel", xl.Contains("C1\tPoint\t\tm\t\t\t\t\t" + (4.5).ToString("F2", cc) + "\t" + (3.0).ToString("F2", cc)).ToString(), "True");
            Shape moved = c1.Clone();
            ShapeOps.DragHandle(moved, c1, new Handle(c1.Pts[0], HKind.Point, 0), new PointF(510, 110), false);
            check("Label follows the point", P(moved.Pts[1]), "550.00,80.00");
            List<Shape> rt = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { org, c1 }));
            check("Coordinates round-trip", rt.Count + "|" + rt[0].Kind + "|" + rt[1].Kind + "|" + rt[1].Pts.Count, "2|Origin|Coord|2");

            // line styles, cloud callout, highlight forms, text box, perimeter, freehand arrows
            Shape rc = Mk(cv, Tool.Rect, 120, 120, 300, 220); rc.Dash = (int)LineStyle.Cloud;
            Shape el = Mk(cv, Tool.Ellipse, 320, 120, 480, 220); el.Dash = (int)LineStyle.Dashed;
            Shape cl = Mk(cv, Tool.Cloud, 520, 140, 700, 260, 780, 320); cl.Mode = (int)CloudCallout.Triangle; cl.Text = "3";
            Shape cl2 = Mk(cv, Tool.Cloud, 120, 420, 300, 520, 380, 560); cl2.Mode = (int)CloudCallout.TextBox; cl2.Text = "Changed bracket";
            Shape hp = Mk(cv, Tool.Highlight, 600, 380, 760, 500); hp.Mode = (int)HighlightMode.Pixelate; hp.Form = (int)HighlightForm.Ellipse;
            Shape ht = Mk(cv, Tool.Highlight, 780, 380, 880, 500); ht.Form = (int)HighlightForm.Triangle; ht.Mode = (int)HighlightMode.Marker;
            Shape tx = Mk(cv, Tool.Text, 330, 260, 470, 260); tx.Text = "A long note that has to wrap inside the box"; tx.Frame = true; tx.Width = 2;
            Shape fh = Mk(cv, Tool.Freehand, 20, 560, 60, 540, 100, 560, 140, 545); fh.HeadEnd = (int)HeadStyle.Filled;
            Shape po = Mk(cv, Tool.Perimeter, 600, 520, 700, 520, 700, 580);
            check("Perimeter open by default", po.IsOpenPath + "|" + Measure.Rows(cv.Shapes, sc, false).Find(r => r.Shape == po).Type, "True|Path length");
            po.Mode = 0;
            check("Perimeter closed", po.IsOpenPath + "|" + Measure.Rows(cv.Shapes, sc, false).Find(r => r.Shape == po).Type, "False|Perimeter");
            SizeF ts = Renderer.TextSize(tx);
            check("Text box wraps at its width", ts.Width.ToString("F0") + "|" + (ts.Height > tx.FontSize * 1.9f), "140|True");
            Shape tx2 = tx.Clone();
            ShapeOps.DragHandle(tx2, tx, new Handle(PointF.Empty, HKind.Box, 3), new PointF(534, 270), false);   // handle sits 4 px outside the text
            check("Text box width handle", Renderer.WrapWidth(tx2).ToString("F0"), "200");
            string err = "ok";
            try { using (Bitmap f = cv.Flatten(null)) f.Save(Path.Combine(dir, "selftest_v12.png"), ImageFormat.Png); } catch (Exception ex) { err = ex.Message; }
            check("1.2 shapes draw", err, "ok");
            check("Callout hit test", ShapeOps.Hit(cl, new PointF(780, 318), 2) + "|" + ShapeOps.Hit(cl2, new PointF(380, 560), 2), "True|True");
            List<Shape> rt2 = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { rc, cl, hp, tx, fh }));
            check("Styles round-trip", rt2[0].Dash + "|" + rt2[1].Mode + "/" + rt2[1].Text + "/" + rt2[1].Pts.Count + "|" + rt2[2].Mode + "/" + rt2[2].Form + "|" +
                  rt2[3].Frame + "/" + Renderer.WrapWidth(rt2[3]).ToString("F0") + "|" + rt2[4].HeadEnd, "5|1/3/3|4/1|True/140|1");
            List<Shape> old = ProjectFile.ShapesFromXml("<shapes><shape kind=\"Freehand\" stroke=\"FFFF0000\" fill=\"FFFFFF00\" heads=\"1\" headEnd=\"1\"><p x=\"1\" y=\"1\"/><p x=\"50\" y=\"9\"/></shape></shapes>");
            check("Older freehand gets no arrow", old[0].HeadStart + "/" + old[0].HeadEnd, "0/0");

            // a 2-point open path and a custom stamp must survive save / copy-paste
            Shape po2 = Mk(cv, Tool.Perimeter, 600, 600, 760, 600); po2.Mode = 1;
            List<Shape> rt3 = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { po2 }));
            check("2-point open path round-trip", rt3.Count + "|" + (rt3.Count > 0 ? rt3[0].Kind + "/" + rt3[0].Mode + "/" + rt3[0].Pts.Count : "-"), "1|Perimeter/1/2");
            Shape stc = Mk(cv, Tool.Stamp, 100, 600, 260, 660); stc.Mode = Stamps.Custom; stc.Text = "MY STAMP";
            List<Shape> rt4 = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { stc }));
            check("Custom stamp round-trip", rt4.Count + "|" + (rt4.Count > 0 ? (rt4[0].Mode == Stamps.Custom) + "/" + rt4[0].Text : "-"), "1|True/MY STAMP");

            // a capture saved straight to disk (Settings → Capture → save, without the editor)
            using (Bitmap b = new Bitmap(64, 40))
            {
                using (Graphics g = Graphics.FromImage(b)) g.Clear(Color.SteelBlue);
                string res = "";
                foreach (string ext in new string[] { "png", "jpg", "pdf" })
                {
                    string p = Path.Combine(dir, "capture_direct." + ext);
                    Output.SaveBitmap(b, p);
                    res += ext + "=" + (File.Exists(p) && new FileInfo(p).Length > 50) + " ";
                }
                check("Save capture without the editor", res.Trim(), "png=True jpg=True pdf=True");
            }

            cv.Shapes.Clear();
            cv.Shapes.AddRange(saved);
            cv.Selected = null;
        }

        /// <summary>Checks for the 1.1 features: line ends, quantities, cut-outs, counts, presets, notes, stitching, PDF, OCR, update check.</summary>
        static void V11(Canvas cv, string dir, Action<string, string, string> check)
        {
            CultureInfo cc = CultureInfo.CurrentCulture, inv = CultureInfo.InvariantCulture;
            List<Shape> saved = new List<Shape>(cv.Shapes);
            ScaleInfo sc = cv.Scale;      // 100 px = 1 m

            // line ends
            Shape ar = Mk(cv, Tool.Arrow, 20, 20, 300, 20);
            ar.HeadStart = (int)HeadStyle.Slash; ar.HeadEnd = (int)HeadStyle.Dot;
            Shape dm = Mk(cv, Tool.Dimension, 20, 60, 300, 60, 160, 50);
            dm.HeadStart = dm.HeadEnd = (int)HeadStyle.Tick;
            List<Shape> rt = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { ar, dm }));
            check("Line ends round-trip", rt[0].HeadStart + "/" + rt[0].HeadEnd + "|" + rt[1].HeadStart + "/" + rt[1].HeadEnd, "4/6|5/5");
            List<Shape> old = ProjectFile.ShapesFromXml("<shapes><shape kind=\"Arrow\" stroke=\"FFFF0000\" fill=\"FFFFFF00\" heads=\"3\"><p x=\"1\" y=\"1\"/><p x=\"50\" y=\"1\"/></shape>" +
                                                         "<shape kind=\"Dimension\" stroke=\"FFFF0000\" fill=\"FFFFFF00\"><p x=\"1\" y=\"1\"/><p x=\"50\" y=\"1\"/></shape></shapes>");
            check("1.0 files: heads kept", old[0].HeadStart + "/" + old[0].HeadEnd + "|" + old[1].HeadStart + "/" + old[1].HeadEnd, "1/1|1/1");
            // every end style draws without trouble
            string bad = "";
            using (Bitmap b = new Bitmap(200, 40))
            using (Graphics g = Graphics.FromImage(b))
                for (int st = 0; st <= 9; st++)
                    try { Renderer.DrawHead(g, new PointF(100, 20), new PointF(1, 0), st, Color.Red, 3, 30, 16); } catch (Exception ex) { bad += st + ":" + ex.GetType().Name + " "; }
            check("All 10 line ends draw", bad == "" ? "ok" : bad, "ok");

            // path length, perimeter
            Shape pl = Mk(cv, Tool.Polyline, 100, 380, 400, 380, 400, 780);
            check("Path length (3 + 4 m)", sc.Len(Geo.Perimeter(pl.Pts, false)), (7.0).ToString("N2", cc) + " m");
            Shape pm = Mk(cv, Tool.Perimeter, 600, 100, 700, 100, 700, 200, 600, 200);
            pm.Mode = 0;    // closed
            check("Perimeter (4 × 1 m)", sc.Len(Geo.Perimeter(pm.Pts, true)), (4.0).ToString("N2", cc) + " m");
            Dictionary<Shape, string> names = Renderer.Labels(cv.Shapes, sc);
            check("Path / perimeter names", names[pl] + "|" + names[pm], "L1|P1");

            // area with a cut-out: 4 × 2.5 m minus 1 × 1 m
            Shape area = Mk(cv, Tool.Area, 100, 100, 500, 100, 500, 350, 100, 350);
            area.Text = "Spandrel";
            area.Holes = new List<List<PointF>> { new List<PointF> { new PointF(200, 150), new PointF(300, 150), new PointF(300, 250), new PointF(200, 250) } };
            check("Net area with cut-out", sc.Area(Measure.NetArea(area)) + "|" + sc.Area(Measure.GrossArea(area)) + "|" + sc.Area(Measure.HolesArea(area)),
                  (9.0).ToString("N2", cc) + " m²|" + (10.0).ToString("N2", cc) + " m²|" + (1.0).ToString("N2", cc) + " m²");
            Shape moved = area.Clone(); moved.Offset(10, 5);
            check("Cut-out moves with the area", P(moved.Holes[0][0]) + "|" + P(area.Holes[0][0]), "210.00,155.00|200.00,150.00");
            List<Shape> rta = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { area }));
            check("Cut-out round-trip", rta.Count + "|" + (rta[0].Holes != null ? rta[0].Holes.Count + "|" + sc.Area(Measure.NetArea(rta[0])) : "none"), "1|1|" + (9.0).ToString("N2", cc) + " m²");

            // a polygon with a hollow (1.3.1): the hole is kept, saved and left unfilled
            {
                Shape poly = new Shape { Kind = ShapeKind.Polygon, Fill = Color.Black, FillOpacity = 100, StrokeOpacity = 0 };
                poly.Pts.AddRange(new[] { new PointF(10, 10), new PointF(90, 10), new PointF(90, 90), new PointF(10, 90) });
                poly.Holes = new List<List<PointF>> { new List<PointF> { new PointF(30, 30), new PointF(70, 30), new PointF(70, 70), new PointF(30, 70) } };
                List<Shape> rtp = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { poly }));
                string px;
                using (Bitmap hb = new Bitmap(100, 100))
                {
                    using (Graphics hg = Graphics.FromImage(hb)) { hg.Clear(Color.White); Renderer.Draw(hg, rtp[0], hb, sc, null); }
                    px = hb.GetPixel(20, 20).R + "/" + hb.GetPixel(50, 50).R;
                }
                check("Polygon hollow: kept and unfilled", (rtp[0].Holes != null ? rtp[0].Holes.Count : 0) + "|" + Canvas.CanHaveHoles(poly) + "|" + px, "1|True|0/255");
            }
            // area Show: None hides the label, the area is still listed
            {
                Shape an = area.Clone(); an.Mode = 3;
                using (Bitmap hb = new Bitmap(600, 400)) using (Graphics hg = Graphics.FromImage(hb)) Renderer.Draw(hg, an, hb, sc, "A9");
                List<MeasureRow> nrow = Measure.Rows(new List<Shape> { an }, sc, false);
                check("Area Show: None", an.LabelRect.IsEmpty + "|" + nrow.Count + "|" + (nrow.Count > 0 ? nrow[0].Type : "-"), "True|1|Area (net)");
            }

            // counts, labels and the Excel export
            int numbers = Measure.Count(cv.Shapes, false);
            sc.NumberLabel = "Blind panel";
            List<MeasureRow> rows = Measure.Rows(cv.Shapes, sc, true);
            MeasureRow cnt = rows.Find(r => r.Type == "Count");
            check("Marker count row", cnt != null ? cnt.Name + "|" + cnt.Value + "|" + cnt.Label : "none", "Numbers|" + numbers + "|Blind panel");
            string xl = Measure.ExcelText(cv.Shapes, sc);
            string netTxt = (9.0).ToString("F2", cc), grossTxt = (10.0).ToString("F2", cc), cutTxt = (1.0).ToString("F2", cc);
            check("Excel: header, net area, counts",
                  xl.StartsWith("Name\tType\tValue\tUnit\tPerimeter\tGross\tCut-outs\tLabel").ToString() + "|" +
                  xl.Contains("\tArea (net)\t" + netTxt + "\tm²\t") + "|" + xl.Contains("\t" + grossTxt + "\t" + cutTxt + "\tSpandrel") + "|" +
                  xl.Contains("Numbers\tCount\t" + numbers + "\tnr\t\t\t\tBlind panel") + "|" + xl.Contains("L1\tPath length\t" + (7.0).ToString("F2", cc)),
                  "True|True|True|True|True");
            ScaleInfo sc3 = new ScaleInfo();
            string p3 = Path.Combine(dir, "selftest_v11.snapm");
            ProjectFile.Save(p3, cv.Image, cv.Shapes, sc);
            using (Bitmap i3 = ProjectFile.Load(p3, new List<Shape>(), sc3)) { }
            check("Series label saved", sc3.NumberLabel, "Blind panel");
            sc.NumberLabel = "";

            // non-printing note
            Shape note = Mk(cv, Tool.Note, 820, 520);
            note.Text = "Check bracket type with the engineer";
            RectangleF nr = Renderer.NoteRect(note);
            Point nc = Point.Round(Geo.Center(nr));
            Color with, without;
            using (Bitmap f1 = cv.Flatten(Color.White)) with = f1.GetPixel(nc.X, nc.Y);
            cv.Shapes.Remove(note);
            using (Bitmap f2 = cv.Flatten(Color.White)) without = f2.GetPixel(nc.X, nc.Y);
            cv.Shapes.Add(note);
            check("Note left out of saved pictures", (with.ToArgb() == without.ToArgb()).ToString(), "True");
            check("Note in the Excel export", Measure.ExcelText(cv.Shapes, sc).Contains("Note\tNote\t\t\t\t\t\tCheck bracket type with the engineer").ToString(), "True");

            // markup, cloud, stamp
            Shape cl = Mk(cv, Tool.Cloud, 560, 300, 860, 460);
            Shape stp = Mk(cv, Tool.Stamp, 600, 480, 830, 560); stp.Text = Canvas.StampText(1, "");
            Shape mk1 = Mk(cv, Tool.Markup, 108, 515, 330, 545);
            Shape mk2 = Mk(cv, Tool.Markup, 108, 465, 330, 500); mk2.Mode = (int)MarkupMode.StrikeOut;
            check("Stamp text", stp.Text.Split('\n')[0] + "|" + stp.Text.Split('\n')[1].Length, "REVISE|10");
            string err = "ok";
            try { using (Bitmap f3 = cv.Flatten(null)) f3.Save(Path.Combine(dir, "selftest_v11.png"), ImageFormat.Png); } catch (Exception ex) { err = ex.Message; }
            check("Cloud / stamp / markup / path draw", err, "ok");
            check("Stamp and cloud hit test", ShapeOps.Hit(stp, new PointF(700, 520), 2) + "|" + ShapeOps.Hit(cl, new PointF(700, 380), 2), "True|True");

            // drawing-scale presets
            check("1:50, A1 landscape across 841 px", ScalePresets.Upp(ScalePresets.SheetWidthMm(1, true) / 841.0, 50, "m").ToString("G6", inv), "0.05");
            check("1:100 at 300 dpi (mm per px)", ScalePresets.Upp(25.4 / 300, 100, "mm").ToString("F4", inv), "8.4667");
            check("1:20 PDF at 150 dpi (m per px)", ScalePresets.Upp(25.4 / 150, 20, "m").ToString("G6", inv), "0.00338667");

            // update check (offline parts)
            check("Release tag", UpdateCheck.ParseTag("v1.1") + "|" + UpdateCheck.ParseTag("1.0.2") + "|" + (UpdateCheck.ParseTag("latest") == null), "1.1.0|1.0.2|True");
            check("Newer?", UpdateCheck.IsNewer(new Version(1, 1, 0), new Version(1, 0, 1, 0)) + "|" + UpdateCheck.IsNewer(new Version(1, 1, 0), new Version(1, 1, 0, 0)), "True|False");
            check("Tag from GitHub answer", UpdateCheck.TagFromJson("{\"url\":\"x\",\"tag_name\":\"v1.2\",\"name\":\"SnapMeasure 1.2\"}"), "v1.2");

            // scrolling capture: frames cut from a tall page, with a sticky header, join back together
            using (Bitmap page = new Bitmap(320, 1400, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(page))
                {
                    g.Clear(Color.White);
                    Random rnd = new Random(7);
                    for (int y = 0; y < 1400; y += 7)
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)))) g.FillRectangle(b, rnd.Next(0, 200), y, rnd.Next(20, 120), 5);
                }
                List<Bitmap> frames = new List<Bitmap>();
                foreach (int top in new int[] { 0, 260, 520, 780 })
                {
                    Bitmap fr = new Bitmap(320, 400, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(fr))
                    {
                        g.DrawImage(page, new Rectangle(0, 0, 320, 400), new Rectangle(0, top, 320, 400), GraphicsUnit.Pixel);
                        g.FillRectangle(Brushes.SteelBlue, 0, 0, 320, 30);      // sticky header bar
                    }
                    frames.Add(fr);
                }
                using (Bitmap st = ScrollCapture.Stitch(frames))
                {
                    bool same = true;
                    for (int y = 40; y < st.Height && same; y += 13)
                        for (int x = 0; x < 320; x += 17)
                            if (st.GetPixel(x, y).ToArgb() != page.GetPixel(x, y).ToArgb()) { same = false; break; }
                    check("Scrolling capture stitch", st.Width + "x" + st.Height + "|" + same, "320x1180|True");
                }
                foreach (Bitmap fr in frames) fr.Dispose();
            }

            // PDF page import (Windows PDF renderer) – the PDF written by the export check above: 675 × 450 pt
            string pdfPath = Path.Combine(dir, "export.pdf");
            if (File.Exists(pdfPath))
            {
                string res;
                try
                {
                    PdfImport.Info info = PdfImport.Read(pdfPath);
                    double dpi = 72;
                    SizeF mm;
                    using (Bitmap pg = PdfImport.Render(pdfPath, 0, ref dpi, out mm))
                        res = info.Pages + "|" + pg.Width + "x" + pg.Height + "|" + mm.Width.ToString("F1", inv) + "x" + mm.Height.ToString("F1", inv);
                }
                catch (Exception ex) { res = ex.GetType().Name + ": " + ex.Message; }
                check("PDF page import", res, "1|675x450|238.1x158.8");
            }

            // OCR (needs a Windows text-recognition language – there is one on every normal install)
            if (TextRecognition.Available)
                using (Bitmap tb = new Bitmap(700, 120, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(tb))
                    {
                        g.Clear(Color.White);
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        g.DrawString("Blind panel BP-14 RAL 7016", new Font("Segoe UI", 34, FontStyle.Regular, GraphicsUnit.Pixel), Brushes.Black, 20, 30);
                    }
                    string t;
                    int words;
                    try { TextRecognition.Result r = TextRecognition.Read(tb, null); t = r.Text; words = r.Boxes.Count; }
                    catch (Exception ex) { t = "error " + ex.Message; words = 0; }
                    check("OCR reads text", (t.Contains("Blind panel") && t.Contains("7016")) + "|" + (words >= 4), "True|True");
                }
            else check("OCR reads text", "no OCR language installed", "no OCR language installed");

            cv.Shapes.Clear();
            cv.Shapes.AddRange(saved);
            cv.Selected = null;
        }

        static string P(PointF p) { return p.X.ToString("F2", CultureInfo.InvariantCulture) + "," + p.Y.ToString("F2", CultureInfo.InvariantCulture); }

        public static void Run(string dir)
        {
            Directory.CreateDirectory(dir);
            log.Clear();     // the shared log: Main writes it to selftest_error.txt if a check throws
            Action<string, string, string> check = (name, got, want) =>
                log.AppendLine((got == want ? "PASS  " : "FAIL  ") + name + ": got '" + got + "', expected '" + want + "'");
            CultureInfo cc = CultureInfo.CurrentCulture;

            Bitmap img = new Bitmap(900, 600, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(img))
            {
                g.Clear(Color.White);
                using (Pen grid = new Pen(Color.FromArgb(225, 230, 235)))
                {
                    for (int x = 0; x <= 900; x += 50) g.DrawLine(grid, x, 0, x, 600);
                    for (int y = 0; y <= 600; y += 50) g.DrawLine(grid, 0, y, 900, y);
                }
                using (Pen wall = new Pen(Color.Black, 4)) g.DrawRectangle(wall, 100, 100, 400, 250);
                using (Pen wall = new Pen(Color.Black, 3))
                    g.DrawPolygon(wall, new Point[] { new Point(600, 150), new Point(850, 150), new Point(850, 300), new Point(750, 300), new Point(750, 450), new Point(600, 450) });
                g.DrawString("Secret serial: SN-4471-XQ", new Font("Segoe UI", 14), Brushes.DarkBlue, 110, 470);
                g.DrawString("Important note to highlight", new Font("Segoe UI", 12), Brushes.Black, 110, 520);
                g.DrawString("Plan view – grid 50 px", new Font("Segoe UI", 11), Brushes.Gray, 610, 520);
            }

            EditorForm f = new EditorForm(img, "Self test");
            Canvas cv = f.CanvasControl;
            cv.Scale.Upp = 0.01; cv.Scale.Unit = "m"; cv.Scale.Decimals = 2;     // 100 px = 1 m

            Shape dh = Mk(cv, Tool.Dimension, 100, 100, 500, 100, 300, 65);      // 4.00 m
            Shape dv = Mk(cv, Tool.Dimension, 500, 100, 500, 350, 545, 225);     // 2.50 m
            dv.Text = "H =";
            Shape a = Mk(cv, Tool.Area, 100, 100, 500, 100, 500, 350, 100, 350); // 10.00 m², P 13.00 m
            a.Text = "Room 1";
            Shape poly = Mk(cv, Tool.Polygon, 600, 150, 850, 150, 850, 300, 750, 300, 750, 450, 600, 450);
            poly.Shadow = true;
            Shape arrow = Mk(cv, Tool.Arrow, 520, 570, 640, 480); arrow.Shadow = true;
            Shape m1 = Mk(cv, Tool.Counter, 130, 135);
            Shape m2 = Mk(cv, Tool.Counter, 470, 135);
            Shape m3 = Mk(cv, Tool.Counter, 625, 175);
            m3.Shadow = true;
            Mk(cv, Tool.Pixelate, 230, 466, 380, 498);
            Shape hl = Mk(cv, Tool.Highlight, 106, 518, 318, 546); hl.Mode = (int)HighlightMode.Marker;   // text marker (from 1.2 files)
            Shape mag = Mk(cv, Tool.Highlight, 20, 380, 90, 440); mag.Mode = (int)HighlightMode.Magnify;
            Shape rr = Mk(cv, Tool.Rect, 220, 180, 380, 260); rr.Angle = 30; rr.FillOpacity = 25; rr.Shadow = true;
            Shape el = Mk(cv, Tool.Ellipse, 30, 30, 80, 80); el.Fill = Color.Lime; el.FillOpacity = 40;
            Shape txt = Mk(cv, Tool.Text, 640, 470); txt.Text = "Rotated text\nwith shadow"; txt.Angle = -8; txt.Shadow = true;
            Shape bub = Mk(cv, Tool.Bubble, 700, 20, 880, 95, 640, 140); bub.Text = "Check this\ncorner!"; bub.Shadow = true;

            // ---- measurements ----
            check("Horizontal dimension", cv.Scale.Len(Geo.Dist(dh.Pts[0], dh.Pts[1])), (4.0).ToString("N2", cc) + " m");
            check("Vertical dimension", cv.Scale.Len(Geo.Dist(dv.Pts[0], dv.Pts[1])), (2.5).ToString("N2", cc) + " m");
            check("Area", cv.Scale.Area(Math.Abs(Geo.SignedArea(a.Pts))), (10.0).ToString("N2", cc) + " m²");
            check("Perimeter", cv.Scale.Len(Geo.Perimeter(a.Pts, true)), (13.0).ToString("N2", cc) + " m");
            check("L-polygon area (px2)", Math.Abs(Geo.SignedArea(poly.Pts)).ToString("F0"), (250 * 150 + 150 * 150).ToString());
            check("Hit inside polygon", ShapeOps.Hit(poly, new PointF(650, 400), 3).ToString(), "True");
            check("Hit in L-notch (outside)", ShapeOps.Hit(poly, new PointF(800, 400), 3).ToString(), "False");

            // ---- markers renumber + letters ----
            Func<Shape, string> lab = s => Renderer.Labels(cv.Shapes, cv.Scale)[s];
            check("Markers 1-2-3", lab(m1) + lab(m2) + lab(m3), "123");
            cv.Shapes.Remove(m2);
            check("Renumber after delete", lab(m1) + lab(m3), "12");
            cv.Shapes.Insert(cv.Shapes.IndexOf(m3), m2);
            cv.Scale.MarkerStart = 5;
            check("Start at 5", lab(m1) + lab(m2) + lab(m3), "567");
            cv.Scale.MarkerStart = 1;
            // letter markers are a separate series next to the numbers
            Shape l1 = Mk(cv, Tool.Letter, 300, 300), l2 = Mk(cv, Tool.Letter, 340, 300);
            check("Two series: numbers + letters", lab(m1) + lab(l1) + lab(m2) + lab(l2) + lab(m3), "1A2B3");
            cv.Scale.LetterStart = 3;
            check("Letters from C", lab(l1) + lab(l2), "CD");
            cv.Scale.LetterStart = 26;
            check("Letters go on Z-AA", lab(l1) + "," + lab(l2) + "," + ScaleInfo.LetterName(51) + "," + ScaleInfo.LetterName(701) + "," + ScaleInfo.LetterNumber("ab"), "Z,AA,AZ,ZZ,28");
            cv.Scale.LetterStart = 1;
            cv.Shapes.Remove(l1); cv.Shapes.Remove(l2);

            // ---- angle (3 points) ----
            Shape ang = Mk(cv, Tool.Angle, 760, 520, 700, 560, 860, 560);
            check("Angle 3-point", Geo.AngleAt(ang.Pts[0], ang.Pts[1], ang.Pts[2]).ToString("F1", CultureInfo.InvariantCulture), "33.7");
            check("Angle label", lab(ang), "∠1");
            check("Right angle", Geo.AngleAt(new PointF(0, -10), PointF.Empty, new PointF(10, 0)).ToString("F1", CultureInfo.InvariantCulture), "90.0");

            // ---- arrowhead grows with line width ----
            float hl1, hw1, hl2, hw2;
            Geo.HeadSize(1.5f, 1000, out hl1, out hw1); Geo.HeadSize(20f, 1000, out hl2, out hw2);
            check("Arrowhead scales with width", (hl2 / hl1 > 8f).ToString() + "|" + (hw1 / hl1).ToString("F2", CultureInfo.InvariantCulture), "True|0.55");

            // ---- rotation handle + rotated hit test ----
            PointF rc = ShapeOps.Center(rr);
            check("Rotated rect centre", P(rc), "300.00,220.00");
            PointF corner = Geo.Rot(new PointF(380, 260), rc, 30);
            check("Hit rotated corner", ShapeOps.Hit(rr, corner, 2).ToString(), "True");
            check("Miss unrotated corner", ShapeOps.Hit(rr, new PointF(379, 259), 1).ToString(), "False");
            Shape rot = rr.Clone();
            ShapeOps.DragHandle(rot, rr, new Handle(PointF.Empty, HKind.Rotate, 0), new PointF(rc.X + 100, rc.Y), false);
            check("Rotate knob to the right = 90°", rot.Angle.ToString("F1", CultureInfo.InvariantCulture), "90.0");
            Shape cen = el.Clone();
            ShapeOps.DragHandle(cen, el, new Handle(PointF.Empty, HKind.Center, 0), new PointF(500, 350), false);
            check("Move circle by centre", P(ShapeOps.Center(cen)), "500.00,350.00");

            // ---- render ----
            f.Show();
            Application.DoEvents();
            using (Bitmap flat = cv.Flatten(null)) flat.Save(Path.Combine(dir, "selftest_render.png"), ImageFormat.Png);

            // ---- project round-trip ----
            string proj = Path.Combine(dir, "selftest.snapm");
            ProjectFile.Save(proj, cv.Image, cv.Shapes, cv.Scale);
            List<Shape> back = new List<Shape>();
            ScaleInfo sc2 = new ScaleInfo();
            using (Bitmap img2 = ProjectFile.Load(proj, back, sc2))
            {
                check("Project shape count", back.Count.ToString(), cv.Shapes.Count.ToString());
                check("Project scale", sc2.Len(Geo.Dist(back[0].Pts[0], back[0].Pts[1])), cv.Scale.Len(400));
                check("Project image size", img2.Width + "x" + img2.Height, "900x600");
                int ri = cv.Shapes.IndexOf(rr), bi = cv.Shapes.IndexOf(bub);
                check("Project angle/shadow/bubble", back[ri].Angle + "|" + back[ri].Shadow + "|" + back[bi].Text.Replace("\n", "/") + "|" + back[bi].Pts.Count,
                      "30|True|Check this/corner!|3");
            }

            // ---- image rotate / mirror round trips ----
            string d0 = P(dh.Pts[0]) + " " + P(dh.Pts[1]), r0 = P(ShapeOps.Center(rr));
            cv.ImageOp("rotcw");
            check("Rotate right: size", cv.Image.Width + "x" + cv.Image.Height, "600x900");
            int ir = 0; for (int i = 0; i < cv.Shapes.Count; i++) if (cv.Shapes[i].Kind == ShapeKind.Rect) ir = i;
            check("Rotate right: shape angle", cv.Shapes[ir].Angle.ToString("F0"), "120");
            check("Rotate right: dimension length", cv.Scale.Len(Geo.Dist(cv.Shapes[0].Pts[0], cv.Shapes[0].Pts[1])), (4.0).ToString("N2", cc) + " m");
            using (Bitmap flat = cv.Flatten(null)) flat.Save(Path.Combine(dir, "selftest_rotated.png"), ImageFormat.Png);
            cv.ImageOp("rotccw");
            check("Rotate back: dimension points", P(cv.Shapes[0].Pts[0]) + " " + P(cv.Shapes[0].Pts[1]), d0);
            check("Rotate back: rect centre", P(ShapeOps.Center(cv.Shapes[ir])), r0);
            cv.ImageOp("fliph"); cv.ImageOp("fliph");
            check("Mirror twice: unchanged", P(cv.Shapes[0].Pts[0]) + " " + P(ShapeOps.Center(cv.Shapes[ir])) + " " + cv.Shapes[ir].Angle.ToString("F0"),
                  P(dh.Pts[0]) + " " + r0 + " 30");

            // ---- effects ----
            cv.ImageOp("border");
            check("Border adds 4 px each side", cv.Image.Width + "x" + cv.Image.Height, "908x608");
            check("Border moves annotations", P(cv.Shapes[0].Pts[0]), "104.00,104.00");
            cv.ImageOp("torn");
            cv.ImageOp("shadow");
            check("Drop shadow canvas", cv.Image.Width + "x" + cv.Image.Height, (908 + Effects.ShadowOffset + Effects.ShadowBlur) + "x" + (608 + Effects.ShadowOffset + Effects.ShadowBlur));
            using (Bitmap flat = cv.Flatten(null)) flat.Save(Path.Combine(dir, "selftest_effects.png"), ImageFormat.Png);
            cv.Undo(); cv.Undo(); cv.Undo();
            check("Undo effects", cv.Image.Width + "x" + cv.Image.Height, "900x600");

            // undo / redo over picture changes must never reach a freed (disposed) picture
            string ur;
            try
            {
                cv.ImageOp("rotcw"); cv.ImageOp("border");
                cv.Undo(); cv.Undo(); cv.Redo(); cv.Redo();
                using (Bitmap fl = cv.Flatten(null)) { }
                string a1 = cv.Image.Width + "x" + cv.Image.Height;
                cv.Undo(); cv.Undo();
                cv.ImageOp("gray");                                   // a new step drops the redo pictures
                cv.Undo();
                for (int i = 0; i < 305; i++) cv.PushUndo(cv.Snapshot());   // past the 300-step limit
                for (int i = 0; i < 5; i++) cv.Undo();
                cv.Redo();
                using (Bitmap fl = cv.Flatten(null)) { }
                ur = a1 + "|" + cv.Image.Width + "x" + cv.Image.Height;
            }
            catch (Exception ex) { ur = ex.GetType().Name + ": " + ex.Message; }
            check("Undo / redo after rotate + border", ur, "608x908|900x600");

            // a bubble started near the bottom-right corner is pulled fully inside the image
            {
                RectangleF br = new RectangleF(850, 580, 170, 70);
                PointF bt = Canvas.FitBubble(ref br, new RectangleF(0, 0, 900, 600));
                bool inside = br.X >= 0 && br.Y >= 0 && br.Right <= 900 && br.Bottom <= 600 && bt.X >= 0 && bt.X <= 900 && bt.Y >= 0 && bt.Y <= 600;
                check("Bubble kept inside image", inside ? "inside" : (br + " tail " + bt), "inside");
            }

            // undo keeps the edited shape selected, so the style panel shows its restored values
            {
                Shape hs = Mk(cv, Tool.Highlight, 10, 10, 100, 40); hs.Mode = (int)HighlightMode.Marker;
                cv.Shapes.Add(hs); cv.Selected = hs;
                cv.PushUndo(cv.Snapshot()); hs.Mode = (int)HighlightMode.Spotlight;
                cv.Undo();
                check("Undo keeps selection", cv.Selected != null ? ((HighlightMode)cv.Selected.Mode).ToString() : "none", "Marker");
                cv.Shapes.Remove(cv.Selected); cv.Selected = null;
            }

            // ---- multi-select, clipboard-free duplicate, draw order ----
            {
                int n0 = cv.Shapes.Count;
                cv.SelectAll();
                check("Select all", cv.Sel.Count.ToString(), n0.ToString());
                // (image operations above replaced the shapes with copies, so pick the current markers)
                List<Shape> marks = cv.Shapes.FindAll(x => x.Kind == ShapeKind.Counter);
                cv.Selected = marks[0];
                cv.Sel.Add(marks[1]);
                cv.DuplicateSelection();
                check("Duplicate 2 shapes", (cv.Shapes.Count - n0) + "|" + cv.Sel.Count + "|" + P(cv.Sel[0].Pts[0]), "2|2|" + P(new PointF(marks[0].Pts[0].X + 12, marks[0].Pts[0].Y + 12)));
                Shape dup = cv.Sel[0];
                cv.Selected = dup;
                cv.Order(3);
                check("Send to back", (cv.Shapes.IndexOf(dup) == 0).ToString(), "True");
                cv.Order(0);
                check("Bring to front", (cv.Shapes.IndexOf(dup) == cv.Shapes.Count - 1).ToString(), "True");
                cv.Undo(); cv.Undo(); cv.Undo();
                check("Undo duplicate + order", cv.Shapes.Count.ToString(), n0.ToString());
                string xml = ProjectFile.ShapesToXml(new Shape[] { txt, bub });
                List<Shape> back2 = ProjectFile.ShapesFromXml(xml);
                check("Shape XML round-trip", back2.Count + "|" + back2[1].Text.Replace("\n", "/") + "|" + back2[1].Align, "2|Check this/corner!|1");
                cv.Selected = null;
            }

            // ---- arrowheads, pasted pictures ----
            {
                Shape ln = Mk(cv, Tool.Line, 10, 10, 200, 10);
                Shape ar = Mk(cv, Tool.Arrow, 10, 40, 200, 40);
                check("Default heads: line none, arrow end", ln.Heads + "|" + ar.Heads, "0|1");
                ar.Heads = 3;
                using (Bitmap pic = new Bitmap(40, 20)) { using (Graphics pg = Graphics.FromImage(pic)) pg.Clear(Color.Teal); cv.InsertImage((Bitmap)pic.Clone(), new PointF(450, 300)); }
                Shape im = cv.Selected;
                check("Picture inserted", im.Kind + "|" + P(ShapeOps.Center(im)), "Image|450.00,300.00");
                Shape drag = im.Clone();
                ShapeOps.DragHandle(drag, im, new Handle(PointF.Empty, HKind.Box, 4), new PointF(530, 400), true);
                RectangleF rr2 = Geo.Rect(drag.Pts[0], drag.Pts[1]);
                check("Picture corner + Shift keeps proportions", (rr2.Width / rr2.Height).ToString("F2", CultureInfo.InvariantCulture), "2.00");
                Shape drag2 = im.Clone();
                ShapeOps.DragHandle(drag2, im, new Handle(PointF.Empty, HKind.Box, 4), new PointF(530, 400), false);
                RectangleF rr3 = Geo.Rect(drag2.Pts[0], drag2.Pts[1]);
                check("Picture corner stretches freely", rr3.Width.ToString("F0", CultureInfo.InvariantCulture) + "x" + rr3.Height.ToString("F0", CultureInfo.InvariantCulture), "100x110");
                List<Shape> back3 = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { ar, im }));
                check("Heads + picture round-trip", back3.Count + "|" + back3[0].Heads + "|" + (back3[1].Img != null ? back3[1].Img.Width + "x" + back3[1].Img.Height : "none"), "2|3|40x20");
                using (Bitmap flat = cv.Flatten(null)) flat.Save(Path.Combine(dir, "selftest_heads.png"), ImageFormat.Png);
                cv.Shapes.Remove(ln); cv.Shapes.Remove(ar); cv.Shapes.Remove(im); cv.Selected = null;
            }

            // ---- 1.3.2: rounding, radius labels, reverse markers, project preview, language table ----
            {
                Func<double, string> inv = v => v.ToString("0.######", CultureInfo.InvariantCulture);
                check("Ceil rounds up", inv(ScaleInfo.RoundTo(1.231, 2, 1)), "1.24");
                check("Floor rounds down", inv(ScaleInfo.RoundTo(1.239, 2, 2)), "1.23");
                check("Ceil ignores float noise", inv(ScaleInfo.RoundTo(0.1 * 3, 1, 1)) + "|" + inv(ScaleInfo.RoundTo(2.3, 2, 1)), "0.3|2.3");
                check("Floor ignores float noise", inv(ScaleInfo.RoundTo(0.7 + 0.1, 1, 2)), "0.8");
                check("Ceil of a negative, no -0", inv(ScaleInfo.RoundTo(-1.231, 2, 1)) + "|" + inv(ScaleInfo.RoundTo(-0.004, 2, 1)), "-1.23|0");
                check("Nearest unchanged", inv(ScaleInfo.RoundTo(1.23456, 2, 0)), "1.23456");

                ScaleInfo rs = new ScaleInfo();
                rs.Unit = "mm"; rs.Upp = 1; rs.Decimals = 1;
                check("Radius label: diameter", Renderer.RadiusText(2, rs, 10f, 31.4159).Replace("\n", " | "), "Ø = " + (20.0).ToString("N1") + " mm");
                check("Radius label: diameter + arc", Renderer.RadiusText(3, rs, 10f, 31.4159).Replace("\n", " | "), "Ø = " + (20.0).ToString("N1") + " mm | Arc = " + (31.4).ToString("N1") + " mm");
                check("Radius label: radius + arc", Renderer.RadiusText(4, rs, 10f, 31.4159).Replace("\n", " | "), "R = " + (10.0).ToString("N1") + " mm | Arc = " + (31.4).ToString("N1") + " mm");
                check("Radius label: none", Renderer.RadiusText(5, rs, 10f, 31.4159), "");
                rs.Rounding = 1;
                check("Radius label rounds up", Renderer.RadiusText(1, rs, 10.01f, 0), "R = " + (10.1).ToString("N1") + " mm");

                ScaleInfo mk = new ScaleInfo();
                mk.LetterStart = 26; mk.LetterReverse = true;
                check("Letters count down Z→A, then wrap", mk.MarkerLabel(0, true) + mk.MarkerLabel(1, true) + mk.MarkerLabel(25, true) + mk.MarkerLabel(26, true), "ZYAZ");
                mk.MarkerStart = 10; mk.MarkerReverse = true;
                check("Numbers count down 10→1, never below 1", mk.MarkerLabel(0, false) + "," + mk.MarkerLabel(9, false) + "," + mk.MarkerLabel(12, false), "10,1,1");
                mk.LetterReverse = mk.MarkerReverse = false; mk.LetterStart = 1; mk.MarkerStart = 1;
                check("Markers count up as before", mk.MarkerLabel(0, true) + mk.MarkerLabel(27, true) + "," + mk.MarkerLabel(4, false), "AAB,5");

                // project keeps the new settings, and stores a preview first in the file
                string pj = Path.Combine(dir, "selftest_132.snapm");
                ScaleInfo ps = new ScaleInfo(); ps.Rounding = 2; ps.LetterReverse = true; ps.MarkerReverse = true; ps.LetterStart = 26; ps.MarkerStart = 9;
                Shape pr = new Shape(); pr.Kind = ShapeKind.Rect; pr.Pts.Add(new PointF(10, 10)); pr.Pts.Add(new PointF(200, 120)); pr.Width = 6;
                using (Bitmap pimg = new Bitmap(1600, 900, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(pimg)) g.Clear(Color.SteelBlue);
                    ProjectFile.Save(pj, pimg, new List<Shape> { pr }, ps);
                }
                ScaleInfo back132 = new ScaleInfo();
                List<Shape> backShapes = new List<Shape>();
                using (Bitmap bimg = ProjectFile.Load(pj, backShapes, back132)) { }
                check("Project keeps rounding + reverse", back132.Rounding + "|" + back132.LetterReverse + "|" + back132.MarkerReverse + "|" + back132.LetterStart + "|" + back132.MarkerStart + "|" + backShapes.Count,
                      "2|True|True|26|9|1");
                string firstChild = "";
                using (XmlReader xr = XmlReader.Create(pj))
                {
                    xr.ReadToFollowing("snapmeasure");
                    while (xr.Read()) if (xr.NodeType == XmlNodeType.Element) { firstChild = xr.Name; break; }
                }
                check("Preview stored before the picture", firstChild, "preview");
                using (FileStream fs = File.OpenRead(pj))
                using (Bitmap pv = ProjectFile.ReadPreview(fs))
                {
                    Color mid = pv.GetPixel(pv.Width / 2, pv.Height / 2), edge = pv.GetPixel(Math.Min(pv.Width - 1, (int)(12 * pv.Width / 1600f)), Math.Min(pv.Height - 1, (int)(60 * pv.Height / 900f)));
                    check("Preview size and markups", pv.Width + "x" + pv.Height + "|" + (mid.B > 100 && mid.R < 120) + "|" + (edge.R > 150 && edge.B < 100),
                          ProjectFile.PreviewSize + "x" + (ProjectFile.PreviewSize * 900 / 1600) + "|True|True");
                    pv.Save(Path.Combine(dir, "selftest_preview.png"), ImageFormat.Png);
                }

                // the Korean table loads and translates, keys in brackets kept; English stays as written
                string was = SnapMeasure.Settings.Get("Language", "en");
                SnapMeasure.Settings.Set("Language", "ko");
                string k1 = Lang.T("Rectangle (R)"), k2 = Lang.T("Save as…"), k3 = Lang.T("no such text"), f1 = Lang.UiFamily;
                SnapMeasure.Settings.Set("Language", "en");
                string e1 = Lang.T("Rectangle (R)"), f2 = Lang.UiFamily;
                SnapMeasure.Settings.Set("Language", was);
                check("Korean table", k1 + "|" + k2 + "|" + k3 + "|" + f1, "사각형 (R)|다른 이름으로 저장…|no such text|Malgun Gothic");
                check("English unchanged", e1 + "|" + f2, "Rectangle (R)|Segoe UI");
                ScaleInfo sa = new ScaleInfo(); sa.Unit = "m"; sa.Upp = 0.01; sa.Decimals = 1;
                string arcOnly = Renderer.RadiusText(6, sa, 100f, 314.159);
                sa.Rounding = 1; string angUp = sa.Angle(33.69);
                sa.Rounding = 2; string angDown = sa.Angle(33.69);
                sa.Rounding = 0; sa.Decimals = 0; string angNear = sa.Angle(126.7);
                check("Arc length only + angle decimals/rounding", arcOnly + "|" + angUp + "|" + angDown + "|" + angNear,
                      "Arc = " + (3.1).ToString("N1") + " m|" + (33.7).ToString("N1") + "°|" + (33.6).ToString("N1") + "°|127°");
                PointF arm = Canvas.SnapArm(new PointF(0, 0), new PointF(10, 0), new PointF(3.09f, 9.51f), 45);   // 72° → 90°
                PointF arm2 = Canvas.SnapArm(new PointF(0, 0), new PointF(10, 10), new PointF(10, 1), 45);       // 45° arm, p at 5.7° → 0°
                check("Angle Shift snaps to 45° steps", Math.Round(Geo.AngleAt(new PointF(10, 0), new PointF(0, 0), arm), 3) + "|" +
                      Math.Round(Geo.AngleAt(new PointF(10, 10), new PointF(0, 0), arm2), 3) + "|" + Math.Round(Geo.Dist(new PointF(0, 0), arm), 2), "90|45|10");
                check("CAD apps get the crosshair step", ScreenCapture.IsCadApp("acadlt") + "|" + ScreenCapture.IsCadApp("ACAD") + "|" + ScreenCapture.IsCadApp("chrome") + "|" + ScreenCapture.IsCadApp(null), "True|True|False|False");
                // pixelate: every block solid and opaque, edges included (the outer blocks used to be half see-through)
                using (Bitmap pz = new Bitmap(100, 60, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(pz)) { g.Clear(Color.White); g.FillRectangle(Brushes.Black, 0, 0, 4, 60); g.FillRectangle(Brushes.Black, 96, 0, 4, 60); }
                    using (Bitmap bl = Renderer.Blocks(pz, new Rectangle(0, 0, 100, 60), 40))
                    {
                        Color pa = bl.GetPixel(0, 0), pb = bl.GetPixel(39, 39), pc = bl.GetPixel(99, 59), pd = bl.GetPixel(80, 40);
                        check("Pixelate edge blocks are solid", pa.A + "|" + (pa == pb) + "|" + pc.A + "|" + (pc == pd) + "|" + pa.R, "255|True|255|True|229");
                    }
                }
                Shape mkS = new Shape(); mkS.Kind = ShapeKind.Counter; mkS.FontSize = 18; mkS.Pts.Add(new PointF(100, 100));
                Shape mkD = mkS.Clone();
                ShapeOps.DragHandle(mkD, mkS, new Handle(PointF.Empty, HKind.Box, 4), new PointF(127, 118), false);
                check("Marker stretches by its corner", ShapeOps.Handles(mkS, 20).Count + "|" + mkD.FontSize.ToString("F0", CultureInfo.InvariantCulture), "4|30");
                check("Title drops file extension", Output.MakeName("${title}", DateTime.Now, "plan A.snapm", 1) + "|" + Output.MakeName("${title}", DateTime.Now, "photo.JPG", 1) + "|" + Output.MakeName("${title}", DateTime.Now, "Chrome", 1), "plan A|photo|Chrome");
            }

            // ---- security: hostile project files and clipboard data ----
            {
                string evil = Path.Combine(dir, "evil.snapm");
                string secret = Path.Combine(dir, "secret.txt");
                File.WriteAllText(secret, "TOPSECRET");
                File.WriteAllText(evil, "<?xml version=\"1.0\"?><!DOCTYPE s [<!ENTITY x SYSTEM \"file:///" + secret.Replace('\\', '/') + "\">]>" +
                                        "<snapmeasure unit=\"px\"><image>&x;</image></snapmeasure>");
                string res;
                try { ProjectFile.Load(evil, new List<Shape>(), new ScaleInfo()); res = "loaded"; }
                catch (XmlException) { res = "blocked"; }
                catch (Exception ex) { res = ex.Message.Contains("TOPSECRET") ? "LEAKED" : "blocked"; }
                check("XXE / DTD blocked", res, "blocked");

                string bad = "<shapes>" +
                    "<shape kind=\"Rect\" stroke=\"FFFF0000\" fill=\"FFFFFF00\" width=\"NaN\" font=\"1e30\"><p x=\"1\" y=\"1\"/><p x=\"NaN\" y=\"5\"/></shape>" +   // NaN point -> dropped
                    "<shape kind=\"Dimension\" stroke=\"FFFF0000\" fill=\"FFFFFF00\"><p x=\"1\" y=\"1\"/></shape>" +                                          // too few points -> dropped
                    "<shape kind=\"99\" stroke=\"FFFF0000\" fill=\"FFFFFF00\"><p x=\"1\" y=\"1\"/><p x=\"2\" y=\"2\"/></shape>" +                            // unknown kind -> dropped
                    "<shape kind=\"Rect\" stroke=\"zz\" fill=\"FFFFFF00\"><p x=\"1\" y=\"1\"/><p x=\"2\" y=\"2\"/></shape>" +                                 // bad colour -> dropped
                    "<shape kind=\"Line\" stroke=\"FFFF0000\" fill=\"FFFFFF00\" width=\"1e9\" heads=\"77\"><p x=\"1e30\" y=\"1\"/><p x=\"2\" y=\"2\"/></shape>" + // clamped
                    "<shape kind=\"Image\" stroke=\"FFFF0000\" fill=\"FFFFFF00\"><p x=\"1\" y=\"1\"/><p x=\"2\" y=\"2\"/><img>bm90IGFuIGltYWdl</img></shape>" + // not an image -> dropped
                    "</shapes>";
                List<Shape> got = ProjectFile.ShapesFromXml(bad);
                check("Damaged shapes skipped, rest clamped", got.Count + "|" + (got.Count > 0 ? got[0].Width + "|" + got[0].Heads + "|" + got[0].Pts[0].X : ""),
                      "1|200|1|1000000");
                string err;
                try { ProjectFile.CheckImageSize(40000, 10); err = "accepted"; } catch (InvalidDataException) { err = "rejected"; }
                check("Oversized image rejected", err, "rejected");
                // drawing a degenerate shape must not throw
                Shape weird = Mk(cv, Tool.Arrow, 5, 5, 5, 5); weird.Width = 0.01f;
                Shape weird2 = Mk(cv, Tool.Angle, 5, 5, 5, 5, 5, 5);
                try { using (Bitmap b2 = cv.Flatten(null)) { } res = "ok"; } catch (Exception ex) { res = ex.GetType().Name; }
                check("Degenerate shapes draw safely", res, "ok");
                cv.Shapes.Remove(weird); cv.Shapes.Remove(weird2);
            }

            // ---- output file names ----
            {
                DateTime t0 = new DateTime(2026, 11, 18, 14, 5, 33);
                check("Name pattern", Output.MakeName("${capturetime:yyyy-MM-dd HH_mm_ss}-${title}", t0, "Region 120×80", 1), "2026-11-18 14_05_33-Region 120×80");
                check("Name tokens", Output.MakeName("Shot_${date}_${time}_${n}", t0, "", 7), "Shot_2026-11-18_14-05-33_007");
                check("Name can't leave the folder", Output.MakeName("..\\..\\Windows\\${title}", t0, "a/b:c", 1), "_._Windows_a_b_c");
                check("Reserved name made safe", Output.MakeName("CON", t0, "", 1), "_CON");
                check("Empty name falls back", Output.MakeName("   ", t0, "", 1), "SnapMeasure");
                string od = Path.Combine(dir, "out"); Directory.CreateDirectory(od);
                File.WriteAllText(Path.Combine(od, "shot.png"), "x");
                check("No overwrite", Path.GetFileName(Output.UniqueBase(od, "shot", "png", true)), "shot (2)");
            }

            // ---- window title for file names (like Greenshot) ----
            {
                List<Rectangle> ws = new List<Rectangle> { new Rectangle(0, 0, 500, 400), new Rectangle(0, 0, 1920, 1080) };
                List<string> ts = new List<string> { "SnapMeasure – old", "Autodesk AutoCAD LT 2026 - [QF01012-R00.dwg]" };
                check("Title of captured app", ScreenCapture.TitleFor(new Rectangle(100, 100, 50, 50), ws, ts, "Slack"), "Autodesk AutoCAD LT 2026 - [QF01012-R00.dwg]");
                check("Falls back to active app", ScreenCapture.TitleFor(new Rectangle(3000, 100, 50, 50), ws, ts, "Rhino 8 Commercial - [Parallel]"), "Rhino 8 Commercial - [Parallel]");
                check("Greenshot-style default name",
                      Output.MakeName(Output.DefaultPattern, new DateTime(2026, 11, 18, 11, 34, 9), "Autodesk AutoCAD LT 2026 - [QF01012-R00.dwg]", 0),
                      "2026-11-18 11_34_09-Autodesk AutoCAD LT 2026 - [QF01012-R00.dwg]");
            }

            // ---- extra export formats ----
            using (Bitmap flat = cv.Flatten(Color.White))
            {
                string bx = Path.Combine(dir, "export");
                Output.SaveGif(flat, bx + ".gif");
                flat.Save(bx + ".tif", ImageFormat.Tiff);
                Output.SaveIco(flat, bx + ".ico");
                Output.SavePdf(flat, bx + ".pdf", 90);
                string sizes = "";
                foreach (string e in new string[] { ".gif", ".tif" })
                    using (Image im = Image.FromFile(bx + e)) sizes += e + "=" + im.Width + "x" + im.Height + " ";
                byte[] ico = File.ReadAllBytes(bx + ".ico");
                int entries = ico[4] | ico[5] << 8, last = ico[6 + 16 * (entries - 1)];
                sizes += ".ico=" + entries + " sizes, largest " + (last == 0 ? 256 : last);
                check("GIF / TIFF / ICO export", sizes.Trim(), ".gif=900x600 .tif=900x600 .ico=7 sizes, largest 256");
                using (Bitmap g2 = new Bitmap(bx + ".gif"))
                {
                    Color c = g2.GetPixel(475, 25);   // white background (between grid lines) stays white
                    check("GIF keeps colours (±3)", (c.R >= 252 && c.G >= 252 && c.B >= 252).ToString(), "True");
                }
                string pdf = File.ReadAllText(bx + ".pdf", Encoding.ASCII);
                check("PDF export", (pdf.StartsWith("%PDF-1.4") && pdf.Contains("/MediaBox [0 0 675 450]") && pdf.TrimEnd().EndsWith("%%EOF")).ToString(), "True");
            }

            V11(cv, dir, check);
            V12(cv, dir, check);
            V13(cv, dir, check);

            // ---- adjustable crop ----
            {
                cv.CropRect = new Rectangle(100, 100, 400, 250);
                using (Bitmap flat = cv.Flatten(null))
                {
                    check("Crop export size", flat.Width + "x" + flat.Height, "400x250");
                    flat.Save(Path.Combine(dir, "selftest_crop.png"), ImageFormat.Png);
                }
                cv.ImageOp("rotcw");
                check("Crop follows rotation", cv.CropRect.HasValue ? cv.CropRect.Value.ToString() : "none", new Rectangle(250, 100, 250, 400).ToString());
                cv.Undo();
                cv.ImageOp("border");
                check("Border bakes the crop", cv.Image.Width + "x" + cv.Image.Height + "|" + cv.CropRect.HasValue, "408x258|False");
                cv.Undo();
                check("Undo restores crop", cv.Image.Width + "x" + cv.Image.Height + "|" + (cv.CropRect.HasValue ? cv.CropRect.Value.ToString() : "none"),
                      "900x600|" + new Rectangle(100, 100, 400, 250).ToString());
                cv.CropRect = null;
            }

            // spotlight + grayscale highlight render
            Shape sp = Mk(cv, Tool.Highlight, 90, 90, 510, 360); sp.Mode = (int)HighlightMode.Spotlight; sp.FillOpacity = 55;
            Shape gr = Mk(cv, Tool.Highlight, 590, 140, 860, 460); gr.Mode = (int)HighlightMode.Grayscale;
            using (Bitmap flat = cv.Flatten(null)) flat.Save(Path.Combine(dir, "selftest_spotlight.png"), ImageFormat.Png);
            cv.Shapes.Remove(sp); cv.Shapes.Remove(gr);

            // ---- editor UI snapshot ----
            cv.CurrentTool = Tool.Select;
            cv.SelectShape(rr);
            f.RefreshAll();
            cv.Fit();
            Application.DoEvents();
            using (Bitmap ui = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(ui, new Rectangle(0, 0, f.Width, f.Height));
                ui.Save(Path.Combine(dir, "selftest_ui.png"), ImageFormat.Png);
            }
            f.Hide();
            f.Dispose();
            File.WriteAllText(Path.Combine(dir, "selftest.txt"), log.ToString());
        }
    }

    internal static class App
    {
        static bool busy;

        public static void Later(int ms, Action a)
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = Math.Max(1, ms);
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); a(); };
            t.Start();
        }

        /// <summary>Builds one hidden editor and throws it away, a few seconds after the tray starts, so its code is
        /// compiled and loaded before the first capture: the first editor then opens as fast as later ones.
        /// Nothing is shown and nothing is saved.</summary>
        public static void WarmUp()
        {
            try
            {
                Bitmap pic = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
                using (EditorForm f = new EditorForm(pic, "warm-up"))
                {
                    TouchHandles(f);       // create the windows of every bar and box, without showing them
                    using (Bitmap shot = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height)))
                        f.DrawToBitmap(shot, new Rectangle(0, 0, shot.Width, shot.Height));
                }
                pic.Dispose();
            }
            catch (Exception ex) { ErrorLog.Write("warm-up", ex); }
        }

        static void TouchHandles(Control c)
        {
            IntPtr h = c.Handle;
            foreach (Control child in c.Controls) TouchHandles(child);
        }

        /// <summary>The captured app's window title (e.g. "Autodesk AutoCAD LT 2026 - [plan.dwg]"), or a fallback name.</summary>
        static string Named(string windowTitle, string fallback)
        {
            string t = (windowTitle ?? "").Trim();
            if (t.Length == 0 || t.StartsWith("SnapMeasure")) return fallback;
            return t.Length > 120 ? t.Substring(0, 120) : t;
        }

        /// <summary>Tray notification (set by the tray icon).</summary>
        public static Action<string> Notify;
        /// <summary>Started by Windows at sign-in: no welcome balloon.</summary>
        public static bool QuietStart;

        static void Tell(string msg)
        {
            if (Notify != null && Settings.GetBool("Notify", true)) Notify(msg);
        }

        /// <summary>What happens after a capture (Settings → Capture): open the editor, copy, save, or copy + save.</summary>
        static void Deliver(Bitmap b, string title) { Deliver(b, title, Rectangle.Empty); }

        /// <summary>'area' = where the capture was on screen, to place the mouse pointer (Settings → Capture the mouse pointer).</summary>
        static void Deliver(Bitmap b, string title, Rectangle area)
        {
            if (b == null) { ScreenCapture.ForgetCursor(); return; }
            ScreenCapture.CursorShot cur = ScreenCapture.TakeCursor();
            Point curAt = Point.Empty;
            if (cur != null)
            {
                curAt = new Point(cur.ScreenPos.X - area.X, cur.ScreenPos.Y - area.Y);
                if (area.IsEmpty || !new Rectangle(0, 0, b.Width, b.Height).IntersectsWith(new Rectangle(curAt, cur.Image.Size))) { cur.Image.Dispose(); cur = null; }
            }
            int afterMode = Settings.GetInt("AfterCapture", 0);
            if (cur != null && afterMode != 0)
            {
                // no editor: the pointer goes into the picture itself
                using (Graphics g = Graphics.FromImage(b)) g.DrawImage(cur.Image, curAt.X, curAt.Y, cur.Image.Width, cur.Image.Height);
                cur.Image.Dispose(); cur = null;
            }
            if (Settings.GetBool("CameraSound", false)) Shutter.Play();
            int after = Settings.GetInt("AfterCapture", 0);
            bool copy = after == 1 || after == 3 || (after == 0 && Settings.GetBool("CopyOnCapture", true));
            if (copy) { try { Clipboard.SetImage(b); } catch { } }
            if (after == 2 || after == 3)
            {
                try
                {
                    string dir = Settings.SaveFolder;
                    Directory.CreateDirectory(dir);
                    string ext = Output.Format;
                    string name = Output.MakeName(Output.Pattern, DateTime.Now, title, Output.Pattern.Contains("${n") ? Output.NextNumber() : 0);
                    string path = Output.UniqueBase(dir, name, ext, false) + "." + ext;
                    Output.SaveBitmap(b, path);
                    if (Output.CopyPath && after == 2) { try { Clipboard.SetText(path); } catch { } }
                    Tell((after == 3 ? "Copied and saved: " : "Saved: ") + Path.GetFileName(path));
                }
                catch (Exception ex) { MessageBox.Show("Could not save the capture: " + ex.Message, "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                b.Dispose();
                return;
            }
            if (after == 1) { Tell("Capture copied to the clipboard (" + b.Width + " × " + b.Height + ")"); b.Dispose(); return; }
            EditorForm f = new EditorForm(b, title);
            if (cur != null) f.CanvasControl.AddPointerPicture(cur.Image, curAt);   // a picture on top: move it or press Delete
            f.Show();
            f.Activate();
        }

        /// <summary>Settings → Capture: wait a moment (lets menus close) and decide whether to include the mouse pointer.</summary>
        static int PreCapture()
        {
            ScreenCapture.IncludeCursor = Settings.GetBool("CaptureCursor", false);
            ScreenCapture.ForgetCursor();
            return Math.Max(0, Math.Min(10000, Settings.GetInt("CaptureDelay", 0)));
        }

        /// <summary>Time AutoCAD needs to erase its crosshair after losing the focus (measured: gone within 30 ms).</summary>
        const int CadCrosshairWait = 60;
        static Form focusSink;

        /// <summary>AutoCAD and other CAD programs draw their own crosshair into the drawing, so it lands in any screen
        /// grab (the Windows pointer setting can't remove it). They erase it as soon as they lose the focus: take the
        /// focus with an invisible 1×1 window for a moment (like Greenshot, whose own window is in front when it grabs).
        /// Only for CAD programs: taking the focus would close an open menu in other apps.</summary>
        static bool HideCadCrosshair(IntPtr target)
        {
            if (target == IntPtr.Zero || !ScreenCapture.IsCadApp(Native.ProcessNameOf(target))) return false;
            if (focusSink == null || focusSink.IsDisposed)
            {
                focusSink = new Form();
                focusSink.FormBorderStyle = FormBorderStyle.None;
                focusSink.ShowInTaskbar = false;
                focusSink.StartPosition = FormStartPosition.Manual;
                focusSink.Bounds = new Rectangle(-32000, -32000, 1, 1);
                focusSink.Text = "SnapMeasure";
            }
            focusSink.Show();
            return Native.SetForegroundWindow(focusSink.Handle);
        }

        static void GiveFocusBack(IntPtr target)
        {
            try
            {
                if (focusSink == null || focusSink.IsDisposed) return;
                bool ours = Native.GetForegroundWindow() == focusSink.Handle;
                focusSink.Hide();
                if (ours && target != IntPtr.Zero) Native.SetForegroundWindow(target);
            }
            catch (Exception) { }
        }

        /// <summary>Runs one capture at a time. The capture delay is a timer, so the tray, editors and overlays keep
        /// working while it counts down; 'busy' stays set until the delayed capture has finished.</summary>
        static void Guard(Action a)
        {
            if (busy) return;
            busy = true;
            ScreenCapture.Target = Native.GetForegroundWindow();
            bool tookFocus = false;
            Action run = () =>
            {
                try { a(); }
                catch (Exception ex) { MessageBox.Show("Capture failed: " + ex.Message, "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally
                {
                    // nothing of ours took over (cancelled, or copied to the clipboard): back to the app you were in
                    if (tookFocus) GiveFocusBack(ScreenCapture.Target);
                    ScreenCapture.Target = IntPtr.Zero;
                    busy = false;
                }
            };
            int ms;
            try
            {
                ms = PreCapture();
                tookFocus = HideCadCrosshair(ScreenCapture.Target);
                if (tookFocus) ms = Math.Max(ms, CadCrosshairWait);
            }
            catch { busy = false; ScreenCapture.Target = IntPtr.Zero; throw; }
            if (ms > 0)
            {
                try { Later(ms, run); }
                catch { busy = false; throw; }
            }
            else run();
        }

        public static void CaptureRegion()
        {
            Guard(() =>
            {
                Rectangle r;
                string title;
                Bitmap b = ScreenCapture.SelectRegion(out r, out title);
                if (b == null) return;
                Settings.Set("LastRegion", r.X + "," + r.Y + "," + r.Width + "," + r.Height);
                Deliver(b, Named(title, "Region " + r.Width + "×" + r.Height), r);
            });
        }

        public static void CaptureActiveWindow()
        {
            Guard(() =>
            {
                IntPtr h = ScreenCapture.TargetOrForeground;
                Rectangle r = h == IntPtr.Zero ? Rectangle.Empty : Native.GetWindowBounds(h);
                r.Intersect(ScreenCapture.VirtualBounds);
                if (r.Width < 2 || r.Height < 2) return;
                Deliver(ScreenCapture.Grab(r), Named(Native.WindowTitle(h), "Window " + r.Width + "×" + r.Height), r);
            });
        }

        public static void CaptureScreenUnderCursor()
        {
            Guard(() =>
            {
                Rectangle r = Screen.FromPoint(Cursor.Position).Bounds;
                Deliver(ScreenCapture.Grab(r), Named(Native.WindowTitle(ScreenCapture.TargetOrForeground), "Screen " + r.Width + "×" + r.Height), r);
            });
        }

        public static void CaptureAllScreens()
        {
            Guard(() =>
            {
                Rectangle r = ScreenCapture.VirtualBounds;
                Deliver(ScreenCapture.Grab(r), "All screens " + r.Width + "×" + r.Height, r);
            });
        }

        public static void CaptureLastRegion()
        {
            string[] p = Settings.Get("LastRegion", "").Split(',');
            int x, y, w, h;
            if (p.Length != 4 || !int.TryParse(p[0], out x) || !int.TryParse(p[1], out y) || !int.TryParse(p[2], out w) || !int.TryParse(p[3], out h))
            {
                CaptureRegion();
                return;
            }
            Guard(() =>
            {
                Rectangle r = new Rectangle(x, y, w, h);
                r.Intersect(ScreenCapture.VirtualBounds);
                if (r.Width < 2 || r.Height < 2) return;
                List<string> titles = new List<string>();
                List<Rectangle> wins = Native.GetVisibleWindowRects(titles);
                string t = ScreenCapture.TitleFor(r, wins, titles, Native.WindowTitle(ScreenCapture.TargetOrForeground));
                Deliver(ScreenCapture.Grab(r), Named(t, "Last region " + r.Width + "×" + r.Height), r);
            });
        }

        public static void OpenFromClipboard()
        {
            try
            {
                if (!Clipboard.ContainsImage()) { MessageBox.Show("The clipboard doesn't contain an image.", "SnapMeasure"); return; }
                using (Image img = Clipboard.GetImage())
                {
                    if (img == null) return;
                    ProjectFile.CheckImageSize(img.Width, img.Height);
                    EditorForm f = new EditorForm(new Bitmap(img), "Clipboard");
                    f.Show();
                }
            }
            catch (Exception ex) { MessageBox.Show("Could not read the clipboard: " + ex.Message, "SnapMeasure"); }
        }

        public static string FailedHotkeys = "";

        /// <summary>About = the About tab of the settings window.</summary>
        public static void ShowAbout(IWin32Window owner)
        {
            Dialogs.ShowSettings(owner, 3);       // tabs: General, Capture, Output, About
        }

        /// <summary>Empty canvas to draw and scribble on.</summary>
        public static void NewBlankCanvas(IWin32Window owner)
        {
            int w, h; Color bg; bool grid;
            if (!Dialogs.NewCanvas(owner, out w, out h, out bg, out grid)) return;
            Bitmap b = Canvas.BlankImage(w, h, bg);
            if (grid)
                using (Graphics g = Graphics.FromImage(b))
                using (Pen minor = new Pen(Color.FromArgb(232, 236, 242)))
                using (Pen major = new Pen(Color.FromArgb(205, 214, 226)))
                {
                    for (int x = 0; x <= w; x += 10) g.DrawLine(x % 50 == 0 ? major : minor, x, 0, x, h);
                    for (int y = 0; y <= h; y += 10) g.DrawLine(y % 50 == 0 ? major : minor, 0, y, w, y);
                }
            EditorForm f = new EditorForm(b, "Blank " + w + "×" + h);
            f.Show();
            f.Activate();
        }

        /// <summary>Opens one page of a PDF (Windows' own PDF renderer). The page's paper size makes drawing scales exact.</summary>
        static Form LoadPdf(string path)
        {
            PdfImport.Info info = PdfImport.Read(path);
            if (info.Pages < 1) throw new InvalidDataException("The PDF has no pages.");
            int page, dpi;
            if (!Dialogs.PdfPage(null, path, info.Pages, out page, out dpi)) return null;
            double useDpi = dpi;
            SizeF mm;
            Bitmap b = PdfImport.Render(path, page - 1, ref useDpi, out mm);
            string title = Path.GetFileNameWithoutExtension(path) + (info.Pages > 1 ? " – page " + page : "");
            EditorForm f = new EditorForm(b, title);
            f.PaperMmPerPx = 25.4 / useDpi;
            return f;
        }

        /// <summary>Scrolling capture: pick the area, then SnapMeasure scrolls it and joins the pieces.</summary>
        public static void CaptureScrolling()
        {
            Guard(() =>
            {
                Rectangle r;
                string title;
                Bitmap sel = ScreenCapture.SelectRegion(out r, out title);
                if (sel == null) return;
                sel.Dispose();
                ScreenCapture.IncludeCursor = false;      // the pointer sits in the middle while scrolling
                Thread.Sleep(150);
                Bitmap b = ScrollCapture.Capture(r);
                if (b == null) return;
                Deliver(b, Named(title, "Scrolling " + b.Width + "×" + b.Height));
            });
        }

        /// <summary>Puts text on the clipboard, retrying briefly if another program has it open. False = still locked.</summary>
        public static bool TrySetClipboardText(string t)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(t); return true; }
                catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(60); }
            }
            return false;
        }

        /// <summary>Same for a picture.</summary>
        public static bool TrySetClipboardImage(Image img)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetImage(img); return true; }
                catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(60); }
            }
            return false;
        }

        /// <summary>Capture text: pick an area of the screen, the text in it goes to the clipboard.
        /// 'done' gets the status message (after the capture delay, if one is set).</summary>
        public static void CaptureText(Action<string> done)
        {
            Guard(() =>
            {
                string msg = null;
                Rectangle r;
                string title;
                Bitmap b = ScreenCapture.SelectRegion(out r, out title);
                if (b == null) return;
                using (b)
                {
                    if (!TextRecognition.Available) msg = "Windows has no text-recognition language installed (Settings → Time & language → Language).";
                    else
                    {
                        string t = TextRecognition.Read(b, null).Text;
                        if (t.Trim().Length == 0) msg = "No text found in that area.";
                        else if (!TrySetClipboardText(t)) msg = "The clipboard is busy (another program has it open). Try again.";
                        else
                        {
                            string one = t.Replace("\r", "").Replace("\n", " · ");
                            msg = "Text copied: " + (one.Length > 120 ? one.Substring(0, 120) + "…" : one);
                        }
                    }
                }
                if (done != null) done(msg);
            });
        }

        public static Form LoadEditor(string path)
        {
            try
            {
                if (Path.GetExtension(path).ToLowerInvariant() == ".snapm") return EditorForm.FromProject(path);
                if (Path.GetExtension(path).ToLowerInvariant() == ".pdf") return LoadPdf(path);
                Bitmap b;
                using (FileStream fs = File.OpenRead(path))
                using (Image tmp = Image.FromStream(fs, false, true))
                {
                    ProjectFile.CheckImageSize(tmp.Width, tmp.Height);
                    b = new Bitmap(tmp);
                }
                return new EditorForm(b, Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open " + Path.GetFileName(path) + ":\n" + ex.Message, "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }

        public static void OpenFile(string path)
        {
            Form f = LoadEditor(path);
            if (f != null) { f.Show(); f.Activate(); }
        }
    }

    internal class HotkeyWindow : NativeWindow, IDisposable
    {
        public event Action<int> Pressed;
        readonly List<int> ids = new List<int>();

        public HotkeyWindow() { CreateHandle(new CreateParams()); }

        public bool Register(int id, uint mods, Keys key)
        {
            bool ok = Native.RegisterHotKey(Handle, id, mods | Native.MOD_NOREPEAT, (uint)key);
            if (ok) ids.Add(id);
            return ok;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && Pressed != null) Pressed(m.WParam.ToInt32());
            base.WndProc(ref m);
        }

        public void UnregisterAll()
        {
            foreach (int id in ids) Native.UnregisterHotKey(Handle, id);
            ids.Clear();
        }

        public void Dispose()
        {
            UnregisterAll();
            DestroyHandle();
        }
    }

    /// <summary>The capture keys, two per capture (Settings → General). Stored as numbers in SnapMeasure.ini.</summary>
    internal static class Hotkeys
    {
        public static readonly string[] Ids = { "Region", "Window", "Screen", "LastRegion", "Scrolling", "Text" };
        public static readonly string[] Labels = { "Capture region / window", "Capture active window", "Capture screen under mouse",
                                                   "Capture last region again", "Scrolling capture (long page)", "Capture text (OCR)" };
        const Keys CS = Keys.Control | Keys.Shift;
        static readonly Keys[] DefMain = { Keys.PrintScreen, Keys.Alt | Keys.PrintScreen, Keys.Control | Keys.PrintScreen, Keys.Shift | Keys.PrintScreen, Keys.None, Keys.None };
        static readonly Keys[] DefAlt = { CS | Keys.D1, CS | Keys.D2, CS | Keys.D3, CS | Keys.D4, CS | Keys.D5, CS | Keys.D6 };

        /// <summary>Set by the tray: true = let go of every capture key (while a key box is being filled in), false = take them again.</summary>
        public static Action<bool> Pause;

        static string Key(int i, int slot) { return "Hotkey" + Ids[i] + (slot == 0 ? "" : "2"); }

        public static Keys Default(int i, int slot) { return slot == 0 ? DefMain[i] : DefAlt[i]; }

        public static Keys Get(int i, int slot)
        {
            string v = Settings.Get(Key(i, slot), null);
            int n;
            if (v == null || !int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return Default(i, slot);
            return (Keys)n;
        }

        public static void Set(int i, int slot, Keys k) { Settings.Set(Key(i, slot), ((int)k).ToString(CultureInfo.InvariantCulture)); }

        public static string Signature()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Ids.Length; i++) sb.Append((int)Get(i, 0)).Append(',').Append((int)Get(i, 1)).Append(';');
            return sb.ToString();
        }

        public static uint Mods(Keys k)
        {
            uint m = 0;
            if ((k & Keys.Alt) != 0) m |= Native.MOD_ALT;
            if ((k & Keys.Control) != 0) m |= Native.MOD_CONTROL;
            if ((k & Keys.Shift) != 0) m |= Native.MOD_SHIFT;
            return m;
        }

        /// <summary>A key on its own may only be one that never types text (PrtSc, F1–F24, Pause …).</summary>
        public static bool Usable(Keys k)
        {
            Keys code = k & Keys.KeyCode;
            if (code == Keys.None || code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu || code == Keys.LWin || code == Keys.RWin) return false;
            if ((k & (Keys.Control | Keys.Alt)) != 0) return true;
            return code == Keys.PrintScreen || code == Keys.Pause || code == Keys.Scroll || (code >= Keys.F1 && code <= Keys.F24);
        }

        public static string Text(Keys k)
        {
            if (k == Keys.None) return "";
            List<string> p = new List<string>();
            if ((k & Keys.Control) != 0) p.Add("Ctrl");
            if ((k & Keys.Shift) != 0) p.Add("Shift");
            if ((k & Keys.Alt) != 0) p.Add("Alt");
            Keys code = k & Keys.KeyCode;
            string name;
            if (code == Keys.PrintScreen) name = "PrtSc";
            else if (code >= Keys.D0 && code <= Keys.D9) name = ((int)(code - Keys.D0)).ToString(CultureInfo.InvariantCulture);
            else if (code >= Keys.NumPad0 && code <= Keys.NumPad9) name = "Num " + (int)(code - Keys.NumPad0);
            else if (code == Keys.Scroll) name = "Scroll Lock";
            else name = code.ToString();
            p.Add(name);
            return string.Join("+", p.ToArray());
        }
    }

    /// <summary>A box that records a key combination: click it and press the keys; Delete or Backspace clears it.</summary>
    internal class HotkeyBox : TextBox
    {
        Keys value;
        public Label Note;                   // where a refused combination is explained

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            Cursor = Cursors.Hand;
            ShortcutsEnabled = false;
        }

        public Keys Value { get { return value; } set { this.value = value; Text = value == Keys.None ? "(none)" : Hotkeys.Text(value); } }

        protected override void OnEnter(EventArgs e) { base.OnEnter(e); if (Hotkeys.Pause != null) Hotkeys.Pause(true); BackColor = Color.FromArgb(255, 250, 220); }
        protected override void OnLeave(EventArgs e) { base.OnLeave(e); if (Hotkeys.Pause != null) Hotkeys.Pause(false); BackColor = SystemColors.Window; }

        protected override bool IsInputKey(Keys keyData) { return (keyData & Keys.KeyCode) != Keys.Tab || (keyData & (Keys.Control | Keys.Alt)) != 0; }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true; e.Handled = true;
            Take(e.KeyData);
        }

        // Print Screen only sends key-up
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.PrintScreen) Take(e.KeyData);
            e.Handled = true;
        }

        void Take(Keys k)
        {
            Keys code = k & Keys.KeyCode;
            if ((code == Keys.Delete || code == Keys.Back) && (k & Keys.Modifiers) == 0) { Value = Keys.None; Say(""); return; }
            if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu) return;     // still choosing modifiers
            if (!Hotkeys.Usable(k)) { Say(Hotkeys.Text(k) + " would block typing – add Ctrl or Alt, or use PrtSc / F-keys."); return; }
            Value = k; Say("");
        }

        void Say(string s) { if (Note != null) Note.Text = s; }
    }

    internal class TrayContext : ApplicationContext
    {
        readonly NotifyIcon ni;
        readonly HotkeyWindow hk;
        readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();
        readonly List<string> okKeys = new List<string>(), failedKeys = new List<string>();

        public TrayContext()
        {
            ni = new NotifyIcon();
            ni.Icon = AppIcon.Get();
            ni.Text = Lang.T("SnapMeasure – Print Screen to capture");
            ni.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem region = Item(menu, "Capture region / window", "", () => App.CaptureRegion());
            region.Font = new Font(region.Font, FontStyle.Bold);
            region.Image = Icons.Cmd("region", 16);
            ToolStripMenuItem win = Item(menu, "Capture active window", "", () => App.CaptureActiveWindow(), false);
            win.Image = Icons.Cmd("window", 16);
            ToolStripMenuItem scr = Item(menu, "Capture screen under mouse", "", () => App.CaptureScreenUnderCursor());
            scr.Image = Icons.Cmd("screen", 16);
            Item(menu, "Capture all screens", "", () => App.CaptureAllScreens()).Image = Icons.Cmd("allscreens", 16);
            ToolStripMenuItem last = Item(menu, "Capture last region again", "", () => App.CaptureLastRegion());
            last.Image = Icons.Cmd("lastregion", 16);
            Item(menu, "Capture region in 3 seconds", "", () => App.Later(3000, App.CaptureRegion)).Image = Icons.Cmd("timer", 16);
            ToolStripMenuItem scroll = Item(menu, "Scrolling capture (long page)", "", () => App.CaptureScrolling());
            scroll.Image = Icons.Cmd("scroll", 16);
            ToolStripMenuItem text = Item(menu, "Capture text (OCR) to clipboard", "", CaptureTextNow);
            text.Image = Icons.Cmd("textcap", 16);
            keyItems = new ToolStripMenuItem[] { region, win, scr, last, scroll, text };
            menu.Items.Add(new ToolStripSeparator());
            Item(menu, "New blank canvas…", "", () => App.NewBlankCanvas(null), false).Image = Icons.Cmd("blank", 16);
            Item(menu, "Open image, PDF or project…", "", OpenDialog, false).Image = Icons.Cmd("open", 16);
            Item(menu, "Open image from clipboard", "", () => App.OpenFromClipboard(), false).Image = Icons.Cmd("clipboard", 16);
            menu.Items.Add(new ToolStripSeparator());
            Item(menu, "On-screen ruler", "", Overlays.ShowRuler, false).Image = Icons.Cmd("ruler", 16);
            Item(menu, "On-screen protractor", "", Overlays.ShowProtractor, false).Image = Icons.Cmd("protractor", 16);
            Item(menu, "Close rulers && protractors", "", Overlays.CloseAll, false).Image = Icons.Cmd("close", 16);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem copy = new ToolStripMenuItem("Copy every capture to clipboard");
            copy.Checked = Settings.GetBool("CopyOnCapture", true);
            copy.Click += (s, e) => { copy.Checked = !copy.Checked; Settings.Set("CopyOnCapture", copy.Checked ? "1" : "0"); };
            menu.Items.Add(copy);
            ToolStripMenuItem boot = new ToolStripMenuItem("Start with Windows");
            boot.ToolTipText = "Start SnapMeasure in the tray when you sign in, so the capture keys always work";
            boot.Click += (s, e) =>
            {
                string note;
                boot.Checked = Startup.Set(!boot.Checked, out note);
                if (note != null) MessageBox.Show(note, "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            menu.Items.Add(boot);
            // read the state each time the menu opens (it can also be changed in Windows Settings → Apps → Startup)
            menu.Opening += (s, e) => boot.Checked = Startup.IsOn;
            Startup.RefreshPath();
            Item(menu, "Settings (capture, output)…", "", () => Dialogs.ShowSettings(null, 0), false).Image = Icons.Cmd("settings", 16);
            Item(menu, "Open save folder", "", OpenFolder, false).Image = Icons.Cmd("folderopen", 16);
            menu.Items.Add(new ToolStripSeparator());
            Item(menu, "About SnapMeasure && shortcuts…", "", ShowAbout, false).Image = Icons.Cmd("help", 16);
            Item(menu, "Exit", "", ExitApp, false).Image = Icons.Cmd("exit", 16);
            ni.ContextMenuStrip = menu;
            Lang.Apply(menu.Items);
            App.Notify = m => { try { ni.ShowBalloonTip(3000, "SnapMeasure", Lang.T(m), ToolTipIcon.Info); } catch { } };
            ni.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) App.CaptureRegion(); };

            hk = new HotkeyWindow();
            hk.Pressed += id => { Action a; if (actions.TryGetValue(id, out a)) a(); };
            RegisterKeys();
            // compile the editor in the background of start-up, unless a capture or window is already open
            App.Later(3000, () => { if (Application.OpenForms.Count == 0) App.WarmUp(); });
            App.Later(5000, ShellPreview.Refresh);      // .snapm thumbnails in File Explorer follow the exe if it was moved
            // a key box in Settings needs the keys itself; another SnapMeasure window may change them in the settings file
            Hotkeys.Pause = p => { if (p) hk.UnregisterAll(); else RegisterKeys(); };
            keyWatch = new System.Windows.Forms.Timer();
            keyWatch.Interval = 2000;
            keyWatch.Tick += (s, e) => { if (Hotkeys.Signature() != keySig) RegisterKeys(); };
            keyWatch.Start();

            string first = Hotkeys.Text(Hotkeys.Get(0, 0)), alt = Hotkeys.Text(Hotkeys.Get(0, 1));
            string msg = failedKeys.Count == 0
                ? string.Format(Lang.T("Press {0} to capture. Right-click this icon for more."), first.Length > 0 ? first : alt)
                : string.Format(Lang.T("Ready. These keys are taken by another app (e.g. Windows Snipping Tool or Greenshot): {0}."), string.Join(", ", failedKeys.ToArray())) +
                  "\n" + Lang.T("Pick other keys in Settings → General.");
            if (!App.QuietStart || failedKeys.Count > 0)
                ni.ShowBalloonTip(5000, Lang.T("SnapMeasure is running"), msg, failedKeys.Count == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }

        ToolStripMenuItem[] keyItems;
        System.Windows.Forms.Timer keyWatch;
        string keySig = "";

        /// <summary>(Re)registers the capture keys from Settings → General and shows them in the tray menu.</summary>
        void RegisterKeys()
        {
            hk.UnregisterAll();
            actions.Clear(); okKeys.Clear(); failedKeys.Clear();
            Action[] runs = { App.CaptureRegion, App.CaptureActiveWindow, App.CaptureScreenUnderCursor, App.CaptureLastRegion, App.CaptureScrolling, CaptureTextNow };
            int id = 1;
            for (int i = 0; i < Hotkeys.Ids.Length; i++)
            {
                string shown = "";
                for (int slot = 0; slot < 2; slot++)
                {
                    Keys k = Hotkeys.Get(i, slot);
                    if (k == Keys.None || !Hotkeys.Usable(k)) continue;
                    Reg(id++, Hotkeys.Mods(k), k & Keys.KeyCode, Hotkeys.Text(k), runs[i]);
                    if (shown.Length == 0) shown = Hotkeys.Text(k);
                }
                if (keyItems != null && i < keyItems.Length) keyItems[i].ShortcutKeyDisplayString = shown;
            }
            keySig = Hotkeys.Signature();
            App.FailedHotkeys = string.Join(", ", failedKeys.ToArray());
        }

        ToolStripMenuItem Item(ContextMenuStrip m, string text, string keys, Action a, bool delayed = true)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            if (!string.IsNullOrEmpty(keys)) it.ShortcutKeyDisplayString = keys;
            // let the menu fade out before the screen is frozen
            it.Click += (s, e) => { if (delayed) App.Later(250, a); else a(); };
            m.Items.Add(it);
            return it;
        }

        void Reg(int id, uint mods, Keys key, string name, Action a)
        {
            actions[id] = a;
            if (hk.Register(id, mods, key)) okKeys.Add(name); else failedKeys.Add(name);
        }

        void CaptureTextNow()
        {
            App.CaptureText(msg =>
            {
                if (!string.IsNullOrEmpty(msg)) ni.ShowBalloonTip(4000, "SnapMeasure – text", msg, ToolTipIcon.Info);
            });
        }

        void OpenDialog()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Images, PDF and projects|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.pdf;*.snapm|All files|*.*";
                if (d.ShowDialog() == DialogResult.OK) App.OpenFile(d.FileName);
            }
        }

        void OpenFolder()
        {
            try
            {
                string dir = Settings.SaveFolder;
                Directory.CreateDirectory(dir);
                // the shell opens the folder itself: no quoting, so a trailing \ (e.g. D:\Shots\) can't break the path
                ProcessStartInfo psi = new ProcessStartInfo(Path.GetFullPath(dir));
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "SnapMeasure"); }
        }

        void ShowAbout()
        {
            App.FailedHotkeys = string.Join(", ", failedKeys.ToArray());
            App.ShowAbout(null);
        }

        void ExitApp()
        {
            List<Form> open = new List<Form>();
            foreach (Form f in Application.OpenForms) open.Add(f);
            foreach (Form f in open)
            {
                f.Close();
                if (!f.IsDisposed && f.Visible) return;   // user cancelled closing an unsaved editor
            }
            keyWatch.Stop(); keyWatch.Dispose();
            Hotkeys.Pause = null;
            hk.Dispose();
            ni.Visible = false;
            ni.Dispose();
            ExitThread();
        }
    }

    internal static class AboutText
    {
        public static string Shortcuts(string failed)
        {
            // one shortcut per line, keys in a column of their own: the box is narrow and must not wrap a line
            // in the middle (that broke the columns); grouped like the editor's tool column
            StringBuilder sb = new StringBuilder();
            Action<string, string> row = (key, what) => sb.AppendLine("  " + key.PadRight(KeyCol) + what);
            sb.AppendLine("Capture – your keys, set in Settings → General");
            for (int i = 0; i < Hotkeys.Ids.Length; i++)
            {
                string a = Hotkeys.Text(Hotkeys.Get(i, 0)), b = Hotkeys.Text(Hotkeys.Get(i, 1));
                string keys = a.Length > 0 && b.Length > 0 ? a + " / " + b : (a + b);
                row(keys.Length > 0 ? keys : "(no key)", Hotkeys.Labels[i]);
            }
            row("Esc", "finish a scrolling capture");
            if (!string.IsNullOrEmpty(failed)) sb.AppendLine("  Not available (used by another app): " + failed);
            sb.AppendLine();
            sb.AppendLine("Measure");
            row("K", "calibrate the scale");
            row("D", "dimension");
            row("W  /  Q", "path length  /  perimeter");
            row("M", "area");
            row("X", "cut-out: an opening, or a hollow in a polygon");
            row("G", "angle (Shift = 45° steps)");
            row("Shift+R", "radius / diameter / arc length");
            row("I", "coordinates");
            row("Double-click", "label a measurement or marker");
            sb.AppendLine();
            sb.AppendLine("Count");
            row("N  /  Shift+N", "number markers  /  letter markers");
            sb.AppendLine();
            sb.AppendLine("Draw");
            row("A  /  L", "arrow  /  line");
            row("Shift+P", "polyline");
            row("Shift+L", "arc");
            row("F", "freehand");
            row("R  /  E", "rectangle  /  ellipse");
            row("P", "polygon");
            row("U", "revision cloud");
            sb.AppendLine();
            sb.AppendLine("Highlight");
            row("H", "focus: highlight, spotlight, gray, magnify");
            row("B", "pixelate");
            row("Shift+H", "highlighter pen");
            sb.AppendLine();
            sb.AppendLine("Text");
            row("T", "text");
            row("S", "speech bubble");
            row("Z", "stamp");
            row("O", "note (not printed)");
            row("Shift+T", "copy text from an area (OCR)");
            sb.AppendLine();
            sb.AppendLine("Edit");
            row("C", "crop (Delete = remove the crop)");
            row("Ctrl+A", "select all");
            row("Ctrl+C / X / V", "copy, cut, paste shapes");
            row("Ctrl+D", "duplicate");
            row("Ctrl+] / Ctrl+[", "forward / backward (+Shift = front / back)");
            row("Ctrl+Shift+M", "match properties");
            row("Alt+1 … 9", "line colour (Alt+Shift = fill colour)");
            row("Alt+L / Alt+F", "line / fill colour picker");
            sb.AppendLine();
            sb.AppendLine("View and save");
            row("Home", "fit and centre");
            row("Ctrl+wheel", "zoom");
            row("Space+drag", "pan (or the middle mouse button)");
            row("Shift", "15° steps, squares and circles");
            row("Alt", "no snapping");
            row("Ctrl+R", "rulers");
            row("F4", "scale panel");
            row("Ctrl+L", "keep inside the picture (lock)");
            row("Ctrl+S", "save the picture (and the .snapm)");
            row("Ctrl+Shift+S", "save as");
            row("Ctrl+P", "pin to the screen");
            row("R / H / V / C", "in a pin: rotate, mirror, flip, crop");
            sb.AppendLine();
            sb.AppendLine("Settings file: " + Settings.FilePath);
            return sb.ToString();
        }

        /// <summary>Width of the keys column (characters): the longest capture keys, "Shift+PrtSc / Ctrl+Shift+4".</summary>
        const int KeyCol = 28;
    }

    /// <summary>Portable settings: SnapMeasure.ini next to the exe (falls back to %APPDATA% if that folder is read-only).</summary>
    internal static class Settings
    {
        static readonly Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static string FilePath;

        public static void Load()
        {
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (CanWrite(exeDir)) FilePath = Path.Combine(exeDir, "SnapMeasure.ini");
            else
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapMeasure");
                Directory.CreateDirectory(dir);
                FilePath = Path.Combine(dir, "SnapMeasure.ini");
            }
            ReadFile();
        }

        static DateTime readStamp = DateTime.MinValue;

        static void ReadFile()
        {
            try
            {
                if (FilePath == null || !File.Exists(FilePath)) return;
                DateTime t = File.GetLastWriteTimeUtc(FilePath);
                string[] lines = File.ReadAllLines(FilePath);
                foreach (string line in lines)
                {
                    int i = line.IndexOf('=');
                    if (i > 0 && !line.StartsWith(";")) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                readStamp = t;
            }
            catch (Exception) { }
        }

        /// <summary>Another SnapMeasure window (e.g. an editor opened from Explorer runs as its own program) may have
        /// changed the settings file: read it again so every window uses the latest settings and none overwrites another's.</summary>
        static void Refresh()
        {
            try
            {
                if (FilePath != null && File.Exists(FilePath) && File.GetLastWriteTimeUtc(FilePath) != readStamp) ReadFile();
            }
            catch (Exception) { }
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string t = Path.Combine(dir, ".snapmeasure_write_test");
                File.WriteAllText(t, "x");
                File.Delete(t);
                return true;
            }
            catch { return false; }
        }

        static void Save()
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("; SnapMeasure settings");
                foreach (KeyValuePair<string, string> kv in d) lines.Add(kv.Key + "=" + kv.Value);
                // write a temporary file next to it and swap it in, so another SnapMeasure process never reads half a file
                string tmp = FilePath + "." + Process.GetCurrentProcess().Id + ".tmp";
                try
                {
                    File.WriteAllLines(tmp, lines.ToArray());
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            // .NET 4: File.Replace needs an existing target and File.Move can't overwrite
                            if (File.Exists(FilePath))
                            {
                                try { File.Replace(tmp, FilePath, null, true); }
                                catch (PlatformNotSupportedException) { File.Copy(tmp, FilePath, true); }   // e.g. FAT USB stick
                            }
                            else File.Move(tmp, FilePath);
                            break;
                        }
                        catch (IOException)
                        {
                            if (attempt >= 4) throw;
                            Thread.Sleep(40);      // another SnapMeasure is swapping the file in right now
                        }
                    }
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
                readStamp = File.GetLastWriteTimeUtc(FilePath);
            }
            catch { }
        }

        public static string Get(string key, string def) { Refresh(); string v; return d.TryGetValue(key, out v) ? v : def; }
        public static void Set(string key, string val) { Refresh(); d[key] = val; Save(); }
        public static bool GetBool(string key, bool def) { string v = Get(key, null); return v == null ? def : v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); }
        public static int GetInt(string key, int def) { int v; return int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def; }
        public static double GetDouble(string key, double def) { double v; return double.TryParse(Get(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0 ? v : def; }

        public static string SaveFolder
        {
            get
            {
                string f = Get("SaveFolder", "");
                if (string.IsNullOrEmpty(f)) f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnapMeasure");
                return f;
            }
        }
    }

    /// <summary>Always-on-top floating copy of an image. Drag to move, drag an edge or corner to resize (keeps proportions),
    /// right-click → Crop to keep just a part, wheel = opacity, Ctrl+wheel = size, double-click/Esc = close.</summary>
    internal class PinForm : Form
    {
        readonly Bitmap img;
        Rectangle src;          // the part of the image that is shown (crop)
        float z;
        bool cropping;
        Point cropA, cropB;
        bool cropDragging;
        const int Grip = 8;
        readonly int B;         // the magenta frame round the pinned picture

        public PinForm(Bitmap b)
        {
            img = b;
            B = Math.Max(2, (int)Math.Round(2 * Native.ScaleAt(Cursor.Position)));
            src = new Rectangle(0, 0, img.Width, img.Height);
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = true;
            Text = "SnapMeasure pin";
            Icon = AppIcon.Get();
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            KeyPreview = true;
            MinimumSize = new Size(24, 24);
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            z = Math.Min(1f, Math.Min(wa.Width * 0.8f / img.Width, wa.Height * 0.8f / img.Height));
            ApplySize();
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);

            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = Native.UiFont("Segoe UI", 9f, Native.ScaleAt(Cursor.Position));
            m.Items.Add("Copy", Icons.Cmd("copy", 16), (s, e) => CopyShown());
            m.Items.Add("Open in editor", Icons.Cmd("open", 16), (s, e) => { new EditorForm(ShownImage(), "Pinned image").Show(); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Crop  (C) – drag the part to keep", Icons.Make(Tool.Crop, 16), (s, e) => StartCrop());
            ToolStripItem reset = m.Items.Add("Undo crop", null, (s, e) => { src = new Rectangle(0, 0, img.Width, img.Height); ApplySize(); });
            ToolStripMenuItem size = new ToolStripMenuItem("Size");
            foreach (int p in new int[] { 25, 50, 75, 100, 150, 200 })
            {
                int pp = p;
                size.DropDownItems.Add(p + "%", null, (s, e) => { z = pp / 100f; ApplySize(); });
            }
            ToolStripMenuItem turn = new ToolStripMenuItem("Rotate / mirror");
            turn.DropDownItems.Add("Rotate right 90°   (R)", null, (s, e) => Turn(RotateFlipType.Rotate90FlipNone));
            turn.DropDownItems.Add("Rotate left 90°   (Shift+R)", null, (s, e) => Turn(RotateFlipType.Rotate270FlipNone));
            turn.DropDownItems.Add("Rotate 180°", null, (s, e) => Turn(RotateFlipType.Rotate180FlipNone));
            turn.DropDownItems.Add(new ToolStripSeparator());
            turn.DropDownItems.Add("Mirror left ↔ right   (H)", null, (s, e) => Turn(RotateFlipType.RotateNoneFlipX));
            turn.DropDownItems.Add("Flip upside down   (V)", null, (s, e) => Turn(RotateFlipType.RotateNoneFlipY));
            m.Items.Add(turn);
            m.Items.Add(size);
            ToolStripMenuItem op = new ToolStripMenuItem("Opacity");
            foreach (int p in new int[] { 100, 80, 60, 40, 25 })
            {
                int pp = p;
                op.DropDownItems.Add(p + "%", null, (s, e) => Opacity = pp / 100.0);
            }
            m.Items.Add(op);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Unpin – back to the editor  (Ctrl+P / Esc)", Icons.Cmd("close", 16), (s, e) => Close());
            m.Opening += (s, e) => reset.Enabled = src.Width != img.Width || src.Height != img.Height;
            ContextMenuStrip = m;
        }

        /// <summary>Rotates or mirrors the pinned picture (and its crop) about the pin's centre; the size stays the same.</summary>
        public void Turn(RotateFlipType t)
        {
            if (cropping) return;
            int W = img.Width, H = img.Height;
            Rectangle r = src;
            switch (t)
            {
                case RotateFlipType.Rotate90FlipNone: r = new Rectangle(H - src.Bottom, src.X, src.Height, src.Width); break;
                case RotateFlipType.Rotate270FlipNone: r = new Rectangle(src.Y, W - src.Right, src.Height, src.Width); break;
                case RotateFlipType.Rotate180FlipNone: r = new Rectangle(W - src.Right, H - src.Bottom, src.Width, src.Height); break;
                case RotateFlipType.RotateNoneFlipX: r = new Rectangle(W - src.Right, src.Y, src.Width, src.Height); break;
                case RotateFlipType.RotateNoneFlipY: r = new Rectangle(src.X, H - src.Bottom, src.Width, src.Height); break;
                default: return;
            }
            Point c = new Point(Left + Width / 2, Top + Height / 2);
            img.RotateFlip(t);
            src = r;
            ApplySize();
            Location = new Point(c.X - Width / 2, c.Y - Height / 2);
        }

        /// <summary>For the self-test: the pinned picture's size and the shown part.</summary>
        public string Describe() { return img.Width + "x" + img.Height + " " + src.X + "," + src.Y + "," + src.Width + "," + src.Height; }

        /// <summary>The part of the picture shown (as after a crop).</summary>
        public Rectangle ShownPart { get { return src; } set { src = Rectangle.Intersect(value, new Rectangle(0, 0, img.Width, img.Height)); ApplySize(); } }

        /// <summary>For the self-test: the colour of a pixel of the shown part.</summary>
        public Color ShownPixel(int x, int y) { return img.GetPixel(src.X + x, src.Y + y); }

        Bitmap ShownImage()
        {
            Bitmap b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b)) g.DrawImage(img, new Rectangle(0, 0, src.Width, src.Height), src, GraphicsUnit.Pixel);
            return b;
        }

        void CopyShown()
        {
            using (Bitmap b = ShownImage())
                if (!App.TrySetClipboardImage(b))
                    MessageBox.Show(this, "The clipboard is busy (another program has it open). Try again.", "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ApplySize()
        {
            ClientSize = new Size(Math.Max(16, (int)(src.Width * z)) + 2 * B, Math.Max(16, (int)(src.Height * z)) + 2 * B);
            Invalidate();
        }

        void StartCrop()
        {
            cropping = true;
            cropDragging = false;
            Cursor = Cursors.Cross;
            Invalidate();
        }

        Rectangle ScreenCropRect { get { return Rectangle.FromLTRB(Math.Min(cropA.X, cropB.X), Math.Min(cropA.Y, cropB.Y), Math.Max(cropA.X, cropB.X), Math.Max(cropA.Y, cropB.Y)); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.White);
            g.InterpolationMode = Math.Abs(z - 1f) < 0.001f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(img, new Rectangle(B, B, ClientSize.Width - 2 * B, ClientSize.Height - 2 * B), src, GraphicsUnit.Pixel);
            g.PixelOffsetMode = PixelOffsetMode.Default;
            using (SolidBrush mb = new SolidBrush(Color.Magenta))
            using (Region fr = new Region(ClientRectangle))
            {
                fr.Exclude(new Rectangle(B, B, ClientSize.Width - 2 * B, ClientSize.Height - 2 * B));
                g.FillRegion(mb, fr);
            }
            if (cropping)
            {
                Rectangle r = cropDragging ? ScreenCropRect : Rectangle.Empty;
                using (Region rg = new Region(ClientRectangle))
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                {
                    if (r.Width > 0) rg.Exclude(r);
                    g.FillRegion(dim, rg);
                }
                if (r.Width > 0) using (Pen p = new Pen(Color.White, 1.5f)) { p.DashStyle = DashStyle.Dash; g.DrawRectangle(p, r); }
                g.DrawString("Drag the part to keep · Esc = cancel", Renderer.GetFont(12f, false), Brushes.White, 6, 6);
            }
        }

        // resize from the edges and corners, like a normal window
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84, WM_SIZING = 0x214;
            if (m.Msg == WM_NCHITTEST && !cropping)
            {
                base.WndProc(ref m);
                Point p = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
                bool l = p.X < Grip, r = p.X >= ClientSize.Width - Grip, t = p.Y < Grip, b = p.Y >= ClientSize.Height - Grip;
                if (t && l) m.Result = (IntPtr)13; else if (t && r) m.Result = (IntPtr)14;
                else if (b && l) m.Result = (IntPtr)16; else if (b && r) m.Result = (IntPtr)17;
                else if (l) m.Result = (IntPtr)10; else if (r) m.Result = (IntPtr)11;
                else if (t) m.Result = (IntPtr)12; else if (b) m.Result = (IntPtr)15;
                return;
            }
            if (m.Msg == WM_SIZING)
            {
                // keep the picture's proportions while resizing
                Native.RECT rc = (Native.RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                int edge = m.WParam.ToInt32();
                int w = rc.Right - rc.Left - 2 * B, h = rc.Bottom - rc.Top - 2 * B;
                float ratio = (float)src.Width / src.Height;
                if (edge == 1 || edge == 2) h = (int)Math.Round(w / ratio);          // left / right
                else if (edge == 3 || edge == 6) w = (int)Math.Round(h * ratio);     // top / bottom
                else { if (w / ratio > h) h = (int)Math.Round(w / ratio); else w = (int)Math.Round(h * ratio); }
                w = Math.Max(16, w); h = Math.Max(16, h);
                if (edge == 1 || edge == 4 || edge == 7) rc.Left = rc.Right - w - 2 * B; else rc.Right = rc.Left + w + 2 * B;
                if (edge == 3 || edge == 4 || edge == 5) rc.Top = rc.Bottom - h - 2 * B; else rc.Bottom = rc.Top + h + 2 * B;
                System.Runtime.InteropServices.Marshal.StructureToPtr(rc, m.LParam, false);
                z = (float)w / src.Width;
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (cropping)
            {
                if (e.Button == MouseButtons.Left) { cropA = cropB = e.Location; cropDragging = true; Invalidate(); }
                return;
            }
            if (e.Button == MouseButtons.Left && e.Clicks == 1)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (cropping && cropDragging)
            {
                cropB = new Point(Math.Max(B, Math.Min(ClientSize.Width - B - 1, e.X)), Math.Max(B, Math.Min(ClientSize.Height - B - 1, e.Y)));
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (cropping && cropDragging)
            {
                cropDragging = false;
                cropping = false;
                Cursor = Cursors.Default;
                Rectangle r = ScreenCropRect;
                if (r.Width >= 4 && r.Height >= 4)
                {
                    float sx = (float)src.Width / (ClientSize.Width - 2 * B), sy = (float)src.Height / (ClientSize.Height - 2 * B);
                    Point oldTopLeft = PointToScreen(r.Location);
                    Rectangle ns = Rectangle.Round(new RectangleF(src.X + (r.X - B) * sx, src.Y + (r.Y - B) * sy, r.Width * sx, r.Height * sy));
                    ns.Intersect(src);
                    if (ns.Width >= 2 && ns.Height >= 2)
                    {
                        src = ns;
                        ApplySize();
                        Location = new Point(oldTopLeft.X - B, oldTopLeft.Y - B);   // the kept part stays where it was
                    }
                }
                Invalidate();
                return;
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e) { if (!cropping) Close(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) != 0) { z = Math.Max(0.05f, Math.Min(8f, z * (e.Delta > 0 ? 1.1f : 0.9f))); ApplySize(); }
            else Opacity = Math.Max(0.15, Math.Min(1.0, Opacity + (e.Delta > 0 ? 0.1 : -0.1)));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                if (cropping) { cropping = cropDragging = false; Cursor = Cursors.Default; Invalidate(); }
                else Close();
            }
            else if (e.Control && e.KeyCode == Keys.P) Close();          // unpin: back to the editor
            else if (e.Control && e.KeyCode == Keys.C) CopyShown();
            else if (e.KeyCode == Keys.C && !e.Control && !e.Alt) StartCrop();
            else if (e.KeyCode == Keys.R && !e.Control && !e.Alt) Turn(e.Shift ? RotateFlipType.Rotate270FlipNone : RotateFlipType.Rotate90FlipNone);
            else if (e.KeyCode == Keys.H && !e.Control && !e.Alt) Turn(RotateFlipType.RotateNoneFlipX);
            else if (e.KeyCode == Keys.V && !e.Control && !e.Alt) Turn(RotateFlipType.RotateNoneFlipY);
        }

        /// <summary>The editor this pin came from: it was minimized when pinning and comes back when the pin closes.</summary>
        public Form Source;
        public FormWindowState SourceState = FormWindowState.Normal;

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (Source != null && !Source.IsDisposed)
            {
                if (Source.WindowState == FormWindowState.Minimized) Source.WindowState = SourceState;
                Source.Activate();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) img.Dispose();
            base.Dispose(disposing);
        }
    }


    internal static class Dialogs
    {
        static float K(IWin32Window owner)
        {
            return Native.ScaleAt(Cursor.Position);
        }

        static Form Base(string title, float k, int w, int h)
        {
            Form f = new Form();
            f.Text = title;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.CenterParent;
            f.AutoScaleMode = AutoScaleMode.None;
            f.Font = Native.UiFont("Segoe UI", 9f, k);
            f.ClientSize = new Size((int)(w * k), (int)(h * k));
            f.Load += (s, e) => Lang.Apply(f, null);       // translated once all its boxes and buttons are in
            return f;
        }

        /// <summary>A tick box with a long text: a fixed box the text wraps in (an auto-sized tick box never wraps,
        /// it runs past the edge of the tab), the tick next to the first line.</summary>
        static void WrapBox(CheckBox c, Rectangle bounds)
        {
            c.AutoSize = false;
            c.CheckAlign = ContentAlignment.TopLeft;
            c.TextAlign = ContentAlignment.TopLeft;
            c.Bounds = bounds;
        }

        /// <summary>
        /// A dialog opened from an always-on-top overlay (on-screen ruler / protractor) must stay above it and
        /// must not cover it, otherwise the overlay hides the input box: place it just below (or above) the overlay.
        /// </summary>
        static void KeepClearOf(Form f, IWin32Window owner)
        {
            Form of = owner as Form;
            if (of == null || !of.TopMost) return;
            f.TopMost = true;
            f.StartPosition = FormStartPosition.Manual;
            Rectangle wa = Screen.FromControl(of).WorkingArea, ob = of.Bounds;
            int gap = 12, w = f.Width, h = f.Height;
            int x = Math.Max(wa.Left, Math.Min(ob.Left + (ob.Width - w) / 2, wa.Right - w));
            int y = ob.Bottom + gap;
            if (y + h > wa.Bottom) y = ob.Top - gap - h;
            if (y < wa.Top) y = Math.Max(wa.Top, Math.Min(ob.Bottom + gap, wa.Bottom - h));
            f.Location = new Point(x, y);
        }

        static void Buttons(Form f, float k)
        {
            Button ok = new Button(), cancel = new Button();
            ok.Text = "OK"; cancel.Text = "Cancel";
            ok.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel;
            ok.Size = cancel.Size = new Size((int)(84 * k), (int)(28 * k));
            cancel.Location = new Point(f.ClientSize.Width - cancel.Width - (int)(12 * k), f.ClientSize.Height - cancel.Height - (int)(12 * k));
            ok.Location = new Point(cancel.Left - ok.Width - (int)(8 * k), cancel.Top);
            f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;
        }

        public static string Ask(IWin32Window owner, string title, string initial, bool multiline)
        {
            float k = K(owner);
            using (Form f = Base(title, k, 380, multiline ? 170 : 96))
            {
                TextBox t = new TextBox();
                t.Multiline = multiline;
                t.ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None;
                t.Text = initial ?? "";
                t.SetBounds((int)(12 * k), (int)(12 * k), f.ClientSize.Width - (int)(24 * k), multiline ? (int)(90 * k) : (int)(26 * k));
                f.Controls.Add(t);
                Buttons(f, k);
                KeepClearOf(f, owner);
                f.Shown += (s, e) => { f.Activate(); t.Focus(); t.SelectAll(); };
                return f.ShowDialog(owner) == DialogResult.OK ? t.Text : null;
            }
        }

        /// <summary>A question with three plain-worded buttons. Returns 0, 1 or 2 (the button), or -1 if closed.</summary>
        public static int Choose(IWin32Window owner, string title, string message, string b0, string b1, string b2)
        {
            float k = K(owner);
            Func<float, int> S = v => (int)Math.Round(v * k);
            using (Form f = Base(title, k, 470, 120))
            {
                if (owner == null) f.StartPosition = FormStartPosition.CenterScreen;
                PictureBox ic = new PictureBox();
                ic.Image = SystemIcons.Question.ToBitmap();
                f.FormClosed += (s, e) => { Image im = ic.Image; ic.Image = null; if (im != null) im.Dispose(); };
                ic.SizeMode = PictureBoxSizeMode.Zoom;
                ic.SetBounds(S(14), S(14), S(32), S(32));
                Label l = new Label(); l.Text = message; l.AutoSize = true; l.MaximumSize = new Size(S(390), 0); l.Location = new Point(S(58), S(20));
                f.Controls.Add(ic); f.Controls.Add(l);
                int result = -1;
                string[] texts = { b0, b1, b2 };
                int x = S(458);
                for (int i = 2; i >= 0; i--)
                {
                    Button b = new Button();
                    b.Text = texts[i];
                    int w = Math.Max(S(96), TextRenderer.MeasureText(texts[i], f.Font).Width + S(24));
                    x -= w + S(8);
                    b.SetBounds(x + S(8), f.ClientSize.Height - S(40), w, S(28));
                    int idx = i;
                    b.Click += (s, e) => { result = idx; f.Close(); };
                    f.Controls.Add(b);
                    if (i == 0) f.AcceptButton = b;
                    if (i == 2) f.CancelButton = b;
                }
                f.ShowDialog(owner);
                return result;
            }
        }

        /// <summary>Output settings: save folder, file name pattern, format, quality, and what else to do on save.</summary>
        public static void OutputSettings(IWin32Window owner) { ShowSettings(owner, 2); }

        /// <summary>
        /// Settings window: Capture (what PrtSc does, mouse pointer, sound, notifications, magnifier, delay),
        /// Output (folder, file name, format, quality) and About (version, GitHub, updates, shortcuts).
        /// </summary>
        public static void ShowSettings(IWin32Window owner, int startTab)
        {
            float k = K(owner);
            Func<float, int> S = v => (int)Math.Round(v * k);
            using (Form f = Base("SnapMeasure settings", k, 560, 520))
            {
                if (owner == null) f.StartPosition = FormStartPosition.CenterScreen;
                f.Icon = AppIcon.Get();
                f.ShowIcon = true;
                TabControl tabs = new TabControl();
                tabs.SetBounds(S(8), S(8), S(544), S(458));
                TabPage tGen = new TabPage("General"), tCap = new TabPage("Capture"), tOut = new TabPage("Output"), tAbout = new TabPage("About");
                tabs.TabPages.Add(tGen); tabs.TabPages.Add(tCap); tabs.TabPages.Add(tOut); tabs.TabPages.Add(tAbout);
                f.Controls.Add(tabs);
                f.FormClosed += (s, e) => { if (Hotkeys.Pause != null) Hotkeys.Pause(false); };   // never leave the keys switched off

                // ---------- General: capture keys and icon size ----------
                HotkeyBox[,] keyBox = new HotkeyBox[Hotkeys.Ids.Length, 2];
                ComboBox iconSize = new ComboBox();
                {
                    Label intro = new Label(); intro.AutoSize = true; intro.MaximumSize = new Size(S(510), 0); intro.Location = new Point(S(12), S(12));
                    intro.Text = "Capture keys – click a box and press the keys you want. Delete or Backspace clears it.";
                    Label hk1 = new Label(); hk1.Text = "Key"; hk1.AutoSize = true; hk1.Location = new Point(S(232), S(40)); hk1.Font = new Font(f.Font, FontStyle.Bold);
                    Label hk2 = new Label(); hk2.Text = "Also"; hk2.AutoSize = true; hk2.Location = new Point(S(382), S(40)); hk2.Font = new Font(f.Font, FontStyle.Bold);
                    Label note = new Label(); note.AutoSize = true; note.MaximumSize = new Size(S(510), 0); note.ForeColor = Color.FromArgb(170, 60, 0);
                    note.Location = new Point(S(12), S(62 + Hotkeys.Ids.Length * 30 + 4));
                    tGen.Controls.AddRange(new Control[] { intro, hk1, hk2, note });
                    for (int i = 0; i < Hotkeys.Ids.Length; i++)
                    {
                        Label l = new Label(); l.Text = Hotkeys.Labels[i]; l.AutoSize = true; l.Location = new Point(S(12), S(64 + i * 30));
                        tGen.Controls.Add(l);
                        for (int slot = 0; slot < 2; slot++)
                        {
                            HotkeyBox b = new HotkeyBox();
                            b.SetBounds(S(230 + slot * 150), S(60 + i * 30), S(140), S(24));
                            b.Value = Hotkeys.Get(i, slot);
                            b.Note = note;
                            keyBox[i, slot] = b;
                            tGen.Controls.Add(b);
                        }
                    }
                    Button reset = new Button(); reset.Text = "Default keys"; reset.SetBounds(S(380), S(62 + Hotkeys.Ids.Length * 30 + 26), S(140), S(26));
                    reset.Click += (s, e) => { for (int i = 0; i < Hotkeys.Ids.Length; i++) for (int slot = 0; slot < 2; slot++) keyBox[i, slot].Value = Hotkeys.Default(i, slot); note.Text = ""; };
                    Label sepG = new Label(); sepG.BorderStyle = BorderStyle.Fixed3D; sepG.SetBounds(S(12), S(300), S(510), 2);
                    Label li = new Label(); li.Text = "Tool icon size"; li.AutoSize = true; li.Location = new Point(S(12), S(318));
                    iconSize.DropDownStyle = ComboBoxStyle.DropDownList;
                    iconSize.Items.AddRange(new object[] { "Small (16 px)", "Medium (20 px)", "Large (24 px)" });
                    int curIcon = SnapMeasure.Settings.GetInt("IconSize", 20);
                    iconSize.SelectedIndex = curIcon <= 16 ? 0 : (curIcon >= 24 ? 2 : 1);
                    iconSize.SetBounds(S(230), S(314), S(140), S(26));
                    Label lin = new Label(); lin.Text = "(new editor windows)"; lin.AutoSize = true;
                    lin.ForeColor = Color.FromArgb(90, 90, 90); lin.Location = new Point(S(378), S(318));
                    CheckBox cLock = new CheckBox(); cLock.Name = "lock"; cLock.AutoSize = true; cLock.Location = new Point(S(12), S(348));
                    cLock.Text = "Editor: start with \"Keep inside\" locked (Ctrl+L switches it in the editor)";
                    cLock.Checked = SnapMeasure.Settings.GetBool("LockInside", false);
                    Label lLang = new Label(); lLang.Text = "Language"; lLang.AutoSize = true; lLang.Location = new Point(S(12), S(382));
                    ComboBox lang = new ComboBox(); lang.Name = "lang"; lang.DropDownStyle = ComboBoxStyle.DropDownList;
                    lang.Font = new Font("Malgun Gothic", f.Font.Size, GraphicsUnit.Pixel);     // shows 한국어 in English mode too
                    lang.Items.AddRange(Lang.Names);
                    lang.SelectedIndex = Math.Max(0, Array.IndexOf(Lang.Codes, Lang.Code));
                    lang.SetBounds(S(230), S(378), S(140), S(26));
                    Label lLangN = new Label(); lLangN.Text = "(new windows; restart for the tray menu)"; lLangN.AutoSize = true;
                    lLangN.ForeColor = Color.FromArgb(90, 90, 90); lLangN.Location = new Point(S(378), S(382));
                    lLangN.MaximumSize = new Size(S(150), 0);
                    tGen.Controls.AddRange(new Control[] { reset, sepG, li, iconSize, lin, cLock, lLang, lang, lLangN });
                }

                // ---------- Capture ----------
                {
                    Label la = new Label(); la.Text = "After a capture (PrtSc, Ctrl+Shift+1 …):"; la.AutoSize = true; la.Location = new Point(S(12), S(14));
                    tCap.Controls.Add(la);
                    string[] acts = { "Open it in the SnapMeasure editor (default)", "Only copy it to the clipboard", "Only save it to the save folder", "Copy it and save it (no editor)" };
                    int cur = SnapMeasure.Settings.GetInt("AfterCapture", 0);
                    for (int i = 0; i < acts.Length; i++)
                    {
                        RadioButton r = new RadioButton(); r.Text = acts[i]; r.AutoSize = true; r.Location = new Point(S(24), S(38 + i * 24));
                        r.Checked = i == cur; r.Tag = i; r.Name = "after" + i;
                        tCap.Controls.Add(r);
                    }
                    CheckBox cClip = new CheckBox(); cClip.Name = "clip"; cClip.Text = "When the editor opens, also copy the capture to the clipboard";
                    cClip.AutoSize = true; cClip.Location = new Point(S(24), S(136)); cClip.Checked = SnapMeasure.Settings.GetBool("CopyOnCapture", true);
                    Label sep = new Label(); sep.BorderStyle = BorderStyle.Fixed3D; sep.SetBounds(S(12), S(166), S(510), 2);
                    CheckBox cCur = new CheckBox(); cCur.Name = "cursor"; cCur.Text = "Capture the mouse pointer"; cCur.AutoSize = true; cCur.Location = new Point(S(12), S(178));
                    cCur.Checked = SnapMeasure.Settings.GetBool("CaptureCursor", false);
                    CheckBox cSnd = new CheckBox(); cSnd.Name = "sound"; cSnd.Text = "Play a camera sound"; cSnd.AutoSize = true; cSnd.Location = new Point(S(12), S(204));
                    cSnd.Checked = SnapMeasure.Settings.GetBool("CameraSound", false);
                    CheckBox cNot = new CheckBox(); cNot.Name = "notify"; cNot.Text = "Show a notification when a capture is copied or saved"; cNot.AutoSize = true; cNot.Location = new Point(S(12), S(230));
                    cNot.Checked = SnapMeasure.Settings.GetBool("Notify", true);
                    CheckBox cMag = new CheckBox(); cMag.Name = "mag"; cMag.Text = "Show the magnifier while picking an area"; cMag.AutoSize = true; cMag.Location = new Point(S(12), S(256));
                    cMag.Checked = SnapMeasure.Settings.GetBool("Magnifier", true);
                    NumericUpDown nd = new NumericUpDown(); nd.Name = "delay"; nd.Minimum = 0; nd.Maximum = 10000; nd.Increment = 100;
                    nd.Value = Math.Max(0, Math.Min(10000, SnapMeasure.Settings.GetInt("CaptureDelay", 0)));
                    nd.SetBounds(S(12), S(284), S(80), S(26));
                    Label ld = new Label(); ld.Text = "milliseconds to wait before capturing (lets menus close)"; ld.AutoSize = true; ld.Location = new Point(S(98), S(287));
                    Label sep2 = new Label(); sep2.BorderStyle = BorderStyle.Fixed3D; sep2.SetBounds(S(12), S(320), S(510), 2);
                    CheckBox cMatch = new CheckBox(); cMatch.Name = "match"; cMatch.Text = "Editor: match the capture size (open the window just big enough for the picture at 100 %)";
                    WrapBox(cMatch, new Rectangle(S(12), S(332), S(510), S(40)));
                    cMatch.Checked = SnapMeasure.Settings.GetBool("MatchCaptureSize", false);
                    foreach (Control c in new Control[] { cClip, sep, cCur, cSnd, cNot, cMag, nd, ld, sep2, cMatch }) tCap.Controls.Add(c);
                }

                // ---------- Output ----------
                TabPage tp = tOut;
                Func<string, int, Label> lab = (t, y) =>
                {
                    Label l = new Label(); l.Text = t; l.AutoSize = true; l.Location = new Point(S(12), S(y + 4)); tp.Controls.Add(l); return l;
                };
                lab("Save folder", 14); lab("File name", 48); lab("Example", 80); lab("Image format", 112); lab("JPEG quality", 146);

                TextBox folder = new TextBox(); folder.Text = Settings.SaveFolder; folder.SetBounds(S(120), S(14), S(340), S(26));
                Button browse = new Button(); browse.Text = "…"; browse.SetBounds(S(466), S(13), S(40), S(26));
                browse.Click += (s, e) =>
                {
                    using (FolderBrowserDialog d = new FolderBrowserDialog())
                    {
                        d.Description = "Where should Save (Ctrl+S) put your pictures?";
                        try { d.SelectedPath = folder.Text; } catch { }
                        if (d.ShowDialog(f) == DialogResult.OK) folder.Text = d.SelectedPath;
                    }
                };
                TextBox pattern = new TextBox(); pattern.Text = Output.Pattern; pattern.SetBounds(S(120), S(48), S(340), S(26));
                Button help = new Button(); help.Text = "?"; help.SetBounds(S(466), S(47), S(40), S(26));
                help.Click += (s, e) => MessageBox.Show(f,
                    "Placeholders you can use in the file name:\n\n" +
                    "${capturetime:yyyy-MM-dd HH_mm_ss}   date and time in any format\n" +
                    "${date}   e.g. 2026-11-18\n" +
                    "${time}   e.g. 14-05-33\n" +
                    "${title}   the window or capture name\n" +
                    "${n}   running number 001, 002 … (${n:D4} = 0001)\n\n" +
                    "Characters that aren't allowed in file names become _ .",
                    "File name pattern", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Label example = new Label(); example.AutoSize = true; example.ForeColor = Color.FromArgb(0, 90, 170); example.Location = new Point(S(120), S(84));
                ComboBox fmt = new ComboBox(); fmt.DropDownStyle = ComboBoxStyle.DropDownList;
                fmt.Items.AddRange(Output.FormatNames);
                fmt.SetBounds(S(120), S(112), S(340), S(26));
                fmt.SelectedIndex = Array.IndexOf(Output.Formats, Output.Format);
                TrackBar q = new TrackBar(); q.Minimum = 10; q.Maximum = 100; q.TickStyle = TickStyle.None; q.AutoSize = false;
                q.SetBounds(S(116), S(144), S(300), S(28)); q.Value = Output.JpegQuality;
                Label ql = new Label(); ql.AutoSize = true; ql.Location = new Point(S(424), S(150)); ql.Text = q.Value + " %";
                q.ValueChanged += (s, e) => ql.Text = q.Value + " %";
                CheckBox proj = new CheckBox(); proj.Text = "Ctrl+S also saves an editable .snapm project next to the picture";
                proj.AutoSize = true; proj.Location = new Point(S(14), S(186)); proj.Checked = Output.SaveProject;
                CheckBox copy = new CheckBox(); copy.Text = "Copy the file path to the clipboard after saving";
                copy.AutoSize = true; copy.Location = new Point(S(14), S(212)); copy.Checked = Output.CopyPath;
                CheckBox explorer = new CheckBox(); explorer.Text = "Show .snapm projects with their picture in File Explorer (thumbnails and preview pane)";
                explorer.AutoSize = true; explorer.MaximumSize = new Size(S(510), 0); explorer.Location = new Point(S(14), S(238));
                explorer.Checked = ShellPreview.Supported && ShellPreview.IsOn; explorer.Enabled = ShellPreview.Supported;
                if (!ShellPreview.Supported) explorer.Text += " – not in the Store version yet";
                bool paneWas = ShellPreview.Supported && ShellPreview.PaneIsOn;
                CheckBox pane = new CheckBox(); pane.Text = "Also in the preview pane (Alt+P), and for projects from other PCs – Windows asks for administrator approval once";
                WrapBox(pane, new Rectangle(S(34), S(262), S(490), S(40)));
                pane.Checked = paneWas; pane.Enabled = ShellPreview.Supported;

                Action update = () =>
                {
                    string ext = fmt.SelectedIndex >= 0 ? Output.Formats[fmt.SelectedIndex] : "png";
                    string n = Output.MakeName(pattern.Text, DateTime.Now, "Autodesk AutoCAD LT 2026 - [plan.dwg]", 1);
                    example.Text = n + "." + ext + (proj.Checked ? "   +   " + n + ".snapm" : "");
                    q.Enabled = ext == "jpg" || ext == "pdf";
                };
                pattern.TextChanged += (s, e) => update();
                fmt.SelectedIndexChanged += (s, e) => update();
                proj.CheckedChanged += (s, e) => update();
                update();

                foreach (Control c in new Control[] { folder, browse, pattern, help, example, fmt, q, ql, proj, copy, explorer, pane }) tOut.Controls.Add(c);

                // ---------- About ----------
                {
                    PictureBox logo = new PictureBox(); logo.Image = AppIcon.Render(S(56)); logo.SetBounds(S(14), S(14), S(56), S(56));
                    f.FormClosed += (s, e) => { Image im = logo.Image; logo.Image = null; if (im != null) im.Dispose(); };
                    Label name = new Label(); name.Text = AppInfo.Name + "  " + AppInfo.Version; name.Font = Native.UiFont("Segoe UI", 15f, k, FontStyle.Bold);
                    name.AutoSize = true; name.Location = new Point(S(82), S(12));
                    Label sub = new Label(); sub.UseMnemonic = false; sub.AutoSize = true; sub.ForeColor = Color.FromArgb(90, 90, 90); sub.Location = new Point(S(84), S(46));
                    sub.Text = "Portable screenshot, measuring & quantity takeoff tool\nMade by " + AppInfo.Author + "  ·  free and open source (MIT licence)";
                    LinkLabel repo = new LinkLabel(); repo.Text = AppInfo.RepoUrl.Replace("https://", ""); repo.AutoSize = true; repo.Location = new Point(S(84), S(84));
                    repo.LinkClicked += (s, e) => AppInfo.OpenUrl(AppInfo.RepoUrl);
                    LinkLabel store = new LinkLabel(); store.Text = "Microsoft Store"; store.AutoSize = true;
                    store.Location = new Point(S(84) + TextRenderer.MeasureText(repo.Text, f.Font).Width + S(24), S(84));
                    store.LinkClicked += (s, e) => AppInfo.OpenUrl(AppInfo.StoreUrl);
                    new ToolTip().SetToolTip(store, "SnapMeasure in the Microsoft Store: signed, and it updates itself");
                    LinkLabel upd = new LinkLabel(); upd.Text = "Check for updates"; upd.AutoSize = true; upd.Location = new Point(S(84), S(106));
                    LinkLabel iss = new LinkLabel(); iss.Text = "Report a problem or ask for a feature"; iss.AutoSize = true; iss.Location = new Point(S(220), S(106));
                    iss.LinkClicked += (s, e) => AppInfo.OpenUrl(AppInfo.IssuesUrl);
                    Label us = new Label(); us.AutoSize = true; us.ForeColor = Color.FromArgb(90, 90, 90); us.Location = new Point(S(84), S(126));
                    upd.LinkClicked += (s, e) => { us.Text = Lang.T("Checking GitHub…"); UpdateCheck.Start(f, m => us.Text = m); };
                    if (AppInfo.IsStoreApp)
                    {
                        // the Microsoft Store keeps this copy up to date
                        upd.Visible = false;
                        us.Text = Lang.T("Installed from the Microsoft Store – updates arrive through the Store.");
                        iss.Location = upd.Location;
                    }
                    TextBox keys = new TextBox(); keys.Multiline = true; keys.ReadOnly = true; keys.ScrollBars = ScrollBars.Vertical;
                    keys.BackColor = Color.FromArgb(247, 248, 250); keys.Font = Native.UiFont("Consolas", 8.5f, k);
                    keys.Text = AboutText.Shortcuts(App.FailedHotkeys).Replace("\r\n", "\n").Replace("\n", "\r\n");
                    keys.SetBounds(S(12), S(150), S(512), S(220));
                    foreach (Control c in new Control[] { logo, name, sub, repo, store, upd, iss, us, keys }) tAbout.Controls.Add(c);
                }

                tabs.SelectedIndex = Math.Max(0, Math.Min(tabs.TabCount - 1, startTab));
                Buttons(f, k);
                while (f.ShowDialog(owner) == DialogResult.OK)
                {
                    // the same key may not start two different captures
                    Dictionary<Keys, string> used = new Dictionary<Keys, string>();
                    string clash = null;
                    for (int i = 0; i < Hotkeys.Ids.Length && clash == null; i++)
                        for (int slot = 0; slot < 2; slot++)
                        {
                            Keys kk = keyBox[i, slot].Value;
                            if (kk == Keys.None) continue;
                            string other;
                            if (used.TryGetValue(kk, out other) && other != Hotkeys.Labels[i]) { clash = Hotkeys.Text(kk) + " is set for both \"" + other + "\" and \"" + Hotkeys.Labels[i] + "\"."; break; }
                            used[kk] = Hotkeys.Labels[i];
                        }
                    if (clash != null) { tabs.SelectedTab = tGen; MessageBox.Show(f, clash + "\n\nGive each capture its own keys.", "Settings"); continue; }
                    for (int i = 0; i < Hotkeys.Ids.Length; i++)
                        for (int slot = 0; slot < 2; slot++) Hotkeys.Set(i, slot, keyBox[i, slot].Value);
                    SnapMeasure.Settings.Set("IconSize", (iconSize.SelectedIndex == 0 ? 16 : iconSize.SelectedIndex == 2 ? 24 : 20).ToString(CultureInfo.InvariantCulture));
                    CheckBox lockBox = tGen.Controls["lock"] as CheckBox;
                    if (lockBox != null) SnapMeasure.Settings.Set("LockInside", lockBox.Checked ? "1" : "0");
                    ComboBox langBox = tGen.Controls["lang"] as ComboBox;
                    if (langBox != null && langBox.SelectedIndex >= 0) SnapMeasure.Settings.Set("Language", Lang.Codes[langBox.SelectedIndex]);

                    string dir = folder.Text.Trim();
                    bool okDir = false;
                    try { okDir = dir.Length > 0 && Path.IsPathRooted(dir); if (okDir) Directory.CreateDirectory(dir); }
                    catch (Exception) { okDir = false; }
                    if (!okDir) { tabs.SelectedTab = tOut; MessageBox.Show(f, "Choose a folder that exists or can be created, e.g. C:\\Users\\you\\Pictures\\SnapMeasure", "Settings"); continue; }
                    SnapMeasure.Settings.Set("SaveFolder", dir);
                    SnapMeasure.Settings.Set("FilePattern", string.IsNullOrWhiteSpace(pattern.Text) ? Output.DefaultPattern : pattern.Text.Trim());
                    SnapMeasure.Settings.Set("ImageFormat", Output.Formats[Math.Max(0, fmt.SelectedIndex)]);
                    SnapMeasure.Settings.Set("JpegQuality", q.Value.ToString(CultureInfo.InvariantCulture));
                    SnapMeasure.Settings.Set("SaveProjectToo", proj.Checked ? "1" : "0");
                    SnapMeasure.Settings.Set("CopyPathOnSave", copy.Checked ? "1" : "0");
                    if (ShellPreview.Supported && explorer.Checked != ShellPreview.IsOn)
                    {
                        SnapMeasure.Settings.Set("ExplorerPreview", explorer.Checked ? "1" : "0");
                        string note = ShellPreview.Set(explorer.Checked);
                        if (note != null) MessageBox.Show(f, note, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    if (ShellPreview.Supported && pane.Checked != paneWas)
                    {
                        string note = ShellPreview.SetPane(f, pane.Checked);
                        if (note != null) MessageBox.Show(f, note, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    for (int i = 0; i < 4; i++)
                    {
                        RadioButton r = tCap.Controls["after" + i] as RadioButton;
                        if (r != null && r.Checked) SnapMeasure.Settings.Set("AfterCapture", i.ToString(CultureInfo.InvariantCulture));
                    }
                    Func<string, bool> chk = n => { CheckBox c = tCap.Controls[n] as CheckBox; return c != null && c.Checked; };
                    SnapMeasure.Settings.Set("CopyOnCapture", chk("clip") ? "1" : "0");
                    SnapMeasure.Settings.Set("CaptureCursor", chk("cursor") ? "1" : "0");
                    SnapMeasure.Settings.Set("CameraSound", chk("sound") ? "1" : "0");
                    SnapMeasure.Settings.Set("Notify", chk("notify") ? "1" : "0");
                    SnapMeasure.Settings.Set("Magnifier", chk("mag") ? "1" : "0");
                    SnapMeasure.Settings.Set("MatchCaptureSize", chk("match") ? "1" : "0");
                    NumericUpDown dl = tCap.Controls["delay"] as NumericUpDown;
                    if (dl != null) SnapMeasure.Settings.Set("CaptureDelay", ((int)dl.Value).ToString(CultureInfo.InvariantCulture));
                    return;
                }
            }
        }

        /// <summary>Size and background for a new blank canvas.</summary>
        public static bool NewCanvas(IWin32Window owner, out int w, out int h, out Color bg, out bool grid)
        {
            w = h = 0; bg = Color.White; grid = false;
            float k = K(owner);
            Rectangle scr = Screen.FromPoint(Cursor.Position).Bounds;
            string[] names = { "Screen size (" + scr.Width + " × " + scr.Height + ")", "1920 × 1080 (Full HD)", "1280 × 720", "A4 portrait (1240 × 1754)", "A4 landscape (1754 × 1240)", "800 × 600", "Custom" };
            int[,] sizes = { { scr.Width, scr.Height }, { 1920, 1080 }, { 1280, 720 }, { 1240, 1754 }, { 1754, 1240 }, { 800, 600 }, { 0, 0 } };
            using (Form f = Base("New blank canvas", k, 360, 190))
            {
                Func<string, int, Label> lab = (t, y) =>
                {
                    Label l = new Label(); l.Text = t; l.AutoSize = true; l.Location = new Point((int)(12 * k), (int)((y + 4) * k)); f.Controls.Add(l); return l;
                };
                lab("Size", 12); lab("Width × height", 46); lab("Background", 80);
                ComboBox preset = new ComboBox(); preset.DropDownStyle = ComboBoxStyle.DropDownList; preset.Items.AddRange(names);
                preset.SetBounds((int)(110 * k), (int)(12 * k), (int)(236 * k), (int)(26 * k));
                NumericUpDown nw = new NumericUpDown(), nh = new NumericUpDown();
                foreach (NumericUpDown n in new NumericUpDown[] { nw, nh }) { n.Minimum = 16; n.Maximum = 10000; }
                nw.SetBounds((int)(110 * k), (int)(46 * k), (int)(80 * k), (int)(26 * k));
                nh.SetBounds((int)(206 * k), (int)(46 * k), (int)(80 * k), (int)(26 * k));
                ComboBox back = new ComboBox(); back.DropDownStyle = ComboBoxStyle.DropDownList;
                back.Items.AddRange(new string[] { "White", "Grid paper", "Transparent", "Black" });
                back.SetBounds((int)(110 * k), (int)(80 * k), (int)(236 * k), (int)(26 * k));
                bool busy = false;
                preset.SelectedIndexChanged += (s, e) =>
                {
                    int i = preset.SelectedIndex;
                    if (i < 0 || i == names.Length - 1) return;
                    busy = true; nw.Value = Math.Max(16, Math.Min(10000, sizes[i, 0])); nh.Value = Math.Max(16, Math.Min(10000, sizes[i, 1])); busy = false;
                };
                EventHandler custom = (s, e) => { if (!busy) preset.SelectedIndex = names.Length - 1; };
                nw.ValueChanged += custom; nh.ValueChanged += custom;
                preset.SelectedIndex = Math.Max(0, Math.Min(names.Length - 2, Settings.GetInt("BlankPreset", 1)));
                back.SelectedIndex = Math.Max(0, Math.Min(3, Settings.GetInt("BlankBack", 0)));
                f.Controls.Add(preset); f.Controls.Add(nw); f.Controls.Add(nh); f.Controls.Add(back);
                Buttons(f, k);
                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                w = (int)nw.Value; h = (int)nh.Value;
                int bi = back.SelectedIndex;
                bg = bi == 2 ? Color.Transparent : (bi == 3 ? Color.Black : Color.White);
                grid = bi == 1;
                if (preset.SelectedIndex < names.Length - 1) Settings.Set("BlankPreset", preset.SelectedIndex.ToString(CultureInfo.InvariantCulture));
                Settings.Set("BlankBack", bi.ToString(CultureInfo.InvariantCulture));
                return true;
            }
        }

        public static bool Calibrate(IWin32Window owner, double px, ScaleInfo sc, string what = null)
        {
            float k = K(owner);
            using (Form f = Base("Set scale", k, 440, 150))
            {
                Label l = new Label();
                l.Text = (what ?? "The line you drew") + " is " + px.ToString("N1") + " pixels long.\nWhat is its real length?";
                l.AutoSize = true;
                l.MaximumSize = new Size((int)(416 * k), 0);
                l.Location = new Point((int)(12 * k), (int)(12 * k));
                TextBox t = new TextBox();
                t.SetBounds((int)(12 * k), (int)(58 * k), (int)(160 * k), (int)(26 * k));
                ComboBox u = new ComboBox();
                u.DropDownStyle = ComboBoxStyle.DropDownList;
                u.Items.AddRange(ScaleInfo.Units);
                u.SetBounds((int)(180 * k), (int)(58 * k), (int)(90 * k), (int)(26 * k));
                int idx = Array.IndexOf(ScaleInfo.Units, sc.Unit);
                u.SelectedIndex = sc.Unit == "px" || idx < 0 ? 0 : idx;
                if (sc.Calibrated) t.Text = (px * sc.Upp).ToString("G6", CultureInfo.CurrentCulture);
                f.Controls.Add(l); f.Controls.Add(t); f.Controls.Add(u);
                Buttons(f, k);
                KeepClearOf(f, owner);
                f.Shown += (s, e) => { f.Activate(); t.Focus(); t.SelectAll(); };
                while (f.ShowDialog(owner) == DialogResult.OK)
                {
                    double v;
                    if ((double.TryParse(t.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v) ||
                         double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) && v > 0)
                    {
                        sc.Unit = (string)u.SelectedItem;
                        sc.Upp = v / px;
                        return true;
                    }
                    MessageBox.Show(f, "Please enter a positive number, e.g. 1200", "Set scale");
                }
                return false;
            }
        }

        /// <summary>
        /// Drawing-scale presets: "1:50 on an A1 sheet", "1:100 at 300 dpi", the exact size of a PDF page,
        /// or a scale saved earlier. imgWidth = picture width in px. Returns true if sc was changed.
        /// </summary>
        public static bool ScalePreset(IWin32Window owner, ScaleInfo sc, int imgWidth, double? pdfMmPerPx)
        {
            float k = K(owner);
            Func<float, int> S = v => (int)Math.Round(v * k);
            using (Form f = Base("Drawing scale", k, 470, 330))
            {
                Func<string, int, int, Label> lab = (t, x, y) =>
                {
                    Label l = new Label(); l.Text = t; l.AutoSize = true; l.Location = new Point(S(x), S(y + 4)); f.Controls.Add(l); return l;
                };
                lab("Drawing scale   1 :", 12, 12);
                ComboBox ratio = new ComboBox();
                foreach (int r in ScalePresets.Ratios) ratio.Items.Add(r.ToString(CultureInfo.InvariantCulture));
                ratio.SetBounds(S(140), S(12), S(90), S(26));
                ratio.Text = Settings.Get("PresetRatio", "50");
                lab("Units", 250, 12);
                ComboBox unit = new ComboBox(); unit.DropDownStyle = ComboBoxStyle.DropDownList;
                unit.Items.AddRange(new string[] { "mm", "cm", "m", "in", "ft" });
                unit.SetBounds(S(300), S(12), S(70), S(26));
                unit.SelectedItem = Settings.Get("PresetUnit", "m");
                if (unit.SelectedIndex < 0) unit.SelectedIndex = 2;

                RadioButton rPdf = new RadioButton(), rDpi = new RadioButton(), rSheet = new RadioButton();
                rPdf.Text = "PDF page (exact – SnapMeasure knows the paper size)";
                rDpi.Text = "Picture resolution";
                rSheet.Text = "The picture shows the whole sheet width of";
                rPdf.AutoSize = rDpi.AutoSize = rSheet.AutoSize = true;
                rPdf.Location = new Point(S(12), S(54));
                rDpi.Location = new Point(S(12), S(84));
                rSheet.Location = new Point(S(12), S(114));
                rPdf.Enabled = pdfMmPerPx.HasValue;
                NumericUpDown dpi = new NumericUpDown(); dpi.Minimum = 10; dpi.Maximum = 2400;
                dpi.Value = Math.Max(10, Math.Min(2400, Settings.GetInt("PresetDpi", 300)));
                dpi.SetBounds(S(160), S(82), S(70), S(26));
                lab("dpi", 236, 84);
                ComboBox sheet = new ComboBox(); sheet.DropDownStyle = ComboBoxStyle.DropDownList;
                sheet.Items.AddRange(ScalePresets.Sheets);
                sheet.SelectedIndex = Math.Max(0, Math.Min(4, Settings.GetInt("PresetSheet", 1)));
                sheet.SetBounds(S(300), S(112), S(56), S(26));
                ComboBox orient = new ComboBox(); orient.DropDownStyle = ComboBoxStyle.DropDownList;
                orient.Items.AddRange(new string[] { "landscape", "portrait" });
                orient.SelectedIndex = Settings.GetInt("PresetPortrait", 0) == 1 ? 1 : 0;
                orient.SetBounds(S(362), S(112), S(96), S(26));
                if (pdfMmPerPx.HasValue) rPdf.Checked = true;
                else if (Settings.GetInt("PresetMode", 1) == 2) rSheet.Checked = true;
                else rDpi.Checked = true;

                Label result = new Label();
                result.AutoSize = true;
                result.Font = Native.UiFont("Segoe UI", 9f, k, FontStyle.Bold);
                result.ForeColor = Color.FromArgb(0, 90, 170);
                result.Location = new Point(S(12), S(150));

                // saved scales
                Label sep = new Label(); sep.BorderStyle = BorderStyle.Fixed3D; sep.SetBounds(S(12), S(182), S(446), 2);
                lab("Saved scales", 12, 194);
                ComboBox saved = new ComboBox(); saved.DropDownStyle = ComboBoxStyle.DropDownList;
                saved.SetBounds(S(110), S(194), S(200), S(26));
                Button bUse = new Button(), bSave = new Button(), bDel = new Button();
                bUse.Text = "Use"; bSave.Text = "Save current scale as…"; bDel.Text = "Delete";
                bUse.SetBounds(S(316), S(193), S(64), S(28));
                bDel.SetBounds(S(386), S(193), S(72), S(28));
                bSave.SetBounds(S(110), S(228), S(200), S(28));
                List<ScalePresets.Saved> list = ScalePresets.Load();
                Action fill = () =>
                {
                    saved.Items.Clear();
                    foreach (ScalePresets.Saved p in list) saved.Items.Add(p.Name + "   (1 px = " + p.Upp.ToString("G5", CultureInfo.CurrentCulture) + " " + p.Unit + ")");
                    if (saved.Items.Count > 0) saved.SelectedIndex = 0;
                    bUse.Enabled = bDel.Enabled = saved.Items.Count > 0;
                };
                fill();

                double upp = 0;
                Func<bool> compute = () =>
                {
                    double r;
                    if (!double.TryParse(ratio.Text.Replace("1:", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r) || r <= 0 || r > 1e6)
                    { result.Text = Lang.T("Type the scale, e.g. 50 for 1:50"); return false; }
                    double mmPerPx;
                    if (rPdf.Checked && pdfMmPerPx.HasValue) mmPerPx = pdfMmPerPx.Value;
                    else if (rDpi.Checked) mmPerPx = 25.4 / (double)dpi.Value;
                    else mmPerPx = ScalePresets.SheetWidthMm(sheet.SelectedIndex, orient.SelectedIndex == 0) / Math.Max(1, imgWidth);
                    string u = (string)unit.SelectedItem;
                    upp = ScalePresets.Upp(mmPerPx, r, u);
                    result.Text = "1:" + r.ToString("G", CultureInfo.InvariantCulture) + "  →  1 pixel = " + upp.ToString("G5", CultureInfo.CurrentCulture) + " " + u +
                                  "   (picture width = " + (imgWidth * upp).ToString("N2", CultureInfo.CurrentCulture) + " " + u + ")";
                    return true;
                };
                EventHandler re = (s, e) => compute();
                ratio.TextChanged += re; unit.SelectedIndexChanged += re; dpi.ValueChanged += re; sheet.SelectedIndexChanged += re; orient.SelectedIndexChanged += re;
                rPdf.CheckedChanged += re; rDpi.CheckedChanged += re; rSheet.CheckedChanged += re;
                compute();

                bool changed = false;
                bUse.Click += (s, e) =>
                {
                    if (saved.SelectedIndex < 0) return;
                    ScalePresets.Saved p = list[saved.SelectedIndex];
                    sc.Unit = p.Unit; sc.Upp = p.Upp;
                    changed = true;
                    f.DialogResult = DialogResult.Cancel;   // done – the scale is set already
                    f.Close();
                };
                bDel.Click += (s, e) =>
                {
                    if (saved.SelectedIndex < 0) return;
                    list.RemoveAt(saved.SelectedIndex);
                    ScalePresets.Store(list);
                    fill();
                };
                bSave.Click += (s, e) =>
                {
                    if (!sc.Calibrated) { MessageBox.Show(f, "Set a scale first (calibrate, or pick a drawing scale above and press OK).", "Saved scales"); return; }
                    string n = Ask(f, "Name for this scale (e.g. Project X – 1:20)", "", false);
                    if (string.IsNullOrEmpty(n) || n.Trim().Length == 0) return;
                    ScalePresets.Saved p = new ScalePresets.Saved();
                    p.Name = n.Trim(); p.Unit = sc.Unit; p.Upp = sc.Upp;
                    list.RemoveAll(x => x.Name == p.Name);
                    list.Add(p);
                    ScalePresets.Store(list);
                    fill();
                };

                foreach (Control c in new Control[] { ratio, unit, rPdf, rDpi, rSheet, dpi, sheet, orient, result, sep, saved, bUse, bDel, bSave }) f.Controls.Add(c);
                Buttons(f, k);
                f.Shown += (s, e) => { ratio.Focus(); ratio.SelectAll(); };
                while (f.ShowDialog(owner) == DialogResult.OK)
                {
                    if (!compute()) continue;
                    sc.Unit = (string)unit.SelectedItem;
                    sc.Upp = upp;
                    Settings.Set("PresetRatio", ratio.Text.Trim());
                    Settings.Set("PresetUnit", sc.Unit);
                    Settings.Set("PresetDpi", ((int)dpi.Value).ToString(CultureInfo.InvariantCulture));
                    Settings.Set("PresetSheet", sheet.SelectedIndex.ToString(CultureInfo.InvariantCulture));
                    Settings.Set("PresetPortrait", orient.SelectedIndex.ToString(CultureInfo.InvariantCulture));
                    Settings.Set("PresetMode", rSheet.Checked ? "2" : "1");
                    return true;
                }
                return changed;
            }
        }

        /// <summary>Which page of a PDF to open, and how sharp. Returns false if cancelled.</summary>
        public static bool PdfPage(IWin32Window owner, string file, int pages, out int page, out int dpi)
        {
            page = 1; dpi = 150;
            float k = K(owner);
            Func<float, int> S = v => (int)Math.Round(v * k);
            using (Form f = Base("Open PDF page", k, 380, 150))
            {
                Label l1 = new Label(); l1.AutoSize = true; l1.UseMnemonic = false;
                l1.Text = System.IO.Path.GetFileName(file) + "  –  " + pages + " page" + (pages == 1 ? "" : "s");
                l1.MaximumSize = new Size(S(356), 0);
                l1.Location = new Point(S(12), S(12));
                Label l2 = new Label(); l2.AutoSize = true; l2.Text = "Page"; l2.Location = new Point(S(12), S(48));
                NumericUpDown np = new NumericUpDown(); np.Minimum = 1; np.Maximum = Math.Max(1, pages); np.Value = 1;
                np.SetBounds(S(70), S(46), S(70), S(26));
                Label l3 = new Label(); l3.AutoSize = true; l3.Text = "Sharpness"; l3.Location = new Point(S(160), S(48));
                ComboBox q = new ComboBox(); q.DropDownStyle = ComboBoxStyle.DropDownList;
                int[] dpis = { 100, 150, 200, 300 };
                q.Items.AddRange(new string[] { "100 dpi (fast)", "150 dpi", "200 dpi", "300 dpi (sharp)" });
                q.SelectedIndex = Math.Max(0, Array.IndexOf(dpis, Settings.GetInt("PdfDpi", 150)));
                q.SetBounds(S(236), S(46), S(130), S(26));
                f.Controls.Add(l1); f.Controls.Add(l2); f.Controls.Add(np); f.Controls.Add(l3); f.Controls.Add(q);
                Buttons(f, k);
                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                page = (int)np.Value;
                dpi = dpis[Math.Max(0, q.SelectedIndex)];
                Settings.Set("PdfDpi", dpi.ToString(CultureInfo.InvariantCulture));
                return true;
            }
        }
    }

    internal static class Icons
    {
        // one colour per group of tools, so the colour of an icon says which kind of tool it is:
        // measure = drafting blue, count = violet, draw = red, highlight = amber, text = green, the rest ink
        public static readonly Color MeasureCol = Color.FromArgb(0, 105, 190), CountCol = Color.FromArgb(118, 62, 192),
            DrawCol = Color.FromArgb(208, 46, 46), HighlightCol = Color.FromArgb(208, 132, 0), TextCol = Color.FromArgb(18, 128, 96),
            ToolInk = Color.FromArgb(52, 56, 64);

        /// <summary>The colour of the tool's group (see the headings in the editor's tool column).</summary>
        public static Color Group(Tool t)
        {
            switch (t)
            {
                case Tool.Calibrate: case Tool.Dimension: case Tool.Perimeter: case Tool.Area: case Tool.Cutout:
                case Tool.Angle: case Tool.Radius: case Tool.Coord:
                    return MeasureCol;
                case Tool.Counter: case Tool.Letter:
                    return CountCol;
                case Tool.Arrow: case Tool.Line: case Tool.PolyDraw: case Tool.Polyline: case Tool.Arc: case Tool.Freehand:
                case Tool.Rect: case Tool.Ellipse: case Tool.Polygon: case Tool.Cloud:
                    return DrawCol;
                case Tool.Highlight: case Tool.Marker: case Tool.Pixelate:
                    return HighlightCol;
                case Tool.Text: case Tool.Bubble: case Tool.Stamp: case Tool.Note: case Tool.Markup:
                    return TextCol;
                default:
                    return ToolInk;
            }
        }

        /// <summary>c lightened towards white (0 = c, 1 = white): fills that stay the group's colour.</summary>
        static Color Light(Color c, float f)
        {
            return Color.FromArgb((int)(c.R + (255 - c.R) * f), (int)(c.G + (255 - c.G) * f), (int)(c.B + (255 - c.B) * f));
        }

        /// <summary>Tool icons drawn as vectors at the exact pixel size requested (no stretching): the group's colour
        /// for the shape the tool makes, ink for the rest.</summary>
        public static Bitmap Make(Tool t, int size)
        {
            Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.ScaleTransform(size / 32f, size / 32f);
                Color ink = ToolInk, acc = Group(t);
                Color red = acc, blue = acc, amber = acc;
                using (Pen p = new Pen(ink, 2.2f))
                using (Pen pr = new Pen(red, 2.4f))
                using (Pen pb = new Pen(blue, 2f))
                using (SolidBrush bi = new SolidBrush(ink))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                    p.StartCap = p.EndCap = LineCap.Round;
                    pr.StartCap = pr.EndCap = LineCap.Round;
                    switch (t)
                    {
                        case Tool.Select:
                            {
                                PointF[] a = { new PointF(9, 4), new PointF(9, 26), new PointF(14, 21), new PointF(18, 29), new PointF(22, 27.5f), new PointF(18, 19.5f), new PointF(25, 19.5f) };
                                g.FillPolygon(Brushes.White, a);
                                using (Pen pp = new Pen(ink, 1.8f)) { pp.LineJoin = LineJoin.Round; g.DrawPolygon(pp, a); }
                                break;
                            }
                        case Tool.Rect: g.DrawRectangle(pr, 5, 8, 22, 16); break;
                        case Tool.Ellipse:
                            g.DrawEllipse(pr, 4, 4, 24, 24);
                            using (Pen pp = new Pen(ink, 1.4f)) { g.DrawLine(pp, 13, 16, 19, 16); g.DrawLine(pp, 16, 13, 16, 19); }
                            break;
                        case Tool.Line: g.DrawLine(pr, 6, 26, 26, 6); break;
                        case Tool.Arrow:
                            using (AdjustableArrowCap cap = new AdjustableArrowCap(3.5f, 3.5f, true))
                            {
                                pr.CustomEndCap = cap;
                                g.DrawLine(pr, 6, 26, 25, 7);
                            }
                            break;
                        case Tool.Freehand: g.DrawBezier(pr, 4, 22, 11, 2, 18, 32, 28, 8); break;
                        case Tool.Marker:
                            {
                                // a chisel highlighter over a yellow see-through stroke, with a small T: it marks text
                                using (Font tf = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel))
                                using (SolidBrush tb = new SolidBrush(Color.FromArgb(60, 60, 60)))
                                    g.DrawString("T", tf, tb, new RectangleF(0, 0, 15, 18), sf);
                                using (Pen hp = new Pen(Color.FromArgb(190, 255, 215, 0), 7f)) { hp.StartCap = hp.EndCap = LineCap.Square; g.DrawLine(hp, 4, 26, 16, 26); }
                                PointF[] body = { new PointF(15, 17), new PointF(24, 8), new PointF(29, 13), new PointF(20, 22) };
                                using (SolidBrush bb = new SolidBrush(Color.FromArgb(70, 70, 70))) g.FillPolygon(bb, body);
                                PointF[] nib = { new PointF(15, 17), new PointF(20, 22), new PointF(14, 25), new PointF(12, 23) };
                                using (SolidBrush nb = new SolidBrush(Color.FromArgb(250, 190, 0))) g.FillPolygon(nb, nib);
                                break;
                            }
                        case Tool.Text:
                            using (SolidBrush ab = new SolidBrush(acc))
                            using (Font mkFont = new Font("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("T", mkFont, ab, new RectangleF(0, 0, 32, 32), sf);
                            break;
                        case Tool.Polygon:
                            {
                                PointF[] a = { new PointF(4, 13), new PointF(13, 4), new PointF(28, 9), new PointF(24, 20), new PointF(28, 27), new PointF(9, 28) };
                                using (SolidBrush f = new SolidBrush(Light(acc, 0.8f))) g.FillPolygon(f, a);
                                using (Pen pp = new Pen(acc, 2f)) { pp.LineJoin = LineJoin.Round; g.DrawPolygon(pp, a); }
                                foreach (PointF q in a) g.FillRectangle(bi, q.X - 1.5f, q.Y - 1.5f, 3, 3);
                                break;
                            }
                        case Tool.PolyDraw:
                            {
                                // open zig-zag line with points and an arrowhead
                                PointF[] a = { new PointF(4, 26), new PointF(11, 10), new PointF(19, 22), new PointF(27, 6) };
                                using (Pen pp = new Pen(red, 2.2f)) { pp.LineJoin = LineJoin.Round; pp.StartCap = LineCap.Round; g.DrawLines(pp, a); }
                                using (SolidBrush rb = new SolidBrush(red)) g.FillPolygon(rb, new PointF[] { new PointF(29, 3), new PointF(28.5f, 11), new PointF(23, 7) });
                                foreach (PointF q in new PointF[] { a[0], a[1], a[2] }) g.FillRectangle(bi, q.X - 1.5f, q.Y - 1.5f, 3, 3);
                                break;
                            }
                        case Tool.Dimension:
                            {
                                using (Pen pp = new Pen(red, 1.8f))
                                {
                                    g.DrawLine(pp, 4, 9, 4, 25);
                                    g.DrawLine(pp, 28, 9, 28, 25);
                                    g.DrawLine(pp, 5, 20, 27, 20);
                                }
                                using (SolidBrush rb = new SolidBrush(red))
                                {
                                    g.FillPolygon(rb, new PointF[] { new PointF(4.5f, 20), new PointF(10, 17.5f), new PointF(10, 22.5f) });
                                    g.FillPolygon(rb, new PointF[] { new PointF(27.5f, 20), new PointF(22, 17.5f), new PointF(22, 22.5f) });
                                }
                                using (Font mkFont = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("12", mkFont, bi, new RectangleF(0, 3, 32, 14), sf);
                                break;
                            }
                        case Tool.Area:
                            {
                                PointF[] a = { new PointF(4, 8), new PointF(22, 4), new PointF(28, 18), new PointF(17, 28), new PointF(5, 24) };
                                using (SolidBrush f = new SolidBrush(Light(acc, 0.75f))) g.FillPolygon(f, a);
                                g.DrawPolygon(pb, a);
                                using (SolidBrush bb = new SolidBrush(blue))
                                    using (Font mkFont = new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("m²", mkFont, bb, new RectangleF(0, 2, 32, 30), sf);
                                break;
                            }
                        case Tool.Calibrate:
                            {
                                GraphicsState st = g.Save();
                                g.TranslateTransform(16, 16);
                                g.RotateTransform(-35);
                                RectangleF r = new RectangleF(-14, -5, 28, 10);
                                using (SolidBrush f = new SolidBrush(Light(acc, 0.72f))) g.FillRectangle(f, r);
                                using (Pen pp = new Pen(acc, 1.4f))
                                {
                                    g.DrawRectangle(pp, r.X, r.Y, r.Width, r.Height);
                                    for (int i = 0; i < 6; i++) { float x = -11 + i * 4.5f; g.DrawLine(pp, x, -5, x, i % 2 == 0 ? 0 : -2); }
                                }
                                g.Restore(st);
                                break;
                            }
                        case Tool.Counter:
                            using (SolidBrush rb = new SolidBrush(red)) g.FillEllipse(rb, 5, 5, 22, 22);
                            using (Font mkFont = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("1", mkFont, Brushes.White, new RectangleF(5, 5, 22, 23), sf);
                            break;
                        case Tool.Letter:
                            // the letter marker: the same violet, as a ring, so it is told apart from the number marker
                            g.FillEllipse(Brushes.White, 5, 5, 22, 22);
                            using (Pen rp = new Pen(acc, 2.2f)) g.DrawEllipse(rp, 6, 6, 20, 20);
                            using (SolidBrush ab = new SolidBrush(acc))
                            using (Font mkFont = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("A", mkFont, ab, new RectangleF(5, 5, 22, 23), sf);
                            break;
                        case Tool.Angle:
                            using (Pen pp = new Pen(blue, 2.2f))
                            {
                                pp.StartCap = pp.EndCap = LineCap.Round; pp.LineJoin = LineJoin.Round;
                                g.DrawLines(pp, new PointF[] { new PointF(27, 6), new PointF(5, 26), new PointF(28, 26) });
                            }
                            using (Pen pp = new Pen(ink, 1.6f)) g.DrawArc(pp, -6, 15, 22, 22, -42, 42);
                            break;
                        case Tool.Pixelate:
                            for (int y = 0; y < 4; y++)
                                for (int x = 0; x < 4; x++)
                                {
                                    int v = 90 + ((x * 37 + y * 53) % 4) * 40;
                                    using (SolidBrush f = new SolidBrush(Color.FromArgb(v, v, v))) g.FillRectangle(f, 4 + x * 6, 4 + y * 6, 6, 6);
                                }
                            break;
                        case Tool.Crop:
                            using (Pen pp = new Pen(ink, 2.6f))
                            {
                                g.DrawLines(pp, new PointF[] { new PointF(9, 3), new PointF(9, 23), new PointF(29, 23) });
                                g.DrawLines(pp, new PointF[] { new PointF(3, 9), new PointF(23, 9), new PointF(23, 29) });
                            }
                            break;
                        case Tool.Highlight:
                            {
                                // Focus: a bright spot in a darkened square
                                using (SolidBrush dark = new SolidBrush(Color.FromArgb(110, 115, 125))) g.FillRectangle(dark, 3, 5, 26, 22);
                                using (SolidBrush lit = new SolidBrush(Color.FromArgb(255, 245, 190))) g.FillEllipse(lit, 9, 9, 15, 14);
                                using (Pen pp = new Pen(amber, 1.8f)) g.DrawEllipse(pp, 9, 9, 15, 14);
                                break;
                            }
                        case Tool.Arc:
                            using (Pen pp = new Pen(red, 2.4f)) { pp.StartCap = pp.EndCap = LineCap.Round; g.DrawArc(pp, 4, 8, 24, 30, 200, 140); }
                            foreach (PointF q in new PointF[] { new PointF(5.4f, 18.5f), new PointF(16, 8), new PointF(26.6f, 18.5f) }) g.FillEllipse(bi, q.X - 2, q.Y - 2, 4, 4);
                            break;
                        case Tool.Radius:
                            {
                                using (Pen pp = new Pen(acc, 2.2f)) { pp.StartCap = pp.EndCap = LineCap.Round; g.DrawArc(pp, 3, 3, 26, 26, 180, 180); }
                                using (Pen pp = new Pen(ink, 1.4f))
                                {
                                    g.DrawLine(pp, 13, 16, 19, 16); g.DrawLine(pp, 16, 13, 16, 19);   // centre mark
                                    pp.DashStyle = DashStyle.Dash;
                                    g.DrawLine(pp, 16, 16, 25, 7);                                   // radius line
                                }
                                using (Font rf = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("R", rf, bi, new RectangleF(1, 18, 14, 14), sf);
                                break;
                            }
                        case Tool.Bubble:
                            using (GraphicsPath gp = Renderer.BubblePath(new RectangleF(3, 4, 26, 17), new PointF(9, 29)))
                            {
                                g.FillPath(Brushes.White, gp);
                                using (Pen pp = new Pen(acc, 2f)) { pp.LineJoin = LineJoin.Round; g.DrawPath(pp, gp); }
                            }
                            using (Pen pp = new Pen(ink, 1.6f)) { g.DrawLine(pp, 8, 10, 24, 10); g.DrawLine(pp, 8, 15, 19, 15); }
                            break;
                        case Tool.Coord:
                            {
                                // origin cross with X / Y arrows and a point
                                using (Pen ap = new Pen(blue, 2f))
                                {
                                    ap.StartCap = LineCap.Round;
                                    g.DrawLine(ap, 6, 26, 26, 26);
                                    g.DrawLine(ap, 6, 26, 6, 6);
                                }
                                using (SolidBrush bb = new SolidBrush(blue))
                                {
                                    g.FillPolygon(bb, new PointF[] { new PointF(29, 26), new PointF(24, 23), new PointF(24, 29) });
                                    g.FillPolygon(bb, new PointF[] { new PointF(6, 3), new PointF(3, 8), new PointF(9, 8) });
                                }
                                using (Pen xp = new Pen(ink, 2f)) { g.DrawLine(xp, 15, 10, 21, 16); g.DrawLine(xp, 15, 16, 21, 10); }
                                break;
                            }
                        case Tool.Polyline:
                            {
                                Color purple = acc;
                                PointF[] a = { new PointF(4, 24), new PointF(12, 10), new PointF(20, 20), new PointF(28, 6) };
                                using (Pen pp = new Pen(purple, 2.4f)) { pp.LineJoin = LineJoin.Round; pp.StartCap = pp.EndCap = LineCap.Round; g.DrawLines(pp, a); }
                                using (SolidBrush pb2 = new SolidBrush(purple)) foreach (PointF q in a) g.FillEllipse(pb2, q.X - 2.6f, q.Y - 2.6f, 5.2f, 5.2f);
                                break;
                            }
                        case Tool.Perimeter:
                            {
                                Color green = acc;
                                PointF[] a = { new PointF(5, 9), new PointF(24, 5), new PointF(28, 22), new PointF(9, 27) };
                                using (Pen pp = new Pen(green, 2.4f)) { pp.LineJoin = LineJoin.Round; g.DrawPolygon(pp, a); }
                                using (SolidBrush gb = new SolidBrush(green))
                                    using (Font mkFont = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("P", mkFont, gb, new RectangleF(0, 1, 33, 32), sf);
                                break;
                            }
                        case Tool.Cutout:
                            {
                                using (SolidBrush f = new SolidBrush(Light(acc, 0.75f))) g.FillRectangle(f, 3, 4, 26, 24);
                                g.DrawRectangle(pb, 3, 4, 26, 24);
                                g.FillRectangle(Brushes.White, 11, 11, 11, 10);
                                using (Pen dp = new Pen(ink, 1.6f)) { dp.DashStyle = DashStyle.Dash; g.DrawRectangle(dp, 11, 11, 11, 10); }
                                break;
                            }
                        case Tool.Cloud:
                            using (GraphicsPath gp = Renderer.CloudPath(new RectangleF(6, 8, 20, 16), 0.2f))
                            using (Pen pp = new Pen(red, 2f)) { pp.LineJoin = LineJoin.Round; g.DrawPath(pp, gp); }
                            break;
                        case Tool.Stamp:
                            {
                                Color sg = acc;
                                using (GraphicsPath gp = Renderer.RoundRect(new RectangleF(2, 8, 28, 16), 3))
                                using (Pen pp = new Pen(sg, 2f)) g.DrawPath(pp, gp);
                                using (SolidBrush sb = new SolidBrush(sg))
                                    using (Font mkFont = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("OK", mkFont, sb, new RectangleF(2, 8, 28, 16.5f), sf);
                                break;
                            }
                        case Tool.Note:
                            {
                                PointF[] body = { new PointF(5, 4), new PointF(27, 4), new PointF(27, 20), new PointF(20, 28), new PointF(5, 28) };
                                using (SolidBrush yb = new SolidBrush(Light(acc, 0.8f))) g.FillPolygon(yb, body);
                                using (Pen pp = new Pen(acc, 1.6f)) { pp.LineJoin = LineJoin.Round; g.DrawPolygon(pp, body); g.DrawLines(pp, new PointF[] { new PointF(27, 20), new PointF(20, 20), new PointF(20, 28) }); }
                                using (Pen pp = new Pen(ink, 1.4f)) { g.DrawLine(pp, 9, 11, 23, 11); g.DrawLine(pp, 9, 16, 21, 16); }
                                break;
                            }
                        case Tool.Markup:
                            using (Font mkFont = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("ab", mkFont, bi, new RectangleF(0, -2, 32, 28), sf);
                            using (Pen up = new Pen(red, 2.4f)) g.DrawLine(up, 5, 25, 27, 25);
                            break;
                        case Tool.Ocr:
                            using (Pen cp = new Pen(ink, 2f))
                            {
                                g.DrawLines(cp, new PointF[] { new PointF(3, 10), new PointF(3, 3), new PointF(10, 3) });
                                g.DrawLines(cp, new PointF[] { new PointF(22, 3), new PointF(29, 3), new PointF(29, 10) });
                                g.DrawLines(cp, new PointF[] { new PointF(29, 22), new PointF(29, 29), new PointF(22, 29) });
                                g.DrawLines(cp, new PointF[] { new PointF(10, 29), new PointF(3, 29), new PointF(3, 22) });
                            }
                            using (Font mkFont = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("T", mkFont, bi, new RectangleF(0, 0, 32, 32), sf);
                            break;
                    }
                }
            }
            return b;
        }

        /// <summary>Small command icons for the editor's top bar, drawn as vectors at the exact size.</summary>
        public static Bitmap Cmd(string name, int size)
        {
            Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(size / 16f, size / 16f);
                Color ink = Color.FromArgb(55, 55, 60), blue = Color.FromArgb(0, 110, 210), red = Color.FromArgb(215, 45, 45),
                      green = Color.FromArgb(30, 150, 70), amber = Color.FromArgb(235, 165, 0);
                using (Pen p = new Pen(ink, 1.3f))
                using (Pen pb = new Pen(blue, 1.4f))
                using (SolidBrush bi = new SolidBrush(ink))
                using (SolidBrush bb = new SolidBrush(blue))
                {
                    p.StartCap = p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                    pb.StartCap = pb.EndCap = LineCap.Round; pb.LineJoin = LineJoin.Round;
                    switch (name)
                    {
                        case "new":     // dashed capture frame + plus
                            using (Pen d = new Pen(ink, 1.2f)) { d.DashStyle = DashStyle.Dash; g.DrawRectangle(d, 1.5f, 3.5f, 10, 9); }
                            using (Pen gp = new Pen(green, 1.8f)) { g.DrawLine(gp, 12.5f, 1, 12.5f, 7); g.DrawLine(gp, 9.5f, 4, 15.5f, 4); }
                            break;
                        case "open":
                            using (SolidBrush f = new SolidBrush(Color.FromArgb(250, 205, 90))) g.FillPolygon(f, new PointF[] { new PointF(1, 4), new PointF(6, 4), new PointF(7.5f, 5.5f), new PointF(15, 5.5f), new PointF(15, 14), new PointF(1, 14) });
                            using (Pen pp = new Pen(Color.FromArgb(180, 130, 20), 1f)) g.DrawPolygon(pp, new PointF[] { new PointF(1, 4), new PointF(6, 4), new PointF(7.5f, 5.5f), new PointF(15, 5.5f), new PointF(15, 14), new PointF(1, 14) });
                            break;
                        case "save":
                        case "saveas":
                            g.FillRectangle(bb, 1.5f, 1.5f, 13, 13);
                            g.FillRectangle(Brushes.White, 4, 1.5f, 7, 4.5f);
                            g.FillRectangle(Brushes.White, 3.5f, 9, 9, 5.5f);
                            if (name == "saveas") using (Pen pp = new Pen(red, 1.6f)) { pp.StartCap = pp.EndCap = LineCap.Round; g.DrawLine(pp, 9, 14.5f, 15, 8.5f); }
                            break;
                        case "copy":
                            g.FillRectangle(Brushes.White, 1.5f, 1.5f, 9, 10); g.DrawRectangle(p, 1.5f, 1.5f, 9, 10);
                            g.FillRectangle(Brushes.White, 5.5f, 4.5f, 9, 10); g.DrawRectangle(p, 5.5f, 4.5f, 9, 10);
                            break;
                        case "pin":
                            {
                                GraphicsState st = g.Save();
                                g.TranslateTransform(8, 8); g.RotateTransform(40);
                                using (SolidBrush rb = new SolidBrush(red)) { g.FillRectangle(rb, -3, -7, 6, 6); g.FillRectangle(rb, -5, -1.5f, 10, 2); }
                                g.DrawLine(p, 0, 0.5f, 0, 8);
                                g.Restore(st);
                                break;
                            }
                        case "undo":
                        case "redo":
                            {
                                if (name == "redo") { g.TranslateTransform(16, 0); g.ScaleTransform(-1, 1); }
                                using (Pen pp = new Pen(blue, 1.8f)) { pp.StartCap = pp.EndCap = LineCap.Round; g.DrawArc(pp, 3, 4, 11, 9, 200, 250); }
                                g.FillPolygon(bb, new PointF[] { new PointF(0.5f, 6), new PointF(6, 3.5f), new PointF(5, 9.5f) });
                                break;
                            }
                        case "rotate":
                            using (Pen pp = new Pen(green, 1.8f)) { pp.StartCap = LineCap.Round; g.DrawArc(pp, 2, 2, 12, 12, -60, 290); }
                            using (SolidBrush gb = new SolidBrush(green)) g.FillPolygon(gb, new PointF[] { new PointF(10.5f, 0), new PointF(15.5f, 3.5f), new PointF(10, 6.5f) });
                            break;
                        case "effects":     // magic wand + sparkles
                            using (Pen pp = new Pen(ink, 2f)) { pp.StartCap = pp.EndCap = LineCap.Round; g.DrawLine(pp, 2, 14, 10, 6); }
                            using (SolidBrush ab = new SolidBrush(amber))
                            {
                                g.FillPolygon(ab, Star(12, 4, 4, 1.4f));
                                g.FillPolygon(ab, Star(5, 3, 2.2f, 0.8f));
                                g.FillPolygon(ab, Star(14, 11, 2.2f, 0.8f));
                            }
                            break;
                        case "rotcw":       // picture with a turning arrow (left: mirrored)
                        case "rotccw":
                            if (name == "rotccw") { g.TranslateTransform(16, 0); g.ScaleTransform(-1, 1); }
                            g.FillRectangle(Brushes.White, 1.5f, 6.5f, 8, 8); g.DrawRectangle(p, 1.5f, 6.5f, 8, 8);
                            using (Pen pp = new Pen(green, 1.6f)) { pp.StartCap = LineCap.Round; g.DrawArc(pp, 5, 1.5f, 9, 9, 200, 160); }
                            using (SolidBrush gb = new SolidBrush(green)) g.FillPolygon(gb, new PointF[] { new PointF(11.5f, 5), new PointF(16, 5), new PointF(13.75f, 9) });
                            break;
                        case "fliph":       // two halves facing a dashed mirror line
                        case "flipv":
                            if (name == "flipv") { g.TranslateTransform(8, 8); g.RotateTransform(90); g.TranslateTransform(-8, -8); }
                            using (Pen d = new Pen(blue, 1.2f)) { d.DashStyle = DashStyle.Dash; g.DrawLine(d, 8, 0.5f, 8, 15.5f); }
                            g.FillPolygon(bi, new PointF[] { new PointF(6.5f, 3), new PointF(6.5f, 13), new PointF(1, 13) });
                            using (SolidBrush lb = new SolidBrush(Color.FromArgb(185, 185, 190))) g.FillPolygon(lb, new PointF[] { new PointF(9.5f, 3), new PointF(9.5f, 13), new PointF(15, 13) });
                            break;
                        case "border":      // picture inside a thick frame
                            using (Pen fp = new Pen(blue, 2.2f)) g.DrawRectangle(fp, 1.5f, 1.5f, 13, 13);
                            using (SolidBrush sk = new SolidBrush(Color.FromArgb(150, 205, 245))) g.FillRectangle(sk, 4, 4, 8, 8);
                            using (SolidBrush gr = new SolidBrush(green)) g.FillPolygon(gr, new PointF[] { new PointF(4, 12), new PointF(7, 8), new PointF(9, 10), new PointF(10.5f, 8.5f), new PointF(12, 12) });
                            break;
                        case "shadow":      // card with a soft shadow
                            using (SolidBrush sb = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillRectangle(sb, 4.5f, 4.5f, 10.5f, 10.5f);
                            using (SolidBrush sb = new SolidBrush(Color.FromArgb(45, 0, 0, 0))) g.FillRectangle(sb, 5.5f, 5.5f, 10.5f, 10.5f);
                            g.FillRectangle(Brushes.White, 1.5f, 1.5f, 10.5f, 10.5f); g.DrawRectangle(p, 1.5f, 1.5f, 10.5f, 10.5f);
                            break;
                        case "torn":        // page with a ragged bottom edge
                            {
                                PointF[] pg = { new PointF(2, 1.5f), new PointF(14, 1.5f), new PointF(14, 11), new PointF(12, 13.5f), new PointF(10, 11.5f), new PointF(8, 14), new PointF(6, 11.5f), new PointF(4, 13.5f), new PointF(2, 11) };
                                g.FillPolygon(Brushes.White, pg); g.DrawPolygon(p, pg);
                                using (Pen lp = new Pen(Color.FromArgb(150, 150, 155), 1f)) { g.DrawLine(lp, 4.5f, 4.5f, 11.5f, 4.5f); g.DrawLine(lp, 4.5f, 7.5f, 11.5f, 7.5f); }
                                break;
                            }
                        case "gray":        // half colour, half gray circle
                            using (SolidBrush cb = new SolidBrush(Color.FromArgb(235, 120, 40))) g.FillPie(cb, 1.5f, 1.5f, 13, 13, 90, 180);
                            using (SolidBrush gb = new SolidBrush(Color.FromArgb(150, 150, 150))) g.FillPie(gb, 1.5f, 1.5f, 13, 13, -90, 180);
                            g.DrawEllipse(p, 1.5f, 1.5f, 13, 13);
                            break;
                        case "invert":      // circle half black, half white
                            g.FillEllipse(Brushes.White, 1.5f, 1.5f, 13, 13);
                            g.FillPie(bi, 1.5f, 1.5f, 13, 13, -90, 180);
                            g.DrawEllipse(p, 1.5f, 1.5f, 13, 13);
                            break;
                        case "zoomin":
                        case "zoomout":
                            using (Pen pp = new Pen(ink, 1.5f)) { g.DrawEllipse(pp, 1.5f, 1.5f, 9.5f, 9.5f); pp.StartCap = pp.EndCap = LineCap.Round; pp.Width = 2.2f; g.DrawLine(pp, 10, 10, 14.5f, 14.5f); }
                            g.DrawLine(pb, 4, 6.25f, 8.5f, 6.25f);
                            if (name == "zoomin") g.DrawLine(pb, 6.25f, 4, 6.25f, 8.5f);
                            break;
                        case "fit":
                            using (Pen pp = new Pen(ink, 1.5f))
                            {
                                pp.StartCap = pp.EndCap = LineCap.Round;
                                g.DrawLines(pp, new PointF[] { new PointF(1.5f, 5.5f), new PointF(1.5f, 1.5f), new PointF(5.5f, 1.5f) });
                                g.DrawLines(pp, new PointF[] { new PointF(10.5f, 1.5f), new PointF(14.5f, 1.5f), new PointF(14.5f, 5.5f) });
                                g.DrawLines(pp, new PointF[] { new PointF(14.5f, 10.5f), new PointF(14.5f, 14.5f), new PointF(10.5f, 14.5f) });
                                g.DrawLines(pp, new PointF[] { new PointF(5.5f, 14.5f), new PointF(1.5f, 14.5f), new PointF(1.5f, 10.5f) });
                            }
                            g.FillRectangle(bb, 5, 5, 6, 6);
                            break;
                        case "match":
                            // paint brush: handle, ferrule and a blue tip
                            using (Pen hp = new Pen(ink, 2.2f)) { hp.StartCap = hp.EndCap = LineCap.Round; g.DrawLine(hp, 14f, 2f, 9.5f, 6.5f); }
                            g.FillPolygon(bi, new PointF[] { new PointF(9.6f, 4.6f), new PointF(11.4f, 6.4f), new PointF(8.6f, 9.2f), new PointF(6.8f, 7.4f) });
                            g.FillPolygon(bb, new PointF[] { new PointF(6.6f, 7.6f), new PointF(8.4f, 9.4f), new PointF(5f, 14.5f), new PointF(1.5f, 14.5f), new PointF(1.5f, 11f) });
                            break;
                        case "zoomarea":
                            // dashed box with a magnifier on its corner
                            using (Pen pp = new Pen(ink, 1.2f))
                            {
                                pp.DashStyle = DashStyle.Dash;
                                g.DrawRectangle(pp, 1.5f, 1.5f, 9f, 7f);
                                pp.DashStyle = DashStyle.Solid; pp.Width = 1.5f;
                                g.DrawEllipse(pp, 7.5f, 7.5f, 5f, 5f);
                                pp.StartCap = pp.EndCap = LineCap.Round; pp.Width = 2f;
                                g.DrawLine(pp, 12f, 12f, 14.5f, 14.5f);
                            }
                            break;
                        case "lock":
                            // padlock: shackle and body with a keyhole
                            using (Pen pp = new Pen(ink, 1.6f))
                            using (SolidBrush lb = new SolidBrush(ink))
                            {
                                g.DrawArc(pp, 4.5f, 1.5f, 7f, 8f, 180f, 180f);
                                g.DrawLine(pp, 4.5f, 5.5f, 4.5f, 8f);
                                g.DrawLine(pp, 11.5f, 5.5f, 11.5f, 8f);
                                g.FillRectangle(lb, 2.5f, 7.5f, 11f, 7.5f);
                                using (SolidBrush hole = new SolidBrush(Color.White)) g.FillEllipse(hole, 7f, 9.5f, 2f, 2.5f);
                            }
                            break;
                        case "actual":
                            g.DrawRectangle(p, 1.5f, 2.5f, 13, 11);
                            using (Font f = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Pixel))
                            using (StringFormat sf = new StringFormat())
                            {
                                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                                g.DrawString("1:1", f, bi, new RectangleF(0, 2, 16, 12), sf);
                            }
                            break;
                        case "rulers":     // L-shaped ruler pair
                            using (SolidBrush yb = new SolidBrush(Color.FromArgb(255, 225, 120)))
                            {
                                g.FillRectangle(yb, 1, 1, 14, 4); g.FillRectangle(yb, 1, 1, 4, 14);
                            }
                            g.DrawRectangle(p, 1, 1, 14, 4); g.DrawRectangle(p, 1, 1, 4, 14);
                            for (int i = 0; i < 3; i++) { g.DrawLine(p, 7 + i * 3, 1, 7 + i * 3, 3); g.DrawLine(p, 1, 7 + i * 3, 3, 7 + i * 3); }
                            using (Pen gp = new Pen(Color.FromArgb(0, 170, 255), 1f)) { gp.DashStyle = DashStyle.Dash; g.DrawLine(gp, 10, 6, 10, 15); }
                            break;
                        case "ruler":
                            {
                                GraphicsState st = g.Save();
                                g.TranslateTransform(8, 8); g.RotateTransform(-35);
                                using (SolidBrush yb = new SolidBrush(Color.FromArgb(255, 225, 120))) g.FillRectangle(yb, -8, -3, 16, 6);
                                using (Pen pp = new Pen(ink, 1f))
                                {
                                    g.DrawRectangle(pp, -8, -3, 16, 6);
                                    for (int i = 0; i < 5; i++) g.DrawLine(pp, -6 + i * 3, -3, -6 + i * 3, i % 2 == 0 ? 0.5f : -1);
                                }
                                g.Restore(st);
                                break;
                            }
                        case "protractor":
                            using (SolidBrush pf = new SolidBrush(Color.FromArgb(120, 0, 120, 215))) g.FillPie(pf, 1, 3, 14, 14, 180, 180);
                            g.DrawArc(pb, 1, 3, 14, 14, 180, 180);
                            g.DrawLine(pb, 1, 10, 15, 10);
                            using (Pen rp = new Pen(red, 1.5f)) { rp.StartCap = rp.EndCap = LineCap.Round; g.DrawLine(rp, 8, 10, 13, 3.5f); }
                            break;
                        case "region":      // dashed selection with a crosshair
                            using (Pen d = new Pen(ink, 1.2f)) { d.DashStyle = DashStyle.Dash; g.DrawRectangle(d, 1.5f, 2.5f, 13, 11); }
                            using (Pen rp = new Pen(red, 1.5f)) { g.DrawLine(rp, 8, 5, 8, 11); g.DrawLine(rp, 5, 8, 11, 8); }
                            break;
                        case "window":
                            g.FillRectangle(Brushes.White, 1.5f, 2.5f, 13, 11);
                            g.FillRectangle(bb, 1.5f, 2.5f, 13, 3);
                            g.DrawRectangle(p, 1.5f, 2.5f, 13, 11);
                            break;
                        case "screen":
                        case "allscreens":
                            {
                                float w = name == "screen" ? 13 : 8;
                                g.FillRectangle(Brushes.White, 1.5f, 2.5f, w, 9);
                                g.DrawRectangle(p, 1.5f, 2.5f, w, 9);
                                if (name == "allscreens") { g.FillRectangle(Brushes.White, 8.5f, 4.5f, 6.5f, 7); g.DrawRectangle(p, 8.5f, 4.5f, 6.5f, 7); }
                                g.DrawLine(p, 5, 14, 11, 14);
                                g.DrawLine(p, 8, 11.5f, 8, 14);
                                break;
                            }
                        case "lastregion":
                            using (Pen d = new Pen(ink, 1.2f)) { d.DashStyle = DashStyle.Dash; g.DrawRectangle(d, 1.5f, 2.5f, 13, 11); }
                            using (Pen gp = new Pen(green, 1.6f)) { gp.StartCap = LineCap.Round; g.DrawArc(gp, 4.5f, 5, 7, 6, 200, 280); }
                            using (SolidBrush gb = new SolidBrush(green)) g.FillPolygon(gb, new PointF[] { new PointF(3, 6), new PointF(7, 5.5f), new PointF(5, 9) });
                            break;
                        case "timer":
                            g.FillEllipse(Brushes.White, 2, 3, 12, 12); g.DrawEllipse(p, 2, 3, 12, 12);
                            g.DrawLine(p, 8, 9, 8, 5.5f); g.DrawLine(p, 8, 9, 10.5f, 10.5f);
                            g.DrawLine(p, 6.5f, 1, 9.5f, 1);
                            break;
                        case "clipboard":
                            using (SolidBrush cb = new SolidBrush(Color.FromArgb(205, 160, 100))) g.FillRectangle(cb, 2.5f, 2.5f, 11, 13);
                            g.FillRectangle(Brushes.White, 4.5f, 5, 7, 8.5f);
                            g.FillRectangle(bi, 5.5f, 1, 5, 3);
                            g.DrawLine(pb, 6, 8, 10, 8); g.DrawLine(pb, 6, 10.5f, 9, 10.5f);
                            break;
                        case "close":
                        case "exit":
                            if (name == "exit")
                            {
                                using (Pen rp = new Pen(red, 1.8f)) { rp.StartCap = rp.EndCap = LineCap.Round; g.DrawArc(rp, 2.5f, 3, 11, 11, -60, 300); g.DrawLine(rp, 8, 1.5f, 8, 7.5f); }
                            }
                            else
                                using (Pen rp = new Pen(red, 2f)) { rp.StartCap = rp.EndCap = LineCap.Round; g.DrawLine(rp, 4, 4, 12, 12); g.DrawLine(rp, 12, 4, 4, 12); }
                            break;
                        case "folder":
                        case "folderopen":
                            using (SolidBrush f = new SolidBrush(Color.FromArgb(250, 205, 90))) g.FillPolygon(f, new PointF[] { new PointF(1, 3), new PointF(6, 3), new PointF(7.5f, 4.5f), new PointF(15, 4.5f), new PointF(15, 13.5f), new PointF(1, 13.5f) });
                            if (name == "folderopen")
                                using (Pen gp = new Pen(green, 1.8f)) { gp.StartCap = gp.EndCap = LineCap.Round; g.DrawLine(gp, 6, 11, 11, 7); g.DrawLine(gp, 8, 7, 11, 7); g.DrawLine(gp, 11, 7, 11, 10); }
                            break;
                        case "help":
                            g.FillEllipse(bb, 1.5f, 1.5f, 13, 13);
                            using (Font f = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Pixel))
                            using (StringFormat sf = new StringFormat())
                            {
                                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                                g.DrawString("?", f, Brushes.White, new RectangleF(1.5f, 1.5f, 13, 13.5f), sf);
                            }
                            break;
                        case "blank":       // empty page with a pencil
                            g.FillRectangle(Brushes.White, 2.5f, 1.5f, 10, 13); g.DrawRectangle(p, 2.5f, 1.5f, 10, 13);
                            using (Pen yp = new Pen(amber, 3f)) g.DrawLine(yp, 8, 12, 14, 5);
                            using (Pen tp = new Pen(ink, 1f)) g.DrawLine(tp, 6.5f, 14, 8, 12);
                            break;
                        case "settings":    // gear
                            {
                                using (SolidBrush gb = new SolidBrush(Color.FromArgb(95, 100, 110)))
                                {
                                    for (int i = 0; i < 8; i++)
                                    {
                                        GraphicsState st = g.Save();
                                        g.TranslateTransform(8, 8); g.RotateTransform(i * 45);
                                        g.FillRectangle(gb, -1.6f, -7.5f, 3.2f, 3.5f);
                                        g.Restore(st);
                                    }
                                    g.FillEllipse(gb, 2.5f, 2.5f, 11, 11);
                                }
                                g.FillEllipse(Brushes.White, 5.5f, 5.5f, 5, 5);
                                break;
                            }
                        case "panel":       // window with a right side panel
                            g.FillRectangle(Brushes.White, 1.5f, 2.5f, 13, 11); g.DrawRectangle(p, 1.5f, 2.5f, 13, 11);
                            using (SolidBrush pb2 = new SolidBrush(Color.FromArgb(180, 0, 110, 210))) g.FillRectangle(pb2, 10, 3, 4, 10);
                            break;
                        case "alignl":
                        case "alignc":
                        case "alignr":
                            {
                                float[] w = { 12, 8, 12, 6 };
                                for (int i = 0; i < 4; i++)
                                {
                                    float x = name == "alignl" ? 2 : name == "alignr" ? 14 - w[i] : 8 - w[i] / 2f;
                                    g.FillRectangle(bi, x, 2.5f + i * 3.2f, w[i], 1.6f);
                                }
                                break;
                            }
                        case "ocr":         // text lines inside scan corners
                            using (Pen cp = new Pen(blue, 1.4f))
                            {
                                g.DrawLines(cp, new PointF[] { new PointF(1, 5), new PointF(1, 1), new PointF(5, 1) });
                                g.DrawLines(cp, new PointF[] { new PointF(11, 1), new PointF(15, 1), new PointF(15, 5) });
                                g.DrawLines(cp, new PointF[] { new PointF(15, 11), new PointF(15, 15), new PointF(11, 15) });
                                g.DrawLines(cp, new PointF[] { new PointF(5, 15), new PointF(1, 15), new PointF(1, 11) });
                            }
                            g.FillRectangle(bi, 4, 4.5f, 8, 1.6f); g.FillRectangle(bi, 4, 7.5f, 8, 1.6f); g.FillRectangle(bi, 4, 10.5f, 5, 1.6f);
                            break;
                        case "scroll":      // long page with a down arrow
                            g.FillRectangle(Brushes.White, 2.5f, 0.5f, 8, 15); g.DrawRectangle(p, 2.5f, 0.5f, 8, 15);
                            using (Pen ap = new Pen(blue, 1.6f)) { ap.StartCap = ap.EndCap = LineCap.Round; g.DrawLine(ap, 13.5f, 3, 13.5f, 12); g.DrawLine(ap, 11.5f, 10, 13.5f, 12.5f); g.DrawLine(ap, 15.5f, 10, 13.5f, 12.5f); }
                            break;
                        case "textcap":     // capture frame with "T"
                            using (Pen d = new Pen(ink, 1.2f)) { d.DashStyle = DashStyle.Dash; g.DrawRectangle(d, 1.5f, 1.5f, 13, 13); }
                            using (Font f = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Pixel))
                            using (StringFormat sf = new StringFormat())
                            {
                                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                                g.DrawString("T", f, bb, new RectangleF(1.5f, 1.5f, 13, 13.5f), sf);
                            }
                            break;
                        case "scale":       // 1:50 ruler
                            using (SolidBrush f = new SolidBrush(Color.FromArgb(255, 225, 120))) g.FillRectangle(f, 1, 8, 14, 6);
                            g.DrawRectangle(p, 1, 8, 14, 6);
                            for (int i = 0; i < 5; i++) g.DrawLine(p, 3 + i * 2.6f, 8, 3 + i * 2.6f, i % 2 == 0 ? 11 : 10);
                            using (Font f = new Font("Segoe UI", 6.5f, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("1:50", f, bb, -0.5f, -0.5f);
                            break;
                    }
                }
            }
            return b;
        }

        public static readonly string[] HeadNames = { "None", "Arrow (filled)", "Arrow (open)", "Arrow (hollow)", "Slash", "Tick", "Dot", "Circle", "Diamond", "Square" };

        /// <summary>A short line with the given end style, for menus. atEnd = the head is on the right.</summary>
        public static Bitmap HeadPreview(int style, bool atEnd, int size) { return HeadPreviewWide(style, atEnd, size, size); }

        public static Bitmap HeadPreviewWide(int style, bool atEnd, int w, int h)
        {
            Bitmap b = new Bitmap(Math.Max(4, w), Math.Max(4, h), PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float lw = Math.Max(1.2f, h / 12f);
                float pad = h * 0.32f, y = h / 2f;
                PointF a = new PointF(pad, y), e = new PointF(w - pad, y);
                PointF tip = atEnd ? e : a, other = atEnd ? a : e;
                float L = Geo.Dist(a, e);
                PointF u = new PointF((tip.X - other.X) / L, 0);
                float hl = h * 0.42f, hw = hl * 0.55f;
                float back = Renderer.HeadBack(style, lw, hl, hw);
                Color col = Color.FromArgb(40, 40, 45);
                using (Pen p = new Pen(col, lw))
                {
                    p.StartCap = p.EndCap = LineCap.Flat;
                    g.DrawLine(p, other, new PointF(tip.X - u.X * back, tip.Y));
                }
                Renderer.DrawHead(g, tip, u, style, col, lw, hl, hw);
            }
            return b;
        }

        static PointF[] Star(float cx, float cy, float r, float ri)
        {
            PointF[] p = new PointF[8];
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4 - Math.PI / 2;
                float rr = i % 2 == 0 ? r : ri;
                p[i] = new PointF(cx + (float)Math.Cos(a) * rr, cy + (float)Math.Sin(a) * rr);
            }
            return p;
        }
    }

    internal static class AppIcon
    {
        static Icon icon;

        public static Icon Get()
        {
            if (icon == null)
            {
                using (Bitmap b = Render(32)) icon = Icon.FromHandle(b.GetHicon());
            }
            return icon;
        }

        public static Bitmap Render(int size)
        {
            Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.ScaleTransform(size / 64f, size / 64f);
                // Logo: a capture frame (viewfinder corners) measured corner to corner with a dimension arrow
                bool small = size <= 24;

                // background squircle: soft top-to-bottom gradient and a faint inner edge
                using (GraphicsPath path = Squircle(2f, 60f, 28f))
                using (LinearGradientBrush br = new LinearGradientBrush(new PointF(0, 2), new PointF(0, 62),
                                                    Color.FromArgb(0x3A, 0x3A, 0x38), Color.FromArgb(0x22, 0x22, 0x20)))
                    g.FillPath(br, path);
                if (!small)
                    using (GraphicsPath inner = Squircle(3f, 58f, 26f))
                    using (Pen edge = new Pen(Color.FromArgb(22, 255, 255, 255), 1.2f))
                        g.DrawPath(edge, inner);

                // viewfinder corner brackets framing the capture area (12,12)–(52,52)
                const float a0 = 12f, a1 = 52f, arm = 10f;
                using (Pen w = new Pen(Color.White, small ? 4.5f : 3.2f))
                {
                    w.StartCap = w.EndCap = LineCap.Round;
                    w.LineJoin = LineJoin.Round;
                    g.DrawLines(w, new PointF[] { new PointF(a0, a0 + arm), new PointF(a0, a0), new PointF(a0 + arm, a0) });
                    g.DrawLines(w, new PointF[] { new PointF(a1 - arm, a0), new PointF(a1, a0), new PointF(a1, a0 + arm) });
                    g.DrawLines(w, new PointF[] { new PointF(a1, a1 - arm), new PointF(a1, a1), new PointF(a1 - arm, a1) });
                    g.DrawLines(w, new PointF[] { new PointF(a0 + arm, a1), new PointF(a0, a1), new PointF(a0, a1 - arm) });
                }

                // amber double-headed dimension arrow, bottom-left (18,46) to top-right (46,18)
                Color y = Color.FromArgb(0xFA, 0xC7, 0x75);
                PointF p0 = new PointF(18, 46), p1 = new PointF(46, 18);
                float len = (float)Math.Sqrt(28 * 28 * 2);
                PointF u = new PointF((p1.X - p0.X) / len, (p1.Y - p0.Y) / len);   // along the arrow
                PointF n = new PointF(-u.Y, u.X);                                    // across it (towards bottom-right)
                float headL = small ? 10f : 8.5f, headW = small ? 6f : 4.6f;
                PointF b0 = new PointF(p0.X + u.X * headL, p0.Y + u.Y * headL);      // where each head meets the shaft
                PointF b1 = new PointF(p1.X - u.X * headL, p1.Y - u.Y * headL);
                using (Pen shaft = new Pen(y, small ? 5f : 3.2f))
                {
                    shaft.StartCap = shaft.EndCap = LineCap.Flat;
                    g.DrawLine(shaft, new PointF(b0.X - u.X, b0.Y - u.Y), new PointF(b1.X + u.X, b1.Y + u.Y));
                }
                using (SolidBrush yb = new SolidBrush(y))
                {
                    g.FillPolygon(yb, new PointF[] { p0, new PointF(b0.X + n.X * headW, b0.Y + n.Y * headW), new PointF(b0.X - n.X * headW, b0.Y - n.Y * headW) });
                    g.FillPolygon(yb, new PointF[] { p1, new PointF(b1.X + n.X * headW, b1.Y + n.Y * headW), new PointF(b1.X - n.X * headW, b1.Y - n.Y * headW) });
                }
                if (size >= 40)
                {
                    // ruler ticks along the shaft so it reads as measuring, not resizing
                    // (from 40 px up: at 32 px they fall between pixels and look like jagged edges)
                    using (Pen tick = new Pen(y, 1.6f))
                    {
                        tick.StartCap = tick.EndCap = LineCap.Round;
                        float[] at = { 0.1f, 0.3f, 0.5f, 0.7f, 0.9f };
                        for (int i = 0; i < at.Length; i++)
                        {
                            PointF c = new PointF(b0.X + (b1.X - b0.X) * at[i], b0.Y + (b1.Y - b0.Y) * at[i]);
                            float tl = i == 2 ? 5.5f : 3.8f;          // longer tick in the middle
                            g.DrawLine(tick, c, new PointF(c.X + n.X * tl, c.Y + n.Y * tl));
                        }
                    }
                }
            }
            return b;
        }

        /// <summary>Rounded square from (o,o) with side 'side' and corner diameter 'd' (64 × 64 logo grid).</summary>
        static GraphicsPath Squircle(float o, float side, float d)
        {
            GraphicsPath p = new GraphicsPath();
            float e = o + side;
            p.AddArc(o, o, d, d, 180, 90); p.AddArc(e - d, o, d, d, 270, 90);
            p.AddArc(e - d, e - d, d, d, 0, 90); p.AddArc(o, e - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Write a multi-resolution .ico (PNG-compressed entries) for embedding in the exe.</summary>
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            List<byte[]> pngs = new List<byte[]>();
            foreach (int s in sizes)
                using (Bitmap b = Render(s))
                using (MemoryStream ms = new MemoryStream())
                {
                    b.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            using (BinaryWriter w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length);
                    w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) w.Write(p);
            }
        }
    }
}
