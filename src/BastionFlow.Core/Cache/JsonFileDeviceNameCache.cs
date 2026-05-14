using System.Text.Json;

namespace BastionFlow.Core.Cache;

/// <summary>
/// Per-tenant JSON file cache at %AppData%\BastionFlow\cache\{tenantId}.json.
/// Safe for concurrent reads; writes are serialized.
/// </summary>
public sealed class JsonFileDeviceNameCache : IDeviceNameCache
{
    private readonly string _root;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonFileDeviceNameCache(string? rootOverride = null)
    {
        _root = rootOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "BastionFlow", "cache");
        Directory.CreateDirectory(_root);
    }

    private string FileFor(string tenantId) => Path.Combine(_root, $"{tenantId}.json");

    public async Task<string?> TryGetAsync(string tenantId, string vmName, CancellationToken ct = default)
    {
        var all = await GetAllAsync(tenantId, ct).ConfigureAwait(false);
        return all.TryGetValue(vmName, out var entra) ? entra : null;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(string tenantId, CancellationToken ct = default)
    {
        var path = FileFor(tenantId);
        if (!File.Exists(path)) return new Dictionary<string, string>();
        await using var fs = File.OpenRead(path);
        var dict = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(fs, cancellationToken: ct)
                   .ConfigureAwait(false);
        return dict ?? new Dictionary<string, string>();
    }

    public async Task SetAsync(string tenantId, string vmName, string entraDeviceName, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = FileFor(tenantId);
            var dict = (await GetAllAsync(tenantId, ct).ConfigureAwait(false)).ToDictionary(k => k.Key, v => v.Value);
            dict[vmName] = entraDeviceName;
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
