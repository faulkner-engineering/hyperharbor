using System.Net;
using HyperHarbor.Host.Core.Tailscale;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Tailscale;

public sealed class TailscaleTests
{
    // Shaped like "tailscale status --json" from Tailscale 1.7x, reduced to the fields HyperHarbor reads.
    private const string StatusJson = """
        {
          "Version": "1.76.0",
          "BackendState": "Running",
          "MagicDNSSuffix": "tail1234.ts.net",
          "Self": { "ID": "nSELF1CNTRL", "HostName": "GAMING-PC", "DNSName": "gaming-pc.tail1234.ts.net.", "TailscaleIPs": ["100.64.0.1", "fd7a:115c:a1e0::1"], "Tags": ["tag:hyperharbor-host"], "Online": true },
          "Peer": {
            "nodekey:aaa": { "ID": "nDIRECT1CNTRL", "HostName": "laptop", "DNSName": "laptop.tail1234.ts.net.", "TailscaleIPs": ["100.64.0.2"], "Online": true, "Active": true, "CurAddr": "203.0.113.5:41641", "Relay": "nyc" },
            "nodekey:bbb": { "ID": "nRELAY1CNTRL", "HostName": "phone", "DNSName": "phone.tail1234.ts.net.", "TailscaleIPs": ["100.64.0.3"], "Online": true, "Active": true, "CurAddr": "", "Relay": "fra", "Tags": ["tag:hyperharbor-host"] },
            "nodekey:ccc": { "ID": "nIDLE1CNTRL", "HostName": "idle", "DNSName": "idle.tail1234.ts.net.", "TailscaleIPs": ["100.64.0.4"], "Online": true, "Active": false, "CurAddr": "", "Relay": "ord" },
            "nodekey:ddd": { "ID": "nOFF1CNTRL", "HostName": "offline", "DNSName": "offline.tail1234.ts.net.", "TailscaleIPs": ["100.64.0.5"], "Online": false, "Active": true, "CurAddr": "198.51.100.9:41641", "Relay": "" }
          }
        }
        """;

    [Fact]
    public void Status_ReadsSelfAndPeers()
    {
        var status = TailscaleStatusParser.ParseStatus(StatusJson);

        Assert.Equal(TailscaleState.Running, status.State);
        Assert.Equal("tail1234.ts.net", status.MagicDnsSuffix);
        Assert.Equal("gaming-pc.tail1234.ts.net", status.Self!.DnsName);
        Assert.Equal("100.64.0.1", TailscaleAddress.FirstIpv4(status.Self.Addresses));
        Assert.True(TailscaleCommands.HasHostTag(status.Self));
        Assert.Equal(4, status.Peers.Count);
    }

    [Fact]
    public void Status_TellsDirectRelayedIdleAndOfflinePeersApart()
    {
        var peers = TailscaleStatusParser.ParseStatus(StatusJson).Peers.ToDictionary(peer => peer.Node.HostName);

        Assert.Equal(TailscalePath.Direct, peers["laptop"].Path);
        Assert.Null(peers["laptop"].Relay);
        Assert.Equal(TailscalePath.Relayed, peers["phone"].Path);
        Assert.Equal("fra", peers["phone"].Relay);
        Assert.Equal(TailscalePath.Unknown, peers["idle"].Path);
        Assert.Equal(TailscalePath.Unknown, peers["offline"].Path);
        Assert.False(peers["offline"].Online);
        Assert.True(TailscaleCommands.HasHostTag(peers["phone"].Node));
        Assert.False(TailscaleCommands.HasHostTag(peers["laptop"].Node));
    }

