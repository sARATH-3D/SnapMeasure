using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SnapMeasure
{
    /// <summary>File Explorer support for .snapm projects: a thumbnail and a preview-pane picture, both read from the
    /// small preview a project stores at the start of the file. Switched in Settings → Output.
    ///
    /// Windows only starts COM handlers in its isolated helper processes (dllhost for thumbnails, prevhost for the
    /// preview pane) when they are registered for the whole machine; per-user registrations are ignored there, and
    /// Explorer always uses those helpers. So:
    ///  - without admin rights: the thumbnail handler is registered for the current user, loaded into SnapMeasure
    ///    itself (from a copy of the exe in %LOCALAPPDATA%), and every project SnapMeasure saves gets its thumbnail
    ///    made at once and put in Windows' thumbnail cache, where Explorer finds it (WarmThumbnail);
    ///  - with one administrator approval (Settings → Output): thumbnail and preview pane are registered for the
    ///    machine and run in Windows' helpers from a copy that is refreshed when SnapMeasure is updated – then
    ///    every .snapm shows, also projects from other PCs.</summary>
    internal static class ShellPreview
    {
        public const string ThumbClsid = "{727caae8-5343-4ae5-a6e8-feb1876f0b2e}";
        public const string PreviewClsid = "{baa97b7f-0e07-4ec4-9d3c-fb5edf99c512}";
        const string ThumbIid = "{e357fccd-a995-4576-b01f-234630154e96}";      // IThumbnailProvider
        const string PreviewIid = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";    // IPreviewHandler
        const string PrevHost64 = "{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}";    // Windows' 64-bit preview host
        const string Assoc = @"Software\Classes\SystemFileAssociations\.snapm\ShellEx\";
        const string Handlers = @"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers";
        public const string ElevatedArg = "--explorer-preview-pane";

        /// <summary>The Store version can't register shell handlers from inside its package.</summary>
        public static bool Supported { get { return !AppInfo.IsStoreApp; } }

        public static bool IsOn { get { return Settings.GetBool("ExplorerPreview", true); } }

        static string ExePath { get { return typeof(ShellPreview).Assembly.Location; } }

        static string CopyRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"SnapMeasure\Explorer"); } }

        /// <summary>This build's copy for Explorer's thumbnails: one folder per build, so a newer exe never has to
        /// overwrite a copy Explorer still has open.</summary>
        static string ThumbCopy
        {
            get
            {
                FileInfo fi = new FileInfo(ExePath);
                string tag = typeof(ShellPreview).Assembly.GetName().Version + "-" + fi.LastWriteTimeUtc.Ticks.ToString("x") + "-" + fi.Length.ToString("x");
                return Path.Combine(CopyRoot, tag, "SnapMeasure.exe");
            }
        }

        /// <summary>The preview pane's copy: one fixed place (the machine registration points here), replaced when
        /// a newer SnapMeasure starts and the preview host doesn't have it open.</summary>
        static string PaneCopy { get { return Path.Combine(CopyRoot, @"pane\SnapMeasure.exe"); } }

        /// <summary>The form regasm writes: file:/// and forward slashes, spaces left as they are (%20 is not found).</summary>
        static string CodeBase(string path) { return "file:///" + path.Replace('\\', '/'); }

        /// <summary>The full name is needed (without a version the class isn't found), but for an unsigned assembly
        /// the version isn't checked: the machine registration stays valid when the copy is updated.</summary>
        static string AssemblyName { get { return typeof(ShellPreview).Assembly.FullName; } }

        static bool Registered(RegistryKey root, string clsid, string path)
        {
            using (RegistryKey k = root.OpenSubKey(@"Software\Classes\CLSID\" + clsid + @"\InprocServer32"))
                return k != null && (k.GetValue("CodeBase") as string) == CodeBase(path) && File.Exists(path);
        }

        /// <summary>Is the preview pane registered for this machine (and pointing at this user's copy)?</summary>
        public static bool PaneIsOn
        {
            get { try { return Registered(Registry.LocalMachine, PreviewClsid, PaneCopy); } catch (Exception) { return false; } }
        }

        /// <summary>At start: keep the registrations pointing at this build when the setting is on.</summary>
        public static void Refresh()
        {
            if (!Supported) return;
            try
            {
                if (PaneIsOn) { UpdateCopy(PaneCopy); RemoveUserThumb(); }       // the machine registration does it all
                else if (IsOn && !Registered(Registry.CurrentUser, ThumbClsid, ThumbCopy)) Set(true);
            }
            catch (Exception ex) { ErrorLog.Write("explorer previews", ex); }
        }

        /// <summary>Puts this exe at 'copy' unless the same build is already there. False if the old one is in use.</summary>
        static bool UpdateCopy(string copy)
        {
            FileInfo src = new FileInfo(ExePath), dst = new FileInfo(copy);
            if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc) return true;
            try
            {
                Directory.CreateDirectory(dst.DirectoryName);
                File.Copy(ExePath, copy + ".tmp", true);
                if (dst.Exists) File.Delete(copy);
                File.Move(copy + ".tmp", copy);
                return true;
            }
            catch (Exception) { try { File.Delete(copy + ".tmp"); } catch (Exception) { } return false; }
        }

        /// <summary>Copies the exe for Explorer's thumbnails and removes copies of older builds nothing has open.</summary>
        static string MakeThumbCopy()
        {
            string copy = ThumbCopy;
            UpdateCopy(copy);
            foreach (string d in Directory.GetDirectories(CopyRoot))
            {
                string name = Path.GetFileName(d);
                if (name == "pane" || string.Equals(d, Path.GetDirectoryName(copy), StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(d, true); } catch (Exception) { }     // still open in Explorer: next time
            }
            return copy;
        }

        /// <summary>Switches the Explorer thumbnails on or off (current user). Returns a note if it failed.</summary>
        public static string Set(bool on)
        {
            if (!Supported) return "The Microsoft Store version can't add File Explorer previews yet.";
            try
            {
                RegistryKey cu = Registry.CurrentUser;
                if (on)
                {
                    Server(cu, ThumbClsid, "SnapMeasure project thumbnail", typeof(SnapmThumbnail), MakeThumbCopy(), null, true);
                    using (RegistryKey k = cu.CreateSubKey(Assoc + ThumbIid)) k.SetValue("", ThumbClsid);
                }
                else
                {
                    cu.DeleteSubKeyTree(@"Software\Classes\CLSID\" + ThumbClsid, false);
                    cu.DeleteSubKeyTree(Assoc + ThumbIid, false);
                    if (Directory.Exists(CopyRoot))
                        foreach (string d in Directory.GetDirectories(CopyRoot))
                            if (Path.GetFileName(d) != "pane") try { Directory.Delete(d, true); } catch (Exception) { }
                }
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);       // SHCNE_ASSOCCHANGED: Explorer re-reads handlers
                return null;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("explorer previews", ex);
                return "Couldn't change the File Explorer thumbnails: " + ex.Message;
            }
        }

        /// <summary>Preview pane on or off: runs SnapMeasure once more with administrator rights (Windows asks) to
        /// write the machine registration. Returns a note if it didn't happen.</summary>
        public static string SetPane(IWin32Window owner, bool on)
        {
            if (!Supported) return "The Microsoft Store version can't add a File Explorer preview yet.";
            try
            {
                if (on && !UpdateCopy(PaneCopy)) return "The preview pane copy is in use – close File Explorer's preview pane and try again.";
                System.Diagnostics.ProcessStartInfo si = new System.Diagnostics.ProcessStartInfo(ExePath,
                    ElevatedArg + " " + (on ? "on" : "off") + " \"" + PaneCopy + "\"");
                si.Verb = "runas";
                si.UseShellExecute = true;
                si.ErrorDialog = true;
                if (owner != null) si.ErrorDialogParentHandle = owner.Handle;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(si))
                {
                    p.WaitForExit();
                    if (p.ExitCode != 0) return "Windows couldn't register the preview pane (code " + p.ExitCode + "). Details: " + ErrorLog.FilePath;
                }
                if (on) RemoveUserThumb();        // a per-user entry would hide the machine one from Explorer
                else
                {
                    try { Directory.Delete(Path.GetDirectoryName(PaneCopy), true); } catch (Exception) { }
                    if (IsOn) Set(true);
                }
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
                return null;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return "The preview pane wasn't changed (administrator approval was cancelled).";
                ErrorLog.Write("explorer preview pane", ex);
                return "Couldn't change the preview pane: " + ex.Message;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("explorer preview pane", ex);
                return "Couldn't change the preview pane: " + ex.Message;
            }
        }

        /// <summary>SnapMeasure.exe --explorer-preview-pane on|off "copy": the elevated half of SetPane.</summary>
        public static int MachineRegister(string[] args)
        {
            try
            {
                bool on = args.Length > 1 && args[1] == "on";
                RegistryKey lm = Registry.LocalMachine;
                if (on)
                {
                    string path = args.Length > 2 ? args[2] : "";
                    if (!File.Exists(path) || !string.Equals(Path.GetFileName(path), "SnapMeasure.exe", StringComparison.OrdinalIgnoreCase)) return 2;
                    // registered for the machine, both run in Windows' own isolated helpers (dllhost / prevhost)
                    Server(lm, ThumbClsid, "SnapMeasure project thumbnail", typeof(SnapmThumbnail), path, null, false);
                    using (RegistryKey k = lm.CreateSubKey(Assoc + ThumbIid)) k.SetValue("", ThumbClsid);
                    Server(lm, PreviewClsid, "SnapMeasure project preview", typeof(SnapmPreviewHandler), path, PrevHost64, false);
                    using (RegistryKey k = lm.CreateSubKey(Assoc + PreviewIid)) k.SetValue("", PreviewClsid);
                    using (RegistryKey k = lm.CreateSubKey(Handlers)) k.SetValue(PreviewClsid, "SnapMeasure project preview");
                }
                else
                {
                    lm.DeleteSubKeyTree(@"Software\Classes\CLSID\" + ThumbClsid, false);
                    lm.DeleteSubKeyTree(Assoc + ThumbIid, false);
                    lm.DeleteSubKeyTree(@"Software\Classes\CLSID\" + PreviewClsid, false);
                    lm.DeleteSubKeyTree(Assoc + PreviewIid, false);
                    using (RegistryKey k = lm.OpenSubKey(Handlers, true))
                        if (k != null) k.DeleteValue(PreviewClsid, false);
                }
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
                return 0;
            }
            catch (Exception ex) { ErrorLog.Write("explorer preview pane (admin)", ex); return 1; }
        }

        /// <summary>An in-process COM server hosted by the .NET runtime (the same keys regasm /codebase writes).
        /// inCaller: loaded straight into the process that asks (DisableProcessIsolation), else into Windows' helper
        /// process; appId picks that helper (the preview host) where Windows needs it.</summary>
        static void Server(RegistryKey root, string clsid, string name, Type type, string path, string appId, bool inCaller)
        {
            root.DeleteSubKeyTree(@"Software\Classes\CLSID\" + clsid, false);
            using (RegistryKey k = root.CreateSubKey(@"Software\Classes\CLSID\" + clsid))
            {
                k.SetValue("", name);
                k.SetValue("DisplayName", name);
                if (appId != null) k.SetValue("AppID", appId);
                if (inCaller) k.SetValue("DisableProcessIsolation", 1, RegistryValueKind.DWord);
                using (RegistryKey s = k.CreateSubKey("InprocServer32"))
                {
                    s.SetValue("", "mscoree.dll");
                    s.SetValue("ThreadingModel", inCaller ? "Both" : "Apartment");
                    s.SetValue("Class", type.FullName);
                    s.SetValue("Assembly", AssemblyName);
                    s.SetValue("RuntimeVersion", "v4.0.30319");
                    s.SetValue("CodeBase", CodeBase(path));
                }
                using (RegistryKey c = k.CreateSubKey(@"Implemented Categories\{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}")) { }   // .NET category
            }
        }

        static void RemoveUserThumb()
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\" + ThumbClsid, false);
            Registry.CurrentUser.DeleteSubKeyTree(Assoc + ThumbIid, false);
        }

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { void GetImage(ThumbSize size, int flags, out IntPtr hbitmap); }
        [StructLayout(LayoutKind.Sequential)] struct ThumbSize { public int cx, cy; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid riid, out IShellItemImageFactory item);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        /// <summary>After a project is saved: has Windows make its thumbnail now (in this process, where the per-user
        /// handler works) so it is in the thumbnail cache when File Explorer shows the folder. In the background.</summary>
        public static void WarmThumbnail(string path)
        {
            if (!Supported || !IsOn || string.IsNullOrEmpty(path)) return;
            System.Threading.Thread t = new System.Threading.Thread(() =>
            {
                try
                {
                    Guid iid = typeof(IShellItemImageFactory).GUID;
                    IShellItemImageFactory f;
                    SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out f);
                    foreach (int size in new[] { 256, 96 })
                    {
                        IntPtr h;
                        f.GetImage(new ThumbSize { cx = size, cy = size }, 0x8, out h);    // SIIGBF_THUMBNAILONLY
                        DeleteObject(h);
                    }
                    Marshal.ReleaseComObject(f);
                }
                catch (Exception) { }        // no thumbnail is not worth a message
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>The preview stored in a project, read through Explorer's stream.</summary>
        public static Bitmap Read(IStream s)
        {
            using (Stream st = new ComReadStream(s)) return ProjectFile.ReadPreview(st);
        }
    }

    /// <summary>Read-only .NET stream over a COM IStream, read on demand (a project can be large; only its start is needed).</summary>
    internal sealed class ComReadStream : Stream
    {
        readonly IStream s;
        readonly IntPtr count = Marshal.AllocCoTaskMem(4);
        public ComReadStream(IStream s) { this.s = s; }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int n) { throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int n)
        {
            if (n <= 0) return 0;
            byte[] tmp = offset == 0 ? buffer : new byte[n];
            s.Read(tmp, n, count);
            int got = Marshal.ReadInt32(count);
            if (offset != 0 && got > 0) Buffer.BlockCopy(tmp, 0, buffer, offset, got);
            return got;
        }
        protected override void Dispose(bool disposing) { Marshal.FreeCoTaskMem(count); base.Dispose(disposing); }
    }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInitializeWithStream
    {
        void Initialize(IStream stream, uint mode);
    }

    [ComImport, Guid("e357fccd-a995-4576-b01f-234630154e96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IThumbnailProvider
    {
        void GetThumbnail(uint cx, out IntPtr hbitmap, out int alphaType);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PreviewRect { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref PreviewRect rect);
        void SetRect(ref PreviewRect rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr hwnd);
        [PreserveSig] int TranslateAccelerator(ref Message msg);
    }

    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IOleWindow
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
    }

    [ComImport, Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectWithSite
    {
        void SetSite([MarshalAs(UnmanagedType.IUnknown)] object site);
        void GetSite(ref Guid riid, out IntPtr site);
    }

    /// <summary>Explorer thumbnail of a .snapm project.</summary>
    [ComVisible(true), Guid("727caae8-5343-4ae5-a6e8-feb1876f0b2e"), ClassInterface(ClassInterfaceType.None)]
    public sealed class SnapmThumbnail : IInitializeWithStream, IThumbnailProvider
    {
        Bitmap pic;

        void IInitializeWithStream.Initialize(IStream stream, uint mode)
        {
            try { pic = ShellPreview.Read(stream); }
            finally { Marshal.ReleaseComObject(stream); }
        }

        void IThumbnailProvider.GetThumbnail(uint cx, out IntPtr hbitmap, out int alphaType)
        {
            hbitmap = IntPtr.Zero; alphaType = 0;                 // WTSAT_UNKNOWN
            if (pic == null) throw new COMException("No preview", unchecked((int)0x80004005));
            try
            {
                float f = Math.Min(1f, cx / (float)Math.Max(pic.Width, pic.Height));
                int w = Math.Max(1, (int)Math.Round(pic.Width * f)), h = Math.Max(1, (int)Math.Round(pic.Height * f));
                using (Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(pic, new Rectangle(0, 0, w, h));
                    }
                    hbitmap = b.GetHbitmap(Color.White);
                    alphaType = 1;                                // WTSAT_RGB: no transparency
                }
            }
            finally { pic.Dispose(); pic = null; }
        }
    }

    /// <summary>Explorer preview pane for a .snapm project: the stored picture, fitted to the pane.</summary>
    [ComVisible(true), Guid("baa97b7f-0e07-4ec4-9d3c-fb5edf99c512"), ClassInterface(ClassInterfaceType.None)]
    public sealed class SnapmPreviewHandler : IInitializeWithStream, IPreviewHandler, IOleWindow, IObjectWithSite
    {
        Bitmap pic;
        IntPtr parent;
        Rectangle bounds;
        PreviewView view;
        object site;

        [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] static extern IntPtr GetFocus();

        void IInitializeWithStream.Initialize(IStream stream, uint mode)
        {
            try { pic = ShellPreview.Read(stream); }
            catch (Exception) { pic = null; }
            finally { Marshal.ReleaseComObject(stream); }
        }

        static Rectangle R(PreviewRect r) { return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); }

        void IPreviewHandler.SetWindow(IntPtr hwnd, ref PreviewRect rect)
        {
            parent = hwnd; bounds = R(rect);
            if (view != null) { SetParent(view.Handle, parent); view.Bounds = bounds; }
        }

        void IPreviewHandler.SetRect(ref PreviewRect rect)
        {
            bounds = R(rect);
            if (view != null) view.Bounds = bounds;
        }

        void IPreviewHandler.DoPreview()
        {
            if (view == null)
            {
                view = new PreviewView();
                view.Picture = pic;
                view.CreateControl();
                SetParent(view.Handle, parent);
            }
            view.Bounds = bounds;
            view.Visible = true;
            view.Invalidate();
        }

        void IPreviewHandler.Unload()
        {
            if (view != null) { view.Dispose(); view = null; }
            if (pic != null) { pic.Dispose(); pic = null; }
        }

        void IPreviewHandler.SetFocus() { if (view != null) view.Focus(); }
        void IPreviewHandler.QueryFocus(out IntPtr hwnd) { hwnd = GetFocus(); }
        int IPreviewHandler.TranslateAccelerator(ref Message msg) { return 1; }      // S_FALSE: not handled

        void IOleWindow.GetWindow(out IntPtr hwnd) { hwnd = view != null ? view.Handle : IntPtr.Zero; }
        void IOleWindow.ContextSensitiveHelp(bool enterMode) { throw new NotImplementedException(); }

        void IObjectWithSite.SetSite(object s) { site = s; }
        void IObjectWithSite.GetSite(ref Guid riid, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (site == null) throw new COMException("No site", unchecked((int)0x80004005));
            IntPtr unk = Marshal.GetIUnknownForObject(site);
            try { Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, ref riid, out result)); }
            finally { Marshal.Release(unk); }
        }
    }

    internal sealed class PreviewView : Control
    {
        public Bitmap Picture;

        public PreviewView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = SystemColors.Window;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (Picture == null)
            {
                TextRenderer.DrawText(e.Graphics, "No preview in this project.\nSave it again in SnapMeasure to add one.",
                    Font, ClientRectangle, SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            int pad = 8;
            float f = Math.Min(1f, Math.Min((Width - 2f * pad) / Picture.Width, (Height - 2f * pad) / Picture.Height));
            if (f <= 0) return;
            int w = (int)(Picture.Width * f), h = (int)(Picture.Height * f);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(Picture, new Rectangle((Width - w) / 2, (Height - h) / 2, w, h));
        }
    }
}
