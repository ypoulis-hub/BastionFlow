using Azure.Core;
using BastionFlow.Core.Cache;
using BastionFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BastionFlow.Core.Azure;

/// <summary>
/// Resolves the Entra device displayName to use as the AAD-RDP target identifier
/// for a given Azure VM, applying four strategies in order:
///
///   0. Per-tenant JSON cache (instant once warm).
///   1. AVD session host name → Entra device search (works for AVD VMs).
///   2. VM's osProfile.computerName + Entra device search (works when the OS
///      hostname matches the Entra display name, e.g. unrenamed VMs).
///   3. Live `hostname` via `az vm run-command invoke` (~30 s, bullet-proof,
///      result is cached).
///
/// Returns null if no strategy resolves a name; the caller should then fall
/// back to the Azure resource name (and likely fail with 293004).
/// </summary>
public sealed class AadDeviceResolver
{
    private readonly TokenCredential _credential;
    private readonly IDeviceNameCache _cache;
    private readonly EntraDeviceLookup _entra;
    private readonly Func<string, string, string, CancellationToken, Task<string?>>? _liveHostnameFetcher;
    private readonly ILogger<AadDeviceResolver> _log;

    public AadDeviceResolver(
        TokenCredential credential,
        IDeviceNameCache cache,
        EntraDeviceLookup entra,
        Func<string, string, string, CancellationToken, Task<string?>>? liveHostnameFetcher = null,
        ILogger<AadDeviceResolver>? log = null)
    {
        _credential = credential;
        _cache = cache;
        _entra = entra;
        _liveHostnameFetcher = liveHostnameFetcher;
        _log = log ?? NullLogger<AadDeviceResolver>.Instance;
    }

    public sealed record Resolution(string DeviceName, string Strategy);

    public async Task<Resolution?> ResolveAsync(
        string tenantId,
        AzureVm vm,
        AvdSessionHostIndex? avdIndex = null,
        string? osProfileComputerName = null,
        CancellationToken ct = default)
    {
        // Strategy 0: cache.
        var cached = await _cache.TryGetAsync(tenantId, vm.Name, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(cached))
        {
            _log.LogDebug("AAD device cache hit: {Vm} -> {Name}", vm.Name, cached);
            return new Resolution(cached, "cache");
        }

        // Strategy 1: AVD session host name.
        var entry = avdIndex?.Lookup(vm.ResourceId);
        if (entry is not null)
        {
            var full = await _entra.ResolveAsync(entry.SessionHostName, ct).ConfigureAwait(false)
                       ?? entry.SessionHostName;
            await _cache.SetAsync(tenantId, vm.Name, full, ct).ConfigureAwait(false);
            return new Resolution(full, "avd-session-host");
        }

        // Strategy 2: osProfile.computerName.
        if (!string.IsNullOrEmpty(osProfileComputerName))
        {
            var dev = await _entra.ResolveAsync(osProfileComputerName, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(dev))
            {
                await _cache.SetAsync(tenantId, vm.Name, dev, ct).ConfigureAwait(false);
                return new Resolution(dev, "osprofile-computername");
            }
        }

        // Strategy 3: live hostname (only if a fetcher is wired in — caller decides
        // whether to pay the ~30 s cost).
        if (_liveHostnameFetcher is not null)
        {
            var live = await _liveHostnameFetcher(vm.SubscriptionId, vm.ResourceGroup, vm.Name, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(live))
            {
                var dev = await _entra.ResolveAsync(live, ct).ConfigureAwait(false) ?? live;
                await _cache.SetAsync(tenantId, vm.Name, dev, ct).ConfigureAwait(false);
                return new Resolution(dev, "live-hostname");
            }
        }

        return null;
    }
}
