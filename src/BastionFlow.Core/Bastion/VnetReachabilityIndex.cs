using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BastionFlow.Core.Bastion;

/// <summary>
/// Builds an undirected reachability graph over vnets connected by Azure vnet
/// peerings (Connected state). Used to pick a Bastion whose vnet can reach the
/// target VM's vnet — directly or via one peering hop (hub-spoke).
/// </summary>
public sealed class VnetReachabilityIndex
{
    /// <summary>vnet resource id (lowercased) -&gt; set of directly peered vnet ids.</summary>
    private readonly Dictionary<string, HashSet<string>> _peers = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<VnetReachabilityIndex> BuildAsync(
        TokenCredential credential,
        IEnumerable<string> vnetIds,
        CancellationToken ct = default,
        ILogger? log = null)
    {
        log ??= NullLogger.Instance;
        var idx = new VnetReachabilityIndex();
        var arm = new ArmClient(credential);

        foreach (var vnetId in vnetIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(vnetId)) continue;
            try
            {
                var vnet = arm.GetVirtualNetworkResource(new ResourceIdentifier(vnetId));
                var data = (await vnet.GetAsync(cancellationToken: ct).ConfigureAwait(false)).Value;
                var set = idx.GetOrCreate(vnetId);
                foreach (var p in data.Data.VirtualNetworkPeerings)
                {
                    // PeeringState surfaces as string-ish enum across SDK versions; compare textually.
                    if (!string.Equals(p.PeeringState?.ToString(), "Connected", StringComparison.OrdinalIgnoreCase)) continue;
                    var remote = p.RemoteVirtualNetworkId?.ToString();
                    if (string.IsNullOrEmpty(remote)) continue;
                    set.Add(remote);
                    idx.GetOrCreate(remote).Add(vnetId); // mirror back-edge in case the other side isn't enumerated
                }
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Peering enumeration failed for vnet {Vnet}", vnetId);
            }
        }

        return idx;
    }

    private HashSet<string> GetOrCreate(string vnetId)
    {
        if (!_peers.TryGetValue(vnetId, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _peers[vnetId] = set;
        }
        return set;
    }

    /// <summary>True if <paramref name="fromVnet"/> equals or is directly peered with <paramref name="toVnet"/>.</summary>
    public bool IsReachable(string? fromVnet, string? toVnet)
    {
        if (string.IsNullOrEmpty(fromVnet) || string.IsNullOrEmpty(toVnet)) return false;
        if (string.Equals(fromVnet, toVnet, StringComparison.OrdinalIgnoreCase)) return true;
        return _peers.TryGetValue(fromVnet, out var set) && set.Contains(toVnet);
    }
}
