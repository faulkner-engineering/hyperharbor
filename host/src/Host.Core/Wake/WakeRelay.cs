using System.Net;
using System.Net.Sockets;
using HyperHarbor.Host.Core.Tailscale;
using HyperHarbor.Shared.Contracts.Wake;

namespace HyperHarbor.Host.Core.Wake;

/// <summary>An IPv4 network this PC is on.</summary>
public sealed record LocalNetwork(IPAddress Address, IPAddress Mask)
{
    /// <summary>The directed broadcast address of the network.</summary>
    public IPAddress Broadcast
    {
        get
        {
            var address = Address.GetAddressBytes();
            var mask = Mask.GetAddressBytes();
            return new IPAddress(address.Zip(mask, (a, m) => (byte)(a | ~m)).ToArray());
        }
    }

    public bool Contains(IPAddress other)
    {
        var mask = Mask.GetAddressBytes();
        var network = Address.GetAddressBytes();
        var candidate = other.GetAddressBytes();
        return mask.Any(part => part != 0) && mask.Select((part, index) => (candidate[index] & part) == (network[index] & part)).All(same => same);
    }
}

/// <summary>One magic packet to send: from which local address, to which broadcast address.</summary>
public sealed record WakeRelayPacket(byte[] Mac, IPAddress Source, IReadOnlyList<IPAddress> Targets);

/// <summary>The PC's network interfaces and the UDP socket, so tests need neither.</summary>
public interface IWakeRelayNetwork
{
    IReadOnlyList<LocalNetwork> Networks();

    /// <summary>Sends the datagram from <paramref name="source"/> to the target on the wake ports; returns how many datagrams went out.</summary>
    int Send(IPAddress source, IPAddress target, byte[] packet);
}

/// <summary>
/// Sends a magic packet for a host that sleeps on this PC's LAN. Only adapters whose address is on one of this PC's
/// own networks are woken, and the broadcast address comes from that network, not from the request, so a paired device
/// cannot use the relay to send datagrams anywhere else.
/// </summary>
public static class WakeRelayPlanner
{
    public static IReadOnlyList<WakeRelayPacket> Plan(IEnumerable<WakeAdapter> adapters, IReadOnlyList<LocalNetwork> networks)
    {
        var packets = new List<WakeRelayPacket>();
        foreach (var adapter in adapters)
        {
            if (!TryParseMac(adapter.MacAddress, out var mac) || !IPAddress.TryParse(adapter.Ipv4Address, out var target) || target.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            // The tailnet is not a LAN: a magic packet cannot cross it, and 100.64.0.0/10 is shared by every node.
            var network = networks.FirstOrDefault(candidate => candidate.Address.AddressFamily == AddressFamily.InterNetwork
                && !TailscaleAddress.IsTailnet(candidate.Address) && candidate.Contains(target));
            if (network is null)
            {
                continue;
            }

            packets.Add(new WakeRelayPacket(mac, network.Address, [network.Broadcast, IPAddress.Broadcast]));
        }

        return packets;
    }

    public static byte[] MagicPacket(byte[] mac)
    {
        var packet = new byte[102];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var repeat = 0; repeat < 16; repeat++)
        {
            mac.CopyTo(packet, 6 + (repeat * 6));
        }

        return packet;
    }

    public static bool TryParseMac(string text, out byte[] mac)
    {
        mac = [];
        var hex = text.Replace("-", string.Empty, StringComparison.Ordinal).Replace(":", string.Empty, StringComparison.Ordinal);
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit))
        {
            return false;
        }

        mac = Convert.FromHexString(hex);
        return true;
    }
}

public sealed class WakeRelayService(IWakeRelayNetwork network)
{
    public const int MaxAdapters = 8;

    /// <summary>Sends the packets and returns how many datagrams went out; 0 when no adapter is on a local network.</summary>
    /// <exception cref="InvalidWakeRequestException">The request is empty or has too many adapters.</exception>
    public int Relay(IReadOnlyList<WakeAdapter> adapters)
    {
        if (adapters.Count is 0 or > MaxAdapters)
        {
            throw new InvalidWakeRequestException($"Send between 1 and {MaxAdapters} adapters.");
        }

        var sent = 0;
        foreach (var packet in WakeRelayPlanner.Plan(adapters, network.Networks()))
        {
            var datagram = WakeRelayPlanner.MagicPacket(packet.Mac);
            foreach (var target in packet.Targets)
            {
                sent += network.Send(packet.Source, target, datagram);
            }
        }

        return sent;
    }
}

public sealed class SystemWakeRelayNetwork : IWakeRelayNetwork
{
    private static readonly int[] Ports = [9, 7];

    public IReadOnlyList<LocalNetwork> Networks() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address) && address.IPv4Mask is not null)
            .Select(address => new LocalNetwork(address.Address, address.IPv4Mask))
            .ToList();

    public int Send(IPAddress source, IPAddress target, byte[] packet)
    {
        var sent = 0;
        try
        {
            using var socket = new UdpClient(new IPEndPoint(source, 0)) { EnableBroadcast = true };
            foreach (var port in Ports)
            {
                socket.Send(packet, packet.Length, new IPEndPoint(target, port));
                sent++;
            }
        }
        catch (SocketException)
        {
            // This interface cannot send; the other targets may still work.
        }

        return sent;
    }
}
