namespace BastionFlow.Core.Cache;

/// <summary>
/// Maps Azure VM resource name (within a tenant) to the actual Entra device
/// displayName used for AAD-RDP target lookup. Persisted per-tenant.
/// </summary>
public interface IDeviceNameCache
{
    Task<string?> TryGetAsync(string tenantId, string vmName, CancellationToken ct = default);
    Task SetAsync(string tenantId, string vmName, string entraDeviceName, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(string tenantId, CancellationToken ct = default);
}