    [Theory]
    [InlineData("NeedsLogin", TailscaleState.NeedsLogin)]
    [InlineData("Stopped", TailscaleState.Stopped)]
    [InlineData("Starting", TailscaleState.Other)]
    [InlineData("NeedsMachineAuth", TailscaleState.Other)]
    public void Status_MapsTheBackendState_AndHidesSelfWhenNotRunning(string backendState, TailscaleState expected)
    {
        var status = TailscaleStatusParser.ParseStatus("{\"BackendState\":\"" + backendState + "\",\"Self\":{\"ID\":\"n1\",\"HostName\":\"pc\",\"TailscaleIPs\":[\"100.64.0.9\"]}}");

        Assert.Equal(expected, status.State);
        Assert.Null(status.Self);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Status_RejectsText_ThatIsNotAStatus(string text)
    {
        Assert.Equal(TailscaleState.Unreachable, TailscaleStatusParser.ParseStatus(text).State);
    }

    [Fact]
    public void WhoIs_ReadsTheStableNodeId()
    {
        var node = TailscaleStatusParser.ParseWhoIs("""{"Node":{"StableID":"nDIRECT1CNTRL","Name":"laptop.tail1234.ts.net."},"UserProfile":{}}""");

        Assert.Equal(new TailscaleWhoIs("nDIRECT1CNTRL", "laptop.tail1234.ts.net"), node);
        Assert.Null(TailscaleStatusParser.ParseWhoIs("""{"Node":null}"""));
        Assert.Null(TailscaleStatusParser.ParseWhoIs("oops"));
    }

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("100.63.0.1", false)]
    [InlineData("192.168.0.2", false)]
    [InlineData("fd7a:115c:a1e0::5", true)]
    [InlineData("fd7a:115c:a1e1::5", false)]
    [InlineData("::ffff:100.64.0.7", true)]
    [InlineData("not an address", false)]
    public void TailnetAddresses_AreTheCgnatAndUlaRanges(string address, bool expected)
    {
        Assert.Equal(expected, TailscaleAddress.IsTailnet(address));
    }

    [Fact]
    public async Task Provider_FallsBackToTheCommandLine_WhenTheLocalApiDoesNotAnswer()
    {
        var api = new FakeApi { Status = TailscaleReply.Failed("down") };
        var cli = new FakeApi { Status = TailscaleReply.Ok(StatusJson) };
        var provider = new TailscaleStatusProvider([api, cli], new FakeTimeProvider());

        var status = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Equal(TailscaleState.Running, status.State);
        Assert.Equal(1, api.StatusCalls);
        Assert.Equal(1, cli.StatusCalls);
    }

    [Fact]
    public async Task Provider_PrefersTheLocalApi_AndCachesForAFewSeconds()
    {
        var time = new FakeTimeProvider();
        var api = new FakeApi { Status = TailscaleReply.Ok(StatusJson) };
        var cli = new FakeApi { Status = TailscaleReply.Ok("{}") };
        var provider = new TailscaleStatusProvider([api, cli], time);

        await provider.GetStatusAsync(CancellationToken.None);
        await provider.GetStatusAsync(CancellationToken.None);
        Assert.Equal(1, api.StatusCalls);
        Assert.Equal(0, cli.StatusCalls);

        time.Advance(TimeSpan.FromSeconds(6));
        await provider.GetStatusAsync(CancellationToken.None);
        Assert.Equal(2, api.StatusCalls);
    }

    [Fact]
    public async Task Provider_ReportsNotInstalled_OnlyWhenTheCommandLineIsMissing()
    {
        var missing = new TailscaleStatusProvider(
            [new FakeApi { Status = TailscaleReply.Failed("down") }, new FakeApi { Status = new TailscaleReply(TailscaleReplyKind.NotInstalled) }],
            new FakeTimeProvider());
        var broken = new TailscaleStatusProvider(
            [new FakeApi { Status = TailscaleReply.Failed("down") }, new FakeApi { Status = TailscaleReply.Failed("daemon not running") }],
            new FakeTimeProvider());

        Assert.Equal(TailscaleState.NotInstalled, (await missing.GetStatusAsync(CancellationToken.None)).State);
        var unreachable = await broken.GetStatusAsync(CancellationToken.None);
        Assert.Equal(TailscaleState.Unreachable, unreachable.State);
        Assert.Equal("daemon not running", unreachable.Error);
    }

