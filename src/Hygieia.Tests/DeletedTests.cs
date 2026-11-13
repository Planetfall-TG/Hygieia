using System.Buffers.Binary;
using System.Text;

namespace Hygieia.Tests;

public class DeletedTests
{
    static long FileTime(int year, int month, int day)
    {
        var moment = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        return moment.ToFileTimeUtc();
    }

    static byte[] Finish(byte[] buf, int index, bool inUse, bool isDir, int sequence, int pos)
    {
        "FILE"u8.CopyTo(buf);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x10), (ushort)sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x14), 0x38);
        var flags = (ushort)((inUse ? 1 : 0) | (isDir ? 2 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x16), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos), 0xFFFFFFFF);
        pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x18), (uint)pos);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x1C), 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x2C), (uint)index);
        const ushort usn = 0x1234;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x32), BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(510)));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x34), BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(1022)));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x30), usn);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(510), usn);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(1022), usn);
        return buf;
    }

    static int Filename(byte[] buf, int pos, string name, int parent, int parentSeq, long size)
    {
        var encoded = Encoding.Unicode.GetBytes(name);
        var valueLen = 66 + encoded.Length;
        var attrLen = (24 + valueLen + 7) & ~7;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos), 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos + 4), (uint)attrLen);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos + 16), valueLen);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos + 20), 24);
        var start = pos + 24;
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(start), ((ulong)parentSeq << 48) | (uint)parent);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(start + 48), size);
        buf[start + 64] = (byte)name.Length;
        buf[start + 65] = 1;
        encoded.CopyTo(buf, start + 66);
        return pos + attrLen;
    }

    static int Runs(byte[] buf, int pos, byte[] runs, long dataSize)
    {
        const int runOff = 0x40;
        var length = (runOff + runs.Length + 7) & ~7;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos + 4), (uint)length);
        buf[pos + 8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos + 0x20), runOff);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(pos + 0x28), dataSize);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(pos + 0x30), dataSize);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(pos + 0x38), dataSize);
        runs.CopyTo(buf, pos + runOff);
        return pos + length;
    }

    static byte[] Record(int index, bool inUse, bool isDir, int sequence, string? name, int parent, int parentSeq, long size, byte[]? runs = null)
    {
        var buf = new byte[1024];
        var pos = 0x38;
        if (name != null)
            pos = Filename(buf, pos, name, parent, parentSeq, size);
        if (runs != null)
            pos = Runs(buf, pos, runs, size);
        return Finish(buf, index, inUse, isDir, sequence, pos);
    }

    [Fact]
    public void Runlist_relative_offsets()
    {
        var runs = DeletedFiles.ParseRunList(new byte[] { 0x21, 0x10, 0x00, 0x01, 0x21, 0x08, 0x00, 0xFF, 0x00 });
        Assert.Equal(new List<(long? Offset, long Length)> { (0x100, 0x10), (0, 0x08) }, runs);
    }

    [Fact]
    public void Boot_and_deleted_names()
    {
        var boot = new byte[512];
        "NTFS    "u8.CopyTo(boot.AsSpan(3));
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(11), 512);
        boot[13] = 8;
        BinaryPrimitives.WriteInt64LittleEndian(boot.AsSpan(0x30), 2);
        boot[0x40] = unchecked((byte)-10);
        var parsed = DeletedFiles.ParseBoot(boot);
        Assert.Equal(1024, parsed.RecordSize);
        Assert.Equal(4096, parsed.ClusterSize);
        Assert.Equal(8192, parsed.MftOffset);

        var records = new[]
        {
            Record(0, true, false, 1, null, 5, 1, 4096, new byte[] { 0x11, 0x01, 0x02, 0x00 }),
            Record(1, true, true, 1, "Users", 5, 1, 0),
            Record(2, false, false, 2, "tax.pdf", 1, 1, 4321),
            Record(3, true, false, 3, "keep.txt", 1, 1, 8),
        };
        var image = new byte[4096 * 3];
        boot.CopyTo(image, 0);
        var blob = records.SelectMany(record => record).ToArray();
        Assert.Equal(4096, blob.Length);
        blob.CopyTo(image, 8192);

        byte[] Read(long offset, int size) => image.AsSpan((int)offset, size).ToArray();

        var scan = DeletedFiles.ReadMftRecords(Read, parsed, new ListReporter());
        Assert.Null(scan.Note);
        var entries = DeletedFiles.Entries(scan, @"C:\\");
        Assert.Equal(new[] { "tax.pdf" }, entries.Select(entry => entry.Name).ToArray());
        Assert.Equal(@"C:\Users\tax.pdf", entries[0].Path);
        Assert.Equal("File", entries[0].Kind);
        Assert.Equal(4321, entries[0].Size);
        Assert.Equal("NTFS file table", entries[0].Source);
        Assert.Contains("unused space", entries[0].Note);
    }

    [Fact]
    public void Bad_fixup_is_ignored()
    {
        var raw = new byte[1024];
        "FILE"u8.CopyTo(raw);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(4), 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(6), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x30), 0x1234);
        Assert.Null(DeletedFiles.ParseFileRecord(raw, 512, 0));
    }

    [Fact]
    public void Recycle_bin_versions()
    {
        const string path = @"C:\Users\文档\a.txt";
        var filetime = FileTime(2020, 5, 4);
        var encoded = Encoding.Unicode.GetBytes(path + "\0");
        var version2 = new byte[24 + 4 + encoded.Length];
        BinaryPrimitives.WriteInt64LittleEndian(version2, 2);
        BinaryPrimitives.WriteInt64LittleEndian(version2.AsSpan(8), 99);
        BinaryPrimitives.WriteInt64LittleEndian(version2.AsSpan(16), filetime);
        BinaryPrimitives.WriteInt32LittleEndian(version2.AsSpan(24), encoded.Length / 2);
        encoded.CopyTo(version2, 28);
        var (size, deleted, parsed) = DeletedFiles.ParseRecycleInfo(version2);
        Assert.Equal(99, size);
        Assert.Equal(path, parsed);
        Assert.Equal(filetime, deleted);
        Assert.False(string.IsNullOrEmpty(DeletedFiles.FormatFileTime(filetime)));

        var wide = new byte[520];
        Encoding.Unicode.GetBytes(path).CopyTo(wide, 0);
        var version1 = new byte[24 + 520];
        BinaryPrimitives.WriteInt64LittleEndian(version1, 1);
        BinaryPrimitives.WriteInt64LittleEndian(version1.AsSpan(8), 99);
        BinaryPrimitives.WriteInt64LittleEndian(version1.AsSpan(16), filetime);
        wide.CopyTo(version1, 24);
        Assert.Equal(path, DeletedFiles.ParseRecycleInfo(version1).Path);
    }

    [Fact]
    public void Boot_rejects_other_filesystems()
    {
        var data = new byte[512];
        "MSDOS5.0"u8.CopyTo(data.AsSpan(3));
        Assert.Throws<InvalidDataException>(() => DeletedFiles.ParseBoot(data));
    }
}
