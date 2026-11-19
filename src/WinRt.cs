using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace SnapMeasure
{
    /// <summary>
    /// Small bridge to the parts of Windows 10/11 that SnapMeasure uses without installing anything:
    /// the PDF renderer (Windows.Data.Pdf) and text recognition (Windows.Media.Ocr).
    /// Every call runs on its own background thread and waits for the result.
    /// </summary>
    internal static class WinRt
    {
        public static R Wait<R>(IAsyncOperation<R> op)
        {
            using (ManualResetEvent ev = new ManualResetEvent(false))
            {
                op.Completed = (i, s) => { try { ev.Set(); } catch (ObjectDisposedException) { } };
                if (!ev.WaitOne(60000)) throw new TimeoutException("Windows did not answer in time.");
            }
            if (op.Status != AsyncStatus.Completed)
                throw new IOException(op.ErrorCode != null ? op.ErrorCode.Message : "The Windows component reported: " + op.Status);
            return op.GetResults();
        }

        public static void Wait(IAsyncAction op)
        {
            using (ManualResetEvent ev = new ManualResetEvent(false))
            {
                op.Completed = (i, s) => { try { ev.Set(); } catch (ObjectDisposedException) { } };
                if (!ev.WaitOne(60000)) throw new TimeoutException("Windows did not answer in time.");
            }
            if (op.Status != AsyncStatus.Completed)
                throw new IOException(op.ErrorCode != null ? op.ErrorCode.Message : "The Windows component reported: " + op.Status);
        }

        /// <summary>Runs f on a background (MTA) thread and returns its result; exceptions are passed on.</summary>
        public static T Run<T>(Func<T> f)
        {
            // thread-pool threads are MTA, which is what the Windows components want
            System.Threading.Tasks.Task<T> task = System.Threading.Tasks.Task.Factory.StartNew(f, CancellationToken.None,
                System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
            System.Windows.Forms.Cursor old = System.Windows.Forms.Cursor.Current;
            System.Windows.Forms.Cursor.Current = System.Windows.Forms.Cursors.WaitCursor;
            try { task.Wait(); }
            catch (AggregateException ae) { throw ae.InnerException ?? ae; }
            finally { System.Windows.Forms.Cursor.Current = old; }
            return task.Result;
        }

        public static byte[] ReadAll(IRandomAccessStream ms)
        {
            ulong n = ms.Size;
            if (n > 400UL * 1024 * 1024) throw new InvalidDataException("The rendered page is too large.");
            DataReader dr = new DataReader(ms.GetInputStreamAt(0));
            Wait(dr.LoadAsync((uint)n));
            byte[] bytes = new byte[n];
            dr.ReadBytes(bytes);
            return bytes;
        }

        public static InMemoryRandomAccessStream FromBytes(byte[] data)
        {
            InMemoryRandomAccessStream ms = new InMemoryRandomAccessStream();
            DataWriter dw = new DataWriter(ms);
            dw.WriteBytes(data);
            Wait(dw.StoreAsync());
            dw.DetachStream();
            ms.Seek(0);
            return ms;
        }
    }

    /// <summary>Opens pages of a PDF as pictures, using the PDF renderer built into Windows.</summary>
    internal static class PdfImport
    {
        public class Info
        {
            public int Pages;
            public double WidthDip, HeightDip;   // size of the first page in Windows units (1/96 inch)
        }

        public static Info Read(string path)
        {
            return WinRt.Run(() =>
            {
                Windows.Data.Pdf.PdfDocument d = Open(path);
                Info i = new Info();
                i.Pages = (int)d.PageCount;
                if (i.Pages > 0)
                {
                    Windows.Data.Pdf.PdfPage p = d.GetPage(0);
                    i.WidthDip = p.Size.Width; i.HeightDip = p.Size.Height;
                }
                return i;
            });
        }

        static Windows.Data.Pdf.PdfDocument Open(string path)
        {
            Windows.Storage.StorageFile f = WinRt.Wait(Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)));
            try { return WinRt.Wait(Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(f)); }
            catch (Exception ex) { throw new InvalidDataException("Windows could not open this PDF (it may be password-protected or damaged).\n" + ex.Message); }
        }

        /// <summary>Largest resolution that keeps the page inside SnapMeasure's picture size limits.</summary>
        public static double SafeDpi(double wDip, double hDip, double dpi)
        {
            double w = wDip / 96.0 * dpi, h = hDip / 96.0 * dpi, f = 1.0;
            if (w > 16000) f = Math.Min(f, 16000 / w);
            if (h > 16000) f = Math.Min(f, 16000 / h);
            if (w * h * f * f > 120e6) f = Math.Min(f, Math.Sqrt(120e6 / (w * h)));
            return Math.Max(20, dpi * f);
        }

        /// <summary>Renders page 'index' (0-based). Returns the picture and the page size in mm.</summary>
        public static Bitmap Render(string path, int index, ref double dpi, out SizeF pageMm)
        {
            double useDpi = dpi;
            SizeF mm = SizeF.Empty;
            byte[] png = WinRt.Run(() =>
            {
                Windows.Data.Pdf.PdfDocument d = Open(path);
                if (index < 0 || index >= d.PageCount) throw new ArgumentOutOfRangeException("index", "The PDF has " + d.PageCount + " page(s).");
                Windows.Data.Pdf.PdfPage p = d.GetPage((uint)index);
                double w = p.Size.Width, h = p.Size.Height;          // 1/96 inch (DIPs), not PDF points
                mm = new SizeF((float)(w / 96.0 * 25.4), (float)(h / 96.0 * 25.4));
                useDpi = SafeDpi(w, h, useDpi);
                Windows.Data.Pdf.PdfPageRenderOptions o = new Windows.Data.Pdf.PdfPageRenderOptions();
                o.DestinationWidth = (uint)Math.Max(1, Math.Round(w / 96.0 * useDpi));
                o.DestinationHeight = (uint)Math.Max(1, Math.Round(h / 96.0 * useDpi));
                InMemoryRandomAccessStream ms = new InMemoryRandomAccessStream();
                WinRt.Wait(p.RenderToStreamAsync(ms, o));
                return WinRt.ReadAll(ms);
            });
            dpi = useDpi;
            pageMm = mm;
            using (MemoryStream s = new MemoryStream(png))
            using (Image im = Image.FromStream(s, false, true))
            {
                ProjectFile.CheckImageSize(im.Width, im.Height);
                // flatten onto white: PDF pages may come out transparent
                Bitmap b = new Bitmap(im.Width, im.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.White);
                    g.DrawImage(im, new Rectangle(0, 0, im.Width, im.Height), new Rectangle(0, 0, im.Width, im.Height), GraphicsUnit.Pixel);
                }
                return b;
            }
        }
    }

    /// <summary>Text recognition (OCR) built into Windows 10/11.</summary>
    internal static class TextRecognition
    {
        public class Word
        {
            public string Text;
            public Rectangle Box;       // in the coordinates of the picture that was read
        }

        public class Result
        {
            public readonly List<List<Word>> Lines = new List<List<Word>>();
            public string Text
            {
                get
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (List<Word> l in Lines)
                    {
                        for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(' '); sb.Append(l[i].Text); }
                        sb.AppendLine();
                    }
                    return sb.ToString().TrimEnd();
                }
            }
            public List<Rectangle> Boxes
            {
                get { List<Rectangle> r = new List<Rectangle>(); foreach (List<Word> l in Lines) foreach (Word w in l) r.Add(w.Box); return r; }
            }
        }

        static int available = -1;

        /// <summary>True if Windows has a text-recognition language installed.</summary>
        public static bool Available
        {
            get
            {
                if (available < 0)
                {
                    try { available = WinRt.Run(() => Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() != null) ? 1 : 0; }
                    catch (Exception) { available = 0; }
                }
                return available == 1;
            }
        }

        /// <summary>Reads the text in 'area' of the picture (null = all). Boxes are returned in picture coordinates.</summary>
        public static Result Read(Bitmap img, Rectangle? area)
        {
            Rectangle r = area.HasValue ? area.Value : new Rectangle(0, 0, img.Width, img.Height);
            r.Intersect(new Rectangle(0, 0, img.Width, img.Height));
            Result res = new Result();
            if (r.Width < 2 || r.Height < 2) return res;
            // small text reads better a little larger; very large pictures must be scaled down
            float scale = 1f;
            if (Math.Min(r.Width, r.Height) < 60) scale = 3f;
            else if (Math.Min(r.Width, r.Height) < 200) scale = 2f;
            float maxDim = 9500f;
            if (r.Width * scale > maxDim) scale = maxDim / r.Width;
            if (r.Height * scale > maxDim) scale = Math.Min(scale, maxDim / r.Height);
            int w = Math.Max(1, (int)Math.Round(r.Width * scale)), h = Math.Max(1, (int)Math.Round(r.Height * scale));
            byte[] png;
            using (Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(img, new Rectangle(0, 0, w, h), r, GraphicsUnit.Pixel);
                }
                using (MemoryStream ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); png = ms.ToArray(); }
            }
            Windows.Media.Ocr.OcrResult ocr = WinRt.Run(() =>
            {
                Windows.Media.Ocr.OcrEngine eng = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
                if (eng == null) throw new InvalidOperationException("Windows has no text-recognition language installed (Settings → Time & language → Language).");
                InMemoryRandomAccessStream s = WinRt.FromBytes(png);
                Windows.Graphics.Imaging.BitmapDecoder dec = WinRt.Wait(Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(s));
                Windows.Graphics.Imaging.SoftwareBitmap sb = WinRt.Wait(dec.GetSoftwareBitmapAsync(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied));
                return WinRt.Wait(eng.RecognizeAsync(sb));
            });
            foreach (Windows.Media.Ocr.OcrLine line in ocr.Lines)
            {
                List<Word> words = new List<Word>();
                foreach (Windows.Media.Ocr.OcrWord ow in line.Words)
                {
                    Rect br = ow.BoundingRect;
                    Word wd = new Word();
                    wd.Text = ow.Text;
                    wd.Box = Rectangle.Round(new RectangleF((float)(r.X + br.X / scale), (float)(r.Y + br.Y / scale), (float)(br.Width / scale), (float)(br.Height / scale)));
                    words.Add(wd);
                }
                if (words.Count > 0) res.Lines.Add(words);
            }
            return res;
        }
    }
}