    [Fact]
    public async Task Http_ReadsTheStatusFromTheConfiguredPort()
    {
        var handler = new RecordingHandler("""{"BackendState":"Running"}""");
        var api = new TailscaleHttpLocalApi(new HttpClient(handler), () => new TailscaleSettings { LocalApiPort = 5555 });

        var reply = await api.GetStatusAsync(CancellationToken.None);
        var whois = await api.WhoIsAsync("100.64.0.2", CancellationToken.None);

        Assert.Equal(TailscaleReplyKind.Ok, reply.Kind);
        Assert.Equal(["http://127.0.0.1:5555/localapi/v0/status", "http://127.0.0.1:5555/localapi/v0/whois?addr=100.64.0.2"], handler.Urls);
        Assert.All(handler.Headers, header => Assert.Equal("localapi", header));
        Assert.Equal(TailscaleReplyKind.Ok, whois.Kind);
    }

    [Fact]
    public async Task Cli_AcceptsStatusJson_FromANonZeroExit_AndSkipsMissingPrograms()
    {
        var process = new FakeProcess(
            ("first", null),
            ("second", new TailscaleProcessResult(1, """{"BackendState":"Stopped"}""", "stopped")));
        var api = new TailscaleCliLocalApi(process, () => ["first", "second"]);

        var reply = await api.GetStatusAsync(CancellationToken.None);

        Assert.Equal(TailscaleReplyKind.Ok, reply.Kind);
        Assert.Equal(["status", "--json"], process.Calls.Last().Arguments);
    }

    [Fact]
    public async Task Cli_ReportsNotInstalled_WhenNoProgramRuns_AndTheFirstErrorLineOtherwise()
    {
        var none = new TailscaleCliLocalApi(new FakeProcess(("tailscale", null)), () => ["tailscale"]);
        var failing = new TailscaleCliLocalApi(
            new FakeProcess(("tailscale", new TailscaleProcessResult(1, string.Empty, "failed to connect\nmore text"))),
            () => ["tailscale"]);

        Assert.Equal(TailscaleReplyKind.NotInstalled, (await none.GetStatusAsync(CancellationToken.None)).Kind);
        Assert.Equal("failed to connect", (await failing.GetStatusAsync(CancellationToken.None)).Error);
    }

    [Theory]
    [InlineData(null, null, NodeDecision.Unchecked)]
    [InlineData(null, "nA", NodeDecision.Bound)]
    [InlineData("nA", "nA", NodeDecision.Verified)]
    [InlineData("nA", "nB", NodeDecision.Mismatch)]
    [InlineData("nA", null, NodeDecision.Unverifiable)]
    [InlineData("", "nA", NodeDecision.Bound)]
    public void NodeDecision_FollowsTheRecordedNode(string? recorded, string? resolved, NodeDecision expected)
    {
        Assert.Equal(expected, TailscaleNodeVerifier.Decide(recorded, resolved));
    }

    [Fact]
    public async Task Verifier_IgnoresRequestsThatDidNotComeOverTheTailnet()
    {
        var api = new FakeApi();
        var verifier = new TailscaleNodeVerifier(new TailscaleStatusProvider([api], new FakeTimeProvider()));

        var check = await verifier.CheckAsync(IPAddress.Parse("192.168.0.20"), "nA", CancellationToken.None);

        Assert.Equal(NodeDecision.NotTailnet, check.Decision);
        Assert.True(check.Allowed);
        Assert.Equal(0, api.WhoIsCalls);
    }

    [Fact]
    public async Task Verifier_RefusesAnotherNode_AndAcceptsTheRecordedOne()
    {
        var api = new FakeApi { WhoIs = TailscaleReply.Ok("""{"Node":{"StableID":"nB","Name":"other."}}""") };
        var verifier = new TailscaleNodeVerifier(new TailscaleStatusProvider([api], new FakeTimeProvider()));

        var mismatch = await verifier.CheckAsync(IPAddress.Parse("100.64.0.2"), "nA", CancellationToken.None);
        var verified = await verifier.CheckAsync(IPAddress.Parse("100.64.0.2"), "nB", CancellationToken.None);

        Assert.Equal(NodeDecision.Mismatch, mismatch.Decision);
        Assert.False(mismatch.Allowed);
        Assert.Equal(NodeDecision.Verified, verified.Decision);
        Assert.Equal(1, api.WhoIsCalls);
    }

