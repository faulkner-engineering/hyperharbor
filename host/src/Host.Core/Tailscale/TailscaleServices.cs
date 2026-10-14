using System.Collections.Concurrent;
using System.Net;

namespace HyperHarbor.Host.Core.Tailscale;

/// <summary>
/// Reads the local Tailscale client. The sources are tried in order (the local API, then the command line), so a
/// client whose local API is not on TCP still answers. The answer is cached for a few seconds, because the tray and
/// the clients ask often.
/// </summary>
public sealed class TailscaleStatusProvider
{
    private static readonly TimeSpan StatusLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WhoIsLifetime = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<ITailscaleLocalApi> _sources;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (TailscaleWhoIs Node, DateTimeOffset At)> _whois = new();
    private (TailscaleStatus Status, DateTimeOffset At)? _cached;

    public TailscaleStatusProvider(IReadOnlyList<ITailscaleLocalApi> sources, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources;
        _time = time;
    }

    public async Task<TailscaleStatus> GetStatusAsync(CancellationToken cancellationToken, bool refresh = false)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (!refresh && _cached is { } cached && now - cached.At < StatusLifetime)
            {
                return cached.Status;
            }

            var status = await ReadAsync(cancellationToken);
            _cached = (status, now);
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The node behind a tailnet address, or null when Tailscale cannot say.</summary>
    public async Task<TailscaleWhoIs?> WhoIsAsync(IPAddress address, CancellationToken cancellationToken)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var key = address.ToString();
        var now = _time.GetUtcNow();
        if (_whois.TryGetValue(key, out var known) && now - known.At < WhoIsLifetime)
        {
            return known.Node;
        }

        foreach (var source in _sources)
        {
            var reply = await source.WhoIsAsync(key, cancellationToken);
            if (reply.Kind == TailscaleReplyKind.Ok && reply.Body is not null && TailscaleStatusParser.ParseWhoIs(reply.Body) is { } node)
            {
                _whois[key] = (node, now);
                return node;
            }
        }

        return null;
    }

    private async Task<TailscaleStatus> ReadAsync(CancellationToken cancellationToken)
    {
        TailscaleReply? last = null;
        foreach (var source in _sources)
        {
            last = await source.GetStatusAsync(cancellationToken);
            if (last.Kind == TailscaleReplyKind.Ok && last.Body is not null)
            {
                var status = TailscaleStatusParser.ParseStatus(last.Body);
                if (status.State != TailscaleState.Unreachable)
                {
                    return status;
                }

                last = TailscaleReply.Failed(status.Error ?? "Tailscale returned an unexpected status.");
            }
        }

        // The last source is the command line: it tells "not installed" from "installed but not answering".
        return last is null || last.Kind == TailscaleReplyKind.NotInstalled
            ? TailscaleStatus.Missing(TailscaleState.NotInstalled)
            : TailscaleStatus.Missing(TailscaleState.Unreachable, last.Error);
    }
}

/// <summary>The "tailscale up" command that gives this host its tag.</summary>
public static class TailscaleCommands
{
    public const string HostTag = "tag:hyperharbor-host";

    /// <summary>
    /// What the tailnet policy needs before this PC can carry the tag. Tailscale refuses a tag the policy does not allow
    /// by signing the PC out (seen on a real tailnet), so the tag is applied only after this is in place, from the admin
    /// console. HyperHarbor never applies it by running "tailscale up", because it cannot tell beforehand whether it is allowed.
    /// </summary>
    public const string PolicySnippet = "\"tagOwners\": {\n  \"tag:hyperharbor-host\": [\"autogroup:admin\"]\n}";

    /// <summary>
    /// The command that signs a new PC in with the tag. Not for a PC that is already signed in: if the policy does not
    /// allow the tag, Tailscale signs that PC out. Plain "tailscale up" refuses to run when other non-default flags are set and not repeated. The card says so
    /// next to the command, and does not add --reset, which would also drop those other settings.
    /// </summary>
    public static string UpCommand(TailscaleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var command = $"tailscale up --advertise-tags={HostTag}";
        return TailscaleSettings.IsValidControlUrl(settings.ControlUrl) ? $"{command} --login-server={settings.ControlUrl}" : command;
    }

    public static bool HasHostTag(TailscaleNode? node) =>
        node is not null && node.Tags.Any(tag => string.Equals(tag, HostTag, StringComparison.OrdinalIgnoreCase));
}

public enum NodeDecision
{
    /// <summary>The request did not come over the tailnet.</summary>
    NotTailnet,

    /// <summary>The node is the one recorded for the device.</summary>
    Verified,

    /// <summary>The device had no node recorded; this one is recorded now.</summary>
    Bound,

    /// <summary>First use over the tailnet and Tailscale cannot say who the peer is. The certificate still authenticates the device.</summary>
    Unchecked,

    /// <summary>A different node than the one recorded for the device.</summary>
    Mismatch,

    /// <summary>A node is recorded but Tailscale cannot say who the peer is, so the request cannot be trusted.</summary>
    Unverifiable,
}

public sealed record NodeCheck(NodeDecision Decision, TailscaleWhoIs? Node)
{
    public bool Allowed => Decision is NodeDecision.NotTailnet or NodeDecision.Verified or NodeDecision.Bound or NodeDecision.Unchecked;
}

/// <summary>
/// Ties a paired device to the Tailscale node it paired from. The client certificate is still the credential; the
/// node adds a second fact, so a stolen certificate used from another tailnet node is refused.
/// </summary>
public sealed class TailscaleNodeVerifier(TailscaleStatusProvider provider)
{
    public async Task<NodeCheck> CheckAsync(IPAddress remote, string? recordedNodeId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (!TailscaleAddress.IsTailnet(remote))
        {
            return new NodeCheck(NodeDecision.NotTailnet, null);
        }

        var node = await provider.WhoIsAsync(remote, cancellationToken);
        return new NodeCheck(Decide(recordedNodeId, node?.NodeId), node);
    }

    /// <summary>The rule on its own, for a request that came over the tailnet.</summary>
    public static NodeDecision Decide(string? recordedNodeId, string? resolvedNodeId)
    {
        if (string.IsNullOrEmpty(recordedNodeId))
        {
            return string.IsNullOrEmpty(resolvedNodeId) ? NodeDecision.Unchecked : NodeDecision.Bound;
        }

        if (string.IsNullOrEmpty(resolvedNodeId))
        {
            return NodeDecision.Unverifiable;
        }

        return string.Equals(recordedNodeId, resolvedNodeId, StringComparison.Ordinal) ? NodeDecision.Verified : NodeDecision.Mismatch;
    }
}
