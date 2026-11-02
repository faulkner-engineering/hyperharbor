using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Service.Installation;

namespace HyperHarbor.Host.Tests.Installation;

/// <summary>
/// Uninstall deletes the install folder while the installed executable that winget or Settings started still runs from
/// it, waiting for the result. A copy of cmd.exe stands in for it.
/// </summary>
public sealed class InstallFolderRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void DeleteInstallFolder_MovesARunningExecutableAside_AndRemovesTheFolder()
    {
        var layout = new InstallLayout(Path.Combine(_root, "HyperHarbor"));
        var version = SemanticVersion.Parse("0.2.6");
        var source = Path.Combine(_root, "cmd.exe");
        Directory.CreateDirectory(_root);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), source);
        layout.Stage(source, version);
        layout.Activate(version);
        layout.InstallHelper(source);

        var running = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(layout.CurrentExecutable, "/c ping -n 60 127.0.0.1 > nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            var lines = new List<string>();
            new HostInstaller(layout, new Progress(lines)).DeleteInstallFolder();

            Assert.False(Directory.Exists(layout.Root));
            Assert.False(running.HasExited);
            Assert.Empty(lines);
        }
        finally
        {
            running.Kill(entireProcessTree: true);
            running.WaitForExit();
            running.Dispose();
        }
    }

    private sealed class Progress(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }
}
