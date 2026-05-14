using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BastionFlow.Core.Azure;

/// <summary>
/// Runs <c>hostname</c> on the VM via Azure run-command (~20–30 s) to fetch the
/// actual current OS hostname — bullet-proof in the face of stale Azure
/// osProfile.computerName (e.g. when the OS was renamed after deployment).
/// </summary>
public sealed class LiveHostnameProbe
{
    private readonly TokenCredential _credential;
    private readonly ILogger<LiveHostnameProbe> _log;

    public LiveHostnameProbe(TokenCredential credential, ILogger<LiveHostnameProbe>? log = null)
    {
        _credential = credential;
        _log = log ?? NullLogger<LiveHostnameProbe>.Instance;
    }

    public async Task<string?> RunAsync(string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default)
    {
        try
        {
            var arm = new ArmClient(_credential);
            var vmId = VirtualMachineResource.CreateResourceIdentifier(subscriptionId, resourceGroup, vmName);
            var vm = arm.GetVirtualMachineResource(vmId);

            var input = new RunCommandInput("RunPowerShellScript");
            input.Script.Add("hostname");

            var op = await vm.RunCommandAsync(WaitUntil.Completed, input, ct).ConfigureAwait(false);
            var result = op.Value;
            // The result has a list of InstanceViewStatus; the StdOut comes through
            // as a status with code starting with "ComponentStatus/StdOut/" and the
            // message contains the script output.
            var stdoutStatus = result.Value
                .FirstOrDefault(s => s.Code is not null && s.Code.StartsWith("ComponentStatus/StdOut/", StringComparison.Ordinal));
            var message = stdoutStatus?.Message;
            if (string.IsNullOrWhiteSpace(message)) return null;

            // Pick the first non-empty, hostname-shaped line.
            var hostname = message
                .Split('\n', '\r')
                .Select(l => l.Trim())
                .FirstOrDefault(l => !string.IsNullOrEmpty(l)
                                  && l.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' || c == '_'));
            return hostname;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Live hostname probe failed for {Vm}", vmName);
            return null;
        }
    }
}
