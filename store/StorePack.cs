// StorePack: builds the Microsoft Store package (.msix) for SnapMeasure with the MSIX writer that is built into
// Windows (AppxPackaging, IAppxFactory). No Windows SDK, no admin rights. The package is not signed:
// Microsoft signs it when it is uploaded to Partner Center.
//
// usage: StorePack.exe <SnapMeasure.exe> <AppxManifest.xml> <icon.png> <LICENSE> <out.msix>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

static class StorePack
{
    [ComImport, Guid("5842a140-ff9f-4166-8f5c-62f5b7b0c781")]
    class AppxFactory { }

    [StructLayout(LayoutKind.Sequential)]
    struct APPX_PACKAGE_SETTINGS
    {
        public int forceZip32;
        public IntPtr hashMethod;      // IUri*
    }

    [ComImport, Guid("beb94909-e451-438b-b5a7-d79e767b75d8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAppxFactory
    {
        void CreatePackageWriter(IStream outputStream, ref APPX_PACKAGE_SETTINGS settings, out IAppxPackageWriter packageWriter);
    }

    [ComImport, Guid("9099e33b-246f-41e4-881a-008eb613f858"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAppxPackageWriter
    {
        void AddPayloadFile([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.LPWStr)] string contentType,
                            int compressionOption, IStream inputStream);
        void Close(IStream manifest);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IntPtr template, out IStream stream);

    [DllImport("urlmon.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void CreateUri(string uri, uint flags, IntPtr reserved, out IntPtr result);

    const uint STGM_READ = 0, STGM_WRITE = 1, STGM_SHARE_DENY_WRITE = 0x20, STGM_SHARE_EXCLUSIVE = 0x10, STGM_CREATE = 0x1000;
    const int COMPRESS_NORMAL = 1;

    static int Main(string[] a)
    {
        if (a.Length != 5) { Console.WriteLine("usage: StorePack <SnapMeasure.exe> <AppxManifest.xml> <icon.png> <LICENSE> <out.msix>"); return 2; }
        string exe = a[0], manifestTemplate = a[1], icon = a[2], license = a[3], outFile = Path.GetFullPath(a[4]);
        try
        {
            // the package version is the exe's version (Store versions end in .0)
            System.Diagnostics.FileVersionInfo fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            string version = fv.FileMajorPart + "." + fv.FileMinorPart + "." + fv.FileBuildPart + ".0";
            string work = Path.Combine(Path.GetTempPath(), "SnapMeasureStore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(work, "Assets"));
            try
            {
                string manifest = File.ReadAllText(manifestTemplate, Encoding.UTF8).Replace("$VERSION$", version);
                string manifestPath = Path.Combine(work, "AppxManifest.xml");
                File.WriteAllText(manifestPath, manifest, new UTF8Encoding(false));

                // tiles and icons: drawn sharp at every size from the app's own vector logo (SnapMeasure.AppIcon.Render),
                // or scaled from the 256 px png if that can't be loaded
                List<KeyValuePair<string, string>> files = new List<KeyValuePair<string, string>>();
                Func<int, Bitmap> logo = LogoRenderer(exe, icon);
                string makepri = FindMakePri();
                string assets = Path.Combine(work, "Assets");
                if (makepri != null)
                {
                    // the full set Windows expects: taskbar / Start / Explorer icons at their exact pixel size, without the
                    // coloured plate (altform-unplated), and tiles at each display scale. resources.pri tells Windows
                    // which file is which; without it only the plain names below would ever be used.
                    foreach (int t in new[] { 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256 })
                    {
                        Square(logo, t, 0, Path.Combine(assets, "Square44x44Logo.targetsize-" + t + ".png"));
                        Square(logo, t, 0, Path.Combine(assets, "Square44x44Logo.targetsize-" + t + "_altform-unplated.png"));
                        Square(logo, t, 0, Path.Combine(assets, "Square44x44Logo.targetsize-" + t + "_altform-lightunplated.png"));
                    }
                    foreach (int sc in new[] { 100, 125, 150, 200, 400 })
                    {
                        Square(logo, 44 * sc / 100, 0, Path.Combine(assets, "Square44x44Logo.scale-" + sc + ".png"));
                        Square(logo, 150 * sc / 100, 20 * sc / 100, Path.Combine(assets, "Square150x150Logo.scale-" + sc + ".png"));
                        Wide(logo, 310 * sc / 100, 150 * sc / 100, Path.Combine(assets, "Wide310x150Logo.scale-" + sc + ".png"));
                        Square(logo, 50 * sc / 100, 0, Path.Combine(assets, "StoreLogo.scale-" + sc + ".png"));
                    }
                }
                else
                {
                    Console.WriteLine("  (makepri.exe not found: one file per logo, no resources.pri - Start and taskbar icons get the plate)");
                    Square(logo, 44, 0, Path.Combine(assets, "Square44x44Logo.png"));
                    Square(logo, 150, 20, Path.Combine(assets, "Square150x150Logo.png"));
                    Wide(logo, 310, 150, Path.Combine(assets, "Wide310x150Logo.png"));
                    Square(logo, 50, 0, Path.Combine(assets, "StoreLogo.png"));
                }
                File.Copy(exe, Path.Combine(work, "SnapMeasure.exe"));
                File.Copy(license, Path.Combine(work, "LICENSE.txt"));
                if (makepri != null) MakePri(makepri, work, manifestPath);
                foreach (string f in Directory.GetFiles(work, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(work.Length + 1);
                    if (rel == "AppxManifest.xml" || rel == "priconfig.xml") continue;
                    string type = rel.EndsWith(".png") ? "image/png" : rel.EndsWith(".exe") ? "application/x-msdownload" :
                                  rel.EndsWith(".pri") ? "application/octet-stream" : "text/plain";
                    files.Add(new KeyValuePair<string, string>(rel, type));
                }

                if (File.Exists(outFile)) File.Delete(outFile);
                IntPtr sha256;
                CreateUri("http://www.w3.org/2001/04/xmlenc#sha256", 1, IntPtr.Zero, out sha256);
                APPX_PACKAGE_SETTINGS st = new APPX_PACKAGE_SETTINGS { forceZip32 = 1, hashMethod = sha256 };
                IAppxFactory factory = (IAppxFactory)new AppxFactory();
                IStream output;
                SHCreateStreamOnFileEx(outFile, STGM_CREATE | STGM_WRITE | STGM_SHARE_EXCLUSIVE, 0x80, true, IntPtr.Zero, out output);
                IAppxPackageWriter writer;
                factory.CreatePackageWriter(output, ref st, out writer);
                foreach (KeyValuePair<string, string> kv in files)
                {
                    IStream input;
                    SHCreateStreamOnFileEx(Path.Combine(work, kv.Key), STGM_READ | STGM_SHARE_DENY_WRITE, 0, false, IntPtr.Zero, out input);
                    writer.AddPayloadFile(kv.Key, kv.Value, COMPRESS_NORMAL, input);
                    Marshal.ReleaseComObject(input);
                    Console.WriteLine("  + " + kv.Key);
                }
                IStream man;
                SHCreateStreamOnFileEx(manifestPath, STGM_READ | STGM_SHARE_DENY_WRITE, 0, false, IntPtr.Zero, out man);
                writer.Close(man);       // checks the manifest against the schema and writes the block map
                Marshal.ReleaseComObject(man);
                Marshal.ReleaseComObject(writer);
                Marshal.ReleaseComObject(output);
                Marshal.Release(sha256);
                Console.WriteLine("Built " + outFile + " (version " + version + ")");
                return 0;
            }
            finally
            {
                try { Directory.Delete(work, true); } catch (Exception) { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("StorePack failed: " + ex.Message);
            if (File.Exists(outFile)) try { File.Delete(outFile); } catch (Exception) { }
            return 1;
        }
    }

    /// <summary>Draws the logo at any pixel size: the app's own vector drawing when the exe can be loaded, else the png scaled.</summary>
    static Func<int, Bitmap> LogoRenderer(string exe, string png)
    {
        try
        {
            System.Reflection.Assembly asm = System.Reflection.Assembly.LoadFrom(Path.GetFullPath(exe));
            Type t = asm.GetType("SnapMeasure.AppIcon", true);
            System.Reflection.MethodInfo m = t.GetMethod("Render", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            using (Bitmap test = (Bitmap)m.Invoke(null, new object[] { 32 })) { }
            return size => (Bitmap)m.Invoke(null, new object[] { size });
        }
        catch (Exception ex)
        {
            Console.WriteLine("  (vector logo not available, scaling " + png + ": " + ex.Message + ")");
            return size =>
            {
                Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (Bitmap src = new Bitmap(png))
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, size, size));
                }
                return b;
            };
        }
    }

    /// <summary>makepri.exe from the Windows SDK: the MAKEPRI variable, an installed Windows Kit, or the
    /// Microsoft.Windows.SDK.BuildTools NuGet package. Null when there is none.</summary>
    static string FindMakePri()
    {
        string env = Environment.GetEnvironmentVariable("MAKEPRI");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        List<string> found = new List<string>();
        string[] roots = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Windows Kits\10\bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".nuget\packages\microsoft.windows.sdk.buildtools"),
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Environment.GetCommandLineArgs()[0])), "tools") };
        foreach (string r in roots)
            if (Directory.Exists(r))
                try
                {
                    foreach (string f in Directory.GetFiles(r, "makepri.exe", SearchOption.AllDirectories))
                        if (f.IndexOf(@"\x64\", StringComparison.OrdinalIgnoreCase) >= 0) found.Add(f);
                }
                catch (Exception) { }
        if (found.Count == 0) return null;
        found.Sort(StringComparer.OrdinalIgnoreCase);      // newest SDK version last
        return found[found.Count - 1];
    }

    static void MakePri(string makepri, string work, string manifest)
    {
        string cfg = Path.Combine(work, "priconfig.xml");
        Run(makepri, "createconfig /cf \"" + cfg + "\" /dq en-US /pv 10.0.0 /o");
        // one resources.pri for the single package: the default config splits scales into resources.scale-*.pri,
        // which only works in a bundle with resource packages
        System.Xml.XmlDocument x = new System.Xml.XmlDocument();
        x.Load(cfg);
        foreach (System.Xml.XmlNode n in x.SelectNodes("//packaging")) n.ParentNode.RemoveChild(n);
        x.Save(cfg);
        Run(makepri, "new /pr \"" + work + "\" /cf \"" + cfg + "\" /mn \"" + manifest + "\" /of \"" + Path.Combine(work, "resources.pri") + "\" /o");
        File.Delete(cfg);
        Console.WriteLine("  resources.pri made with " + makepri);
    }

    static void Run(string exe, string args)
    {
        System.Diagnostics.ProcessStartInfo si = new System.Diagnostics.ProcessStartInfo(exe, args);
        si.UseShellExecute = false; si.RedirectStandardOutput = true; si.RedirectStandardError = true; si.CreateNoWindow = true;
        using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(si))
        {
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception("makepri failed (" + p.ExitCode + "):\n" + output);
        }
    }

    /// <summary>The icon on a transparent square, with a margin (tiles look better with some space round the icon).</summary>
    static void Square(Func<int, Bitmap> logo, int size, int margin, string path)
    {
        using (Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (Bitmap src = logo(size - 2 * margin))
        {
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                g.DrawImageUnscaled(src, margin, margin);
            }
            b.Save(path, ImageFormat.Png);
        }
    }

    /// <summary>Wide tile: the icon in the middle, its height 70 % of the tile.</summary>
    static void Wide(Func<int, Bitmap> logo, int w, int h, string path)
    {
        int s = (int)(h * 0.7f);
        using (Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        using (Bitmap src = logo(s))
        {
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                g.DrawImageUnscaled(src, (w - s) / 2, (h - s) / 2);
            }
            b.Save(path, ImageFormat.Png);
        }
    }
}
