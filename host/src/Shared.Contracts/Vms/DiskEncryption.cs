namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>BitLocker's conversion state of one guest volume. Schema: GuestEncryptionState.</summary>
public enum GuestEncryptionState
{
    Decrypted,
    Encrypted,
    Encrypting,
    Decrypting,
    Paused,
}

/// <summary>One volume of a Windows guest. Schema: GuestVolumeEncryption.</summary>
/// <param name="MountPoint">The drive letter, for example "C:", or the device path of a volume without one.</param>
/// <param name="OperatingSystem">The volume Windows runs from.</param>
/// <param name="EncryptedPercent">How much of the volume is encrypted; it falls to 0 while decrypting.</param>
public sealed record GuestVolumeEncryption(string MountPoint, bool OperatingSystem, GuestEncryptionState State, int EncryptedPercent);

/// <summary>BitLocker's state in a Windows guest. Schema: VmDiskEncryption.</summary>
/// <param name="Problems">Volumes whose decryption could not be started, and why; empty when reading the state.</param>
public sealed record VmDiskEncryption(Guid VmId, IReadOnlyList<GuestVolumeEncryption> Volumes, IReadOnlyList<string> Problems);
