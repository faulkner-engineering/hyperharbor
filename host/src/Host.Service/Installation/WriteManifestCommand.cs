using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Updates;
using HyperHarbor.Shared.Contracts;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>
/// HyperHarbor.Host.exe write-update-manifest &lt;output&gt; [--channel stable|beta] [--minimum-update-from X.Y.Z]
/// [--release-base-url URL]: writes latest.json for a release of this executable, with its size and SHA-256.
/// Used by scripts\package.ps1 and the release workflow, so the manifest is written by the code that reads it.
/// A prerelease version (1.2.0-beta.1) defaults to the beta channel.
/// </summary>
internal static class WriteManifestCommand
{
    public const string DefaultReleaseBaseUrl = "https://github.com/faulkner-engineering/hyperharbor/releases";

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: HyperHarbor.Host.exe write-update-manifest <output> [--channel stable|beta] [--minimum-update-from X.Y.Z] [--release-base-url URL]");
            return 2;
        }

        var version = HostVersion.Current;
        var channel = version.Prerelease is null ? UpdateOptions.StableChannel : "beta";
        string? minimum = null;
        var baseUrl = DefaultReleaseBaseUrl;
        for (var i = 1; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--channel":
                    channel = Value();
                    break;
                case "--minimum-update-from":
                    minimum = SemanticVersion.Parse(Value()).ToString();
                    break;
                case "--release-base-url":
                    baseUrl = Value();
                    break;
                default:
                    throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        var manifest = UpdateManifest.Create(
            Environment.ProcessPath!, version, channel, baseUrl, minimum, DataFormat.Current, ContractInfo.ApiVersion, DateTimeOffset.UtcNow);
        var output = Path.GetFullPath(args[0]);
        File.WriteAllBytes(output, manifest.ToJson());

        // Read it back through the rules every host applies, so a release never ships a manifest it would reject.
        UpdateManifest.Parse(File.ReadAllBytes(output), new UpdateOptions());
        Console.WriteLine($"Wrote {output} for version {version} on the {channel} channel ({manifest.HostPackage.Size} bytes, SHA-256 {manifest.HostPackage.Sha256}).");
        return 0;
    }
}
