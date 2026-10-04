using System.Text;
using DiscUtils.Iso9660;
using HyperHarbor.Host.Core.Unattend;

namespace HyperHarbor.Host.Tests.Unattend;

/// <summary>Small ISO 9660 images with the layout of Windows Setup media or an Ubuntu installer.</summary>
internal static class TestIsos
{
    public static void Write(string path, Dictionary<string, byte[]> files)
    {
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "TEST" };
        foreach (var (name, content) in files)
        {
            builder.AddFile(name, content);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        builder.Build(path);
    }

    public static void Windows(string path, params string[] editions) =>
        Write(path, new Dictionary<string, byte[]>
        {
            [@"sources\install.wim"] = Wim(editions.Length > 0 ? editions : ["Windows 11 Home", "Windows 11 Pro"]),
            ["setup.exe"] = [0],
        });

    public static void Ubuntu(string path) =>
        Write(path, new Dictionary<string, byte[]>
        {
            [@"casper\vmlinuz"] = [0],
            [@".disk\info"] = Encoding.UTF8.GetBytes("Ubuntu-Server 24.04.1 LTS \"Noble Numbat\" - Release amd64 (20240827)\n"),
        });

    /// <summary>A WIM header whose XML resource lists the images; the image data itself is left out.</summary>
    public static byte[] Wim(params string[] names)
    {
        var images = string.Concat(names.Select((name, index) => $"<IMAGE INDEX=\"{index + 1}\"><NAME>{name}</NAME></IMAGE>"));
        var xml = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes($"<WIM><TOTALBYTES>0</TOTALBYTES>{images}</WIM>")).ToArray();
        var header = new byte[208];
        "MSWIM\0\0\0"u8.CopyTo(header);
        BitConverter.GetBytes(208).CopyTo(header, 8);
        BitConverter.GetBytes((long)xml.Length).CopyTo(header, 72);
        BitConverter.GetBytes(208L).CopyTo(header, 80);
        BitConverter.GetBytes((long)xml.Length).CopyTo(header, 88);
        return [.. header, .. xml];
    }
}

/// <summary>Records key presses.</summary>
internal sealed class FakeVmKeyboard : IVmKeyboard
{
    public List<(Guid VmId, int Key)> Presses { get; } = [];

    public Task TypeKeyAsync(Guid vmId, int virtualKey, CancellationToken cancellationToken)
    {
        lock (Presses)
        {
            Presses.Add((vmId, virtualKey));
        }

        return Task.CompletedTask;
    }
}

/// <summary>Records ejected media.</summary>
internal sealed class FakeVmMedia : IVmMedia
{
    public List<(Guid VmId, string Path)> Ejected { get; } = [];

    public Task EjectAsync(Guid vmId, string isoPath, CancellationToken cancellationToken)
    {
        lock (Ejected)
        {
            Ejected.Add((vmId, isoPath));
        }

        return Task.CompletedTask;
    }
}

/// <summary>Answers as configured, without network access.</summary>
internal sealed class FakeRemoteAccessProbe : IRemoteAccessProbe
{
    public bool Rdp { get; set; }

    public bool Ssh { get; set; }

    public Task<bool> RdpAnswersAsync(string address, CancellationToken cancellationToken) => Task.FromResult(Rdp);

    public Task<bool> SshAnswersAsync(string address, CancellationToken cancellationToken) => Task.FromResult(Ssh);
}
