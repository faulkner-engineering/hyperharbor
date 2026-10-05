using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HyperHarbor.Host.Core.Installation;

/// <summary>
/// NTFS directory junctions (mount point reparse points). Unlike directory symbolic links they need no
/// privilege to create, and the service is registered against a path through one, so switching versions
/// only repoints the junction.
/// </summary>
public static class Junction
{
    private const uint ReparseTagMountPoint = 0xA0000003;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const string NonParsedPrefix = @"\??\";

    /// <summary>Creates the junction <paramref name="link"/> pointing at the existing folder <paramref name="target"/>.</summary>
    /// <exception cref="IOException"><paramref name="link"/> already exists.</exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="target"/> does not exist.</exception>
    public static void Create(string link, string target)
    {
        link = Path.GetFullPath(link);
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (!Directory.Exists(target))
        {
            throw new DirectoryNotFoundException($"The junction target {target} does not exist.");
        }

        if (Path.Exists(link))
        {
            throw new IOException($"{link} already exists.");
        }

        Directory.CreateDirectory(link);
        try
        {
            using var handle = NativeMethods.CreateFile(link, GenericWrite, 0, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open {link} to make it a junction.");
            }

            var buffer = MountPointBuffer(NonParsedPrefix + target, target);
            if (!NativeMethods.DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not make {link} a junction to {target}.");
            }
        }
        catch
        {
            Directory.Delete(link);
            throw;
        }
    }

    /// <summary>True when <paramref name="path"/> is a junction or another directory reparse point.</summary>
    public static bool IsJunction(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>The full path the junction points at, or null when <paramref name="path"/> is not a junction.</summary>
    public static string? GetTarget(string path)
    {
        if (!IsJunction(path) || new DirectoryInfo(path).LinkTarget is not { } target)
        {
            return null;
        }

        return target.StartsWith(NonParsedPrefix, StringComparison.Ordinal) ? target[NonParsedPrefix.Length..] : target;
    }

    /// <summary>Removes the junction itself. The folder it points at is untouched.</summary>
    /// <exception cref="IOException"><paramref name="path"/> is not a junction.</exception>
    public static void Delete(string path)
    {
        if (!IsJunction(path))
        {
            throw new IOException($"{path} is not a junction.");
        }

        // Without recursion this removes only the reparse point.
        Directory.Delete(path, recursive: false);
    }

    /// <summary>
    /// Points <paramref name="link"/> at <paramref name="target"/>, creating it if missing. The new junction is
    /// built beside it as "link.next" first, so a failure before the swap leaves the old one in place.
    /// </summary>
    /// <exception cref="IOException"><paramref name="link"/> exists and is not a junction.</exception>
    public static void Repoint(string link, string target)
    {
        link = Path.TrimEndingDirectorySeparator(Path.GetFullPath(link));
        if (Path.Exists(link) && !IsJunction(link))
        {
            throw new IOException($"{link} is a folder, not a junction. Move it away first.");
        }

        var next = link + ".next";
        if (Path.Exists(next))
        {
            Delete(next);
        }

        Create(next, target);
        if (Path.Exists(link))
        {
            Delete(link);
        }

        Directory.Move(next, link);
    }

    /// <summary>REPARSE_DATA_BUFFER for IO_REPARSE_TAG_MOUNT_POINT: both names, each followed by a null.</summary>
    private static byte[] MountPointBuffer(string substituteName, string printName)
    {
        var substitute = Encoding.Unicode.GetBytes(substituteName);
        var print = Encoding.Unicode.GetBytes(printName);
        var pathBufferLength = substitute.Length + 2 + print.Length + 2;
        const int MountPointHeaderLength = 8;

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(ReparseTagMountPoint);
            writer.Write(checked((ushort)(MountPointHeaderLength + pathBufferLength)));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(checked((ushort)substitute.Length));
            writer.Write(checked((ushort)(substitute.Length + 2)));
            writer.Write(checked((ushort)print.Length));
            writer.Write(substitute);
            writer.Write((ushort)0);
            writer.Write(print);
            writer.Write((ushort)0);
        }

        return stream.ToArray();
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        public static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, byte[] inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
    }
}
