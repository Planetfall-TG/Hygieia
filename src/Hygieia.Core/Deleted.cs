using System.Buffers.Binary;
using System.Text;

namespace Hygieia;

public sealed class DeletedEntry
{
    public string Name { get; }
    public string Path { get; }
    public string Kind { get; }
    public long? Size { get; }
    public string Deleted { get; }
    public string Source { get; }
    public string Note { get; }
    public IReadOnlyList<string> ScrubPaths { get; }

    public DeletedEntry(string name, string path, string kind, long? size, string deleted, string source, string note, IReadOnlyList<string>? scrubPaths = null)
    {
        Name = name;
        Path = path;
        Kind = kind;
        Size = size;
        Deleted = deleted;
        Source = source;
        Note = note;
        ScrubPaths = scrubPaths ?? Array.Empty<string>();
    }
}

public sealed class ScanResult
{
    public List<DeletedEntry> Entries { get; }
    public List<string> Notes { get; }

    public ScanResult(List<DeletedEntry> entries, List<string> notes)
    {
        Entries = entries;
        Notes = notes;
    }
}

public readonly record struct Boot(int BytesPerSector, int SectorsPerCluster, long MftCluster, int RecordSize)
{
    public int ClusterSize => BytesPerSector * SectorsPerCluster;
    public long MftOffset => MftCluster * ClusterSize;
}

public sealed class ParsedRecord
{
    public int Index { get; init; }
    public bool InUse { get; init; }
    public bool IsDirectory { get; init; }
    public int Sequence { get; init; }
    public long BaseRecord { get; init; }
    public long? ParentRecord { get; init; }
    public int? ParentSequence { get; init; }
    public string? Name { get; init; }
    public long? DataSize { get; init; }
    public bool DataResident { get; init; }
}

public sealed class DeletedRaw
{
    public int Index { get; init; }
    public bool IsDirectory { get; init; }
    public long? Size { get; init; }
    public bool Resident { get; init; }
}

public sealed class MftScan
{
    public ushort[] Sequences { get; }
    public byte[] Seen { get; }
    public Dictionary<int, (long Parent, int ParentSequence, string Name)> Nodes { get; } = new();
    public List<DeletedRaw> Deleted { get; } = new();
    public string? Note { get; set; }

    public MftScan(int total)
    {
        Sequences = new ushort[Math.Max(0, total)];
        Seen = new byte[Math.Max(0, total)];
    }
}

public static class DeletedFiles
{
    const int FileNameAttr = 0x30;
    const int DataAttr = 0x80;
    const uint EndMarker = 0xFFFFFFFF;
    const int ResidentHeader = 24;
    const int NonresidentHeader = 64;

