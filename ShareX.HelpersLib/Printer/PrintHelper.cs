#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.Collections.Generic;
using System.Drawing;
using ShareX.Linux.Desktop;
using SkiaSharp;
using Image = SkiaSharp.SKBitmap;

namespace ShareX.HelpersLib
{
    /// <summary>
    /// Lays an image or a block of text out onto pages and sends them to a printer.
    /// </summary>
    /// <remarks>
    /// Pages are measured in hundredths of an inch, the unit the layout code has always used, and
    /// rasterised at 300 dpi before being wrapped in a PDF for the desktop print dialog.
    /// </remarks>
    public class PrintHelper : IDisposable
    {
        private const int RasterScale = 3; // 100ths of an inch -> 300 dpi.
        private const int MaximumPages = 500;

        public PrintType PrintType { get; private set; }
        public Image Image { get; private set; }
        public string Text { get; private set; }
        public PrintSettings Settings { get; set; }

        public bool Printable
        {
            get
            {
                return Settings != null && ((PrintType == PrintType.Image && Image != null) ||
                    (PrintType == PrintType.Text && !string.IsNullOrEmpty(Text) && Settings.TextFont != null));
            }
        }

        private PrintTextHelper printTextHelper;

        public PrintHelper(Image image)
        {
            PrintType = PrintType.Image;
            Image = image;
        }

        public PrintHelper(string text)
        {
            PrintType = PrintType.Text;
            Text = text;
            printTextHelper = new PrintTextHelper { Text = Text };
        }

        public void Dispose()
        {
        }

        public void ShowPreview()
        {
            if (Printable) PrintPreviewWindow.ShowPreview(RenderPreviewPage, Print);
        }

        internal (SKBitmap Bitmap, bool HasMore) RenderPreviewPage(int pageIndex)
        {
            Size size = Settings.GetPaperSize();

            if (PrintType == PrintType.Image) return (RenderImagePage(Image, size, Settings), false);

            PrintTextHelper renderer = new() { Text = Text, Font = Settings.TextFont };
            renderer.BeginPrint();

            Rectangle margin = Settings.GetTextMarginBounds(size);
            SKBitmap page = null;
            bool hasMore = false;

            for (int index = 0; index <= pageIndex; index++)
            {
                page?.Dispose();
                page = renderer.RenderPage(size, margin, out hasMore);
                if (!hasMore) break;
            }

            return (page, hasMore);
        }

        public bool Print()
        {
            if (!Printable) return false;

            List<PrintingService.PrintPage> pages = new();

            try
            {
                Size size = Settings.GetPaperSize();
                float widthPoints = size.Width * 72f / 100f;
                float heightPoints = size.Height * 72f / 100f;

                foreach (SKBitmap page in RenderPages(size))
                {
                    pages.Add(new PrintingService.PrintPage(page, widthPoints, heightPoints));
                }

                if (pages.Count == 0) return false;

                return PrintingService.Print(pages, GetDocumentTitle(), Settings.DefaultPrinterOverride,
                    Settings.ShowPrintDialog);
            }
            finally
            {
                foreach (PrintingService.PrintPage page in pages) page.Image.Dispose();
            }
        }

        private IEnumerable<SKBitmap> RenderPages(Size size)
        {
            if (PrintType == PrintType.Image)
            {
                yield return RenderImagePage(Image, size, Settings);
                yield break;
            }

            printTextHelper.Font = Settings.TextFont;
            printTextHelper.BeginPrint();

            Rectangle margin = Settings.GetTextMarginBounds(size);
            bool hasMore = true;

            for (int index = 0; hasMore && index < MaximumPages; index++)
            {
                SKBitmap page = printTextHelper.RenderPage(size, margin, out hasMore);

                if (page == null) yield break;

                yield return page;
            }
        }

        private string GetDocumentTitle() =>
            PrintType == PrintType.Image ? "ShareX image" : "ShareX text";

        internal static SKBitmap RenderImagePage(SKBitmap source, Size pageSize, PrintSettings settings)
        {
            SKBitmap rotated = null;
            SKBitmap image = source;
            SKBitmap page = SkiaImageHelpers.CreateBitmap(pageSize.Width * RasterScale, pageSize.Height * RasterScale);
            Rectangle rectangle = new(0, 0, pageSize.Width, pageSize.Height);
            rectangle.Inflate(-settings.Margin, -settings.Margin);
            try
            {
                using SKCanvas canvas = new(page);
                canvas.Clear(SKColors.White);
                if (rectangle.Width <= 0 || rectangle.Height <= 0) return page;
                if (settings.AutoRotateImage && ((rectangle.Width > rectangle.Height && source.Width < source.Height) ||
                    (rectangle.Width < rectangle.Height && source.Width > source.Height)))
                {
                    rotated = source.Copy();
                    SkiaImageHelpers.RotateFlipInPlace(rotated, 1);
                    image = rotated;
                }
                canvas.Scale(RasterScale, RasterScale);
                canvas.ClipRect(rectangle.ToSKRect());
                if (settings.AutoScaleImage)
                {
                    float scale = Math.Min(rectangle.Width / (float)image.Width, rectangle.Height / (float)image.Height);
                    if (!settings.AllowEnlargeImage) scale = Math.Min(1, scale);
                    float width = image.Width * scale, height = image.Height * scale;
                    float x = rectangle.X, y = rectangle.Y;
                    if (settings.CenterImage)
                    {
                        x += (rectangle.Width - width) / 2;
                        y += (rectangle.Height - height) / 2;
                    }
                    canvas.DrawImage(image, x, y, width, height);
                }
                else
                {
                    canvas.DrawBitmap(image, rectangle.X, rectangle.Y);
                }
                return page;
            }
            catch { page.Dispose(); throw; }
            finally { rotated?.Dispose(); }
        }
    }
}
