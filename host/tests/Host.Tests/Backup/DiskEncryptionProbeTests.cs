using System.Buffers.Binary;
using System.Text;
using HyperHarbor.Host.Core.Backup;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Backup;

/// <summary>Builds small VHDX images in memory, laid out as MS-VHDX describes, with 1 MiB blocks.</summary>
internal sealed class VhdxImage
{
    private const long OneMegabyte = 1024 * 1024;
    private const long BatOffset = OneMegabyte;
    private const long MetadataOffset = 2 * OneMegabyte;
    private const long FirstPayload = 4 * OneMegabyte;

    private readonly MemoryStream _stream = new();
    private readonly int _sectorSize;
    private long _nextPayload = FirstPayload;

    public VhdxImage(long virtualSize = 64 * OneMegabyte, int sectorSize = 512, bool differencing = false, bool pendingLog = false)
    {
        _sectorSize = sectorSize;
        Write(0, "vhdxfile"u8);
        var header = new byte[64];
        "head"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), 3);
        if (pendingLog)
        {
            Guid.NewGuid().TryWriteBytes(header.AsSpan(48));
        }

        Write(64 * 1024, header);
        var stale = new byte[64];
        "head"u8.CopyTo(stale);
        BinaryPrimitives.WriteUInt64LittleEndian(stale.AsSpan(8), 2);
        Guid.NewGuid().TryWriteBytes(stale.AsSpan(48));
        Write(128 * 1024, stale);

        var regions = new byte[16 + (2 * 32)];
        "regi"u8.CopyTo(regions);
        BinaryPrimitives.WriteUInt32LittleEndian(regions.AsSpan(8), 2);
        Region(regions.AsSpan(16), "2DC27766-F623-4200-9D64-115E9BFD4A08", BatOffset, (int)OneMegabyte);
        Region(regions.AsSpan(48), "8B7CA206-4790-4B9A-B8FE-575F050F886E", MetadataOffset, (int)OneMegabyte);
        Write(192 * 1024, regions);

        var metadata = new byte[32 + (3 * 32)];
        "metadata"u8.CopyTo(metadata);
        BinaryPrimitives.WriteUInt16LittleEndian(metadata.AsSpan(10), 3);
        var parameters = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(parameters, (uint)OneMegabyte);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(4), differencing ? 2u : 0u);
        var size = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(size, (ulong)virtualSize);
        var sector = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sector, (uint)sectorSize);
        Item(metadata.AsSpan(32), "CAA16737-FA36-4D43-B3B6-33F0AA44E76B", 0x10000, parameters);
        Item(metadata.AsSpan(64), "2FA54224-CD1B-4876-B211-5DBED83BF4B8", 0x10008, size);
        Item(metadata.AsSpan(96), "8141BF1D-A96F-4709-BA47-F233A8FAAB5F", 0x10010, sector);
        Write(MetadataOffset, metadata);
        _stream.SetLength(FirstPayload);
    }

    public Stream Stream
    {
        get
        {
            _stream.Position = 0;
            return _stream;
        }
    }

    /// <summary>Writes guest data at a virtual offset, allocating the payload block it falls in.</summary>
    public void WriteVirtual(long virtualOffset, ReadOnlySpan<byte> data)
    {
        var block = virtualOffset / OneMegabyte;
        var batEntry = BatOffset + (block * 8);
        var entry = Read64(batEntry);
        if ((entry & 7) != 6)
        {
            entry = ((ulong)(_nextPayload / OneMegabyte) << 20) | 6;
            Write(batEntry, BitConverter.GetBytes(entry));
            _nextPayload += OneMegabyte;
            _stream.SetLength(_nextPayload);
        }

        Write((long)(entry >> 20) * OneMegabyte + (virtualOffset % OneMegabyte), data);
    }

    public void WriteSector(long lba, ReadOnlySpan<byte> data) => WriteVirtual(lba * _sectorSize, data);

    /// <summary>A protective MBR and a GPT whose partitions start at the LBAs given.</summary>
    public void WriteGpt(params long[] starts)
    {
        var mbr = new byte[512];
        mbr[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 8), 1);
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        WriteSector(0, mbr);

        var header = new byte[92];
        "EFI PART"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(72), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(84), 128);
        WriteSector(1, header);

        var entries = new byte[128 * 128];
        for (var i = 0; i < starts.Length; i++)
        {
            Guid.NewGuid().TryWriteBytes(entries.AsSpan(i * 128));
            BinaryPrimitives.WriteUInt64LittleEndian(entries.AsSpan((i * 128) + 32), (ulong)starts[i]);
        }

        WriteSector(2, entries);
    }

    public void WriteMbr(byte type, uint start)
    {
        var mbr = new byte[512];
        mbr[446 + 4] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 8), start);
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        WriteSector(0, mbr);
    }

    public static byte[] BootSector(string oemId)
    {
        var sector = new byte[512];
        Encoding.ASCII.GetBytes(oemId).CopyTo(sector, 3);
        sector[510] = 0x55;
        sector[511] = 0xAA;
        return sector;
    }

    private static void Region(Span<byte> entry, string id, long offset, int length)
    {
        new Guid(id).TryWriteBytes(entry);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], (ulong)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], 1);
    }

    private void Item(Span<byte> entry, string id, int offset, byte[] value)
    {
        new Guid(id).TryWriteBytes(entry);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], (uint)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], (uint)value.Length);
        Write(MetadataOffset + offset, value);
    }

    private ulong Read64(long offset)
    {
        var buffer = new byte[8];
        _stream.Position = offset;
        _stream.ReadExactly(buffer);
        return BitConverter.ToUInt64(buffer);
    }

    private void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (_stream.Length < offset + data.Length)
        {
            _stream.SetLength(offset + data.Length);
        }

        _stream.Position = offset;
        _stream.Write(data);
    }
}

