using System.Security.Cryptography;
using System.Text.Json;
using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Tests.Installation;

/// <summary>The gate with a fake process standing in for the new version's --self-test run.</summary>
public sealed class SelfTestGateTests : IDisposable
{
    private static readonly SemanticVersion Expected = SemanticVersion.Parse("1.2.0");
    private readonly string _data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeSelfTest _process = new();
    private readonly SelfTestGate _gate;

    public SelfTestGateTests()
    {
        File.WriteAllText(Path.Combine(_data, "users.json"), "users");
        File.WriteAllText(Path.Combine(_data, "paired-devices.json"), "devices");
        Directory.CreateDirectory(Path.Combine(_data, "logs"));
        _gate = new SelfTestGate(_data, _process, TimeSpan.FromSeconds(30));
    }

    public void Dispose() => Directory.Delete(_data, recursive: true);

    [Fact]
    public async Task Passes_WhenTheRunSucceedsForTheExpectedVersion()
    {
        var outcome = await _gate.RunAsync("new.exe", Expected, CancellationToken.None);

        Assert.True(outcome.Passed, outcome.Detail);
        var call = Assert.Single(_process.Calls);
        Assert.Equal("new.exe", call.Executable);
        Assert.Equal(SelfTestGate.Switch, call.Arguments[0]);
    }

    [Fact]
    public async Task RunsAgainstACopy_AndNeverTouchesTheRealData()
    {
        var before = Snapshot(_data);
        _process.OnRun = copy =>
        {
            // The new version migrates its copy.
            Assert.NotEqual(_data, copy);
            Assert.Equal("users", File.ReadAllText(Path.Combine(copy, "users.json")));
            File.WriteAllText(Path.Combine(copy, "users.json"), "migrated");
            File.WriteAllText(Path.Combine(copy, "data-format.json"), """{ "format": 2 }""");
        };

        await _gate.RunAsync("new.exe", Expected, CancellationToken.None);

        Assert.Equal(before, Snapshot(_data));
    }

    [Fact]
    public async Task DeletesTheCopyAndTheResult_WhetherItPassesOrFails()
    {
        await _gate.RunAsync("new.exe", Expected, CancellationToken.None);
        _process.ExitCode = 1;
        await _gate.RunAsync("new.exe", Expected, CancellationToken.None);

        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_data, DataBackup.UpdateFolderName, "selftest")));
    }

    [Fact]
    public async Task Fails_OnANonZeroExitCode_WithTheErrorItPrinted()
    {
        _process.ExitCode = 3;
        _process.Result = null;
        _process.StandardError = "Unhandled exception. DataFormatTooNewException: format 7";

        var outcome = await _gate.RunAsync("new.exe", Expected, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Contains("code 3", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("DataFormatTooNewException", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_WhenItDoesNotFinishInTime()
    {
        _process.TimesOut = true;

        var outcome = await _gate.RunAsync("new.exe", Expected, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Contains("did not finish", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_WhenTheResultIsMissingOrMalformed()
    {
        _process.Result = null;
        Assert.False((await _gate.RunAsync("new.exe", Expected, CancellationToken.None)).Passed);

        _process.RawResult = "{ not json";
        Assert.False((await _gate.RunAsync("new.exe", Expected, CancellationToken.None)).Passed);
    }

    [Fact]
    public async Task Fails_WhenAVersionOtherThanTheDownloadedOneAnswers()
    {
        _process.Result = Succeeded("1.1.0");

        var outcome = await _gate.RunAsync("new.exe", Expected, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Contains("1.1.0", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_WhenACheckFailed_EvenWithExitCodeZero_AndNamesIt()
    {
        _process.Result = new SelfTestResult("1.2.0", false, [new("stores", true, "ok"), new("tls", false, "The host did not serve its own certificate.")]);

        var outcome = await _gate.RunAsync("new.exe", Expected, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Contains("tls: The host did not serve its own certificate.", outcome.Detail, StringComparison.Ordinal);
    }

    private static SelfTestResult Succeeded(string version) => new(version, true, [new("stores", true, "ok")]);

    private static Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + DataBackup.UpdateFolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(folder, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private sealed class FakeSelfTest : ISelfTestProcess
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public int ExitCode { get; set; }

        public string StandardError { get; set; } = string.Empty;

        public bool TimesOut { get; set; }

        public SelfTestResult? Result { get; set; } = Succeeded("1.2.0");

        public string? RawResult { get; set; }

        public Action<string>? OnRun { get; set; }

        public Task<(int ExitCode, string StandardError)?> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add((executable, arguments));
            OnRun?.Invoke(arguments[1]);
            if (TimesOut)
            {
                return Task.FromResult<(int, string)?>(null);
            }

            if (RawResult is not null)
            {
                File.WriteAllText(arguments[2], RawResult);
            }
            else if (Result is not null)
            {
                File.WriteAllBytes(arguments[2], JsonSerializer.SerializeToUtf8Bytes(Result, SelfTestResult.JsonOptions));
            }

            return Task.FromResult<(int, string)?>((ExitCode, StandardError));
        }
    }
}
