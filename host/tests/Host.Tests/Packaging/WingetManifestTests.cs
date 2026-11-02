using System.Diagnostics;
using System.Security.Cryptography;
using HyperHarbor.Host.Service.Installation;
using HyperHarbor.Host.Tests.Profiles;
using Json.Schema;
using YamlDotNet.Serialization;

namespace HyperHarbor.Host.Tests.Packaging;

/// <summary>
/// packaging\winget\New-WingetManifests.ps1 with stand-in installers: the six manifests it writes are valid against the
/// winget-cli schemas kept beside it, and say what the installers do (scope, switches, the Uninstall key winget looks
/// for). An end-to-end test (scripts\e2e.ps1): it runs Windows PowerShell. Where winget is installed, winget validate
/// checks them too; CI's runner may not have it, so there the schemas are the check.
/// </summary>
[Trait("Category", "E2E")]
public sealed class WingetManifestTests : IDisposable
{
    private const string Version = "9.8.7";
    private const string Release = "https://github.com/faulkner-engineering/hyperharbor/releases";
    private static readonly string Repository = FindRepository();

    /// <summary>Loaded once: each schema has an $id, and JsonSchema.Net refuses to register one twice.</summary>
    private static readonly Lazy<Dictionary<string, JsonSchema>> Schemas = new(() => new[] { "version", "installer", "defaultLocale" }.ToDictionary(
        type => type,
        type => JsonSchema.FromFile(Path.Combine(Repository, "packaging", "winget", "schemas", "1.12.0", $"manifest.{type}.1.12.0.json"))));
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [E2EFact]
    public void Manifests_AreValid_AndDescribeBothInstallers()
    {
        var hostInstaller = StandIn("host.exe", 1);
        var clientInstaller = StandIn("client.exe", 2);
        var output = Path.Combine(_root, "out");

        var (exitCode, log) = Generate("-Version", Version, "-HostInstaller", hostInstaller, "-ClientInstaller", clientInstaller, "-ReleaseDate", "2026-11-02", "-OutputDirectory", output);

        Assert.True(exitCode == 0, log);
        var host = Read(output, "Host");
        var client = Read(output, "Client");

        foreach (var manifest in host.Values.Concat(client.Values))
        {
            Assert.Equal(Version, manifest["PackageVersion"]);
            Assert.Equal("1.12.0", manifest["ManifestVersion"]);
            Assert.NotEqual("FaulknerEngineering.HyperHarbor", manifest["PackageIdentifier"]);
        }

        Assert.All(host.Values, manifest => Assert.Equal("FaulknerEngineering.HyperHarbor.Host", manifest["PackageIdentifier"]));
        Assert.All(client.Values, manifest => Assert.Equal("FaulknerEngineering.HyperHarbor.Client", manifest["PackageIdentifier"]));

        var hostSetup = host["installer"];
        Assert.Equal("exe", hostSetup["InstallerType"]);
        Assert.Equal("machine", hostSetup["Scope"]);
        Assert.Equal("elevatesSelf", hostSetup["ElevationRequirement"]);
        Assert.Equal("install --quiet", Map(hostSetup["InstallerSwitches"])["Silent"]);
        Assert.Equal("install --quiet", Map(hostSetup["InstallerSwitches"])["SilentWithProgress"]);
        Assert.Equal(UninstallEntry.ProductCode, hostSetup["ProductCode"]);
        Assert.Equal("2026-11-02", hostSetup["ReleaseDate"]);
        var hostFile = Map(List(hostSetup["Installers"]).Single());
        Assert.Equal($"{Release}/download/v{Version}/HyperHarbor-Host-{Version}.exe", hostFile["InstallerUrl"]);
        Assert.Equal(Sha256(hostInstaller), hostFile["InstallerSha256"]);
        Assert.Equal("x64", hostFile["Architecture"]);

        var clientSetup = client["installer"];
        Assert.Equal("nullsoft", clientSetup["InstallerType"]);
        Assert.Equal("user", clientSetup["Scope"]);
        Assert.Equal("/S", Map(clientSetup["InstallerSwitches"])["Silent"]);
        Assert.Equal("HyperHarbor", clientSetup["ProductCode"]);
        var clientFile = Map(List(clientSetup["Installers"]).Single());
        Assert.Equal($"{Release}/download/v{Version}/HyperHarbor_{Version}_x64-setup.exe", clientFile["InstallerUrl"]);
        Assert.Equal(Sha256(clientInstaller), clientFile["InstallerSha256"]);

        Assert.Equal("HyperHarbor Host", host["defaultLocale"]["PackageName"]);
        Assert.Equal("HyperHarbor Client", client["defaultLocale"]["PackageName"]);
        Assert.False(host["defaultLocale"].ContainsKey("Moniker"));
        Assert.Equal("hyperharbor", client["defaultLocale"]["Moniker"]);
        foreach (var locale in new[] { host["defaultLocale"], client["defaultLocale"] })
        {
            Assert.Equal("Faulkner Engineering LLC", locale["Publisher"]);
            Assert.Equal("https://faulkner.engineering", locale["PublisherUrl"]);
            Assert.Equal("https://github.com/faulkner-engineering/hyperharbor/issues", locale["PublisherSupportUrl"]);
            Assert.Equal("https://faulkner.engineering/hyperharbor", locale["PackageUrl"]);
            Assert.Equal("Apache-2.0", locale["License"]);
            Assert.Equal($"{Release}/tag/v{Version}", locale["ReleaseNotesUrl"]);
        }

        // winget uninstall keeps the data, and the host's notes say so.
        Assert.Contains(@"%ProgramData%\HyperHarbor", (string)host["defaultLocale"]["InstallationNotes"]);

        if (FindWinget() is { } winget)
        {
            var version = Run(winget, "--version").Log.Trim();
            foreach (var package in new[] { "Host", "Client" })
            {
                var (validated, validation) = Run(winget, "validate", "--manifest", Folder(output, package), "--disable-interactivity");
                Assert.True(validated == 0 || OnlySchemaHeaderWarnings(validation), $"winget {version}: {validation}");
            }
        }
    }

