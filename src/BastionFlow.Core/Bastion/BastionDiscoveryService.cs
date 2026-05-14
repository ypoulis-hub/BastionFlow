using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.ResourceManager;
using BastionFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BastionFlow.Core.Bastion;

/// <summary>
/// Enumerates all Bastions visible to the credential and resolves each one's
/// vnet id (from its ipConfigurations subnet). Result is cached for the
/// session — call <see cref="Reset"/> to rebuild after a network change.
/// </summary>
public sealed class BastionDiscoveryService
{
    private readonly TokenCredential _credential;
    private readonly ILogger<BastionDiscoveryService> _log;
    private IReadOnlyList<BastionEndpoint>? _cached;

    public BastionDiscoveryService(TokenCredential credential, ILogger<BastionDiscoveryService>? log = null)
    {
        _credential = credential;
        _log = log ?? NullLogger<BastionDiscoveryService>.Instance;
    }

    public void Reset() => _cached = null;

    public async Task<IReadOnlyList<BastionEndpoint>> ListAllAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        var arm = new ArmClient(_credential);
        var result = new List<BastionEndpoint>();
        await foreach (var sub in arm.GetSubscriptions().GetAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await foreach (var res in sub.GetGenericResourcesAsync(filter: "resourceType eq 'Microsoft.Network/bastionHosts'", cancellationToken: ct).ConfigureAwait(false))
                {
                    var parts = res.Id.ToString().Split('/');
                    var subIdx = Array.IndexOf(parts, "subscriptions");
                    var rgIdx = Array.IndexOf(parts, "resourceGroups");
                    var nameIdx = Array.IndexOf(parts, "bastionHosts");
                    var subId = subIdx >= 0 ? parts[subIdx + 1] : sub.Data.SubscriptionId;
                    var rg = rgIdx >= 0 ? parts[rgIdx + 1] : "?";
                    var name = nameIdx >= 0 ? parts[nameIdx + 1] : res.Data.Name;

                    var vnetId = await ResolveBastionVnetAsync(arm, subId, rg, name, ct).ConfigureAwait(false);
                    result.Add(new BastionEndpoint(name, rg, subId, res.Data.Location.Name, vnetId));
                    _log.LogInformation("Bastion {Name} in {Rg} (vnet {Vnet})", name, rg, vnetId ?? "?");
                }
            }
            catch (global::Azure.RequestFailedException ex) when (ex.Status is 401 or 403)
            {
                _log.LogInformation("Skipping subscription {Sub} for Bastion scan — not authorised", sub.Data.SubscriptionId);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Bastion scan failed in subscription {Sub}", sub.Data.SubscriptionId);
            }
        }
        _cached = result;
        return result;
    }

    private async Task<string?> ResolveBastionVnetAsync(ArmClient arm, string subId, string rg, string name, CancellationToken ct)
    {
        // The strongly-typed SDK shape for BastionHostIPConfiguration changes
        // across versions. Going via raw ARM REST is more durable: GET the
        // resource and read properties.ipConfigurations[0].properties.subnet.id.
        try
        {
            var armToken = await GetArmTokenAsync(ct).ConfigureAwait(false);
            if (armToken is null) return null;
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://management.azure.com/subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.Network/bastionHosts/{name}?api-version=2023-09-01");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("properties", out var props)) return null;
            if (!props.TryGetProperty("ipConfigurations", out var ipcs) || ipcs.ValueKind != JsonValueKind.Array) return null;
            foreach (var cfg in ipcs.EnumerateArray())
            {
                if (!cfg.TryGetProperty("properties", out var cp)) continue;
                if (!cp.TryGetProperty("subnet", out var sn)) continue;
                if (!sn.TryGetProperty("id", out var snId)) continue;
                return ExtractVnetFromSubnet(snId.GetString());
            }
            return null;
        }
        catch { return null; }
    }

    private readonly HttpClient _http = new();

    private async Task<string?> GetArmTokenAsync(CancellationToken ct)
    {
        try
        {
            var ctx = new TokenRequestContext(new[] { "https://management.azure.com/.default" });
            var token = await _credential.GetTokenAsync(ctx, ct).ConfigureAwait(false);
            return token.Token;
        }
        catch { return null; }
    }

    private static string? ExtractVnetFromSubnet(string? subnetId)
    {
        if (string.IsNullOrEmpty(subnetId)) return null;
        // /subscriptions/.../resourceGroups/.../providers/Microsoft.Network/virtualNetworks/{vnet}/subnets/{subnet}
        var marker = "/subnets/";
        var idx = subnetId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? subnetId.Substring(0, idx) : null;
    }
}
