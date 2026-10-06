using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Core.Updates;

/// <summary>A downloaded release that passed every check and is ready to install.</summary>
public sealed record PreparedUpdate(UpdateManifest Manifest, string ExecutablePath, string SignatureDetail, SelfTestOutcome SelfTest);

/// <summary>
/// Finds and prepares an update: reads the channel's manifest, decides whether to take it, then downloads the
/// package, checks its size and SHA-256, its signatures (<see cref="IPackageSignatureVerifier"/>), and finally runs
/// its self-test against a copy of the data. Each check runs only when the one before it passed, and a
/// rejected package is deleted.
/// </summary>
public sealed class UpdatePreparer(
    UpdateOptions options,
    UpdateDownloader downloader,
    IPackageSignatureVerifier signatures,
    SelfTestGate gate,
    string dataDirectory)
{
    public string DownloadFolder => Path.Combine(dataDirectory, DataBackup.UpdateFolderName, "downloads");

    /// <exception cref="UpdateRejectedException">The manifest broke a rule or belongs to another channel.</exception>
    public async Task<(UpdateManifest Manifest, UpdateDecision Decision)> CheckAsync(
        string channel,
        SemanticVersion current,
        IReadOnlyCollection<SemanticVersion> rolledBack,
        CancellationToken cancellationToken)
    {
        var bytes = await downloader.GetManifestAsync(options.ManifestUrl(channel), cancellationToken).ConfigureAwait(false);
        var manifest = UpdateManifest.Parse(bytes, options);
        if (!string.Equals(manifest.Channel, channel, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateRejectedException($"The {channel} channel's manifest is for the {manifest.Channel} channel.");
        }

        return (manifest, UpdatePolicy.Evaluate(manifest, current, rolledBack));
    }

    /// <exception cref="UpdateRejectedException">The package failed a check; it was deleted.</exception>
    public Task<PreparedUpdate> PrepareAsync(UpdateManifest manifest, CancellationToken cancellationToken) =>
        PrepareAsync(manifest, progress: null, cancellationToken);

    /// <param name="progress">Told each step as it starts, and the bytes received while downloading.</param>
    /// <exception cref="UpdateRejectedException">The package failed a check; it was deleted.</exception>
    public async Task<PreparedUpdate> PrepareAsync(UpdateManifest manifest, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        var size = manifest.HostPackage.Size;
        var version = manifest.ParsedVersion;
        Directory.CreateDirectory(DownloadFolder);
        var path = Path.Combine(DownloadFolder, $"HyperHarbor.Host-{version}.exe");
        try
        {
            var bytes = progress is null ? null : new InlineProgress<long>(done => progress.Report(new UpdateProgress(UpdateStep.Downloading, done, size)));
            await downloader.DownloadAsync(manifest.HostPackage, path, bytes, cancellationToken).ConfigureAwait(false);

            progress?.Report(new UpdateProgress(UpdateStep.Verifying, size, size));
            var signature = await signatures.VerifyAsync(path, manifest, cancellationToken).ConfigureAwait(false);
            if (!signature.Accepted)
            {
                throw new UpdateRejectedException($"Version {version} failed signature verification: {signature.Detail}");
            }

            progress?.Report(new UpdateProgress(UpdateStep.Testing, size, size));
            var selfTest = await gate.RunAsync(path, version, cancellationToken).ConfigureAwait(false);
            if (!selfTest.Passed)
            {
                throw new UpdateRejectedException($"Version {version} failed its self-test: {selfTest.Detail}");
            }

            return new PreparedUpdate(manifest, path, signature.Detail, selfTest);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }
}
