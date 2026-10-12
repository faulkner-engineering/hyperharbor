using System.Reflection;
using HyperHarbor.Host.Core.Backup.Kopia;

namespace HyperHarbor.Host.Service.Backup;

/// <summary>The tools carried inside HyperHarbor.Host.exe (see Host.Service.csproj and scripts/fetch-tools.ps1).</summary>
public sealed class EmbeddedToolPayload(Assembly? assembly = null) : IToolPayload
{
    private const string KopiaResource = "HyperHarbor.Host.tools.kopia.exe";
    private const string ManifestResource = "HyperHarbor.Host.tools.json";

    private readonly Assembly _assembly = assembly ?? typeof(EmbeddedToolPayload).Assembly;

    public KopiaToolInfo? Kopia
    {
        get
        {
            using var manifest = _assembly.GetManifestResourceStream(ManifestResource);
            return manifest is null ? null : KopiaToolLocator.ParseManifest(manifest);
        }
    }

    public Stream? OpenKopia() => _assembly.GetManifestResourceStream(KopiaResource);
}
