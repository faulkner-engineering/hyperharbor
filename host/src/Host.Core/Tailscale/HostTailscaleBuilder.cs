using HyperHarbor.Shared.Contracts.Hosts;

namespace HyperHarbor.Host.Core.Tailscale;

/// <summary>Turns what the local Tailscale client reported into the API's <see cref="HostTailscale"/>. Pure.</summary>
public static class HostTailscaleBuilder
{
    public static HostTailscale Build(TailscaleStatus status, TailscaleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(settings);

        return new HostTailscale(
            status.State switch
            {
                TailscaleState.NotInstalled => TailscaleHostState.NotInstalled,
                TailscaleState.Unreachable => TailscaleHostState.Unreachable,
                TailscaleState.NeedsLogin => TailscaleHostState.NeedsLogin,
                TailscaleState.Stopped => TailscaleHostState.Stopped,
                TailscaleState.Running => TailscaleHostState.Running,
                _ => TailscaleHostState.Other,
            },
            status.Error,
            status.Self is null ? null : TailscaleAddress.FirstIpv4(status.Self.Addresses),
            status.Self is { DnsName.Length: > 0 } self ? self.DnsName : null,
            TailscaleCommands.HasHostTag(status.Self),
            TailscaleCommands.HostTag,
            TailscaleCommands.UpCommand(settings),
            new TailscaleConfig(settings.LocalApiPort, settings.ControlUrl));
    }
}
