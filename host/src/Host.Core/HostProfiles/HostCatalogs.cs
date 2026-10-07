using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HyperHarbor.Host.Core.HostProfiles;

/// <summary>A name pattern with the reason it is protected. catalogs/host-startup.yaml and host-guards.yaml.</summary>
public sealed class PatternEntry
{
    public string Pattern { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>A program a host profile can uninstall. catalogs/host-programs.yaml.</summary>
public sealed class HostProgramEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Match { get; set; } = [];
    public List<string> Processes { get; set; } = [];

    /// <summary>standard (the program's quiet uninstall command) or oneDrive (OneDriveSetup.exe /uninstall).</summary>
    public string Uninstall { get; set; } = "standard";

    public string? Note { get; set; }
}

/// <summary>A Steam title that needs Gaming Services and the Xbox Identity Provider.</summary>
public sealed class GameEntry
{
    public long AppId { get; set; }
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>What a guard keeps when its condition holds.</summary>
public sealed class GuardKeep
{
    public List<string> KeepAppx { get; set; } = [];
    public List<string> KeepServices { get; set; } = [];
}

/// <summary>catalogs/host-guards.yaml.</summary>
public sealed class HostGuardsCatalog
{
    public List<PatternEntry> ProtectedServices { get; set; } = [];
    public List<GameEntry> Games { get; set; } = [];
    public GuardKeep GamingServices { get; set; } = new();
    public GuardKeep Spooler { get; set; } = new();
    public GuardKeep Photos { get; set; } = new();
    public List<string> ImageViewers { get; set; } = [];
    public List<string> VirtualPrinterNames { get; set; } = [];
    public List<string> VirtualPrinterPorts { get; set; } = [];
}

/// <summary>
/// The catalogs of the Lean host action: the startup allowlist, the program list, and the guards. Embedded from the
/// repository's catalogs folder like <see cref="Profiles.Catalogs"/>; loaded once and read-only.
/// </summary>
public sealed class HostCatalogs
{
    private static readonly Lazy<HostCatalogs> Embedded = new(() => new HostCatalogs(Read));

    internal HostCatalogs(Func<string, string> read)
    {
        var yaml = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        StartupAllowlist = yaml.Deserialize<List<PatternEntry>>(read("host-startup.yaml"));
        Programs = yaml.Deserialize<List<HostProgramEntry>>(read("host-programs.yaml"));
        Guards = yaml.Deserialize<HostGuardsCatalog>(read("host-guards.yaml"));
        ProgramIndex = Programs.ToDictionary(program => program.Id, StringComparer.Ordinal);
    }

    public static HostCatalogs Default => Embedded.Value;

    /// <summary>Startup entry names a profile never disables.</summary>
    public IReadOnlyList<PatternEntry> StartupAllowlist { get; }

    public IReadOnlyList<HostProgramEntry> Programs { get; }

    public IReadOnlyDictionary<string, HostProgramEntry> ProgramIndex { get; }

    public HostGuardsCatalog Guards { get; }

    private static string Read(string file)
    {
        var name = "HyperHarbor.Catalogs." + file;
        using var stream = typeof(HostCatalogs).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is missing from the host.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
