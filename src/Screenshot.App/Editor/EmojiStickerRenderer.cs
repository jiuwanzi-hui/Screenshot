using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace Screenshot.App.Editor;

/// <summary>
/// Renders emoji sticker bitmaps from the system color emoji font. The glyphs
/// are the same full-color emoji the user knows from chat apps — far livelier
/// than anything hand-drawn — and rendering them locally means no bundled
/// image assets and no licensing concerns.
/// </summary>
/// <remarks>
/// WPF's text stack rasterizes only the monochrome outlines and ignores the
/// font's color layers, so this renderer reads the COLR/CPAL tables of
/// Segoe UI Emoji itself: each emoji is a stack of ordinary outline glyphs,
/// every layer filled with one palette color. Drawing those layers in order
/// through <see cref="GlyphTypeface.GetGlyphOutline"/> reproduces the exact
/// full-color emoji the rest of Windows shows.
/// </remarks>
public static class EmojiStickerRenderer
{
    private const int DefaultImageSize = 96;
    private const char VariationSelector16 = '️';
    private const ushort ForegroundPaletteIndex = 0xFFFF;
    private static readonly ConcurrentDictionary<(string, int), ImageSource> Cache =
        new();
    private static readonly Lazy<ColorEmojiFont?> Font = new(
        ColorEmojiFont.TryLoad,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static ImageSource GetImage(string emoji)
    {
        return GetImage(emoji, DefaultImageSize);
    }

    /// <summary>
    /// The layers are vector outlines, so every display size gets its own
    /// exact rasterization. Reusing one large bitmap for the small palette
    /// tiles pushed them through a heavy downscale that visibly blurred them.
    /// </summary>
    public static ImageSource GetImage(string emoji, int pixelSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emoji);
        var size = Math.Clamp(pixelSize, 12, 512);
        return Cache.GetOrAdd((emoji, size), key => Render(key.Item1, key.Item2));
    }

    private static RenderTargetBitmap Render(string emoji, int pixelSize)
    {
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            DrawEmoji(context, emoji, pixelSize);
        }

