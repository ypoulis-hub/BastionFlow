using BastionFlow.Core.Models;

namespace BastionFlow.Core.Bastion;

/// <summary>
/// Picks the best Bastion for a given VM. Preference order:
///   1. Bastion in the same vnet as the VM.
///   2. Bastion in a vnet directly peered to the VM's vnet.
///   3. Any reachable Bastion (last resort).
/// </summary>
public sealed class BastionPicker
{
    private readonly IReadOnlyList<BastionEndpoint> _bastions;
    private readonly VnetReachabilityIndex _reach;

    public BastionPicker(IReadOnlyList<BastionEndpoint> bastions, VnetReachabilityIndex reach)
    {
        _bastions = bastions;
        _reach = reach;
    }

    public BastionEndpoint? Pick(AzureVm vm)
    {
        if (_bastions.Count == 0) return null;
        if (_bastions.Count == 1) return _bastions[0];

        // Tier 1: same vnet.
        var same = _bastions.FirstOrDefault(b =>
            !string.IsNullOrEmpty(b.VnetId) &&
            !string.IsNullOrEmpty(vm.VnetId) &&
            string.Equals(b.VnetId, vm.VnetId, StringComparison.OrdinalIgnoreCase));
        if (same is not null) return same;

        // Tier 2: peered vnet.
        var peered = _bastions.FirstOrDefault(b => _reach.IsReachable(b.VnetId, vm.VnetId));
        if (peered is not null) return peered;

        // Tier 3: fall back to the first one (better than nothing — Bastion may still reach via
        // hub-spoke chains we didn't traverse, or via cross-subscription routes).
        return _bastions[0];
    }
}
