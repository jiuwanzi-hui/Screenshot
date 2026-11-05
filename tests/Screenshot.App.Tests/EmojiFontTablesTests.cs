using System.Buffers.Binary;
using System.IO;
using Screenshot.App.Editor;

namespace Screenshot.App.Tests;

public sealed class EmojiFontTablesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsExactColorTablesWithoutCopyingUnrelatedFontData(bool collection)
    {
        var bytes = new byte[12 * 1024 * 1024];
        var start = collection ? 32 : 0;
        if (collection)
        {
            "ttcf"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), (uint)start);
        }
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(start + 4), 3);
        WriteRecord(bytes, start + 12, "COLR"u8, 256, 512);
        WriteRecord(bytes, start + 28, "glyf"u8, 1024, (uint)bytes.Length - 1024);
        WriteRecord(bytes, start + 44, "CPAL"u8, 768, 4);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(256), 1); // v1 with v0 records
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(258), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(260), 14);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(264), 20);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(268), 1);
        new byte[] { 21, 42, 63, 84 }.CopyTo(bytes, 276);
        new byte[] { 90, 80, 70, 60 }.CopyTo(bytes, 768);
        using var font = new MemoryStream(bytes);
        _ = EmojiFontTables.Read(font); // JIT before the allocation measurement.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var actual = EmojiFontTables.Read(font)!.Value;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(bytes[256..280], actual.Layers);
        Assert.Equal(new byte[] { 90, 80, 70, 60 }, actual.Palette);
        Assert.True(allocated < 1024, $"Reading two small tables allocated {allocated} bytes.");
    }

    [Fact]
    public void RejectsTruncatedTablesAndPreservesMissingColorFallback()
    {
        var bytes = new byte[64];
        using var font = new MemoryStream(bytes);
        Assert.Null(EmojiFontTables.Read(font));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 2);
        WriteRecord(bytes, 12, "COLR"u8, 60, 100);
        WriteRecord(bytes, 28, "CPAL"u8, 44, 4);
        Assert.Throws<InvalidDataException>(() => EmojiFontTables.Read(font));
    }

    private static void WriteRecord(byte[] bytes, int offset, ReadOnlySpan<byte> tag, uint start, uint size)
    {
        tag.CopyTo(bytes.AsSpan(offset));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 8), start);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 12), size);
    }
}
