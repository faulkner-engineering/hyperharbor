using System.Security.Cryptography;
using System.Text;
using HyperHarbor.Host.Core.Backup.Kopia;

namespace HyperHarbor.Host.Tests.Backup;

public sealed class KopiaToolLocatorTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("pretend this is kopia.exe");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class Payloads(byte[]? bytes, string? hash) : IToolPayload
    {
        public KopiaToolInfo? Kopia => hash is null ? null : new KopiaToolInfo("0.0.1", hash);

        public Stream? OpenKopia() => bytes is null ? null : new MemoryStream(bytes);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private string Tools => Path.Combine(_folder, "tools");

    [Fact]
    public void Ensure_ExtractsTheEmbeddedFile_AndVerifiesIt()
    {
        var path = new KopiaToolLocator(new Payloads(Payload, Sha(Payload)), Tools, _ => null).Ensure();

        Assert.Equal(Path.Combine(Tools, "kopia.exe"), path);
        Assert.Equal(Payload, File.ReadAllBytes(path));
    }

    [Fact]
    public void Ensure_ReplacesAFileThatDoesNotMatchThePinnedHash()
    {
        Directory.CreateDirectory(Tools);
        File.WriteAllText(Path.Combine(Tools, "kopia.exe"), "tampered");

        var path = new KopiaToolLocator(new Payloads(Payload, Sha(Payload)), Tools, _ => null).Ensure();

        Assert.Equal(Payload, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Tools, "*.tmp"));
    }

    [Fact]
    public void Ensure_RefusesAPayloadThatDoesNotMatchItsOwnPin()
    {
        var locator = new KopiaToolLocator(new Payloads(Payload, new string('0', 64)), Tools, _ => null);

        Assert.Throws<KopiaUnavailableException>(locator.Ensure);
    }

    [Fact]
    public void Ensure_WithoutAPayload_UsesTheDevelopmentVariable_OrExplainsWhy()
    {
        var dev = Path.Combine(_folder, "dev-kopia.exe");
        File.WriteAllText(dev, "x");

        Assert.Equal(dev, new KopiaToolLocator(new Payloads(null, null), Tools, n => n == "HH_KOPIA_EXE" ? dev : null).Ensure());
        var error = Assert.Throws<KopiaUnavailableException>(new KopiaToolLocator(new Payloads(null, null), Tools, _ => null).Ensure);
        Assert.Contains("does not include Kopia", error.Message);
    }

    [Fact]
    public void ParseManifest_ReadsTheRepositoryFile_AndRejectsGarbage()
    {
        var real = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tools", "tools.json");
        if (File.Exists(real))
        {
            using var stream = File.OpenRead(real);
            var info = KopiaToolLocator.ParseManifest(stream);
            Assert.Equal(64, info!.ExeSha256.Length);
        }

        Assert.Null(KopiaToolLocator.ParseManifest(new MemoryStream("{}"u8.ToArray())));
        Assert.Null(KopiaToolLocator.ParseManifest(new MemoryStream("nope"u8.ToArray())));
    }
}
