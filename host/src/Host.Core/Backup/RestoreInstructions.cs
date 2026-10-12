using System.Text;
using HyperHarbor.Host.Core.Backup.Kopia;

namespace HyperHarbor.Host.Core.Backup;

/// <summary>
/// The RESTORE.md written into every backup folder: the plain Kopia commands that recover the data without
/// HyperHarbor. It never contains the passphrase; it says where the user keeps it.
/// </summary>
public static class RestoreInstructions
{
    public const string FileName = "RESTORE.md";

    public static string Build(BackupTarget target, DateTimeOffset writtenAt)
    {
        var text = new StringBuilder();
        text.AppendLine("# Restoring this backup without HyperHarbor");
        text.AppendLine();
        text.AppendLine($"Written by HyperHarbor on {writtenAt:yyyy-MM-dd} for the backup target \"{target.Name}\".");
        text.AppendLine();
        text.AppendLine("This folder is a set of [Kopia](https://kopia.io) repositories. Kopia is open source, so the data can be");
        text.AppendLine("recovered with the plain commands below even if HyperHarbor is gone.");
        text.AppendLine();
        text.AppendLine("## The passphrase");
        text.AppendLine();
        text.AppendLine("Every repository is encrypted. **The passphrase (the recovery key HyperHarbor showed you when you set up this");
        text.AppendLine("target) is in your password manager.** It is not stored in this folder. Without it nothing here can be read,");
        text.AppendLine("and nobody, including the HyperHarbor authors, can recover it.");
        text.AppendLine();
        text.AppendLine("## What is here");
        text.AppendLine();
        text.AppendLine("- `vm`: one snapshot source per virtual machine. Each snapshot holds the virtual disk (`disk-0.vhdx`) and a");
        text.AppendLine("  `manifest.json` that describes the VM (generation, processors, memory, Secure Boot, TPM, network, GPU).");
        text.AppendLine("- `host`: the host's own state (pairings, certificates, settings, profiles), in a separate repository.");
        text.AppendLine();
        text.AppendLine("## Commands");
        text.AppendLine();
        text.AppendLine("Install Kopia (`winget install kopia.kopia`), then connect. You are asked for the passphrase.");
        text.AppendLine();
        text.AppendLine("```powershell");
        foreach (var folder in new[] { "vm", "host" })
        {
            text.AppendLine($"# {folder} repository");
            text.AppendLine(ConnectCommand(target, folder));
        }

        text.AppendLine();
        text.AppendLine("kopia snapshot list --all");
        text.AppendLine("kopia snapshot restore <snapshot id> D:\\Restored\\MyVm");
        text.AppendLine("```");
        text.AppendLine();
        text.AppendLine("To create the VM again by hand: restore the snapshot, then in Hyper-V Manager create a VM of the generation");
        text.AppendLine("in `manifest.json` and attach the restored `disk-0.vhdx`. HyperHarbor does this for you: use Restore from");
        text.AppendLine("backup in the HyperHarbor host window or client.");
        text.AppendLine();
        text.AppendLine("## Syncing this folder");
        text.AppendLine();
        text.AppendLine("A local backup folder can sit inside Proton Drive, OneDrive, or a NAS share. Kopia uploads only changed chunks,");
        text.AppendLine("so each backup after the first adds little to the sync. Let the sync client finish before you restore from a");
        text.AppendLine("copy on another PC.");
        return text.ToString();
    }

    private static string ConnectCommand(BackupTarget target, string folder) => target.Kind switch
    {
        BackupTargetKind.Local => $"kopia repository connect filesystem --path \"{Path.Combine(target.Local?.Path ?? "<this folder>", folder)}\"",
        BackupTargetKind.S3 =>
            $"kopia repository connect s3 --bucket {target.S3?.Bucket} --endpoint {target.S3?.Endpoint} --prefix {Prefix(target.S3?.Prefix, folder)} --access-key <access key> --secret-access-key <secret key>",
        BackupTargetKind.Azure =>
            $"kopia repository connect azure --container {target.Azure?.Container} --storage-account {target.Azure?.Account} --prefix {Prefix(target.Azure?.Prefix, folder)} --storage-key <storage key>",
        _ => throw new NotSupportedException(),
    };

    private static string Prefix(string? prefix, string folder) =>
        string.IsNullOrWhiteSpace(prefix) ? $"{folder}/" : $"{prefix.Trim('/')}/{folder}/";
}

/// <summary>Writes RESTORE.md for a local target. Remote targets get the same text uploaded by the backup run.</summary>
public static class RestoreInstructionsWriter
{
    public static string? WriteForLocal(BackupTarget target, DateTimeOffset now)
    {
        if (target.Kind != BackupTargetKind.Local || target.Local is null)
        {
            return null;
        }

        Directory.CreateDirectory(target.Local.Path);
        var path = Path.Combine(target.Local.Path, RestoreInstructions.FileName);
        File.WriteAllText(path, RestoreInstructions.Build(target, now));
        return path;
    }
}
