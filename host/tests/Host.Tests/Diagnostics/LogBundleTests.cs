using System.IO.Compression;
using HyperHarbor.Host.Core.Diagnostics;

namespace HyperHarbor.Host.Tests.Diagnostics;

public sealed class LogBundleTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));

    public LogBundleTests() => Directory.CreateDirectory(Path.Combine(_data, "logs"));

    public void Dispose() => Directory.Delete(_data, recursive: true);

    private void WriteLog(string name, string text) => File.WriteAllText(Path.Combine(_data, "logs", name), text);

    private static ZipArchive Read(MemoryStream zip)
    {
        zip.Position = 0;
        return new ZipArchive(zip, ZipArchiveMode.Read);
    }

    private static string Text(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void Write_PacksTheLogFilesAndASummary()
    {
        WriteLog("host-20261005.log", "old line");
        WriteLog("host-20261006.log", "new line");
        File.WriteAllText(Path.Combine(_data, "audit.log"), "must not be included");
        using var zip = new MemoryStream();

        new LogBundle(_data).Write(zip, ["Host: TC-PC", "Host version: 0.1.5"]);

        using var archive = Read(zip);
        Assert.Equal(["logs/host-20261005.log", "logs/host-20261006.log", "summary.txt"], archive.Entries.Select(e => e.FullName).Order().ToList());
        Assert.Equal("new line", Text(archive, "logs/host-20261006.log"));
        var summary = Text(archive, LogBundle.SummaryName);
        Assert.Contains("Host: TC-PC", summary);
        Assert.Contains("Host version: 0.1.5", summary);
        Assert.Contains("host-20261006.log", summary);
    }

    [Fact]
    public void Write_ReadsAFileTheHostIsStillWriting()
    {
        var path = Path.Combine(_data, "logs", "host-20261006.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write("live entry"u8);
        writer.Flush();
        using var zip = new MemoryStream();

        new LogBundle(_data).Write(zip, []);

        using var archive = Read(zip);
        Assert.Equal("live entry", Text(archive, "logs/host-20261006.log"));
    }

    [Fact]
    public void Write_KeepsTheNewestFilesWithinTheSizeLimit_AndSaysWhatItLeftOut()
    {
        WriteLog("host-20261004.log", new string('a', 600));
        WriteLog("host-20261005.log", new string('b', 600));
        WriteLog("host-20261006.log", new string('c', 600));
        using var zip = new MemoryStream();

        new LogBundle(_data, maxLogBytes: 1300).Write(zip, []);

        using var archive = Read(zip);
        Assert.NotNull(archive.GetEntry("logs/host-20261006.log"));
        Assert.NotNull(archive.GetEntry("logs/host-20261005.log"));
        Assert.Null(archive.GetEntry("logs/host-20261004.log"));
        Assert.Contains("Left out host-20261004.log", Text(archive, LogBundle.SummaryName));
    }

    [Fact]
    public void Write_AlwaysIncludesTheNewestFile_EvenWhenItAloneIsOverTheLimit()
    {
        WriteLog("host-20261006.log", new string('c', 600));
        using var zip = new MemoryStream();

        new LogBundle(_data, maxLogBytes: 100).Write(zip, []);

        using var archive = Read(zip);
        Assert.NotNull(archive.GetEntry("logs/host-20261006.log"));
    }

    [Fact]
    public void Write_WithNoLogsFolder_StillWritesTheSummary()
    {
        Directory.Delete(Path.Combine(_data, "logs"));
        using var zip = new MemoryStream();

        new LogBundle(_data).Write(zip, []);

        using var archive = Read(zip);
        Assert.Single(archive.Entries);
        Assert.Contains("Log files: none found", Text(archive, LogBundle.SummaryName));
    }
}
