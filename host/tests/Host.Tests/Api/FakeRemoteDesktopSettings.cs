using HyperHarbor.Host.Core.RemoteDesktop;

namespace HyperHarbor.Host.Tests;

/// <summary>Remote Desktop settings tests can change; Allow turns connections and the firewall group on.</summary>
internal sealed class FakeRemoteDesktopSettings : IRemoteDesktopSettings
{
    public RemoteDesktopState State { get; set; } = new("Professional", "Windows 11 Pro", false, 3389, false);

    /// <summary>Whether the host process has administrator rights; read when the service is first built.</summary>
    public bool Elevated { get; set; } = true;

    public int AllowCalls { get; private set; }

    public RemoteDesktopState Read() => State;

    public void Allow()
    {
        AllowCalls++;
        State = State with { Enabled = true, FirewallOpen = true };
    }
}
