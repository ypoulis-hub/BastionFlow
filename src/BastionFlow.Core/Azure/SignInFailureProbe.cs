using System.Net.Http.Headers;
using System.Text.Json;
using BastionFlow.Core.Auth;

namespace BastionFlow.Core.Azure;

/// <summary>
/// Polls the Entra ID sign-in logs (Graph beta) for recent failures of the
/// signed-in user against the Microsoft Remote Desktop service principal.
/// Used after a Connect attempt to detect 293004 ("target-device identifier
/// not found in tenant") and surface a fallback option to the user.
/// </summary>
public sealed class SignInFailureProbe
{
    public const int TargetDeviceNotFoundErrorCode = 293004;

    private readonly AzureSignInService _signIn;
    private readonly HttpClient _http;

    public SignInFailureProbe(AzureSignInService signIn, HttpClient? http = null)
    {
        _signIn = signIn;
        _http = http ?? new HttpClient();
    }

    public sealed record FailureSummary(int ErrorCode, string FailureReason, DateTimeOffset At, string Identifier);

    /// <summary>
    /// Returns the most recent sign-in failure for the user, if any, within
    /// <paramref name="lookback"/>. Filters by the Microsoft Remote Desktop app.
    /// </summary>
    public async Task<FailureSummary?> FindRecentFailureAsync(string userPrincipalName, TimeSpan lookback, CancellationToken ct = default)
    {
        try
        {
            var token = await _signIn.AcquireTokenForGraphAsync(ct).ConfigureAwait(false);
            var since = DateTime.UtcNow.Subtract(lookback).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var filter = $"userPrincipalName eq '{userPrincipalName}' and createdDateTime ge {since} and appDisplayName eq 'Microsoft Remote Desktop Client'";
            var url = "https://graph.microsoft.com/beta/auditLogs/signIns?$filter=" + Uri.EscapeDataString(filter) + "&$top=20";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
                return null;

            FailureSummary? best = null;
            foreach (var entry in values.EnumerateArray())
            {
                if (!entry.TryGetProperty("status", out var status)) continue;
                if (!status.TryGetProperty("errorCode", out var ec)) continue;
                var code = ec.GetInt32();
                if (code == 0) continue;
                var reason = status.TryGetProperty("failureReason", out var r) ? (r.GetString() ?? "") : "";
                var created = entry.TryGetProperty("createdDateTime", out var dt) ? dt.GetDateTimeOffset() : DateTimeOffset.MinValue;
                // Use the (signInIdentifier) or trace text — for 293004 the failureReason includes the targetDeviceId.
                var item = new FailureSummary(code, reason, created, "");
                if (best is null || item.At > best.At) best = item;
            }
            return best;
        }
        catch { return null; }
    }
}