        var bitmap = new RenderTargetBitmap(
            pixelSize,
            pixelSize,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawEmoji(
        DrawingContext context,
        string emoji,
        int pixelSize)
    {
        var font = Font.Value;

        if (font is null)
        {
            return;
        }

        var codepoint = GetPrimaryCodepoint(emoji);

        if (!font.Typeface.CharacterToGlyphMap.TryGetValue(
                codepoint,
                out var baseGlyph))
        {
            return;
        }

        var glyphSize = pixelSize * 0.875;
        var layers = font.GetLayers(baseGlyph);
        var group = new DrawingGroup();

        using (var groupContext = group.Open())
        {
            foreach (var (layerGlyph, color) in layers)
            {
                var outline = font.Typeface.GetGlyphOutline(
                    layerGlyph,
                    glyphSize,
                    glyphSize);

                if (outline.IsEmpty())
                {
                    continue;
                }

                groupContext.DrawGeometry(
                    new SolidColorBrush(color),
                    null,
                    outline);
            }
        }

        var bounds = group.Bounds;

        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        // Glyph outlines are baseline-relative; center the finished stack on
        // the sticker canvas instead of reasoning about font metrics.
        group.Transform = new TranslateTransform(
            ((pixelSize - bounds.Width) / 2) - bounds.X,
            ((pixelSize - bounds.Height) / 2) - bounds.Y);
        group.Freeze();
        context.DrawDrawing(group);
    }

    /// <summary>
    /// The catalog uses single-rune emoji, optionally followed by VS16 to
    /// force emoji presentation. The color layers live on the base glyph.
    /// </summary>
    private static int GetPrimaryCodepoint(string emoji)
    {
        var trimmed = emoji.TrimEnd(VariationSelector16);
        return trimmed.Length > 0
            ? char.ConvertToUtf32(trimmed, 0)
            : char.ConvertToUtf32(emoji, 0);
    }

    /// <summary>
    /// The system color emoji typeface together with its parsed COLR layer
    /// list and CPAL palette.
    /// </summary>
    private sealed class ColorEmojiFont
    {
        private readonly Dictionary<ushort, (ushort Glyph, ushort Palette)[]> _layers;
        private readonly Color[] _palette;

        private ColorEmojiFont(
            GlyphTypeface typeface,
            Dictionary<ushort, (ushort Glyph, ushort Palette)[]> layers,
            Color[] palette)
        {
            Typeface = typeface;
            _layers = layers;
            _palette = palette;
        }

        public GlyphTypeface Typeface { get; }

        public IEnumerable<(ushort Glyph, Color Color)> GetLayers(ushort baseGlyph)
        {
            if (!_layers.TryGetValue(baseGlyph, out var layers) ||
                layers.Length == 0)
            {
                // No color layers: fall back to the plain outline in a neutral
                // ink so the sticker is still visible.
                return [(baseGlyph, Color.FromRgb(0x37, 0x2B, 0x25))];
            }

            return layers.Select(layer => (
                layer.Glyph,
                layer.Palette == ForegroundPaletteIndex ||
                layer.Palette >= _palette.Length
                    ? Color.FromRgb(0x37, 0x2B, 0x25)
                    : _palette[layer.Palette]));
        }

        public static ColorEmojiFont? TryLoad()
        {
            try
            {
                var typeface = new Typeface(
                    new System.Windows.Media.FontFamily("Segoe UI Emoji"),
                    FontStyles.Normal,
                    FontWeights.Normal,
                    FontStretches.Normal);

                if (!typeface.TryGetGlyphTypeface(out var glyphTypeface))
                {
                    return null;
                }

                var fontBytes = File.ReadAllBytes(glyphTypeface.FontUri.LocalPath);
                var tables = ReadTableDirectory(fontBytes);

                if (!tables.TryGetValue("COLR", out var colrRange) ||
                    !tables.TryGetValue("CPAL", out var cpalRange))
                {
                    return null;
                }

                var layers = ParseColrLayers(fontBytes, colrRange.Offset);
                var palette = ParseCpalPalette(fontBytes, cpalRange.Offset);
                return new ColorEmojiFont(glyphTypeface, layers, palette);
            }
            catch (Exception)
            {
                // Any parsing surprise falls back to monochrome stickers
                // rather than breaking the editor.
                return null;
            }
        }

        private static Dictionary<string, (int Offset, int Length)> ReadTableDirectory(
            byte[] font)
        {
            var start = 0;

            // A font collection wraps the table directory of each face.
            if (font.Length >= 16 &&
                font[0] == 't' && font[1] == 't' && font[2] == 'c' && font[3] == 'f')
            {
                start = (int)ReadUInt32(font, 12);
            }

            var tableCount = ReadUInt16(font, start + 4);
            var tables = new Dictionary<string, (int, int)>(
                tableCount,
                StringComparer.Ordinal);

            for (var index = 0; index < tableCount; index++)
            {
                var recordOffset = start + 12 + (index * 16);
                var tag = System.Text.Encoding.ASCII.GetString(
                    font,
                    recordOffset,
                    4);
                tables[tag] = (
                    (int)ReadUInt32(font, recordOffset + 8),
                    (int)ReadUInt32(font, recordOffset + 12));
            }

            return tables;
        }

        private static Dictionary<ushort, (ushort, ushort)[]> ParseColrLayers(
            byte[] font,
            int colrOffset)
        {
            var baseGlyphCount = ReadUInt16(font, colrOffset + 2);
            var baseGlyphsOffset = colrOffset + (int)ReadUInt32(font, colrOffset + 4);
            var layerRecordsOffset = colrOffset + (int)ReadUInt32(font, colrOffset + 8);
            var layers = new Dictionary<ushort, (ushort, ushort)[]>(baseGlyphCount);

            for (var index = 0; index < baseGlyphCount; index++)
            {
                var recordOffset = baseGlyphsOffset + (index * 6);
                var baseGlyph = ReadUInt16(font, recordOffset);
                var firstLayer = ReadUInt16(font, recordOffset + 2);
                var layerCount = ReadUInt16(font, recordOffset + 4);
                var glyphLayers = new (ushort, ushort)[layerCount];

                for (var layer = 0; layer < layerCount; layer++)
                {
                    var layerOffset = layerRecordsOffset +
                                      ((firstLayer + layer) * 4);
                    glyphLayers[layer] = (
                        ReadUInt16(font, layerOffset),
                        ReadUInt16(font, layerOffset + 2));
                }

                layers[baseGlyph] = glyphLayers;
            }

            return layers;
        }

        private static Color[] ParseCpalPalette(byte[] font, int cpalOffset)
        {
            var colorRecordCount = ReadUInt16(font, cpalOffset + 6);
            var colorRecordsOffset = cpalOffset +
                                     (int)ReadUInt32(font, cpalOffset + 8);
            // The first palette's records start at the first color index;
            // Segoe UI Emoji keeps its default palette there.
            var firstColorIndex = ReadUInt16(font, cpalOffset + 12);
            var palette = new Color[colorRecordCount];

            for (var index = 0; index < colorRecordCount; index++)
            {
                var recordOffset = colorRecordsOffset +
                                   ((firstColorIndex + index) * 4);

                if (recordOffset + 4 > font.Length)
                {
                    break;
                }

                palette[index] = Color.FromArgb(
                    font[recordOffset + 3],
                    font[recordOffset + 2],
                    font[recordOffset + 1],
                    font[recordOffset]);
            }

            return palette;
        }

        private static ushort ReadUInt16(byte[] data, int offset)
        {
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) |
                   ((uint)data[offset + 1] << 16) |
                   ((uint)data[offset + 2] << 8) |
                   data[offset + 3];
        }
    }
}
