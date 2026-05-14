using Azure.Core;
using Microsoft.Identity.Client;

namespace BastionFlow.Core.Auth;

/// <summary>
/// Bridges MSAL.NET (which BastionFlow uses for WAM-friendly sign-in) to the
/// Azure SDK's <see cref="TokenCredential"/> abstraction so we can pass it to
/// ArmClient / Graph clients.
/// </summary>
public sealed class MsalTokenCredential : TokenCredential
{
    private readonly AzureSignInService _signIn;

    public MsalTokenCredential(AzureSignInService signIn)
    {
        _signIn = signIn;
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        // Azure SDK passes scopes as e.g. ["https://management.azure.com/.default"].
        var result = await _signIn.AcquireTokenForScopesAsync(requestContext.Scopes, cancellationToken).ConfigureAwait(false);
        return new AccessToken(result.AccessToken, result.ExpiresOn);
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();
}