    [Fact]
    public async Task Verifier_RefusesARecordedDevice_WhenTailscaleCannotSayWhoCalled()
    {
        var api = new FakeApi { WhoIs = TailscaleReply.Failed("down") };
        var verifier = new TailscaleNodeVerifier(new TailscaleStatusProvider([api], new FakeTimeProvider()));

        Assert.False((await verifier.CheckAsync(IPAddress.Parse("100.64.0.2"), "nA", CancellationToken.None)).Allowed);
        Assert.True((await verifier.CheckAsync(IPAddress.Parse("100.64.0.2"), null, CancellationToken.None)).Allowed);
    }

    [Fact]
    public void UpCommand_AddsTheTag_AndTheControlServerOnlyWhenItIsValid()
    {
        Assert.Equal("tailscale up --advertise-tags=tag:hyperharbor-host", TailscaleCommands.UpCommand(new TailscaleSettings()));
        Assert.Equal(
            "tailscale up --advertise-tags=tag:hyperharbor-host --login-server=https://hs.example.com",
            TailscaleCommands.UpCommand(new TailscaleSettings { ControlUrl = "https://hs.example.com" }));
        Assert.Equal(
            "tailscale up --advertise-tags=tag:hyperharbor-host",
            TailscaleCommands.UpCommand(new TailscaleSettings { ControlUrl = "https://x.example.com\" & calc" }));
    }

    [Theory]
    [InlineData("https://hs.example.com", true)]
    [InlineData("http://192.168.0.5:8080", true)]
    [InlineData("ftp://hs.example.com", false)]
    [InlineData("hs.example.com", false)]
    [InlineData("https://user:pw@hs.example.com", false)]
    [InlineData("https://hs.example.com; calc", false)]
    [InlineData("https://hs.example.com/%41", false)]
    [InlineData("", false)]
    public void ControlUrl_MustBeASimpleHttpUrl(string url, bool valid)
    {
        Assert.Equal(valid, TailscaleSettings.IsValidControlUrl(url));
    }

    [Fact]
    public void Settings_PersistAndRejectBadValues()
    {
        var directory = Directory.CreateTempSubdirectory("hh-tailscale-");
        try
        {
            var store = new TailscaleSettingsStore(directory.FullName);
            Assert.Equal(41112, store.Load().LocalApiPort);

            store.Save(new TailscaleSettings { LocalApiPort = 5555, ControlUrl = " https://hs.example.com " });
            var reloaded = new TailscaleSettingsStore(directory.FullName).Load();

            Assert.Equal(5555, reloaded.LocalApiPort);
            Assert.Equal("https://hs.example.com", reloaded.ControlUrl);
            Assert.Throws<ArgumentException>(() => store.Save(new TailscaleSettings { LocalApiPort = 0 }));
            Assert.Throws<ArgumentException>(() => store.Save(new TailscaleSettings { ControlUrl = "nope" }));
            Assert.Null(store.Save(new TailscaleSettings { ControlUrl = "  " }).ControlUrl);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class FakeApi : ITailscaleLocalApi
    {
        public TailscaleReply Status { get; set; } = TailscaleReply.Failed("unset");

        public TailscaleReply WhoIs { get; set; } = TailscaleReply.Failed("unset");

        public int StatusCalls { get; private set; }

        public int WhoIsCalls { get; private set; }

        public Task<TailscaleReply> GetStatusAsync(CancellationToken cancellationToken)
        {
            StatusCalls++;
            return Task.FromResult(Status);
        }

        public Task<TailscaleReply> WhoIsAsync(string address, CancellationToken cancellationToken)
        {
            WhoIsCalls++;
            return Task.FromResult(WhoIs);
        }
    }

    private sealed class FakeProcess(params (string File, TailscaleProcessResult? Result)[] results) : ITailscaleProcess
    {
        public List<(string File, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<TailscaleProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add((fileName, arguments));
            return Task.FromResult(results.First(entry => entry.File == fileName).Result);
        }
    }

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        public List<string> Headers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            Headers.Add(request.Headers.GetValues("Sec-Tailscale").Single());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