    public static Boot ParseBoot(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x48 || !data.Slice(3, 8).SequenceEqual("NTFS    "u8))
            throw new InvalidDataException("Not an NTFS boot sector.");
        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(data[11..]);
        var sectorsPerCluster = data[13];
        var mftCluster = BinaryPrimitives.ReadInt64LittleEndian(data[0x30..]);
        var rawRecord = (sbyte)data[0x40];
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096) || sectorsPerCluster == 0)
            throw new InvalidDataException("Unexpected NTFS geometry.");
        int recordSize;
        if (rawRecord < 0)
            recordSize = 1 << (-rawRecord);
        else
            recordSize = rawRecord * sectorsPerCluster * bytesPerSector;
        if (recordSize < 512 || recordSize > 4096)
            throw new InvalidDataException("Unexpected NTFS file-record size.");
        return new Boot(bytesPerSector, sectorsPerCluster, mftCluster, recordSize);
    }

    public static bool ApplyFixup(byte[] record, int sectorSize)
    {
        if (record.Length < 8 || sectorSize < 512)
            return false;
        var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
        if (usaCount < 2 || usaCount > 16)
            return false;
        if (usaOffset < 4 || usaOffset + usaCount * 2 > record.Length)
            return false;
        var usn = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset));
        for (var index = 1; index < usaCount; index++)
        {
            var position = index * sectorSize - 2;
            if (position + 2 > record.Length)
                return false;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position)) != usn)
                return false;
            var replacement = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset + index * 2));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(position), replacement);
        }
        return true;
    }

    static int NamespaceRank(int ns) => ns switch
    {
        3 => 0,
        1 => 1,
        0 => 2,
        2 => 3,
        _ => 4,
    };

    public static ParsedRecord? ParseFileRecord(byte[] raw, int sectorSize, int recordIndex)
    {
        if (raw.Length < 64 || raw[0] != (byte)'F' || raw[1] != (byte)'I' || raw[2] != (byte)'L' || raw[3] != (byte)'E')
            return null;
        var record = (byte[])raw.Clone();
        if (!ApplyFixup(record, sectorSize))
            return null;
        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x10));
        var firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14));
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x16));
        var used = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(0x18));
        var baseRecord = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(0x20)) & 0xFFFFFFFFFFFF;
        if (used < 48 || used > record.Length)
            used = record.Length;
        (int Rank, string Name, long Parent, int ParentSeq, long NameSize)? best = null;
        long? dataSize = null;
        var dataResident = false;
        var offset = (int)firstAttribute;
        while (offset + ResidentHeader <= used)
        {
            var attrType = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset));
            var attrLength = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(offset + 4));
            if (attrType == EndMarker || attrLength == 0)
                break;
            if (offset + attrLength > used || attrLength < ResidentHeader)
                break;
            var nonresident = record[offset + 8];
            var nameLength = record[offset + 9];
            if (nonresident != 0)
            {
                if (attrLength < NonresidentHeader)
                    break;
                if (attrType == DataAttr && nameLength == 0 && dataSize == null)
                {
                    dataSize = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(offset + 0x30));
                    dataResident = false;
                }
            }
            else
            {
                var valueLength = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(offset + 16));
                var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(offset + 20));
                var start = offset + valueOffset;
                var end = start + valueLength;
                if (end <= offset + attrLength)
                {
                    if (attrType == DataAttr && nameLength == 0 && dataSize == null)
                    {
                        dataSize = valueLength;
                        dataResident = true;
                    }
                    else if (attrType == FileNameAttr && valueLength >= 66)
                    {
                        var parsed = ParseFileName(record, start, valueLength);
                        if (parsed != null && (best == null || parsed.Value.Rank < best.Value.Rank))
                            best = parsed;
                    }
                }
            }
            offset += attrLength;
        }
        long? parentRecord = null;
        int? parentSequence = null;
        string? name = null;
        if (best != null)
        {
            name = best.Value.Name;
            parentRecord = best.Value.Parent;
            parentSequence = best.Value.ParentSeq;
            if (dataSize == null)
                dataSize = best.Value.NameSize;
        }
        return new ParsedRecord
        {
            Index = recordIndex,
            InUse = (flags & 0x01) != 0,
            IsDirectory = (flags & 0x02) != 0,
            Sequence = sequence,
            BaseRecord = baseRecord,
            ParentRecord = parentRecord,
            ParentSequence = parentSequence,
            Name = name,
            DataSize = dataSize,
            DataResident = dataResident,
        };
    }

    static (int Rank, string Name, long Parent, int ParentSeq, long NameSize)? ParseFileName(byte[] record, int start, int valueLength)
    {
        var parentRef = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(start));
        var parentRecord = (long)(parentRef & 0xFFFFFFFFFFFF);
        var parentSequence = (int)((parentRef >> 48) & 0xFFFF);
        var nameSize = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(start + 48));
        var nameLength = record[start + 64];
        var ns = record[start + 65];
        var need = 66 + nameLength * 2;
        if (valueLength < need)
            return null;
        var name = Encoding.Unicode.GetString(record, start + 66, nameLength * 2);
        if (name.Length == 0)
            return null;
        return (NamespaceRank(ns), name, parentRecord, parentSequence, nameSize);
    }

    public static List<(long? Offset, long Length)> ParseRunList(ReadOnlySpan<byte> data)
    {
        var runs = new List<(long? Offset, long Length)>();
        var index = 0;
        long current = 0;
        while (index < data.Length)
        {
            var header = data[index];
            index++;
            if (header == 0)
                break;
            var lengthBytes = header & 0x0F;
            var offsetBytes = (header >> 4) & 0x0F;
            if (lengthBytes == 0 || index + lengthBytes + offsetBytes > data.Length)
                break;
            long length = 0;
            for (var i = 0; i < lengthBytes; i++)
                length |= (long)data[index + i] << (8 * i);
            index += lengthBytes;
            if (offsetBytes != 0)
            {
                long relative = 0;
                for (var i = 0; i < offsetBytes; i++)
                    relative |= (long)data[index + i] << (8 * i);
                var signBit = 1L << (offsetBytes * 8 - 1);
                if ((relative & signBit) != 0)
                    relative -= 1L << (offsetBytes * 8);
                index += offsetBytes;
                current += relative;
                runs.Add((current, length));
            }
            else
            {
                runs.Add((null, length));
            }
        }
        return runs;
    }

    static (List<(long? Offset, long Length)> Runs, long DataSize)? Record0Layout(byte[] record, int sectorSize)
    {
        if (ParseFileRecord(record, sectorSize, 0) == null)
            return null;
        var raw = (byte[])record.Clone();
        if (!ApplyFixup(raw, sectorSize))
            return null;
        var used = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x18));
        var offset = (int)BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x14));
        if (used > raw.Length)
            used = raw.Length;
        while (offset + NonresidentHeader <= used)
        {
            var attrType = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset));
            var attrLength = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(offset + 4));
            if (attrType == EndMarker || attrLength == 0)
                break;
            if (offset + attrLength > raw.Length)
                break;
            var nonresident = raw[offset + 8];
            var nameLength = raw[offset + 9];
            if (attrType == DataAttr && nonresident != 0 && nameLength == 0 && attrLength >= NonresidentHeader)
            {
                var runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(offset + 0x20));
                var dataSize = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(offset + 0x30));
                var start = offset + runsOffset;
                if (start >= offset + attrLength)
                    return null;
                var runs = ParseRunList(raw.AsSpan(start, offset + attrLength - start));
                return (runs, dataSize);
            }
            if (attrLength < ResidentHeader)
                break;
            offset += attrLength;
        }
        return null;
    }

    public static MftScan ReadMftRecords(Func<long, int, byte[]> read, Boot boot, IReporter reporter)
    {
        byte[] first;
        try
        {
            first = read(boot.MftOffset, boot.RecordSize);
        }
        catch (Exception ex)
        {
            var failed = new MftScan(0) { Note = $"The file table could not be read: {Text.Friendly(ex)}" };
            return failed;
        }
        var layout = Record0Layout(first, boot.BytesPerSector);
        if (layout == null)
            return new MftScan(0) { Note = "The file table's own record could not be read." };
        var (runs, dataSize) = layout.Value;
        if (dataSize <= 0 || boot.RecordSize == 0)
            return new MftScan(0) { Note = "The file table size looked wrong, so it was not scanned." };
        var totalRecords = dataSize / boot.RecordSize;
        if (totalRecords <= 0 || totalRecords > 20_000_000)
            return new MftScan(0) { Note = "The file table size looked wrong, so it was not scanned." };
        var scan = new MftScan((int)totalRecords);
        var remaining = totalRecords;
        var index = 0;
        var carry = new List<byte>();
        var cluster = boot.ClusterSize;
        var recordSize = boot.RecordSize;
        const int chunkBytes = 4 * 1024 * 1024;
        foreach (var (clusterIndex, count) in runs)
        {
            if (remaining <= 0 || reporter.IsCancelled)
                break;
            if (clusterIndex == null)
            {
                var skip = Math.Min(remaining, (count * cluster) / recordSize);
                index += (int)skip;
                remaining -= skip;
                continue;
            }
            var runOffset = clusterIndex.Value * cluster;
            var runLength = count * cluster;
            long position = 0;
            while (position < runLength && remaining > 0)
            {
                if (reporter.IsCancelled)
                    throw new CancelledException();
                var want = (int)Math.Min(chunkBytes, runLength - position);
                byte[] piece;
                try
                {
                    piece = read(runOffset + position, want);
                }
                catch (Exception ex)
                {
                    scan.Note = $"The file table could not be read completely: {Text.Friendly(ex)}";
                    return scan;
                }
                if (piece.Length == 0)
                {
                    scan.Note = "The file table could not be read completely.";
                    return scan;
                }
                position += piece.Length;
                carry.AddRange(piece);
                while (carry.Count >= recordSize && remaining > 0)
                {
                    var raw = carry.GetRange(0, recordSize).ToArray();
                    carry.RemoveRange(0, recordSize);
                    Consume(scan, raw, boot.BytesPerSector, index);
                    index++;
                    remaining--;
                    if (index % 8192 == 0)
                    {
                        reporter.Progress(
                            index / (double)totalRecords,
                            $"Reading the file table… {Text.Comma(index)} of {Text.Comma(totalRecords)} records. {Text.Comma(scan.Deleted.Count)} deleted names.");
                    }
                }
            }
        }
        if (reporter.IsCancelled)
            throw new CancelledException();
        reporter.Progress(1, $"File table read. {Text.Comma(scan.Deleted.Count)} deleted names.");
        return scan;
    }

    static void Consume(MftScan scan, byte[] raw, int sectorSize, int index)
    {
        if (index >= scan.Seen.Length)
            return;
        var parsed = ParseFileRecord(raw, sectorSize, index);
        if (parsed == null)
            return;
        scan.Seen[index] = 1;
        scan.Sequences[index] = (ushort)(parsed.Sequence & 0xFFFF);
        if (parsed.BaseRecord != 0 && parsed.BaseRecord != index)
            return;
        if (string.IsNullOrEmpty(parsed.Name) || parsed.Name is "." or "..")
            return;
        var keepNode = !parsed.InUse || parsed.IsDirectory;
        if (keepNode && parsed.ParentRecord != null && parsed.ParentSequence != null)
            scan.Nodes[index] = (parsed.ParentRecord.Value, parsed.ParentSequence.Value, parsed.Name);
        if (!parsed.InUse)
        {
            scan.Deleted.Add(new DeletedRaw
            {
                Index = index,
                IsDirectory = parsed.IsDirectory,
                Size = parsed.DataSize,
                Resident = parsed.DataResident && !parsed.IsDirectory,
            });
        }
    }

    public static string VolumeRoot(string drive)
    {
        var text = Path.GetFullPath(drive.Trim());
        if (text.EndsWith(':'))
            text += "\\";
        else if (!text.EndsWith('\\'))
            text += "\\";
        return text;
    }

    public static List<DeletedEntry> Entries(MftScan scan, string drive)
    {
        var cache = new Dictionary<int, (string Path, bool Uncertain)>();

        (string Path, bool Uncertain) Resolve(int index, HashSet<int> stack)
        {
            if (cache.TryGetValue(index, out var cached))
                return cached;
            if (!scan.Nodes.TryGetValue(index, out var node))
            {
                cache[index] = ("", true);
                return cache[index];
            }
            var (parent, parentSeq, name) = node;
            if (!stack.Add(index))
            {
                cache[index] = (name, true);
                return cache[index];
            }
            if (parent is 5 or 0 || parent == index)
            {
                stack.Remove(index);
                cache[index] = (name, false);
                return cache[index];
            }
            if (parent < scan.Seen.Length && scan.Seen[parent] != 0 && scan.Sequences[parent] != parentSeq)
            {
                stack.Remove(index);
                cache[index] = (name, true);
                return cache[index];
            }
            if (!scan.Nodes.ContainsKey((int)parent))
            {
                stack.Remove(index);
                cache[index] = (name, true);
                return cache[index];
            }
            var (prefix, parentUncertain) = Resolve((int)parent, stack);
            stack.Remove(index);
            var path = prefix.Length == 0 ? name : $"{prefix}\\{name}";
            cache[index] = (path, parentUncertain);
            return cache[index];
        }

        var root = VolumeRoot(drive);
        var entries = new List<DeletedEntry>();
        foreach (var raw in scan.Deleted)
        {
            var (relative, uncertain) = Resolve(raw.Index, new HashSet<int>());
            var name = scan.Nodes.TryGetValue(raw.Index, out var node) ? node.Name : relative;
            var full = relative.Length > 0 ? root + relative : root + name;
            var notes = new List<string>();
            if (uncertain)
                notes.Add("Path is uncertain because a parent record was reused.");
            if (raw.IsDirectory)
                notes.Add("The folder name was still in the file table.");
            else if (raw.Resident)
                notes.Add("Contents may still sit inside the file table, which unused-space scrubbing does not rewrite.");
            else
                notes.Add("Contents, if not already reused, are in unused space.");
            entries.Add(new DeletedEntry(
                string.IsNullOrEmpty(name) ? "(unnamed)" : name,
                full,
                raw.IsDirectory ? "Folder" : "File",
                raw.Size,
                "",
                "NTFS file table",
                string.Join(" ", notes)));
        }
        return entries;
    }

    public static (long Size, long Deleted, string Path) ParseRecycleInfo(byte[] data)
    {
        if (data.Length < 24)
            throw new InvalidDataException("Recycle Bin information is too short.");
        var version = BinaryPrimitives.ReadInt64LittleEndian(data);
        var size = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8));
        var deleted = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16));
        string path;
        if (version == 1)
        {
            if (data.Length < 24 + 520)
                throw new InvalidDataException("Recycle Bin information is missing the path.");
            path = Encoding.Unicode.GetString(data, 24, 520);
            var nul = path.IndexOf('\0');
            if (nul >= 0)
                path = path[..nul];
        }
        else if (version == 2)
        {
            if (data.Length < 28)
                throw new InvalidDataException("Recycle Bin information is missing the path length.");
            var characters = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24));
            if (characters <= 0 || characters > 100_000)
                throw new InvalidDataException("Recycle Bin path length is invalid.");
            var bytes = Math.Min(data.Length - 28, characters * 2);
            if (bytes < 2)
                throw new InvalidDataException("Recycle Bin path is missing.");
            path = Encoding.Unicode.GetString(data, 28, bytes).TrimEnd('\0');
        }
        else
        {
            throw new InvalidDataException("Unknown Recycle Bin information version.");
        }
        if (path.Length == 0)
            throw new InvalidDataException("Recycle Bin path is empty.");
        return (size, deleted, path);
    }

    public static string FormatFileTime(long filetime)
    {
        if (filetime <= 0)
            return "";
        try
        {
            return DateTime.FromFileTime(filetime).ToString("yyyy-MM-dd HH:mm");
        }
        catch (Exception)
        {
            return "";
        }
    }

    public static List<DeletedEntry> ScanRecycleBin(string drive, IReporter reporter)
    {
        var root = System.IO.Path.Combine(VolumeRoot(drive), "$Recycle.Bin");
        if (!Directory.Exists(root))
            return new List<DeletedEntry>();
        var entries = new List<DeletedEntry>();
        var bad = 0;
        IEnumerable<string> sidDirs;
        try
        {
            sidDirs = Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex)
        {
            reporter.Log($"Recycle Bin could not be listed. {Text.Friendly(ex)}", error: true);
            return entries;
        }
        foreach (var sid in sidDirs)
        {
            if (reporter.IsCancelled)
                throw new CancelledException();
            string[] items;
            try
            {
                items = Directory.EnumerateFileSystemEntries(sid).ToArray();
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var info in items.Where(item => System.IO.Path.GetFileName(item).StartsWith("$I", StringComparison.Ordinal)))
            {
                if (reporter.IsCancelled)
                    throw new CancelledException();
                long size;
                long deleted;
                string original;
                try
                {
                    using var stream = File.OpenRead(info);
                    var payload = new byte[Math.Min(65536, stream.Length)];
                    _ = stream.Read(payload, 0, payload.Length);
                    (size, deleted, original) = ParseRecycleInfo(payload);
                }
                catch (Exception)
                {
                    bad++;
                    continue;
                }
                var dataName = "$R" + System.IO.Path.GetFileName(info)[2..];
                var dataPath = System.IO.Path.Combine(sid, dataName);
                var exists = PathExists(dataPath);
                var kind = exists && (File.GetAttributes(dataPath) & FileAttributes.Directory) != 0 ? "Folder" : "File";
                var scrub = new List<string>();
                if (exists)
                    scrub.Add(dataPath);
                scrub.Add(info);
                var note = exists
                    ? "Still stored in the Recycle Bin. Scrubbing these copies overwrites them."
                    : "The Recycle Bin information file remains, but the data file is missing.";
                entries.Add(new DeletedEntry(
                    System.IO.Path.GetFileName(original) is { Length: > 0 } file ? file : original,
                    original,
                    kind,
                    size,
                    FormatFileTime(deleted),
                    "Recycle Bin",
                    note,
                    scrub));
            }
        }
        if (bad > 0)
            reporter.Log($"Skipped {Text.Comma(bad)} Recycle Bin information files that could not be read.");
        return entries;
    }

    static bool PathExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static MftScan ReadVolumeMft(string drive, IReporter reporter)
    {
        using var volume = new Native.Volume(drive);
        var boot = ParseBoot(volume.Read(0, 4096));
        return ReadMftRecords(volume.Read, boot, reporter);
    }

    public static ScanResult ScanDrive(string drive, IReporter reporter)
    {
        var notes = new List<string>();
        var entries = new List<DeletedEntry>();
        try
        {
            entries.AddRange(ScanRecycleBin(drive, reporter));
        }
        catch (CancelledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            notes.Add($"Recycle Bin could not be read. {Text.Friendly(ex)}");
        }
        var root = VolumeRoot(drive);
        var (_, filesystem) = Native.VolumeLabel(root);
        if (!filesystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            var label = filesystem.Length == 0 ? "not NTFS" : filesystem;
            notes.Add($"This drive is {label}, so there is no NTFS file table to search.");
        }
        else
        {
            try
            {
                var scan = ReadVolumeMft(drive, reporter);
                entries.AddRange(Entries(scan, drive));
                if (scan.Note != null)
                    notes.Add(scan.Note);
            }
            catch (CancelledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                notes.Add("The NTFS file table needs administrator rights. Recycle Bin items are still listed. Use Scan as administrator for the rest.");
            }
            catch (Exception ex)
            {
                notes.Add($"The file table could not be read. {Text.Friendly(ex)}");
            }
        }
        entries.Sort((a, b) =>
        {
            var path = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            return path != 0 ? path : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        var recycle = entries.Count(entry => entry.Source == "Recycle Bin");
        var table = entries.Count(entry => entry.Source == "NTFS file table");
        reporter.Log($"Found {Text.Comma(recycle)} Recycle Bin items and {Text.Comma(table)} deleted names in the file table.");
        return new ScanResult(entries, notes);
    }
}
