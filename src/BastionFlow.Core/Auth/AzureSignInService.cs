using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace BastionFlow.Core.Auth;

/// <summary>
/// Interactive + silent sign-in via MSAL with the WAM broker on Windows.
/// Uses the public Azure CLI client id (04b07795-…) so users don't have to
/// register their own app registration to try BastionFlow. Production users
/// can override via the constructor.
/// </summary>
public sealed class AzureSignInService
{
    /// <summary>Azure CLI public client id — broadly preauthorized for ARM/Graph delegated scopes.</summary>
    public const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    public const string ArmScope = "https://management.azure.com/.default";
    public const string GraphScope = "https://graph.microsoft.com/.default";

    private readonly IPublicClientApplication _app;
    private readonly Func<IntPtr>? _parentWindowHandleProvider;

    public AzureSignInService(string? clientId = null, Func<IntPtr>? parentWindowHandleProvider = null)
    {
        _parentWindowHandleProvider = parentWindowHandleProvider;
        _app = PublicClientApplicationBuilder
            .Create(clientId ?? AzureCliClientId)
            .WithDefaultRedirectUri()
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
            {
                Title = "BastionFlow"
            })
            .Build();
    }

    public Task<AuthenticationResult> AcquireTokenForArmAsync(CancellationToken ct = default)
        => AcquireTokenForScopesAsync(new[] { ArmScope }, ct);

    public Task<AuthenticationResult> AcquireTokenForGraphAsync(CancellationToken ct = default)
        => AcquireTokenForScopesAsync(new[] { GraphScope }, ct);

    public async Task<AuthenticationResult> AcquireTokenForScopesAsync(IEnumerable<string> scopes, CancellationToken ct = default)
    {
        var scopeArr = scopes.ToArray();
        var accounts = await _app.GetAccountsAsync().ConfigureAwait(false);
        try
        {
            return await _app.AcquireTokenSilent(scopeArr, accounts.FirstOrDefault())
                             .ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (MsalUiRequiredException)
        {
            var builder = _app.AcquireTokenInteractive(scopeArr);
            if (_parentWindowHandleProvider is not null)
            {
                builder = builder.WithParentActivityOrWindow(_parentWindowHandleProvider());
            }
            return await builder.ExecuteAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<IAccount?> GetSignedInAccountAsync()
    {
        var accounts = await _app.GetAccountsAsync().ConfigureAwait(false);
        return accounts.FirstOrDefault();
    }
}
