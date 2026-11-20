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

                // tiles and icons, drawn from the 256 px app icon
                List<KeyValuePair<string, string>> files = new List<KeyValuePair<string, string>>();
                using (Bitmap src = new Bitmap(icon))
                {
                    // one file per logo (no resources.pri, so scale variants would never be picked)
                    Square(src, 44, 0, Path.Combine(work, @"Assets\Square44x44Logo.png"));
                    Square(src, 150, 20, Path.Combine(work, @"Assets\Square150x150Logo.png"));
                    Wide(src, 310, 150, Path.Combine(work, @"Assets\Wide310x150Logo.png"));
                    Square(src, 50, 0, Path.Combine(work, @"Assets\StoreLogo.png"));
                }
                File.Copy(exe, Path.Combine(work, "SnapMeasure.exe"));
                File.Copy(license, Path.Combine(work, "LICENSE.txt"));
                foreach (string f in Directory.GetFiles(work, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(work.Length + 1);
                    if (rel == "AppxManifest.xml") continue;
                    string type = rel.EndsWith(".png") ? "image/png" : rel.EndsWith(".exe") ? "application/x-msdownload" : "text/plain";
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

    /// <summary>The icon on a transparent square, with a margin (tiles look better with some space round the icon).</summary>
    static void Square(Bitmap src, int size, int margin, string path)
    {
        using (Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(src, new Rectangle(margin, margin, size - 2 * margin, size - 2 * margin));
            }
            b.Save(path, ImageFormat.Png);
        }
    }

    /// <summary>Wide tile: the icon in the middle, its height 70 % of the tile.</summary>
    static void Wide(Bitmap src, int w, int h, string path)
    {
        using (Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                int s = (int)(h * 0.7f);
                g.DrawImage(src, new Rectangle((w - s) / 2, (h - s) / 2, s, s));
            }
            b.Save(path, ImageFormat.Png);
        }
    }
}
