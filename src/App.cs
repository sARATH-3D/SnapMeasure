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
[assembly: System.Reflection.AssemblyVersion("1.0.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.1.0")]
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
                try { MessageBox.Show("SnapMeasure hit an unexpected problem and has to close:\n\n" + ((Exception)e.ExceptionObject).Message, "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            };
            if (args.Length == 2 && args[0] == "--write-icon") { AppIcon.WriteIco(args[1]); return; }

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
                MessageBox.Show(e.Exception.Message + "\n\n" + e.Exception.StackTrace, "SnapMeasure – unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Settings.Load();

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
                    else
                        MessageBox.Show("SnapMeasure is already running.\n\nLook for its icon in the system tray (bottom-right, maybe under the ^ arrow), or press Print Screen.",
                            "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                TrayContext tray = new TrayContext();
                foreach (string a in args) App.OpenFile(a);
                Application.Run(tray);
            }
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

        static string P(PointF p) { return p.X.ToString("F2", CultureInfo.InvariantCulture) + "," + p.Y.ToString("F2", CultureInfo.InvariantCulture); }

        public static void Run(string dir)
        {
            Directory.CreateDirectory(dir);
            StringBuilder log = new StringBuilder();
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
            Shape hl = Mk(cv, Tool.Highlight, 106, 518, 318, 546);                // text marker
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
            check("Letters wrap Z-A", lab(l1) + lab(l2), "ZA");
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

            // a bubble started near the bottom-right corner is pulled fully inside the image
            {
                RectangleF br = new RectangleF(850, 580, 170, 70);
                PointF bt = Canvas.FitBubble(ref br, new RectangleF(0, 0, 900, 600));
                bool inside = br.X >= 0 && br.Y >= 0 && br.Right <= 900 && br.Bottom <= 600 && bt.X >= 0 && bt.X <= 900 && bt.Y >= 0 && bt.Y <= 600;
                check("Bubble kept inside image", inside ? "inside" : (br + " tail " + bt), "inside");
            }

            // undo keeps the edited shape selected, so the style panel shows its restored values
            {
                Shape hs = Mk(cv, Tool.Highlight, 10, 10, 100, 40);
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
                ShapeOps.DragHandle(drag, im, new Handle(PointF.Empty, HKind.Box, 4), new PointF(530, 400), false);
                RectangleF rr2 = Geo.Rect(drag.Pts[0], drag.Pts[1]);
                check("Picture keeps proportions", (rr2.Width / rr2.Height).ToString("F2", CultureInfo.InvariantCulture), "2.00");
                List<Shape> back3 = ProjectFile.ShapesFromXml(ProjectFile.ShapesToXml(new Shape[] { ar, im }));
                check("Heads + picture round-trip", back3.Count + "|" + back3[0].Heads + "|" + (back3[1].Img != null ? back3[1].Img.Width + "x" + back3[1].Img.Height : "none"), "2|3|40x20");
                using (Bitmap flat = cv.Flatten(null)) flat.Save(Path.Combine(dir, "selftest_heads.png"), ImageFormat.Png);
                cv.Shapes.Remove(ln); cv.Shapes.Remove(ar); cv.Shapes.Remove(im); cv.Selected = null;
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
                      "1|200|3|1000000");
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

        /// <summary>The captured app's window title (e.g. "Autodesk AutoCAD LT 2026 - [plan.dwg]"), or a fallback name.</summary>
        static string Named(string windowTitle, string fallback)
        {
            string t = (windowTitle ?? "").Trim();
            if (t.Length == 0 || t.StartsWith("SnapMeasure")) return fallback;
            return t.Length > 120 ? t.Substring(0, 120) : t;
        }

        static void Deliver(Bitmap b, string title)
        {
            if (b == null) return;
            if (Settings.GetBool("CopyOnCapture", true))
            {
                try { Clipboard.SetImage(b); } catch { }
            }
            EditorForm f = new EditorForm(b, title);
            f.Show();
            f.Activate();
        }

        static void Guard(Action a)
        {
            if (busy) return;
            busy = true;
            try { a(); }
            catch (Exception ex) { MessageBox.Show("Capture failed: " + ex.Message, "SnapMeasure", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { busy = false; }
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
                Deliver(b, Named(title, "Region " + r.Width + "×" + r.Height));
            });
        }

        public static void CaptureActiveWindow()
        {
            Guard(() =>
            {
                IntPtr h = Native.GetForegroundWindow();
                Rectangle r = h == IntPtr.Zero ? Rectangle.Empty : Native.GetWindowBounds(h);
                r.Intersect(ScreenCapture.VirtualBounds);
                if (r.Width < 2 || r.Height < 2) return;
                Deliver(ScreenCapture.Grab(r), Named(Native.WindowTitle(h), "Window " + r.Width + "×" + r.Height));
            });
        }

        public static void CaptureScreenUnderCursor()
        {
            Guard(() =>
            {
                Rectangle r = Screen.FromPoint(Cursor.Position).Bounds;
                Deliver(ScreenCapture.Grab(r), Named(Native.WindowTitle(Native.GetForegroundWindow()), "Screen " + r.Width + "×" + r.Height));
            });
        }

        public static void CaptureAllScreens()
        {
            Guard(() =>
            {
                Rectangle r = ScreenCapture.VirtualBounds;
                Deliver(ScreenCapture.Grab(r), "All screens " + r.Width + "×" + r.Height);
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
                string t = ScreenCapture.TitleFor(r, wins, titles, Native.WindowTitle(Native.GetForegroundWindow()));
                Deliver(ScreenCapture.Grab(r), Named(t, "Last region " + r.Width + "×" + r.Height));
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
        static AboutForm about;

        public static void ShowAbout(IWin32Window owner)
        {
            if (about != null && !about.IsDisposed) { about.Activate(); return; }
            about = new AboutForm(AboutText.Shortcuts(FailedHotkeys));
            about.FormClosed += (s, e) => about = null;
            about.Show(owner);
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

        public static Form LoadEditor(string path)
        {
            try
            {
                if (Path.GetExtension(path).ToLowerInvariant() == ".snapm") return EditorForm.FromProject(path);
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

        public void Dispose()
        {
            foreach (int id in ids) Native.UnregisterHotKey(Handle, id);
            ids.Clear();
            DestroyHandle();
        }
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
            ni.Text = "SnapMeasure – Print Screen to capture";
            ni.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem region = Item(menu, "Capture region / window", "PrtSc", () => App.CaptureRegion());
            region.Font = new Font(region.Font, FontStyle.Bold);
            region.Image = Icons.Cmd("region", 16);
            Item(menu, "Capture active window", "Alt+PrtSc", () => App.CaptureActiveWindow(), false).Image = Icons.Cmd("window", 16);
            Item(menu, "Capture screen under mouse", "Ctrl+PrtSc", () => App.CaptureScreenUnderCursor()).Image = Icons.Cmd("screen", 16);
            Item(menu, "Capture all screens", "", () => App.CaptureAllScreens()).Image = Icons.Cmd("allscreens", 16);
            Item(menu, "Capture last region again", "Shift+PrtSc", () => App.CaptureLastRegion()).Image = Icons.Cmd("lastregion", 16);
            Item(menu, "Capture region in 3 seconds", "", () => App.Later(3000, App.CaptureRegion)).Image = Icons.Cmd("timer", 16);
            menu.Items.Add(new ToolStripSeparator());
            Item(menu, "New blank canvas…", "", () => App.NewBlankCanvas(null), false).Image = Icons.Cmd("blank", 16);
            Item(menu, "Open image or project…", "", OpenDialog, false).Image = Icons.Cmd("open", 16);
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
            Item(menu, "Output settings…", "", () => Dialogs.OutputSettings(null), false).Image = Icons.Cmd("settings", 16);
            Item(menu, "Open save folder", "", OpenFolder, false).Image = Icons.Cmd("folderopen", 16);
            menu.Items.Add(new ToolStripSeparator());
            Item(menu, "About SnapMeasure && shortcuts…", "", ShowAbout, false).Image = Icons.Cmd("help", 16);
            Item(menu, "Exit", "", ExitApp, false).Image = Icons.Cmd("exit", 16);
            ni.ContextMenuStrip = menu;
            ni.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) App.CaptureRegion(); };

            hk = new HotkeyWindow();
            hk.Pressed += id => { Action a; if (actions.TryGetValue(id, out a)) a(); };
            Reg(1, 0, Keys.PrintScreen, "PrtSc", App.CaptureRegion);
            Reg(2, Native.MOD_ALT, Keys.PrintScreen, "Alt+PrtSc", App.CaptureActiveWindow);
            Reg(3, Native.MOD_CONTROL, Keys.PrintScreen, "Ctrl+PrtSc", App.CaptureScreenUnderCursor);
            Reg(4, Native.MOD_SHIFT, Keys.PrintScreen, "Shift+PrtSc", App.CaptureLastRegion);
            Reg(5, Native.MOD_CONTROL | Native.MOD_SHIFT, Keys.D1, "Ctrl+Shift+1", App.CaptureRegion);
            Reg(6, Native.MOD_CONTROL | Native.MOD_SHIFT, Keys.D2, "Ctrl+Shift+2", App.CaptureActiveWindow);
            Reg(7, Native.MOD_CONTROL | Native.MOD_SHIFT, Keys.D3, "Ctrl+Shift+3", App.CaptureScreenUnderCursor);
            Reg(8, Native.MOD_CONTROL | Native.MOD_SHIFT, Keys.D4, "Ctrl+Shift+4", App.CaptureLastRegion);

            string msg = failedKeys.Count == 0
                ? "Press Print Screen (or Ctrl+Shift+1) to capture. Right-click this icon for more."
                : "Ready. These keys are taken by another app (e.g. Windows Snipping Tool or Greenshot): " + string.Join(", ", failedKeys.ToArray()) +
                  ".\nUse Ctrl+Shift+1 to capture instead.";
            ni.ShowBalloonTip(5000, "SnapMeasure is running", msg, failedKeys.Count == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
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

        void OpenDialog()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Images and projects|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.snapm|All files|*.*";
                if (d.ShowDialog() == DialogResult.OK) App.OpenFile(d.FileName);
            }
        }

        void OpenFolder()
        {
            try { Directory.CreateDirectory(Settings.SaveFolder); Process.Start("explorer.exe", "\"" + Settings.SaveFolder + "\""); }
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
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("CAPTURE");
            sb.AppendLine("  PrtSc / Ctrl+Shift+1\tRegion (or click a window)");
            sb.AppendLine("  Alt+PrtSc / Ctrl+Shift+2\tActive window");
            sb.AppendLine("  Ctrl+PrtSc / Ctrl+Shift+3\tScreen under mouse");
            sb.AppendLine("  Shift+PrtSc / Ctrl+Shift+4\tLast region again");
            if (!string.IsNullOrEmpty(failed)) sb.AppendLine("  Not available (used by another app): " + failed);
            sb.AppendLine();
            sb.AppendLine("EDITOR TOOLS");
            sb.AppendLine("  V Select   D Dimension   M Area   K Calibrate scale");
            sb.AppendLine("  P Polygon   H Highlight   R Rectangle   E Ellipse   L Line");
            sb.AppendLine("  A Arrow   F Freehand   T Text   S Speech bubble   G Angle");
            sb.AppendLine("  N Number marker   Shift+N Letter marker");
            sb.AppendLine("  B Pixelate   C Crop (adjustable; Delete = remove crop)");
            sb.AppendLine();
            sb.AppendLine("  Ctrl+A select all   Ctrl+C/X/V copy, cut, paste shapes   Ctrl+D duplicate");
            sb.AppendLine("  Ctrl+] / Ctrl+[ forward / backward   (+Shift = front / back)   Ctrl+R rulers");
            sb.AppendLine();
            sb.AppendLine("  Home = fit & centre   Shift = 15° / square   Alt = no snapping");
            sb.AppendLine("  Ctrl+wheel = zoom   Space+drag or middle mouse = pan");
            sb.AppendLine("  Alt+1..9 line colour   Alt+Shift+1..9 fill colour   Alt+L / Alt+F pickers");
            sb.AppendLine("  Ctrl+S save picture (+ .snapm)   Ctrl+Shift+S save as   Ctrl+P pin   F4 scale panel");
            sb.AppendLine();
            sb.AppendLine("Settings file: " + Settings.FilePath);
            return sb.ToString();
        }
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
            if (!File.Exists(FilePath)) return;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                int i = line.IndexOf('=');
                if (i > 0 && !line.StartsWith(";")) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
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
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch { }
        }

        public static string Get(string key, string def) { string v; return d.TryGetValue(key, out v) ? v : def; }
        public static void Set(string key, string val) { d[key] = val; Save(); }
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

        public PinForm(Bitmap b)
        {
            img = b;
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
            m.Items.Add(size);
            ToolStripMenuItem op = new ToolStripMenuItem("Opacity");
            foreach (int p in new int[] { 100, 80, 60, 40, 25 })
            {
                int pp = p;
                op.DropDownItems.Add(p + "%", null, (s, e) => Opacity = pp / 100.0);
            }
            m.Items.Add(op);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Close", Icons.Cmd("close", 16), (s, e) => Close());
            m.Opening += (s, e) => reset.Enabled = src.Width != img.Width || src.Height != img.Height;
            ContextMenuStrip = m;
        }

        Bitmap ShownImage()
        {
            Bitmap b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b)) g.DrawImage(img, new Rectangle(0, 0, src.Width, src.Height), src, GraphicsUnit.Pixel);
            return b;
        }

        void CopyShown() { using (Bitmap b = ShownImage()) Clipboard.SetImage(b); }

        void ApplySize()
        {
            ClientSize = new Size(Math.Max(16, (int)(src.Width * z)) + 2, Math.Max(16, (int)(src.Height * z)) + 2);
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
            g.DrawImage(img, new Rectangle(1, 1, ClientSize.Width - 2, ClientSize.Height - 2), src, GraphicsUnit.Pixel);
            using (Pen p = new Pen(Color.FromArgb(0, 120, 215))) g.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
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
                int w = rc.Right - rc.Left - 2, h = rc.Bottom - rc.Top - 2;
                float ratio = (float)src.Width / src.Height;
                if (edge == 1 || edge == 2) h = (int)Math.Round(w / ratio);          // left / right
                else if (edge == 3 || edge == 6) w = (int)Math.Round(h * ratio);     // top / bottom
                else { if (w / ratio > h) h = (int)Math.Round(w / ratio); else w = (int)Math.Round(h * ratio); }
                w = Math.Max(16, w); h = Math.Max(16, h);
                if (edge == 1 || edge == 4 || edge == 7) rc.Left = rc.Right - w - 2; else rc.Right = rc.Left + w + 2;
                if (edge == 3 || edge == 4 || edge == 5) rc.Top = rc.Bottom - h - 2; else rc.Bottom = rc.Top + h + 2;
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
                cropB = new Point(Math.Max(1, Math.Min(ClientSize.Width - 2, e.X)), Math.Max(1, Math.Min(ClientSize.Height - 2, e.Y)));
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
                    float sx = (float)src.Width / (ClientSize.Width - 2), sy = (float)src.Height / (ClientSize.Height - 2);
                    Point oldTopLeft = PointToScreen(r.Location);
                    Rectangle ns = Rectangle.Round(new RectangleF(src.X + (r.X - 1) * sx, src.Y + (r.Y - 1) * sy, r.Width * sx, r.Height * sy));
                    ns.Intersect(src);
                    if (ns.Width >= 2 && ns.Height >= 2)
                    {
                        src = ns;
                        ApplySize();
                        Location = new Point(oldTopLeft.X - 1, oldTopLeft.Y - 1);   // the kept part stays where it was
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
            else if (e.Control && e.KeyCode == Keys.C) CopyShown();
            else if (e.KeyCode == Keys.C && !e.Control && !e.Alt) StartCrop();
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
            return f;
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

        /// <summary>Output settings: save folder, file name pattern, format, quality, and what else to do on save.</summary>
        public static void OutputSettings(IWin32Window owner)
        {
            float k = K(owner);
            Func<float, int> S = v => (int)Math.Round(v * k);
            using (Form f = Base("Output settings", k, 520, 330))
            {
                Func<string, int, Label> lab = (t, y) =>
                {
                    Label l = new Label(); l.Text = t; l.AutoSize = true; l.Location = new Point(S(12), S(y + 4)); f.Controls.Add(l); return l;
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
                CheckBox clip = new CheckBox(); clip.Text = "Copy every new capture to the clipboard";
                clip.AutoSize = true; clip.Location = new Point(S(14), S(238)); clip.Checked = Settings.GetBool("CopyOnCapture", true);

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

                foreach (Control c in new Control[] { folder, browse, pattern, help, example, fmt, q, ql, proj, copy, clip }) f.Controls.Add(c);
                Buttons(f, k);
                while (f.ShowDialog(owner) == DialogResult.OK)
                {
                    string dir = folder.Text.Trim();
                    bool okDir = false;
                    try { okDir = dir.Length > 0 && Path.IsPathRooted(dir); if (okDir) Directory.CreateDirectory(dir); }
                    catch (Exception) { okDir = false; }
                    if (!okDir) { MessageBox.Show(f, "Choose a folder that exists or can be created, e.g. C:\\Users\\you\\Pictures\\SnapMeasure", "Output settings"); continue; }
                    Settings.Set("SaveFolder", dir);
                    Settings.Set("FilePattern", string.IsNullOrWhiteSpace(pattern.Text) ? Output.DefaultPattern : pattern.Text.Trim());
                    Settings.Set("ImageFormat", Output.Formats[Math.Max(0, fmt.SelectedIndex)]);
                    Settings.Set("JpegQuality", q.Value.ToString(CultureInfo.InvariantCulture));
                    Settings.Set("SaveProjectToo", proj.Checked ? "1" : "0");
                    Settings.Set("CopyPathOnSave", copy.Checked ? "1" : "0");
                    Settings.Set("CopyOnCapture", clip.Checked ? "1" : "0");
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
    }

    internal static class Icons
    {
        /// <summary>Tool icons drawn as vectors at the exact pixel size requested (no stretching).</summary>
        public static Bitmap Make(Tool t, int size)
        {
            Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.ScaleTransform(size / 32f, size / 32f);
                Color ink = Color.FromArgb(45, 45, 48), red = Color.FromArgb(220, 40, 40), blue = Color.FromArgb(0, 110, 210), amber = Color.FromArgb(240, 170, 0);
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
                        case Tool.Text: g.DrawString("T", new Font("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Pixel), bi, new RectangleF(0, 0, 32, 32), sf); break;
                        case Tool.Polygon:
                            {
                                PointF[] a = { new PointF(4, 13), new PointF(13, 4), new PointF(28, 9), new PointF(24, 20), new PointF(28, 27), new PointF(9, 28) };
                                using (SolidBrush f = new SolidBrush(Color.FromArgb(130, 255, 215, 0))) g.FillPolygon(f, a);
                                using (Pen pp = new Pen(amber, 2f)) { pp.LineJoin = LineJoin.Round; g.DrawPolygon(pp, a); }
                                foreach (PointF q in a) g.FillRectangle(bi, q.X - 1.5f, q.Y - 1.5f, 3, 3);
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
                                g.DrawString("12", new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel), bi, new RectangleF(0, 3, 32, 14), sf);
                                break;
                            }
                        case Tool.Area:
                            {
                                PointF[] a = { new PointF(4, 8), new PointF(22, 4), new PointF(28, 18), new PointF(17, 28), new PointF(5, 24) };
                                using (SolidBrush f = new SolidBrush(Color.FromArgb(70, 0, 110, 210))) g.FillPolygon(f, a);
                                g.DrawPolygon(pb, a);
                                using (SolidBrush bb = new SolidBrush(blue))
                                    g.DrawString("m²", new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Pixel), bb, new RectangleF(0, 2, 32, 30), sf);
                                break;
                            }
                        case Tool.Calibrate:
                            {
                                GraphicsState st = g.Save();
                                g.TranslateTransform(16, 16);
                                g.RotateTransform(-35);
                                RectangleF r = new RectangleF(-14, -5, 28, 10);
                                using (SolidBrush f = new SolidBrush(Color.FromArgb(255, 225, 120))) g.FillRectangle(f, r);
                                using (Pen pp = new Pen(ink, 1.4f))
                                {
                                    g.DrawRectangle(pp, r.X, r.Y, r.Width, r.Height);
                                    for (int i = 0; i < 6; i++) { float x = -11 + i * 4.5f; g.DrawLine(pp, x, -5, x, i % 2 == 0 ? 0 : -2); }
                                }
                                g.Restore(st);
                                break;
                            }
                        case Tool.Counter:
                            using (SolidBrush rb = new SolidBrush(red)) g.FillEllipse(rb, 5, 5, 22, 22);
                            g.DrawString("1", new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel), Brushes.White, new RectangleF(5, 5, 22, 23), sf);
                            break;
                        case Tool.Letter:
                            using (SolidBrush rb = new SolidBrush(blue)) g.FillEllipse(rb, 5, 5, 22, 22);
                            g.DrawString("A", new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel), Brushes.White, new RectangleF(5, 5, 22, 23), sf);
                            break;
                        case Tool.Angle:
                            using (Pen pp = new Pen(blue, 2.2f))
                            {
                                pp.StartCap = pp.EndCap = LineCap.Round; pp.LineJoin = LineJoin.Round;
                                g.DrawLines(pp, new PointF[] { new PointF(27, 6), new PointF(5, 26), new PointF(28, 26) });
                            }
                            using (Pen pp = new Pen(red, 1.8f)) g.DrawArc(pp, -6, 15, 22, 22, -42, 42);
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
                                using (SolidBrush y = new SolidBrush(Color.FromArgb(170, 255, 225, 0))) g.FillRectangle(y, 3, 17, 26, 9);
                                GraphicsState st = g.Save();
                                g.TranslateTransform(19, 12);
                                g.RotateTransform(40);
                                using (SolidBrush body = new SolidBrush(Color.FromArgb(255, 200, 0))) g.FillRectangle(body, -4, -11, 8, 15);
                                g.FillPolygon(bi, new PointF[] { new PointF(-4, 4), new PointF(4, 4), new PointF(1.5f, 9), new PointF(-1.5f, 9) });
                                using (Pen pp = new Pen(ink, 1.2f)) g.DrawRectangle(pp, -4, -11, 8, 15);
                                g.Restore(st);
                                break;
                            }
                        case Tool.Bubble:
                            using (GraphicsPath gp = Renderer.BubblePath(new RectangleF(3, 4, 26, 17), new PointF(9, 29)))
                            {
                                g.FillPath(Brushes.White, gp);
                                using (Pen pp = new Pen(ink, 1.8f)) { pp.LineJoin = LineJoin.Round; g.DrawPath(pp, gp); }
                            }
                            using (Pen pp = new Pen(ink, 1.6f)) { g.DrawLine(pp, 8, 10, 24, 10); g.DrawLine(pp, 8, 15, 19, 15); }
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
                    }
                }
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
                g.ScaleTransform(size / 64f, size / 64f);
                // Logo "M1": a snip (dashed selection) measured corner to corner with a dimension arrow
                bool small = size <= 24;
                using (GraphicsPath path = new GraphicsPath())
                {
                    float r = 28;
                    path.AddArc(2, 2, r, r, 180, 90); path.AddArc(62 - r, 2, r, r, 270, 90);
                    path.AddArc(62 - r, 62 - r, r, r, 0, 90); path.AddArc(2, 62 - r, r, r, 90, 90);
                    path.CloseFigure();
                    using (SolidBrush br = new SolidBrush(Color.FromArgb(44, 44, 42))) g.FillPath(br, path);
                }
                using (Pen w = new Pen(Color.White, small ? 5f : 3f))
                {
                    if (!small) w.DashPattern = new float[] { 2f, 1.33f };
                    g.DrawRectangle(w, 14, 14, 36, 36);
                }
                Color y = Color.FromArgb(250, 199, 117);
                using (Pen p = new Pen(y, small ? 7f : 4f))
                {
                    if (small) g.DrawLine(p, 14, 50, 50, 14);
                    else g.DrawLine(p, 19, 45, 45, 19);
                }
                if (!small)
                    using (SolidBrush yb = new SolidBrush(y))
                    {
                        g.FillPolygon(yb, new PointF[] { new PointF(14, 50), new PointF(18, 39), new PointF(25, 46) });
                        g.FillPolygon(yb, new PointF[] { new PointF(50, 14), new PointF(46, 25), new PointF(39, 18) });
                    }
            }
            return b;
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
