using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.DesktopVirtualization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BastionFlow.Core.Azure;

/// <summary>
/// Pre-fetches all AVD host pools + session hosts in a subscription and lets
/// callers look up by VM resource id. Built once per subscription per "Refresh"
/// — much cheaper than per-VM queries.
/// </summary>
public sealed class AvdSessionHostIndex
{
    /// <param name="VmResourceId">Lowercase for case-insensitive matching.</param>
    public sealed record Entry(string VmResourceId, string HostPoolName, string SessionHostName, string? Status);

    private readonly Dictionary<string, Entry> _byVmId = new(StringComparer.OrdinalIgnoreCase);

    public Entry? Lookup(string vmResourceId)
        => _byVmId.TryGetValue(vmResourceId, out var e) ? e : null;

    public static async Task<AvdSessionHostIndex> BuildAsync(
        TokenCredential credential,
        string subscriptionId,
        CancellationToken ct = default,
        ILogger? log = null)
    {
        log ??= NullLogger.Instance;
        var idx = new AvdSessionHostIndex();
        try
        {
            var arm = new ArmClient(credential);
            var sub = arm.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{subscriptionId}"));
            await foreach (var pool in sub.GetHostPoolsAsync(cancellationToken: ct).ConfigureAwait(false))
            {
                await foreach (var sh in pool.GetSessionHosts().GetAllAsync(cancellationToken: ct).ConfigureAwait(false))
                {
                    var data = sh.Data;
                    if (data.ResourceId is null) continue;
                    var name = data.Name?.Split('/')?.Last() ?? "?";
                    idx._byVmId[data.ResourceId.ToString()] = new Entry(
                        VmResourceId: data.ResourceId.ToString(),
                        HostPoolName: pool.Data.Name,
                        SessionHostName: name,
                        Status: data.Status?.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "AVD session host index build failed for subscription {Sub}", subscriptionId);
        }
        return idx;
    }
}
