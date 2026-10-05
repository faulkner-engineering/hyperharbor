using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Tests.HyperV;

namespace HyperHarbor.Host.Tests.Installation;

/// <summary>HyperHarbor.Host.exe --self-test run for real, through the gate and its process runner.</summary>
public sealed class SelfTestCommandTests : IDisposable
{
    private static readonly string Executable = Path.Combine(AppContext.BaseDirectory, InstallLayout.ExecutableName);
    private static readonly SemanticVersion BuiltVersion = SemanticVersion.Parse(
        System.Diagnostics.FileVersionInfo.GetVersionInfo(Executable).ProductVersion!);

    private readonly string _data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly SelfTestGate _gate;

    public SelfTestCommandTests()
    {
        var users = new UserStore(_data);
        new PairedDeviceStore(_data, users).Add(users.GetOrCreateDefault().UserId, "Laptop", new string('a', 64), DateTimeOffset.UtcNow);
        new HostCertificateStore(_data, Environment.MachineName).GetOrCreate().Dispose();
        _gate = new SelfTestGate(_data, new SelfTestProcess(), SelfTestGate.DefaultTimeout);
    }

    public void Dispose() => Directory.Delete(_data, recursive: true);

    [HyperVFact]
    public async Task ThisBuild_PassesAgainstACopyOfItsData()
    {
        var outcome = await _gate.RunAsync(Executable, BuiltVersion, CancellationToken.None);

        Assert.True(outcome.Passed, outcome.Detail);
        Assert.Equal(["dataFormat", "stores", "start", "tls", "inventory"], outcome.Result!.Checks.Select(check => check.Name));
        Assert.Contains("1 paired device", outcome.Result.Checks.Single(check => check.Name == "stores").Detail, StringComparison.Ordinal);
        Assert.Null(DataFormat.Read(_data));
    }

    [Fact]
    public async Task DataFromANewerVersion_FailsBeforeTheHostStarts()
    {
        File.WriteAllText(Path.Combine(_data, DataFormat.FileName), """{ "format": 99 }""");

        var outcome = await _gate.RunAsync(Executable, BuiltVersion, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Contains("newer version", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableStore_FailsTheStoresCheck()
    {
        File.WriteAllText(Path.Combine(_data, "paired-devices.json"), "{ this is not json");

        var outcome = await _gate.RunAsync(Executable, BuiltVersion, CancellationToken.None);

        Assert.False(outcome.Passed);
    }
}
