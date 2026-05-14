using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Resources;
using BastionFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BastionFlow.Core.Azure;

/// <summary>
/// Enumerates Windows VMs across all subscriptions reachable by the signed-in
/// credential. Power state is fetched via instance view (one extra call per VM
/// — acceptable for tens of VMs; if a tenant has hundreds we can batch later).
/// </summary>
public sealed class VmDiscoveryService
{
    private readonly TokenCredential _credential;
    private readonly ILogger<VmDiscoveryService> _log;

    public VmDiscoveryService(TokenCredential credential, ILogger<VmDiscoveryService>? log = null)
    {
        _credential = credential;
        _log = log ?? NullLogger<VmDiscoveryService>.Instance;
    }

    public async IAsyncEnumerable<AzureVm> ListVmsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var arm = new ArmClient(_credential);
        await foreach (var sub in arm.GetSubscriptions().GetAllAsync(ct).ConfigureAwait(false))
        {
            // Eagerly materialise this subscription's VMs so we can swallow auth
            // errors here (a 403 from one subscription must not abort the whole
            // tenant scan). Tenants commonly have many subs where the user has
            // no role at all.
            List<AzureVm>? subVms = null;
            try
            {
                subVms = await ListVmsInSubscriptionListAsync(sub, ct).ConfigureAwait(false);
            }
            catch (global::Azure.RequestFailedException ex) when (ex.Status is 401 or 403)
            {
                _log.LogInformation("Skipping subscription {Sub} ({Name}) — not authorised", sub.Data.SubscriptionId, sub.Data.DisplayName);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Subscription scan failed for {Sub}", sub.Data.SubscriptionId);
            }
            if (subVms is null) continue;
            foreach (var vm in subVms) yield return vm;
        }
    }

    private async Task<List<AzureVm>> ListVmsInSubscriptionListAsync(SubscriptionResource sub, CancellationToken ct)
    {
        var list = new List<AzureVm>();
        await foreach (var vm in ListVmsInSubscriptionAsync(sub, ct).ConfigureAwait(false))
        {
            list.Add(vm);
        }
        return list;
    }

    public async IAsyncEnumerable<AzureVm> ListVmsInSubscriptionAsync(
        SubscriptionResource sub,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var vm in sub.GetVirtualMachinesAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            AzureVm? result = null;
            try
            {
                var data = vm.Data;
                if (data.StorageProfile?.OSDisk?.OSType?.ToString() != "Windows")
                    continue;

                var instanceView = await vm.InstanceViewAsync(ct).ConfigureAwait(false);
                var power = instanceView.Value.Statuses
                    .FirstOrDefault(s => s.Code?.StartsWith("PowerState/", StringComparison.OrdinalIgnoreCase) == true)
                    ?.DisplayStatus ?? "unknown";

                var parts = vm.Id.ToString().Split('/');
                var subIdx = Array.IndexOf(parts, "subscriptions");
                var rgIdx = Array.IndexOf(parts, "resourceGroups");
                var subId = subIdx >= 0 ? parts[subIdx + 1] : sub.Data.SubscriptionId;
                var rg = rgIdx >= 0 ? parts[rgIdx + 1] : "?";

                var vnetId = await ResolveVmVnetAsync(vm, ct).ConfigureAwait(false);

                result = new AzureVm(
                    Name: data.Name,
                    ResourceGroup: rg,
                    SubscriptionId: subId,
                    Location: data.Location.Name,
                    PowerState: power,
                    OsType: "Windows",
                    AadDeviceName: null,
                    AvdHostPool: null,
                    AvdSessionHostStatus: null,
                    OsProfileComputerName: data.OSProfile?.ComputerName,
                    VnetId: vnetId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to inspect VM {Vm}", vm.Id);
            }
            if (result is not null) yield return result;
        }
    }

    /// <summary>Resolves the VM's primary NIC subnet/vnet via raw ARM REST (more
    /// durable than the strongly-typed Network SDK whose shape shifts between
    /// versions). Returns the vnet resource id.</summary>
    private async Task<string?> ResolveVmVnetAsync(VirtualMachineResource vm, CancellationToken ct)
    {
        try
        {
            var nicRef = vm.Data.NetworkProfile?.NetworkInterfaces?.FirstOrDefault();
            var nicId = nicRef?.Id?.ToString();
            if (string.IsNullOrEmpty(nicId)) return null;

            var armToken = await GetArmTokenAsync(ct).ConfigureAwait(false);
            if (armToken is null) return null;

            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://management.azure.com{nicId}?api-version=2023-09-01");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", armToken);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("properties", out var props)) return null;
            if (!props.TryGetProperty("ipConfigurations", out var ipcs)) return null;
            foreach (var cfg in ipcs.EnumerateArray())
            {
                if (!cfg.TryGetProperty("properties", out var cp)) continue;
                if (!cp.TryGetProperty("subnet", out var sn)) continue;
                if (!sn.TryGetProperty("id", out var snId)) continue;
                var subnetId = snId.GetString();
                if (string.IsNullOrEmpty(subnetId)) continue;
                var marker = "/subnets/";
                var idx = subnetId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                return idx > 0 ? subnetId.Substring(0, idx) : null;
            }
            return null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "VM vnet lookup failed for {Vm}", vm.Id);
            return null;
        }
    }

    private readonly HttpClient _http = new();

    private async Task<string?> GetArmTokenAsync(CancellationToken ct)
    {
        try
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }), ct).ConfigureAwait(false);
            return token.Token;
        }
        catch { return null; }
    }
}
