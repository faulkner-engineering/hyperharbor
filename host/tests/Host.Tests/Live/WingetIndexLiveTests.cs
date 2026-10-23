using HyperHarbor.Host.Core.Profiles;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Downloads Microsoft's real winget source (source2.msix) and searches it. Skipped unless HH_WINGET_LIVE=1;
/// needs the internet, nothing else.
/// </summary>
public sealed class WingetIndexLiveTests
{
    [EnvironmentFact("HH_WINGET_LIVE")]
    public async Task Live_TheRealIndexDownloads_AndFindsWellKnownPackages()
    {
        var data = Path.Combine(Path.GetTempPath(), $"hh-winget-live-{Guid.NewGuid():N}");
        try
        {
            var index = new WingetIndex(WingetIndex.CreateHttpClient("HyperHarbor-Host/test"), data, TimeProvider.System);

            var path = await index.CurrentAsync(CancellationToken.None);

            Assert.NotNull(path);
            Assert.Equal("VideoLAN.VLC", WingetIndex.Search(path!, "vlc", 5)[0].Id);
            Assert.Contains(WingetIndex.Search(path!, "visual studio code", 10), result => result.Id == "Microsoft.VisualStudioCode");
            Assert.Equal("Git.Git", WingetIndex.Search(path!, "git", 5)[0].Id);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }
}
