using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace HyperHarbor.Host.Core.Diagnostics;

/// <summary>
/// Packs the host's daily log files (logs\host-yyyyMMdd.log) and a short summary into a zip for diagnosis.
/// The audit log is not included: it records what Users did, not why the host misbehaves. Log files hold no
/// secrets (SecretLeakTests check every entry), so the bundle can leave the host.
/// </summary>
public sealed class LogBundle
{
    public const string FolderName = "logs";
    public const string SummaryName = "summary.txt";

    /// <summary>Uncompressed size of the logs in one bundle; the newest files are kept when there are more.</summary>
    public const long DefaultMaxLogBytes = 100L * 1024 * 1024;

    private readonly string _logsFolder;
    private readonly TimeProvider _time;
    private readonly long _maxLogBytes;

    public LogBundle(string dataDirectory, TimeProvider? time = null, long maxLogBytes = DefaultMaxLogBytes)
    {
        _logsFolder = Path.Combine(dataDirectory, FolderName);
        _time = time ?? TimeProvider.System;
        _maxLogBytes = maxLogBytes;
    }

    /// <summary>Writes the zip to <paramref name="output"/>. <paramref name="facts"/> are "name: value" lines for the summary.</summary>
    public void Write(Stream output, IReadOnlyList<string> facts)
    {
        var notes = new List<string>();
        var files = ListLogFiles();

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var included = new List<string>();
        long total = 0;
        foreach (var file in files)
        {
            if (total + file.Length > _maxLogBytes && included.Count > 0)
            {
                notes.Add($"Left out {file.Name} ({file.Length:N0} bytes): the bundle holds at most {_maxLogBytes:N0} bytes of logs, newest first.");
                continue;
            }

            if (TryAdd(archive, file, notes))
            {
                included.Add(file.Name);
                total += file.Length;
            }
        }

        AddSummary(archive, facts, included, notes);
    }

    private List<FileInfo> ListLogFiles()
    {
        if (!Directory.Exists(_logsFolder))
        {
            return [];
        }

        // Names carry the date, so sorting them descending puts the newest first.
        return new DirectoryInfo(_logsFolder)
            .EnumerateFiles("*.log")
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static bool TryAdd(ZipArchive archive, FileInfo file, List<string> notes)
    {
        try
        {
            // The host is still writing today's file, so open it for sharing.
            using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entry = archive.CreateEntry($"{FolderName}/{file.Name}", CompressionLevel.Optimal);
            entry.LastWriteTime = file.LastWriteTime;
            using var target = entry.Open();
            source.CopyTo(target);
            return true;
        }
        catch (IOException ex)
        {
            notes.Add($"Could not read {file.Name}: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            notes.Add($"Could not read {file.Name}: {ex.Message}");
            return false;
        }
    }

    private void AddSummary(ZipArchive archive, IReadOnlyList<string> facts, List<string> included, List<string> notes)
    {
        var text = new StringBuilder();
        text.AppendLine("HyperHarbor host logs");
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Created: {_time.GetUtcNow():u}"));
        foreach (var fact in facts)
        {
            text.AppendLine(fact);
        }

        text.AppendLine();
        text.AppendLine(included.Count == 0 ? "Log files: none found" : "Log files (newest first):");
        foreach (var name in included)
        {
            text.AppendLine($"  {name}");
        }

        if (notes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Notes:");
            foreach (var note in notes)
            {
                text.AppendLine($"  {note}");
            }
        }

        var entry = archive.CreateEntry(SummaryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text.ToString());
    }
}
