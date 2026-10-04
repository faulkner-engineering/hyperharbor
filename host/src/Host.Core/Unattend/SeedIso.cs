using System.Text;
using DiscUtils.Iso9660;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Core.Unattend;

/// <summary>
/// Writes the small ISO that carries an install's answer file, attached to the VM as a second DVD. The
/// file gets the ProtectedFile ACL, because it holds the one-time administrator password (obfuscated for
/// Windows, hashed for Linux); Hyper-V adds the VM's own access when it attaches the image.
/// </summary>
public static class SeedIso
{
    public const string WindowsVolumeLabel = "HHUNATTEND";

    private const int SectorSize = 2048;

    /// <summary>The seed's file name for a VM, placed next to its disk.</summary>
    public static string PathFor(string diskPath) =>
        Path.Combine(Path.GetDirectoryName(diskPath)!, Path.GetFileNameWithoutExtension(diskPath) + ".hyperharbor-seed.iso");

    /// <summary>An ISO 9660 image with Joliet names (so the case of "Autounattend.xml" is kept).</summary>
    public static byte[] Build(string volumeLabel, IReadOnlyDictionary<string, string> files)
    {
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = volumeLabel };
        foreach (var (name, content) in files)
        {
            builder.AddFile(name, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
        }

        using var image = new MemoryStream();
        builder.Build(image);
        var bytes = image.ToArray();
        FixJolietVolumeLabel(bytes, volumeLabel);
        return bytes;
    }

    public static void Write(string path, string volumeLabel, IReadOnlyDictionary<string, string> files)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        ProtectedFile.WriteAllBytes(path, Build(volumeLabel, files));
    }

    public static void ForWindows(string path, WindowsInstall install) =>
        Write(path, WindowsVolumeLabel, new Dictionary<string, string> { [AutounattendBuilder.FileName] = AutounattendBuilder.Build(install) });

    public static void ForLinux(string path, LinuxInstall install) =>
        Write(path, CloudInitBuilder.VolumeLabel, new Dictionary<string, string>
        {
            [CloudInitBuilder.UserDataFileName] = CloudInitBuilder.UserData(install),
            [CloudInitBuilder.MetaDataFileName] = CloudInitBuilder.MetaData(install),
        });

    /// <summary>
    /// DiscUtils pads the Joliet volume label with ASCII spaces, which read as U+2020 in UCS-2. Linux's
    /// blkid may take the label from the Joliet descriptor, and cloud-init finds the seed only by the
    /// exact label CIDATA, so the label is rewritten in UCS-2 big endian padded with U+0020.
    /// </summary>
    internal static void FixJolietVolumeLabel(byte[] image, string volumeLabel)
    {
        const int LabelOffset = 40;
        const int LabelBytes = 32;

        for (var sector = 16; (sector + 1) * SectorSize <= image.Length; sector++)
        {
            var descriptor = image.AsSpan(sector * SectorSize, SectorSize);
            if (descriptor[0] == 255 || !descriptor.Slice(1, 5).SequenceEqual("CD001"u8))
            {
                return;
            }

            // A supplementary descriptor with a UCS-2 escape sequence (%/@, %/C, or %/E) is Joliet.
            if (descriptor[0] != 2 || descriptor[88] != '%' || descriptor[89] != '/')
            {
                continue;
            }

            var characters = volumeLabel.Length > LabelBytes / 2 ? volumeLabel[..(LabelBytes / 2)] : volumeLabel;
            var label = descriptor.Slice(LabelOffset, LabelBytes);
            for (var i = 0; i < LabelBytes / 2; i++)
            {
                var c = i < characters.Length ? characters[i] : ' ';
                label[2 * i] = (byte)(c >> 8);
                label[(2 * i) + 1] = (byte)c;
            }
        }
    }
}
