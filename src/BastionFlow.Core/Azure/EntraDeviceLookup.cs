using System.Net.Http.Headers;
using System.Text.Json;
using BastionFlow.Core.Auth;

namespace BastionFlow.Core.Azure;

/// <summary>
/// Thin wrapper over Graph /v1.0/devices search to resolve a NetBIOS-truncated
/// computer name to the full Entra device displayName (e.g.
/// "HBGAZ-THEOV-AVD" -> "HBGAZ-THEOV-AVD-1").
/// </summary>
public sealed class EntraDeviceLookup
{
    private readonly AzureSignInService _signIn;
    private readonly HttpClient _http;

    public EntraDeviceLookup(AzureSignInService signIn, HttpClient? http = null)
    {
        _signIn = signIn;
        _http = http ?? new HttpClient();
    }

    public async Task<string?> ResolveAsync(string candidate, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        var token = await _signIn.AcquireTokenForGraphAsync(ct).ConfigureAwait(false);
        var url = $"https://graph.microsoft.com/v1.0/devices?$search=\"displayName:{Uri.EscapeDataString(candidate)}\"";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        req.Headers.Add("ConsistencyLevel", "eventual");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            return null;

        // Pick the longest displayName that starts with our candidate prefix
        // (handles NetBIOS truncation: "HBGAZ-THEOV-AVD" -> "HBGAZ-THEOV-AVD-1").
        string? best = null;
        foreach (var dev in values.EnumerateArray())
        {
            if (!dev.TryGetProperty("displayName", out var nameProp)) continue;
            var name = nameProp.GetString();
            if (string.IsNullOrEmpty(name)) continue;
            if (!name.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null || name.Length > best.Length) best = name;
        }
        return best;
    }
}