public sealed class DiskEncryptionProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void AGptDiskWithNtfsVolumes_IsNotEncrypted()
    {
        var image = new VhdxImage();
        image.WriteGpt(2048, 4096);
        image.WriteSector(2048, VhdxImage.BootSector("MSDOS5.0"));
        image.WriteSector(4096, VhdxImage.BootSector("NTFS    "));

        Assert.Equal(DiskEncryption.None, VhdxEncryptionProbe.Probe(image.Stream));
    }

    [Fact]
    public void ABitLockerVolumeBehindTheEfiPartition_IsFound()
    {
        var image = new VhdxImage();
        image.WriteGpt(2048, 4096, 8192);
        image.WriteSector(2048, VhdxImage.BootSector("MSDOS5.0"));
        image.WriteSector(8192, VhdxImage.BootSector("-FVE-FS-"));

        Assert.Equal(DiskEncryption.BitLocker, VhdxEncryptionProbe.Probe(image.Stream));
    }

    [Fact]
    public void ADiskWith4KSectors_IsReadInItsOwnSectors()
    {
        var image = new VhdxImage(sectorSize: 4096);
        image.WriteGpt(256);
        image.WriteSector(256, VhdxImage.BootSector("-FVE-FS-"));

        Assert.Equal(DiskEncryption.BitLocker, VhdxEncryptionProbe.Probe(image.Stream));
    }

    [Fact]
    public void AnMbrDisk_IsReadFromItsPartitionTable()
    {
        var image = new VhdxImage();
        image.WriteMbr(0x07, 2048);
        image.WriteSector(2048, VhdxImage.BootSector("-FVE-FS-"));

        Assert.Equal(DiskEncryption.BitLocker, VhdxEncryptionProbe.Probe(image.Stream));
    }

    [Fact]
    public void ALuksVolume_IsFound()
    {
        var image = new VhdxImage();
        image.WriteGpt(2048, 4096);
        image.WriteSector(4096, [(byte)'L', (byte)'U', (byte)'K', (byte)'S', 0xBA, 0xBE, 0, 2]);

        Assert.Equal(DiskEncryption.Luks, VhdxEncryptionProbe.Probe(image.Stream));
    }

    [Fact]
    public void APartitionInAnUnallocatedBlock_AndABlankDisk_AreNotEncrypted()
    {
        var unwritten = new VhdxImage();
        unwritten.WriteGpt(40960);

        Assert.Equal(DiskEncryption.None, VhdxEncryptionProbe.Probe(unwritten.Stream));
        Assert.Equal(DiskEncryption.None, VhdxEncryptionProbe.Probe(new VhdxImage().Stream));
    }

    [Fact]
    public void DisksThatCannotBeReadAlone_AreUnknown()
    {
        Assert.Equal(DiskEncryption.Unknown, VhdxEncryptionProbe.Probe(new VhdxImage(differencing: true).Stream));
        Assert.Equal(DiskEncryption.Unknown, VhdxEncryptionProbe.Probe(new VhdxImage(pendingLog: true).Stream));
    }

    [Fact]
    public void FilesThatAreNotAWholeVhdx_AreUnknown()
    {
        Assert.Equal(DiskEncryption.Unknown, VhdxEncryptionProbe.Probe(new MemoryStream(new byte[300 * 1024])));
        Assert.Equal(DiskEncryption.Unknown, VhdxEncryptionProbe.Probe(new MemoryStream("vhdxfile"u8.ToArray())));

        var image = new VhdxImage();
        image.WriteGpt(2048);
        var truncated = new MemoryStream();
        image.Stream.CopyTo(truncated);
        truncated.SetLength(3 * 1024 * 1024);
        Assert.Equal(DiskEncryption.Unknown, VhdxEncryptionProbe.Probe(truncated));
    }

    [Fact]
    public void APathThatIsNotAVhdx_OrIsMissing_IsUnknown()
    {
        var probe = new VhdxEncryptionProbe();

        Assert.Equal(DiskEncryption.Unknown, probe.Probe(@"C:\VMs\old.vhd"));
        Assert.Equal(DiskEncryption.Unknown, probe.Probe(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vhdx")));
    }

    /// <summary>HH_DISK_PROBE_LIVE=&lt;path to a .vhdx&gt;: probes a real disk read-only and prints the result.</summary>
    [EnvironmentFact("HH_DISK_PROBE_LIVE")]
    public void Live_ProbesARealDisk()
    {
        var path = Environment.GetEnvironmentVariable("HH_DISK_PROBE_LIVE")!;

        var result = new VhdxEncryptionProbe().Probe(path);

        output.WriteLine($"{path}: {result}");
        Assert.NotEqual(DiskEncryption.Unknown, result);
    }
}
