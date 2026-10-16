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

using Avalonia.Media.Imaging;
using ShareX.ImageEditor.Core.Annotations;
using ShareX.Linux.Desktop;
using SkiaSharp;
using System.Buffers.Binary;

namespace ShareX.ImageEditor.Presentation.Rendering
{
    /// <summary>
    /// Produces bitmaps of mouse cursors for the editor's cursor annotation.
    /// </summary>
    /// <remarks>
    /// A Wayland client cannot read the pointer the compositor is drawing, so the cursor is
    /// loaded from the icon theme on disk instead - the same files the compositor itself draws
    /// from, which means the annotation matches the user's actual pointer. The handful of
    /// cursors ShareX ships as assets cover shapes that have no freedesktop name.
    /// </remarks>
    public static class CursorBitmapRenderer
    {
        /// <summary>Cursors ShareX carries itself, for shapes no icon theme defines.</summary>
        private static readonly IReadOnlyDictionary<CursorType, string> BundledCursors = new Dictionary<CursorType, string>
        {
            [CursorType.HSplit] = "hsplit",
            [CursorType.VSplit] = "vsplit",
            [CursorType.NoMove2D] = "nomove2d",
            [CursorType.NoMoveHoriz] = "nomoveh",
            [CursorType.NoMoveVert] = "nomovev",
            [CursorType.PanEast] = "east",
            [CursorType.PanNE] = "ne",
            [CursorType.PanNorth] = "north",
            [CursorType.PanNW] = "nw",
            [CursorType.PanSE] = "se",
            [CursorType.PanSouth] = "south",
            [CursorType.PanSW] = "sw",
            [CursorType.PanWest] = "west"
        };

        /// <summary>
        /// Each cursor's name in the icon theme. The first name that the theme provides wins,
        /// because themes vary in whether they use the modern CSS names or the old X11 ones.
        /// </summary>
        private static readonly IReadOnlyDictionary<CursorType, string[]> ThemeCursorNames =
            new Dictionary<CursorType, string[]>
            {
                [CursorType.AppStarting] = ["progress", "left_ptr_watch", "half-busy"],
                [CursorType.Arrow] = ["default", "left_ptr", "arrow"],
                [CursorType.Cross] = ["crosshair", "cross"],
                [CursorType.Default] = ["default", "left_ptr", "arrow"],
                [CursorType.Hand] = ["pointer", "hand2", "hand1", "pointing_hand"],
                [CursorType.Help] = ["help", "question_arrow", "whats_this"],
                [CursorType.IBeam] = ["text", "xterm", "ibeam"],
                [CursorType.No] = ["not-allowed", "forbidden", "crossed_circle"],
                [CursorType.SizeAll] = ["all-scroll", "move", "fleur"],
                [CursorType.SizeNESW] = ["nesw-resize", "size_bdiag", "fd_double_arrow"],
                [CursorType.SizeNS] = ["ns-resize", "size_ver", "sb_v_double_arrow", "v_double_arrow"],
                [CursorType.SizeNWSE] = ["nwse-resize", "size_fdiag", "bd_double_arrow"],
                [CursorType.SizeWE] = ["ew-resize", "size_hor", "sb_h_double_arrow", "h_double_arrow"],
                [CursorType.UpArrow] = ["up-arrow", "center_ptr", "sb_up_arrow"],
                [CursorType.WaitCursor] = ["wait", "watch"]
            };

        /// <summary>Cursor themes are commonly authored at 24-32 px; ask for the larger end.</summary>
        private const int PreferredCursorSize = 32;

        private static readonly object SyncRoot = new();
        private static readonly Dictionary<CursorType, SKBitmap?> AnnotationBitmapCache = new();
        private static readonly Dictionary<(CursorType CursorType, int PreviewSize), Bitmap?> PreviewBitmapCache = new();

        public static SKBitmap? CreateAnnotationBitmap(CursorType cursorType)
        {
            lock (SyncRoot)
            {
                if (!AnnotationBitmapCache.TryGetValue(cursorType, out SKBitmap? cachedBitmap))
                {
                    cachedBitmap = RenderCursorBitmap(cursorType);
                    AnnotationBitmapCache[cursorType] = cachedBitmap;
                }

                return cachedBitmap?.Copy();
            }
        }

        public static Bitmap? GetPreviewBitmap(CursorType cursorType, int previewSize = 28)
        {
            lock (SyncRoot)
            {
                var cacheKey = (cursorType, previewSize);

                if (!PreviewBitmapCache.TryGetValue(cacheKey, out Bitmap? previewBitmap))
                {
                    previewBitmap = CreatePreviewBitmap(cursorType, previewSize);
                    PreviewBitmapCache[cacheKey] = previewBitmap;
                }

                return previewBitmap;
            }
        }

        private static Bitmap? CreatePreviewBitmap(CursorType cursorType, int previewSize)
        {
            using SKBitmap? annotationBitmap = CreateAnnotationBitmap(cursorType);
            if (annotationBitmap == null)
            {
                return null;
            }

            int canvasSize = Math.Max(16, previewSize);

            using var previewBitmap = new SKBitmap(new SKImageInfo(canvasSize, canvasSize, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(previewBitmap);
            using var paint = new SKPaint { IsAntialias = true };

            canvas.Clear(SKColors.Transparent);

            float maxWidth = Math.Max(1, canvasSize - 4f);
            float maxHeight = Math.Max(1, canvasSize - 4f);
            float scale = Math.Min(maxWidth / annotationBitmap.Width, maxHeight / annotationBitmap.Height);

            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0)
            {
                scale = 1f;
            }

            float drawWidth = annotationBitmap.Width * scale;
            float drawHeight = annotationBitmap.Height * scale;
            float left = (canvasSize - drawWidth) / 2f;
            float top = (canvasSize - drawHeight) / 2f;

            canvas.DrawBitmap(annotationBitmap, new SKRect(left, top, left + drawWidth, top + drawHeight), paint);
            canvas.Flush();

            return BitmapConversionHelpers.ToAvaloniBitmap(previewBitmap);
        }

        private static SKBitmap? RenderCursorBitmap(CursorType cursorType)
        {
            if (ThemeCursorNames.TryGetValue(cursorType, out string[]? names))
            {
                foreach (string name in names)
                {
                    CursorImage? cursor = XCursorTheme.Load(name, PreferredCursorSize);

                    if (cursor != null) return cursor.Bitmap;
                }
            }

            return LoadBundledCursor(cursorType);
        }

        private static SKBitmap? LoadBundledCursor(CursorType cursorType)
        {
            if (!BundledCursors.TryGetValue(cursorType, out string? asset)) return null;

            using Stream? source = typeof(CursorBitmapRenderer).Assembly.GetManifestResourceStream(
                $"ShareX.ImageEditor.Assets.Cursors.{asset}.cur");
            if (source == null) return null;

            using MemoryStream stream = new();
            source.CopyTo(stream);
            byte[] data = stream.ToArray();

            // CUR and ICO share their DIB payload. Replace the cursor directory's
            // hotspot fields with icon planes/depth, then let Skia decode its mask.
            int offset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18));
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2), 1);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(10), 1);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(12), BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 14)));
            return SKBitmap.Decode(data);
        }
    }
}
