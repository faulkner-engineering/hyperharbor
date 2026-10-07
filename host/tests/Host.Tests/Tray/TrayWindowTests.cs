using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyperHarbor.Host.Tray.Window;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Hosts;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tests.Tray;

/// <summary>The tray's HTML window: the commands the page sends, the files it is served, and the bridge's shape.</summary>
public sealed partial class TrayWindowTests
{
    private sealed class Actions : ITrayActions
    {
        public List<string> Calls { get; } = [];

        public void SetPassphrase(string passphrase) => Calls.Add("passphrase:" + passphrase.Length);

        public void RemoveDevice(Guid deviceId) => Calls.Add("remove:" + deviceId);

        public void ChooseFolder(TrayFolder folder) => Calls.Add("folder:" + folder);

        public void SetUpConsole() => Calls.Add("console");

        public void SetUpPackageSearch() => Calls.Add("packageSearch");

        public void CheckForUpdate() => Calls.Add("check");

        public void InstallUpdate() => Calls.Add("install");

        public void SetUpdateChannel(string channel) => Calls.Add("channel:" + channel);

        public void HostLeanDryRun(string source) => Calls.Add("leanDryRun:" + source);

        public void HostLeanApply(string source) => Calls.Add("leanApply:" + source);

        public void SetHostLeanSchedule(bool enabled) => Calls.Add("leanSchedule:" + enabled);

        public void Open(string target) => Calls.Add("open:" + target);

        public void OpenUrl(string url) => Calls.Add("url:" + url);

        public void CancelPairing(Guid pairingId) => Calls.Add("cancel:" + pairingId);

        public void CloseWindow() => Calls.Add("close");
    }

    private static readonly Guid Id = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    private static (bool Handled, List<string> Calls) Dispatch(string json)
    {
        var actions = new Actions();
        using var document = JsonDocument.Parse(json);
        return (TrayCommands.Dispatch(document.RootElement, actions), actions.Calls);
    }

    [Theory]
    [InlineData("""{"type":"setPassphrase","passphrase":"correct horse"}""", "passphrase:13")]
    [InlineData("""{"type":"removeDevice","deviceId":"0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b"}""", "remove:0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b")]
    [InlineData("""{"type":"chooseFolder","folder":"iso"}""", "folder:Iso")]
    [InlineData("""{"type":"setUpConsole"}""", "console")]
    [InlineData("""{"type":"setUpPackageSearch"}""", "packageSearch")]
    [InlineData("""{"type":"checkUpdate"}""", "check")]
    [InlineData("""{"type":"installUpdate"}""", "install")]
    [InlineData("""{"type":"setChannel","channel":"beta"}""", "channel:beta")]
    [InlineData("""{"type":"hostLeanDryRun","source":"lean"}""", "leanDryRun:lean")]
    [InlineData("""{"type":"hostLeanApply","source":"undo"}""", "leanApply:undo")]
    [InlineData("""{"type":"setHostLeanSchedule","enabled":true}""", "leanSchedule:True")]
    [InlineData("""{"type":"setHostLeanSchedule","enabled":false}""", "leanSchedule:False")]
    [InlineData("""{"type":"open","target":"audit"}""", "open:audit")]
    [InlineData("""{"type":"openUrl","url":"https://github.com/faulkner-engineering/hyperharbor/releases"}""", "url:https://github.com/faulkner-engineering/hyperharbor/releases")]
    [InlineData("""{"type":"cancelPairing","pairingId":"0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b"}""", "cancel:0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b")]
    [InlineData("""{"type":"close"}""", "close")]
    public void PageCommands_CallTheTray(string json, string call)
    {
        var (handled, calls) = Dispatch(json);

        Assert.True(handled);
        Assert.Equal([call], calls);
    }

