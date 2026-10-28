using System.Buffers.Binary;
using System.Text;

namespace HyperHarbor.Host.Core.Backup;

/// <summary>Whether a virtual disk holds an encrypted volume, read from its partition boot sectors.</summary>
public enum DiskEncryption
{
    None,
    BitLocker,

    /// <summary>A Linux LUKS volume.</summary>
    Luks,

    /// <summary>The disk could not be read (not a VHDX, a differencing disk, or an unexpected layout).</summary>
    Unknown,
}

public interface IDiskEncryptionProbe
{
    /// <summary>Never throws; a disk that cannot be read is <see cref="DiskEncryption.Unknown"/>.</summary>
    DiskEncryption Probe(string path);
}

/// <summary>
/// Reads a VHDX's partition table and the first sector of each partition, read-only, and looks for the BitLocker
/// ("-FVE-FS-") and LUKS signatures. Needs no guest access, so it works for any VM, running (on the checkpoint's frozen
/// parent) or off. Encrypted disks matter to backups because their changed pieces do not compress.
/// </summary>
public sealed class VhdxEncryptionProbe : IDiskEncryptionProbe
{
    /// <summary>More GPT entries than this is not a disk Windows made; the probe gives up.</summary>
    private const int MaxGptEntries = 512;

    private static readonly byte[] BitLockerSignature = "-FVE-FS-"u8.ToArray();
    private static readonly byte[] LuksSignature = [(byte)'L', (byte)'U', (byte)'K', (byte)'S', 0xBA, 0xBE];

    public DiskEncryption Probe(string path)
    {
        if (!path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
        {
            return DiskEncryption.Unknown;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            return Probe(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DiskEncryption.Unknown;
        }
    }

    public static DiskEncryption Probe(Stream stream)
    {
        try
        {
            return VhdxReader.Open(stream) is { } disk ? Scan(disk) : DiskEncryption.Unknown;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or OverflowException)
        {
            return DiskEncryption.Unknown;
        }
    }

    private static DiskEncryption Scan(VhdxReader disk)
    {
        var sectorSize = disk.LogicalSectorSize;
        var first = disk.Read(0, sectorSize);
        if (Volume(first) is { } whole)
        {
            // A volume written to the whole disk, without a partition table.
            return whole;
        }

        if (first[510] != 0x55 || first[511] != 0xAA)
        {
            return DiskEncryption.None;
        }

        var found = DiskEncryption.None;
        foreach (var start in PartitionStarts(disk, first))
        {
            if (start * sectorSize >= disk.VirtualSize)
            {
                continue;
            }

            switch (Volume(disk.Read(start * sectorSize, sectorSize)))
            {
                case DiskEncryption.BitLocker:
                    return DiskEncryption.BitLocker;
                case DiskEncryption.Luks:
                    found = DiskEncryption.Luks;
                    break;
            }
        }

        return found;
    }

    /// <summary>The first LBA of each partition, from the GPT when the MBR is protective, otherwise from the MBR.</summary>
    private static IEnumerable<long> PartitionStarts(VhdxReader disk, byte[] mbr)
    {
        var sectorSize = disk.LogicalSectorSize;
        var entries = Enumerable.Range(0, 4).Select(i => mbr.AsSpan(446 + (i * 16), 16).ToArray()).ToList();
        if (entries.Any(e => e[4] == 0xEE))
        {
            var header = disk.Read(sectorSize, sectorSize);
            if (!header.AsSpan(0, 8).SequenceEqual("EFI PART"u8))
            {
                throw new InvalidDataException("The protective MBR has no GPT header after it.");
            }

            var entryLba = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72));
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));
            if (count > MaxGptEntries || size < 128 || size > 1024)
            {
                throw new InvalidDataException("The GPT header is not one Windows writes.");
            }

