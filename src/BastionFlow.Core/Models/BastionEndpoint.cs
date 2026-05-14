namespace BastionFlow.Core.Models;

public sealed record BastionEndpoint(
    string Name,
    string ResourceGroup,
    string SubscriptionId,
    string Location,
    /// <summary>The vnet this Bastion lives in — used to score per-VM picking.</summary>
    string? VnetId = null
)
{
    public string ResourceId =>
        $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}/providers/Microsoft.Network/bastionHosts/{Name}";
}
