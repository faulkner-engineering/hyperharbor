using System.Text.Json;

namespace HyperHarbor.Host.Core.Tailscale;

/// <summary>
/// Reads the JSON that "tailscale status --json" and the local API's /localapi/v0/status both produce, and the local
/// API's whois answer. Pure. Unknown fields are ignored, because Tailscale adds fields in every release.
/// </summary>
public static class TailscaleStatusParser
{
    public static TailscaleStatus ParseStatus(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return TailscaleStatus.Missing(TailscaleState.Unreachable, "Tailscale returned text that is not JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return TailscaleStatus.Missing(TailscaleState.Unreachable, "Tailscale returned an unexpected status.");
            }

            var state = StateOf(Text(root, "BackendState"));
            var suffix = Text(root, "MagicDNSSuffix");
            if (string.IsNullOrEmpty(suffix) && root.TryGetProperty("CurrentTailnet", out var tailnet) && tailnet.ValueKind == JsonValueKind.Object)
            {
                suffix = Text(tailnet, "MagicDNSSuffix");
            }

            TailscaleNode? self = root.TryGetProperty("Self", out var selfElement) && selfElement.ValueKind == JsonValueKind.Object
                ? NodeOf(selfElement)
                : null;

            var peers = new List<TailscalePeer>();
            if (root.TryGetProperty("Peer", out var peerMap) && peerMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in peerMap.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.Object)
                    {
                        peers.Add(PeerOf(entry.Value, entry.Name));
                    }
                }
            }

            return new TailscaleStatus(
                state,
                state == TailscaleState.Running ? self : null,
                string.IsNullOrEmpty(suffix) ? null : suffix,
                peers.OrderBy(peer => peer.Node.HostName, StringComparer.OrdinalIgnoreCase).ToList(),
                null);
        }
    }

    /// <summary>The node in a whois answer, or null when the address is not a tailnet node.</summary>
    public static TailscaleWhoIs? ParseWhoIs(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Node", out var node)
                || node.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var id = Text(node, "StableID");
            return string.IsNullOrEmpty(id) ? null : new TailscaleWhoIs(id, TrimDot(Text(node, "Name")));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TailscalePeer PeerOf(JsonElement element, string key)
    {
        var node = NodeOf(element, key);
        var online = element.TryGetProperty("Online", out var onlineElement) && onlineElement.ValueKind == JsonValueKind.True;
        var currentAddress = Text(element, "CurAddr");
        var relay = Text(element, "Relay");

        // A set CurAddr is a direct path. Without one, a set Relay means traffic goes through DERP. An idle peer has neither.
        var path = !online ? TailscalePath.Unknown
            : currentAddress.Length > 0 ? TailscalePath.Direct
            : relay.Length > 0 ? TailscalePath.Relayed
            : TailscalePath.Unknown;
        return new TailscalePeer(node, online, path, path == TailscalePath.Relayed ? relay : null);
    }

    private static TailscaleNode NodeOf(JsonElement element, string? fallbackId = null)
    {
        var id = Text(element, "ID");
        if (id.Length == 0)
        {
            id = Text(element, "StableID");
        }

        return new TailscaleNode(
            id.Length > 0 ? id : fallbackId ?? string.Empty,
            Text(element, "HostName"),
            TrimDot(Text(element, "DNSName")),
            Strings(element, "TailscaleIPs"),
            Strings(element, "Tags"));
    }

    private static TailscaleState StateOf(string backendState) => backendState switch
    {
        "Running" => TailscaleState.Running,
        "NeedsLogin" or "NoState" => TailscaleState.NeedsLogin,
        "Stopped" => TailscaleState.Stopped,
        _ => TailscaleState.Other,
    };

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static List<string> Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
            : [];

    private static string TrimDot(string name) => name.TrimEnd('.');
}