            var table = disk.Read(entryLba * sectorSize, RoundUp(count * size, sectorSize));
            var starts = new List<long>();
            for (var i = 0; i < count; i++)
            {
                var entry = table.AsSpan(i * size, size);
                if (!entry[..16].SequenceEqual(new byte[16]))
                {
                    starts.Add((long)BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]));
                }
            }

            return starts;
        }

        // Extended partitions (0x05, 0x0F, 0x85) hold further tables; their logical drives are not read.
        return entries
            .Where(e => e[4] is not (0x00 or 0x05 or 0x0F or 0x85))
            .Select(e => (long)BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(8)))
            .ToList();
    }

    private static DiskEncryption? Volume(byte[] bootSector)
    {
        if (bootSector.AsSpan(3, BitLockerSignature.Length).SequenceEqual(BitLockerSignature))
        {
            return DiskEncryption.BitLocker;
        }

        return bootSector.AsSpan(0, LuksSignature.Length).SequenceEqual(LuksSignature) ? DiskEncryption.Luks : null;
    }

    private static int RoundUp(int value, int multiple) => (value + multiple - 1) / multiple * multiple;
}

/// <summary>
/// The part of the VHDX format (MS-VHDX) needed to read sectors of a fixed or dynamic disk: the newest header, the region
/// table, the metadata (block size, virtual size, logical sector size), and the BAT. Differencing disks, and disks whose
/// log still holds unapplied writes, are refused.
/// </summary>
internal sealed class VhdxReader
{
    private const int HeaderOffset1 = 64 * 1024;
    private const int HeaderOffset2 = 128 * 1024;
    private const int RegionTableOffset = 192 * 1024;
    private const int MaxRegionEntries = 2047;
    private const long OneMegabyte = 1024 * 1024;

    private static readonly Guid BatRegion = new("2DC27766-F623-4200-9D64-115E9BFD4A08");
    private static readonly Guid MetadataRegion = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");
    private static readonly Guid FileParameters = new("CAA16737-FA36-4D43-B3B6-33F0AA44E76B");
    private static readonly Guid VirtualDiskSize = new("2FA54224-CD1B-4876-B211-5DBED83BF4B8");
    private static readonly Guid LogicalSectorSizeItem = new("8141BF1D-A96F-4709-BA47-F233A8FAAB5F");

    private readonly Stream _stream;
    private readonly long _blockSize;
    private readonly long _chunkRatio;
    private readonly byte[] _bat;

    private VhdxReader(Stream stream, long blockSize, int logicalSectorSize, long virtualSize, byte[] bat)
    {
        _stream = stream;
        _blockSize = blockSize;
        LogicalSectorSize = logicalSectorSize;
        VirtualSize = virtualSize;
        _bat = bat;
        _chunkRatio = (1L << 23) * logicalSectorSize / blockSize;
    }

    public int LogicalSectorSize { get; }

    public long VirtualSize { get; }

    /// <summary>The reader, or null for a disk it does not handle (differencing, or a log to replay).</summary>
    /// <exception cref="InvalidDataException">The file is not a VHDX.</exception>
    public static VhdxReader? Open(Stream stream)
    {
        if (!ReadAt(stream, 0, 8).AsSpan().SequenceEqual("vhdxfile"u8))
        {
            throw new InvalidDataException("The file is not a VHDX.");
        }

        var header = new[] { HeaderOffset1, HeaderOffset2 }
            .Select(offset => ReadAt(stream, offset, 64))
            .Where(h => h.AsSpan(0, 4).SequenceEqual("head"u8))
            .MaxBy(h => BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(8)))
            ?? throw new InvalidDataException("The VHDX has no valid header.");
        if (new Guid(header.AsSpan(48, 16)) != Guid.Empty)
        {
            // Writes are still in the log; the payload alone may be out of date.
            return null;
        }

        var regions = ReadAt(stream, RegionTableOffset, 16);
        if (!regions.AsSpan(0, 4).SequenceEqual("regi"u8))
        {
            throw new InvalidDataException("The VHDX has no region table.");
        }