    [Theory]
    [InlineData("""{"type":"ready"}""", true)]
    [InlineData("""{"type":"setPassphrase"}""", false)]
    [InlineData("""{"type":"setPassphrase","passphrase":42}""", false)]
    [InlineData("""{"type":"removeDevice","deviceId":"not-an-id"}""", false)]
    [InlineData("""{"type":"chooseFolder","folder":"C:\\Windows"}""", false)]
    [InlineData("""{"type":"open","target":"C:\\Windows\\notepad.exe"}""", false)]
    [InlineData("""{"type":"setChannel","channel":""}""", false)]
    [InlineData("""{"type":"hostLeanDryRun"}""", false)]
    [InlineData("""{"type":"hostLeanApply","source":"format"}""", false)]
    [InlineData("""{"type":"hostLeanApply","source":7}""", false)]
    [InlineData("""{"type":"setHostLeanSchedule"}""", false)]
    [InlineData("""{"type":"setHostLeanSchedule","enabled":"yes"}""", false)]
    [InlineData("""{"type":"format the disk"}""", false)]
    [InlineData("""{"kind":"close"}""", false)]
    [InlineData("""["close"]""", false)]
    public void UnknownOrIncompleteMessages_DoNothing(string json, bool handled)
    {
        var (result, calls) = Dispatch(json);

        Assert.Equal(handled, result);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("https://github.com/faulkner-engineering/hyperharbor/releases/tag/v0.1.4", true)]
    [InlineData("http://github.com/faulkner-engineering/hyperharbor/releases/tag/v0.1.4", false)]
    [InlineData("https://github.com/someone-else/repo", false)]
    [InlineData("https://github.com.example.net/faulkner-engineering/hyperharbor", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("not a url", false)]
    public void OnlyTheProjectsGitHubPages_OpenOutside(string url, bool allowed)
    {
        Assert.Equal(allowed, WebWindow.IsExternalLinkAllowed(url));
    }

    [Fact]
    public void TheEmbeddedApp_HasItsPage_WithAStrictPolicy_AndEveryFileItLoads()
    {
        var index = TrayUiResources.Open("/");
        Assert.NotNull(index);
        Assert.StartsWith("text/html", index.Value.ContentType, StringComparison.Ordinal);
        string html;
        using (var reader = new StreamReader(index.Value.Content))
        {
            html = reader.ReadToEnd();
        }

        Assert.Contains("Content-Security-Policy", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", html, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", html, StringComparison.Ordinal);

        var referenced = AssetReference().Matches(html).Select(match => match.Groups["path"].Value).ToList();
        Assert.NotEmpty(referenced);
        Assert.All(referenced, path => Assert.Contains(path, TrayUiResources.Files));
        Assert.Contains(TrayUiResources.Files, file => file.EndsWith(".js", StringComparison.Ordinal));
        Assert.Contains(TrayUiResources.Files, file => file.EndsWith(".css", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/../Host.Tray.csproj")]
    [InlineData("/assets/..\\..\\secret")]
    [InlineData("/missing.js")]
    public void PathsOutsideTheApp_AreNotServed(string path)
    {
        Assert.Null(TrayUiResources.Open(path));
    }

    /// <summary>
    /// One sample of every message the tray sends, as the page receives it. The page's tests check bridge.ts against
    /// this file. Regenerate it with HH_WRITE_TRAY_FIXTURES=1 after changing BridgeMessages.cs, then update bridge.ts.
    /// </summary>
    [Fact]
    public void BridgeFixtures_AreCurrent()
    {
        var time = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var update = new HostUpdateStatus(true, HostUpdateMode.Auto, "stable", ["beta", "stable"], "03:00", "0.1.4", "0.1.5",
            "https://github.com/faulkner-engineering/hyperharbor/releases/tag/v0.1.5", HostUpdateActivity.Preparing, time, "Downloading and testing version 0.1.5.",
            "Updated from 0.1.3 to 0.1.4.", [], new HostUpdateProgress(HostUpdateStep.Downloading, 52_428_800, 196_083_712));
        var lean = new HostLeanStatus(
            true,
            "Run the installed service to apply changes.",
            "dryRun",
            "Host gaming",
            true,
            true,
            true,
            time.AddDays(30),
            new HostLeanPlanSummary(
                "lean",
                time,
                true,
                [new HostLeanChangeLine("services", "DiagTrack", "Startup type Automatic to Disabled")],
                ["Spooler: a printer is installed"],
                ["Unknown service: Foo"],
                12),
            new HostLeanRunSummary(
                "lean",
                time,
                false,
                7,
                ["Widgets: access denied"],
                new HostLeanMetrics(time, 6144, 210, true),
                new HostLeanMetrics(time.AddMinutes(1), 5120, 180, false),
                "Restore point 12 and registry export"));
        object[] messages =
        [
            new HostToPage.State(new TrayViewState(
                true,
                true,
                [new TrayViewDevice(Id, "Laptop", "AB12CD34EF567890AB12CD34EF567890", time)],
                new TrayViewFolder(@"D:\VMs", false),
                @"D:\ISOs",
                @"C:\Users\Public\Documents\HyperHarbor Backups",
                true,
                false,
                update,
                lean,
                ["folder:iso"],
                new TrayViewPairing(Id, "Laptop", "440141", time.AddMinutes(2)),
                @"C:\ProgramData\HyperHarbor")),
            new HostToPage.State(new TrayViewState(false, null, [], null, null, null, false, false, null, null, [], null, @"C:\ProgramData\HyperHarbor")),
            new HostToPage.Toast("success", "Admin passphrase saved", "Paired devices now need it to change VMs."),
            new HostToPage.Navigate("devices"),
        ];

        var computed = JsonSerializer.Serialize(
            messages.Select(message => JsonSerializer.SerializeToElement(message, message.GetType(), ContractJson.Options)).ToList(),
            new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
        var path = FixturesPath();
        if (Environment.GetEnvironmentVariable("HH_WRITE_TRAY_FIXTURES") == "1")
        {
            File.WriteAllText(path, computed);
        }

        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), computed);
    }

    private static string FixturesPath([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "..", "src", "Host.Tray", "webui", "src", "bridge.fixtures.json"));

    [GeneratedRegex(@"(?:src|href)=""\./(?<path>[^""]+)""")]
    private static partial Regex AssetReference();
}
