namespace BastionFlow.Core.Models;

/// <summary>
/// Lightweight VM representation for the UI list. Populated from ARM and
/// enriched with AAD device name (when resolvable) and host pool info.
/// </summary>
public sealed record AzureVm(
    string Name,
    string ResourceGroup,
    string SubscriptionId,
    string Location,
    string PowerState,
    string OsType,
    string? AadDeviceName,
    string? AvdHostPool,
    string? AvdSessionHostStatus,
    /// <summary>OS-level computer name from Azure metadata (may be stale if VM was renamed post-deploy).</summary>
    string? OsProfileComputerName = null,
    /// <summary>Primary NIC's vnet resource id, used to pick the right Bastion. Null if not resolved yet.</summary>
    string? VnetId = null
)
{
    public string ResourceId =>
        $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}/providers/Microsoft.Compute/virtualMachines/{Name}";

    public bool IsRunning => PowerState.Equals("VM running", StringComparison.OrdinalIgnoreCase);
    public bool IsAvd => !string.IsNullOrEmpty(AvdHostPool);
}