        var regionCount = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(regions.AsSpan(8)), MaxRegionEntries);
        var regionEntries = ReadAt(stream, RegionTableOffset + 16, regionCount * 32);
        (long Offset, int Length)? bat = null, metadata = null;
        for (var i = 0; i < regionCount; i++)
        {
            var entry = regionEntries.AsSpan(i * 32, 32);
            var region = ((long)BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]));
            var id = new Guid(entry[..16]);
            if (id == BatRegion)
            {
                bat = region;
            }
            else if (id == MetadataRegion)
            {
                metadata = region;
            }
        }

        if (bat is not { } batRegion || metadata is not { } metadataRegion)
        {
            throw new InvalidDataException("The VHDX lacks its BAT or metadata region.");
        }

        var items = ReadMetadata(stream, metadataRegion.Offset);
        var parameters = items.TryGetValue(FileParameters, out var p) ? p : throw new InvalidDataException("The VHDX has no file parameters.");
        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(parameters);
        if ((BinaryPrimitives.ReadUInt32LittleEndian(parameters.AsSpan(4)) & 2) != 0)
        {
            // A differencing disk: unallocated blocks come from its parent.
            return null;
        }

        var virtualSize = items.TryGetValue(VirtualDiskSize, out var v) ? (long)BinaryPrimitives.ReadUInt64LittleEndian(v) : throw new InvalidDataException("The VHDX has no size.");
        var sectorSize = items.TryGetValue(LogicalSectorSizeItem, out var s) ? (int)BinaryPrimitives.ReadUInt32LittleEndian(s) : 512;
        if (blockSize < OneMegabyte || blockSize > 256 * OneMegabyte || (blockSize & (blockSize - 1)) != 0 || sectorSize is not (512 or 4096))
        {
            throw new InvalidDataException("The VHDX's block or sector size is not valid.");
        }

        return new VhdxReader(stream, blockSize, sectorSize, virtualSize, ReadAt(stream, batRegion.Offset, batRegion.Length));
    }

    /// <summary>Reads within one payload block; blocks that are not present read as zeros.</summary>
    public byte[] Read(long virtualOffset, int length)
    {
        if (virtualOffset < 0 || virtualOffset + length > VirtualSize || (virtualOffset % _blockSize) + length > _blockSize)
        {
            throw new InvalidDataException("The read is outside the disk or crosses a block.");
        }

        var block = virtualOffset / _blockSize;
        var index = block + (block / _chunkRatio);
        if ((index + 1) * 8 > _bat.Length)
        {
            throw new InvalidDataException("The BAT is shorter than the disk.");
        }

        var entry = BinaryPrimitives.ReadUInt64LittleEndian(_bat.AsSpan((int)(index * 8)));
        var state = entry & 7;
        if (state is not (6 or 7))
        {
            // Not present, undefined, zero, or unmapped: no data was written there.
            return new byte[length];
        }

        var fileOffset = (long)(entry >> 20) * OneMegabyte;
        return ReadAt(_stream, fileOffset + (virtualOffset % _blockSize), length);
    }

    private static Dictionary<Guid, byte[]> ReadMetadata(Stream stream, long regionOffset)
    {
        var table = ReadAt(stream, regionOffset, 32);
        if (!table.AsSpan(0, 8).SequenceEqual("metadata"u8))
        {
            throw new InvalidDataException("The VHDX metadata table is missing.");
        }

        var count = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(10)), (ushort)2047);
        var entries = ReadAt(stream, regionOffset + 32, count * 32);
        var items = new Dictionary<Guid, byte[]>();
        for (var i = 0; i < count; i++)
        {
            var entry = entries.AsSpan(i * 32, 32);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
            var length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]), 64);
            items[new Guid(entry[..16])] = ReadAt(stream, regionOffset + offset, length);
        }

        return items;
    }

    private static byte[] ReadAt(Stream stream, long offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > stream.Length)
        {
            throw new InvalidDataException("The VHDX is shorter than its structures say.");
        }

        var buffer = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }
}
