using System.Buffers.Binary;
using System.IO;

namespace Screenshot.App.Editor;

// Read only the two color tables. Font outlines are already owned by WPF;
// copying the entire font here creates an unnecessary large-object allocation.
internal static class EmojiFontTables
{
    internal static (byte[] Layers, byte[] Palette)? Read(Stream font)
    {
        Span<byte> header = stackalloc byte[12];
        font.Position = 0;
        font.ReadExactly(header);
        if (header[..4].SequenceEqual("ttcf"u8))
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(header[8..]) == 0) return null;
            Span<byte> offset = stackalloc byte[4];
            font.ReadExactly(offset);
            font.Position = BinaryPrimitives.ReadUInt32BigEndian(offset);
            font.ReadExactly(header);
        }
        var count = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        (uint Offset, uint Length)? layers = null, palette = null;
        Span<byte> record = stackalloc byte[16];
        for (var index = 0; index < count; index++)
        {
            font.ReadExactly(record);
            var range = (BinaryPrimitives.ReadUInt32BigEndian(record[8..]),
                BinaryPrimitives.ReadUInt32BigEndian(record[12..]));
            if (record[..4].SequenceEqual("COLR"u8)) layers = range;
            if (record[..4].SequenceEqual("CPAL"u8)) palette = range;
        }
        if (layers is not { } colr || palette is not { } cpal) return null;
        ValidateRange(font, colr);
        ValidateRange(font, cpal);
        // The renderer consumes the version-0 layer records, including those
        // embedded in a version-1 COLR table. Do not load unrelated v1 paints.
        if (colr.Length < 14) throw new InvalidDataException("The font color header is truncated.");
        font.Position = colr.Offset;
        Span<byte> colorHeader = stackalloc byte[14];
        font.ReadExactly(colorHeader);
        var baseEnd = (long)BinaryPrimitives.ReadUInt32BigEndian(colorHeader[4..]) +
            6L * BinaryPrimitives.ReadUInt16BigEndian(colorHeader[2..]);
        var layerEnd = (long)BinaryPrimitives.ReadUInt32BigEndian(colorHeader[8..]) +
            4L * BinaryPrimitives.ReadUInt16BigEndian(colorHeader[12..]);
        var usedLength = Math.Max(14, Math.Max(baseEnd, layerEnd));
        if (usedLength > colr.Length) throw new InvalidDataException("The font color records are truncated.");
        return (ReadTable(font, (colr.Offset, checked((uint)usedLength))), ReadTable(font, cpal));
    }

    private static byte[] ReadTable(Stream font, (uint Offset, uint Length) range)
    {
        ValidateRange(font, range);
        var bytes = new byte[checked((int)range.Length)];
        font.Position = range.Offset;
        font.ReadExactly(bytes);
        return bytes;
    }

    private static void ValidateRange(Stream font, (uint Offset, uint Length) range)
    {
        if (range.Length > int.MaxValue || (long)range.Offset + range.Length > font.Length)
            throw new InvalidDataException("The font color table is outside the file.");
    }
}