    /// <summary>
    /// A winget older than the 1.12.0 schema (CI's runner image) validates the manifests but warns that the
    /// yaml-language-server header "does not match the expected pattern", and exits non-zero. The header is the one
    /// winget-pkgs' own 1.12.0 manifests carry, and the schemas above check the content, so only that warning is accepted.
    /// </summary>
    internal static bool OnlySchemaHeaderWarnings(string validation)
    {
        // winget 1.11 writes "Manifest Warning: ..."; match the word anywhere so another prefix still counts as a warning.
        var warnings = validation.Split('\n').Select(line => line.Trim()).Where(line => line.Contains("Warning:", StringComparison.OrdinalIgnoreCase)).ToList();
        return validation.Contains("Manifest validation succeeded with warnings", StringComparison.Ordinal)
            && warnings.Count > 0
            && warnings.All(line => line.Contains("The schema header URL does not match the expected pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlySchemaHeaderWarnings_AcceptsTheOlderWingetHeaderWarning_AndNothingElse()
    {
        // As winget 1.11.510 on CI's runner writes it.
        const string header = "Manifest Warning: The schema header URL does not match the expected pattern. Value: # yaml-language-server: $schema=https://aka.ms/winget-manifest.version.1.12.0.schema.json Line: 1, Column: 25 File: x.yaml";
        Assert.True(OnlySchemaHeaderWarnings($"Manifest validation succeeded with warnings.\r\n{header}\r\n{header}\r\n"));
        Assert.False(OnlySchemaHeaderWarnings($"Manifest validation succeeded with warnings.\r\n{header}\r\nManifest Warning: Installer type exe requires a silent switch.\r\n"));
        Assert.False(OnlySchemaHeaderWarnings($"Manifest validation failed.\r\n{header}\r\n"));
        Assert.False(OnlySchemaHeaderWarnings("Manifest validation succeeded with warnings.\r\n"));
    }

    [E2EFact]
    public void Prereleases_AreRefused()
    {
        var (exitCode, log) = Generate("-Version", "9.8.7-beta.1", "-HostInstaller", StandIn("host.exe", 1), "-ClientInstaller", StandIn("client.exe", 2), "-OutputDirectory", Path.Combine(_root, "out"));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Prereleases never go to winget", log);
        Assert.False(Directory.Exists(Path.Combine(_root, "out")));
    }

    /// <summary>Reads one package's three manifests by type, each checked against its schema.</summary>
    private static Dictionary<string, Dictionary<object, object>> Read(string output, string package)
    {
        var folder = Folder(output, package);
        var identifier = $"FaulknerEngineering.HyperHarbor.{package}";
        var files = new Dictionary<string, string>
        {
            ["version"] = $"{identifier}.yaml",
            ["installer"] = $"{identifier}.installer.yaml",
            ["defaultLocale"] = $"{identifier}.locale.en-US.yaml",
        };
        Assert.Equal(files.Values.Order(), Directory.GetFiles(folder).Select(Path.GetFileName).Order());

        var manifests = new Dictionary<string, Dictionary<object, object>>();
        foreach (var (type, file) in files)
        {
            var yaml = File.ReadAllText(Path.Combine(folder, file));
            Assert.DoesNotContain("\r", yaml);
            var result = Schemas.Value[type].Evaluate(ProfileYamlTests.ToJson(yaml), new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(result.IsValid, $"{file}: " + string.Join("; ", result.Details?.Where(detail => detail.Errors is not null).SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}")) ?? []));

            var manifest = Map(new DeserializerBuilder().Build().Deserialize<object>(yaml));
            Assert.Equal(type, manifest["ManifestType"]);
            manifests[type] = manifest;
        }

        return manifests;
    }

    private static string Folder(string output, string package) =>
        Path.Combine(output, "manifests", "f", "FaulknerEngineering", "HyperHarbor", package, Version);

    private (int ExitCode, string Log) Generate(params string[] arguments) =>
        Run("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Repository, "packaging", "winget", "New-WingetManifests.ps1"), .. arguments]);

    private static (int ExitCode, string Log) Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // CI runs the tests from PowerShell 7, whose module path Windows PowerShell would inherit and then fail to load
        // its own modules (Get-FileHash, Invoke-WebRequest); without the variable it uses its defaults.
        start.Environment.Remove("PSModulePath");

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error.Result);
    }

    private string StandIn(string name, byte fill)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, 4096).ToArray());
        return path;
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static Dictionary<object, object> Map(object value) => Assert.IsType<Dictionary<object, object>>(value);

    private static List<object> List(object value) => Assert.IsType<List<object>>(value);

    private static string? FindWinget()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(path) ? path : null;
    }

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "packaging", "winget")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository (packaging\\winget) is not above the test output.");
    }
}
