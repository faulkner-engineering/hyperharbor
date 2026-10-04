using System.Text;
using DiscUtils.Iso9660;

namespace HyperHarbor.Host.Core.Unattend;

/// <summary>Writes the small ISO that carries an install's answer file, attached to the VM as a second DVD.</summary>
public static class SeedIso
{
    public const string FileName = "hyperharbor-seed.iso";
    public const string WindowsVolumeLabel = "HHUNATTEND";

    /// <summary>Writes an ISO 9660 image with Joliet names (so the case of "Autounattend.xml" is kept).</summary>
    public static void Write(string path, string volumeLabel, IReadOnlyDictionary<string, string> files)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = volumeLabel };
        foreach (var (name, content) in files)
        {
            builder.AddFile(name, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".partial";
        builder.Build(temporary);
        FixJolietVolumeLabel(temporary, volumeLabel);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// DiscUtils pads the Joliet volume label with ASCII spaces, which read as U+2020 in UCS-2. Linux's
    /// blkid may take the label from the Joliet descriptor, and cloud-init finds the seed only by the
    /// exact label CIDATA, so the label is rewritten in UCS-2 big endian padded with U+0020.
    /// </summary>
    internal static void FixJolietVolumeLabel(string isoPath, string volumeLabel)
    {
        const int SectorSize = 2048;
        const int LabelOffset = 40;
        const int LabelBytes = 32;

        using var iso = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite);
        var descriptor = new byte[SectorSize];
        for (var sector = 16; sector < 32; sector++)
        {
            iso.Position = (long)sector * SectorSize;
            iso.ReadExactly(descriptor);
            if (descriptor[0] == 255 || !descriptor.AsSpan(1, 5).SequenceEqual("CD001"u8))
            {
                return;
            }

            // A supplementary descriptor with a UCS-2 escape sequence (%/@, %/C, or %/E) is Joliet.
            if (descriptor[0] != 2 || descriptor[88] != '%' || descriptor[89] != '/')
            {
                continue;
            }

            var label = new byte[LabelBytes];
            var characters = volumeLabel.Length > LabelBytes / 2 ? volumeLabel[..(LabelBytes / 2)] : volumeLabel;
            for (var i = 0; i < LabelBytes / 2; i++)
            {
                var c = i < characters.Length ? characters[i] : ' ';
                label[2 * i] = (byte)(c >> 8);
                label[(2 * i) + 1] = (byte)c;
            }

            iso.Position = ((long)sector * SectorSize) + LabelOffset;
            iso.Write(label);
        }
    }

    public static void ForWindows(string path, WindowsInstall install) =>
        Write(path, WindowsVolumeLabel, new Dictionary<string, string> { [AutounattendBuilder.FileName] = AutounattendBuilder.Build(install) });

    public static void ForLinux(string path, LinuxInstall install) =>
        Write(path, CloudInitBuilder.VolumeLabel, new Dictionary<string, string>
        {
            [CloudInitBuilder.UserDataFileName] = CloudInitBuilder.UserData(install),
            [CloudInitBuilder.MetaDataFileName] = CloudInitBuilder.MetaData(install),
        });
}
