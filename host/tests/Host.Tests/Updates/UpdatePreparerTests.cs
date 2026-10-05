using System.Text;
using System.Text.Json;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Updates;

namespace HyperHarbor.Host.Tests.Updates;

/// <summary>The order of checks: SHA-256, then signatures, then the self-test; a failure stops the rest.</summary>
public sealed class UpdatePreparerTests : IDisposable
{
    private readonly string _data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeReleaseServer _server = new();
    private readonly RecordingSelfTest _selfTest = new();
    private readonly RecordingVerifier _signatures = new();
    private readonly UpdatePreparer _preparer;

    public UpdatePreparerTests()
    {
        File.WriteAllText(Path.Combine(_data, "users.json"), "users");
        var options = new UpdateOptions();
        _preparer = new UpdatePreparer(options, Releases.Downloader(_server, options), _signatures, new SelfTestGate(_data, _selfTest, TimeSpan.FromSeconds(30)), _data);
        _server.Serve(Releases.PackageUrl, Releases.Package);
    }

    public void Dispose() => Directory.Delete(_data, recursive: true);

    [Fact]
    public async Task Check_ReadsTheChannelsManifest_AndDecides()
    {
        _server.Serve(Releases.ManifestUrl, Encoding.UTF8.GetBytes(Releases.Manifest()));

        var (manifest, decision) = await _preparer.CheckAsync("stable", SemanticVersion.Parse("1.1.0"), [], CancellationToken.None);

        Assert.Equal("1.2.0", manifest.Version);
        Assert.Equal(UpdateDecisionKind.Available, decision.Kind);
    }

    [Fact]
    public async Task Check_RejectsAManifestForAnotherChannel()
    {
        _server.Serve(Releases.ManifestUrl, Encoding.UTF8.GetBytes(Releases.Manifest(channel: "beta")));

        await Assert.ThrowsAsync<UpdateRejectedException>(() => _preparer.CheckAsync("stable", SemanticVersion.Parse("1.1.0"), [], CancellationToken.None));
    }

    [Fact]
    public async Task Prepare_RunsEveryCheck_AndKeepsThePackage()
    {
        var prepared = await _preparer.PrepareAsync(Manifest(), CancellationToken.None);

        Assert.Equal(Releases.Package, File.ReadAllBytes(prepared.ExecutablePath));
        Assert.Equal([prepared.ExecutablePath], _signatures.Verified);
        Assert.Equal([prepared.ExecutablePath], _selfTest.Executables);
        Assert.True(prepared.SelfTest.Passed);
    }

    [Fact]
    public async Task Prepare_WithAHashMismatch_NeverRunsTheSelfTest()
    {
        await Assert.ThrowsAsync<UpdateRejectedException>(() => _preparer.PrepareAsync(Manifest(sha256: new string('0', 64)), CancellationToken.None));

        Assert.Empty(_signatures.Verified);
        Assert.Empty(_selfTest.Executables);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_preparer.DownloadFolder));
    }

    [Fact]
    public async Task Prepare_WithARejectedSignature_NeverRunsTheSelfTest_AndDeletesThePackage()
    {
        _signatures.Accept = false;

        var error = await Assert.ThrowsAsync<UpdateRejectedException>(() => _preparer.PrepareAsync(Manifest(), CancellationToken.None));

        Assert.Contains("signature", error.Message, StringComparison.Ordinal);
        Assert.Empty(_selfTest.Executables);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_preparer.DownloadFolder));
    }

    [Fact]
    public async Task Prepare_WhenTheSelfTestFails_DeletesThePackage()
    {
        _selfTest.Succeed = false;

        var error = await Assert.ThrowsAsync<UpdateRejectedException>(() => _preparer.PrepareAsync(Manifest(), CancellationToken.None));

        Assert.Contains("self-test", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_preparer.DownloadFolder));
    }

    [Fact]
    public async Task TheDefaultVerifier_IsTheMarkedSigningHook_AndAcceptsOnTheHashAlone()
    {
        var check = await new UnsignedPackageVerifier().VerifyAsync("any.exe", Manifest(), CancellationToken.None);

        Assert.True(check.Accepted);
        Assert.Contains("not checked yet", check.Detail, StringComparison.Ordinal);
    }

    private static UpdateManifest Manifest(string? sha256 = null) =>
        UpdateManifest.Parse(Encoding.UTF8.GetBytes(Releases.Manifest(sha256: sha256)), new UpdateOptions());

    private sealed class RecordingVerifier : IPackageSignatureVerifier
    {
        public bool Accept { get; set; } = true;

        public List<string> Verified { get; } = [];

        public Task<SignatureCheck> VerifyAsync(string packagePath, UpdateManifest manifest, CancellationToken cancellationToken)
        {
            Verified.Add(packagePath);
            return Task.FromResult(new SignatureCheck(Accept, Accept ? "fine" : "not signed by HyperHarbor"));
        }
    }

    private sealed class RecordingSelfTest : ISelfTestProcess
    {
        public bool Succeed { get; set; } = true;

        public List<string> Executables { get; } = [];

        public Task<(int ExitCode, string StandardError)?> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Executables.Add(executable);
            var result = new SelfTestResult("1.2.0", Succeed, [new SelfTestCheck("stores", Succeed, Succeed ? "ok" : "broken")]);
            File.WriteAllBytes(arguments[2], JsonSerializer.SerializeToUtf8Bytes(result, SelfTestResult.JsonOptions));
            return Task.FromResult<(int, string)?>((Succeed ? 0 : 1, string.Empty));
        }
    }
}
