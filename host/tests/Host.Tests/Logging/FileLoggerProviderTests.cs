using System.Security.AccessControl;
using System.Security.Principal;
using HyperHarbor.Host.Service.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Logging;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 23, 3, 33, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Entries_GoToTodaysFile_WithLevelCategoryAndException()
    {
        using var provider = new FileLoggerProvider(_directory, _time);
        var logger = provider.CreateLogger("HyperHarbor.Host.Core.Lifecycle.VmJobStore");

        logger.LogInformation("Job {JobId} started.", 42);
        logger.LogError(new InvalidOperationException("Invalid parameter"), "Job {JobId} failed unexpectedly.", 42);
        await provider.FlushAsync();

        var text = await ReadSharedAsync(provider.CurrentPath);
        Assert.EndsWith("host-" + _time.GetLocalNow().ToString("yyyyMMdd") + ".log", provider.CurrentPath, StringComparison.Ordinal);
        Assert.Contains("INFO  HyperHarbor.Host.Core.Lifecycle.VmJobStore: Job 42 started.", text, StringComparison.Ordinal);
        Assert.Contains("ERROR HyperHarbor.Host.Core.Lifecycle.VmJobStore: Job 42 failed unexpectedly.", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: Invalid parameter", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewDay_StartsANewFile()
    {
        using var provider = new FileLoggerProvider(_directory, _time);
        var logger = provider.CreateLogger("Test");
        logger.LogInformation("Before midnight.");
        await provider.FlushAsync();
        var first = provider.CurrentPath;

        _time.Advance(TimeSpan.FromDays(1));
        logger.LogInformation("After midnight.");
        await provider.FlushAsync();

        Assert.NotEqual(first, provider.CurrentPath);
        Assert.Contains("After midnight.", await ReadSharedAsync(provider.CurrentPath), StringComparison.Ordinal);
        Assert.DoesNotContain("After midnight.", await ReadSharedAsync(first), StringComparison.Ordinal);
    }

    [Fact]
    public void OldFiles_AreDeletedAtStartup()
    {
        var folder = Path.Combine(_directory, FileLoggerProvider.FolderName);
        Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, "host-20260901.log");
        var recent = Path.Combine(folder, "host-20261001.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");
        File.SetLastWriteTimeUtc(old, _time.GetUtcNow().UtcDateTime.AddDays(-(FileLoggerProvider.RetentionDays + 1)));
        File.SetLastWriteTimeUtc(recent, _time.GetUtcNow().UtcDateTime.AddDays(-2));

        using var provider = new FileLoggerProvider(_directory, _time);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task LogFile_HasTheRestrictedAcl()
    {
        using var provider = new FileLoggerProvider(_directory, _time);
        provider.CreateLogger("Test").LogInformation("Hello.");
        await provider.FlushAsync();

        var security = new FileInfo(provider.CurrentPath).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var allowed = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), allowed);
    }

    private static async Task<string> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
