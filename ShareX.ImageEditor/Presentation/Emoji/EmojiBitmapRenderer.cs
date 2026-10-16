using System.Collections.Generic;
using System.Text;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace ShareX.ImageEditor.Presentation.Emoji;

/// <summary>
/// Rasterises emoji for the editor's sticker tool and emoji picker.
/// </summary>
/// <remarks>
/// Skia draws the glyphs using whichever colour emoji font the system has - Noto Color Emoji on
/// most distributions - so the result matches what the rest of the desktop shows. Nothing is
/// rendered when no colour emoji font is installed; the picker then simply has no previews.
/// </remarks>
public static class EmojiBitmapRenderer
{
    private const int PreviewPadding = 6;
    private const int StickerPadding = 14;
    private const int MaxStickerCacheEntries = 256;

    /// <summary>Colour emoji fonts in the order they are preferred, before falling back to a glyph lookup.</summary>
    private static readonly string[] emojiFontFamilies =
    [
        "Noto Color Emoji",
        "Apple Color Emoji",
        "Segoe UI Emoji",
        "Twemoji Mozilla",
        "JoyPixels",
        "EmojiOne Color",
        "OpenMoji Color"
    ];

    private static readonly object syncRoot = new();
    private static readonly Dictionary<string, WeakReference<Bitmap>> previewCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SKBitmap> stickerCache = new(StringComparer.Ordinal);
    private static readonly Lazy<SKTypeface?> emojiTypeface = new(ResolveEmojiTypeface);

    /// <summary>True when the system has a font that can draw colour emoji.</summary>
    public static bool IsSupported => emojiTypeface.Value != null;

    public static Bitmap? RenderPreviewBitmap(string unicodeSequence, int size)
    {
        if (string.IsNullOrWhiteSpace(unicodeSequence) || size <= 0)
        {
            return null;
        }

        string cacheKey = $"{unicodeSequence}:{size}";

        lock (syncRoot)
        {
            if (previewCache.TryGetValue(cacheKey, out WeakReference<Bitmap>? reference) &&
                reference.TryGetTarget(out Bitmap? cached))
            {
                return cached;
            }

            using SKBitmap? bitmap = RenderSquareBitmap(unicodeSequence, size, PreviewPadding);

            if (bitmap == null)
            {
                return null;
            }

            Bitmap preview = BitmapConversionHelpers.ToAvaloniBitmap(bitmap);

            // Controls borrow previews; evict references without disposing images still in use.
            if (previewCache.Count >= MaxStickerCacheEntries) previewCache.Clear();
            previewCache[cacheKey] = new WeakReference<Bitmap>(preview);
            return preview;
        }
    }

    public static SKBitmap? RenderStickerBitmap(string unicodeSequence, int size = 160)
    {
        if (string.IsNullOrWhiteSpace(unicodeSequence) || size <= 0)
        {
            return null;
        }

        return RenderStickerBitmapCore(unicodeSequence, size);
    }

    public static SKBitmap? RenderInteractiveStickerBitmap(string unicodeSequence, int size = 160)
    {
        if (string.IsNullOrWhiteSpace(unicodeSequence) || size <= 0)
        {
            return null;
        }

        return RenderStickerBitmapCore(unicodeSequence, GetInteractiveStickerSize(size));
    }

    /// <summary>
    /// Quantises the requested size while a sticker is being dragged, so resizing reuses cache
    /// entries instead of rasterising a new bitmap on every pointer move.
    /// </summary>
    public static int GetInteractiveStickerSize(int size)
    {
        size = Math.Max(1, size);

        if (size <= 64)
        {
            return size;
        }

        int step = size <= 128 ? 4 : size <= 256 ? 8 : 12;
        return Math.Max(64, (int)Math.Round(size / (double)step) * step);
    }

    private static SKBitmap? RenderStickerBitmapCore(string unicodeSequence, int size)
    {
        string cacheKey = $"{unicodeSequence}:{size}";

        lock (syncRoot)
        {
            if (stickerCache.TryGetValue(cacheKey, out SKBitmap? cached))
            {
                return cached;
            }

            SKBitmap? bitmap = RenderSquareBitmap(unicodeSequence, size, StickerPadding);

            if (bitmap == null)
            {
                return null;
            }

            if (stickerCache.Count >= MaxStickerCacheEntries)
            {
                foreach (SKBitmap entry in stickerCache.Values) entry.Dispose();
                stickerCache.Clear();
            }

            stickerCache[cacheKey] = bitmap;
            return bitmap;
        }
    }

    private static SKBitmap? RenderSquareBitmap(string unicodeSequence, int size, int padding)
    {
        SKTypeface? typeface = ResolveTypefaceFor(unicodeSequence);

        if (typeface == null)
        {
            return null;
        }

        int available = Math.Max(1, size - (padding * 2));
        SKBitmap bitmap = new(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));

        try
        {
            using SKCanvas canvas = new(bitmap);
            canvas.Clear(SKColors.Transparent);

            using SKFont font = new(typeface, available);
            using SKPaint paint = new() { IsAntialias = true, Color = SKColors.Black };

            // Measure at the nominal size, then scale so the glyph fills the padded box exactly.
            font.MeasureText(unicodeSequence, out SKRect bounds, paint);

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                bitmap.Dispose();
                return null;
            }

            float scale = Math.Min(available / bounds.Width, available / bounds.Height);
            font.Size = available * scale;
            font.MeasureText(unicodeSequence, out bounds, paint);

            float x = ((size - bounds.Width) / 2f) - bounds.Left;
            float y = ((size - bounds.Height) / 2f) - bounds.Top;

            canvas.DrawText(unicodeSequence, x, y, font, paint);
            canvas.Flush();

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Picks the font for a sequence. The preferred emoji font covers almost everything, but
    /// newer code points can need whatever else the system has, so Skia is asked to match the
    /// first character when the main font has no glyph for it.
    /// </summary>
    private static SKTypeface? ResolveTypefaceFor(string unicodeSequence)
    {
        SKTypeface? preferred = emojiTypeface.Value;

        if (preferred != null && ContainsGlyphs(preferred, unicodeSequence))
        {
            return preferred;
        }

        int codePoint = char.ConvertToUtf32(unicodeSequence, 0);
        SKTypeface? matched = SKFontManager.Default.MatchCharacter(codePoint);

        return matched ?? preferred;
    }

    private static SKTypeface? ResolveEmojiTypeface()
    {
        foreach (string family in emojiFontFamilies)
        {
            SKTypeface? typeface = SKFontManager.Default.MatchFamily(family);

            if (typeface != null && string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            {
                return typeface;
            }
        }

        // No named font matched; ask for whatever can draw a plain emoji.
        return SKFontManager.Default.MatchCharacter(0x1F600);
    }

    private static bool ContainsGlyphs(SKTypeface typeface, string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            // Variation selectors and joiners are not expected to have glyphs of their own.
            if (rune.Value is 0xFE0F or 0xFE0E or 0x200D) continue;

            if (typeface.GetGlyph(rune.Value) == 0) return false;
        }

        return true;
    }
}
