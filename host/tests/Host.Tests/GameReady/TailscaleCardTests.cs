using HyperHarbor.Host.Core.GameReady;
using HyperHarbor.Host.Core.Tailscale;
using HyperHarbor.Shared.Contracts.Hosts;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tests.GameReady;

public sealed class TailscaleCardTests
{
    private static HostTailscale Info(TailscaleHostState state, bool tagged = false, string? error = null) => new(
        state,
        error,
        state == TailscaleHostState.Running ? "100.64.0.1" : null,
        state == TailscaleHostState.Running ? "pc.tail1.ts.net" : null,
        tagged,
        TailscaleCommands.HostTag,
        TailscaleCommands.UpCommand(new TailscaleSettings()),
        TailscaleCommands.PolicySnippet,
        new TailscaleConfig(41112, null));

    [Fact]
    public void WithoutTailscale_TheCardIsUnavailableNotAFailure()
    {
        var card = GameReadyCards.Tailscale(Info(TailscaleHostState.NotInstalled));

        Assert.Equal(GameReadyStates.Unavailable, card.State);
        Assert.Equal(GameReadyCardIds.Tailscale, card.Id);
        Assert.Single(card.Checks);
        Assert.Equal(GameReadyCheckStatuses.Fail, card.Checks[0].Status);
    }

    [Theory]
    [InlineData(TailscaleHostState.NeedsLogin)]
    [InlineData(TailscaleHostState.Stopped)]
    [InlineData(TailscaleHostState.Unreachable)]
    [InlineData(TailscaleHostState.Other)]
    public void ASignedOutStoppedOrSilentClient_NeedsAttention(TailscaleHostState state)
    {
        Assert.Equal(GameReadyStates.Attention, GameReadyCards.Tailscale(Info(state, error: "no answer")).State);
    }

    [Fact]
    public void Running_ShowsTheAddressAndName_AndAsksForTheTagUntilItIsSet()
    {
        var untagged = GameReadyCards.Tailscale(Info(TailscaleHostState.Running));
        var tagged = GameReadyCards.Tailscale(Info(TailscaleHostState.Running, tagged: true));

        Assert.Equal(GameReadyStates.Attention, untagged.State);
        Assert.Equal("100.64.0.1, pc.tail1.ts.net", untagged.Checks.Single(check => check.Id == "address").Detail);
        Assert.Equal(GameReadyCheckStatuses.Warn, untagged.Checks.Single(check => check.Id == "tag").Status);
        Assert.Contains(untagged.Hints, hint => hint.Contains("tagOwners", StringComparison.Ordinal) && hint.Contains("signs the PC out", StringComparison.Ordinal));
        Assert.Equal(GameReadyStates.Ready, tagged.State);
        Assert.DoesNotContain(tagged.Hints, hint => hint.Contains("tagOwners", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHostInfo_CarriesTheCommandForTheCopyButton()
    {
        var info = HostTailscaleBuilder.Build(
            TailscaleStatusParser.ParseStatus("""{"BackendState":"Running","Self":{"ID":"n1","HostName":"pc","TailscaleIPs":["100.64.0.1"]}}"""),
            new TailscaleSettings { ControlUrl = "https://hs.example.com" });

        Assert.Equal("tailscale up --advertise-tags=tag:hyperharbor-host --login-server=https://hs.example.com", info.UpCommand);
        Assert.False(info.Tagged);
    }
}
