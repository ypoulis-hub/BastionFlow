using System.Diagnostics;
using System.Text;
using BastionFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BastionFlow.Core.Bastion;

/// <summary>
/// Drives the proven flow:
///   1. `az network bastion rdp --target-resource-id … --enable-mfa --configure`
///      creates the .rdp file (and auto-launches mstsc; that mstsc gets killed
///      because it loaded the unedited file).
///   2. Apply our edits: enablerdsaadauth=1, enablecredsspsupport=1,
///      prompt for credentials=0, targetisaadjoined=1, replace `full address`
///      with the AAD device name, strip signature/signscope.
///   3. Launch msrdc.exe with the modified file (via Invoke-Item-equivalent
///      file-association — Start-Process msrdc.exe -Wait misbehaves).
/// </summary>
public enum ConnectMode
{
    /// <summary>AAD-RDP via msrdc with our learned .rdp edits (default).</summary>
    AadRdp,
    /// <summary>CredSSP / classic NTLM path — leave the .rdp untouched, user types
    /// a local admin (e.g. <c>hotelbrainadm</c>) at the credential prompt.
    /// Used as fallback when AAD-RDP returns 293004.</summary>
    LocalAdmin,
}

public sealed class BastionRdpOrchestrator
{
    private readonly ILogger<BastionRdpOrchestrator> _log;

    public BastionRdpOrchestrator(ILogger<BastionRdpOrchestrator>? log = null)
    {
        _log = log ?? NullLogger<BastionRdpOrchestrator>.Instance;
    }

    public async Task<ConnectResult> ConnectAsync(
        AzureVm vm,
        BastionEndpoint bastion,
        string? aadDeviceName,
        ConnectMode mode = ConnectMode.AadRdp,
        CancellationToken ct = default)
    {
        var before = new HashSet<string>(SnapshotTempRdpFiles(), StringComparer.OrdinalIgnoreCase);
        var mstscBefore = SnapshotMstscPids();

        // Run az in the background so we can race the auto-launched mstsc.
        var azTask = Task.Run(() => RunAzAsync(vm, bastion, mode, ct), ct);

        // Poll for the new .rdp file + spawned mstsc.
        string? rdpPath = null;
        int[] spawnedMstsc = Array.Empty<int>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            rdpPath ??= SnapshotTempRdpFiles()
                .Where(p => !before.Contains(p))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            spawnedMstsc = SnapshotMstscPids().Except(mstscBefore).ToArray();
            if (rdpPath is not null && spawnedMstsc.Length > 0) break;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        // Kill the auto-spawned mstsc so it doesn't keep displaying the unedited file.
        foreach (var pid in spawnedMstsc)
        {
            try { Process.GetProcessById(pid).Kill(entireProcessTree: false); }
            catch (Exception ex) { _log.LogDebug(ex, "Could not kill mstsc {Pid}", pid); }
        }

        // Let az finish writing.
        try { await azTask.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "az wait timed out"); }

        if (rdpPath is null)
        {
            return new ConnectResult(false, null, "az did not produce a .rdp file within 30 s.");
        }

        // For both paths, force single-monitor on the primary screen. These display
        // fields are NOT in Bastion's signscope, so modifying them does not invalidate
        // the signature (so we can apply them in the LocalAdmin path too).
        var lines = (await File.ReadAllLinesAsync(rdpPath, ct).ConfigureAwait(false)).ToList();
        SetFlag(lines, "use multimon:i:", "0");
        SetFlag(lines, "selectedmonitors:s:", "0");
        SetFlag(lines, "screen mode id:i:", "2");      // 1 = windowed, 2 = fullscreen
        SetFlag(lines, "smart sizing:i:", "1");
        SetFlag(lines, "dynamic resolution:i:", "1");

        if (mode == ConnectMode.AadRdp)
        {
            // AAD-RDP edits (signed fields → must strip signature afterwards).
            SetFlag(lines, "enablerdsaadauth:i:", "1");
            SetFlag(lines, "enablecredsspsupport:i:", "1");
            SetFlag(lines, "prompt for credentials:i:", "0");
            SetFlag(lines, "targetisaadjoined:i:", "1");
            if (!string.IsNullOrEmpty(aadDeviceName))
            {
                SetFlag(lines, "full address:s:", $"{aadDeviceName}:3389");
                SetFlag(lines, "alternate full address:s:", $"{aadDeviceName}:3389");
            }
            lines = lines.Where(l => !l.StartsWith("signature:s:", StringComparison.Ordinal)
                                  && !l.StartsWith("signscope:s:", StringComparison.Ordinal))
                         .ToList();
        }
        // LocalAdmin path: leaves signed fields alone, only display tweaks above.

        await File.WriteAllLinesAsync(rdpPath, lines, new UTF8Encoding(false), ct).ConfigureAwait(false);

        // Launch via OS file handler (msrdc if installed, else mstsc).
        var psi = new ProcessStartInfo { FileName = rdpPath, UseShellExecute = true };
        Process.Start(psi);

        return new ConnectResult(true, rdpPath, null);
    }

    private static List<string> SnapshotTempRdpFiles()
    {
        try
        {
            return Directory
                .EnumerateFiles(Path.GetTempPath(), "conn_*.rdp", SearchOption.TopDirectoryOnly)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    private static List<int> SnapshotMstscPids()
    {
        try { return Process.GetProcessesByName("mstsc").Select(p => p.Id).ToList(); }
        catch { return new List<int>(); }
    }

    private static void SetFlag(List<string> lines, string prefix, string value)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                lines[i] = prefix + value;
                return;
            }
        }
        lines.Add(prefix + value);
    }

    private async Task<int> RunAzAsync(AzureVm vm, BastionEndpoint bastion, ConnectMode mode, CancellationToken ct)
    {
        // --enable-mfa is required for the AAD-RDP path. For local-admin we omit
        // it so Bastion issues a token for plain CredSSP transport.
        var args = new List<string>
        {
            "network", "bastion", "rdp",
            "--name", bastion.Name,
            "--resource-group", bastion.ResourceGroup,
            "--target-resource-id", vm.ResourceId,
            "--configure"
        };
        if (mode == ConnectMode.AadRdp) args.Add("--enable-mfa");

        // az is usually shimmed via az.cmd on PATH. Start-Info needs the .cmd to
        // be hosted by cmd /c so the shell resolves it.
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "az", },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start az.");
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return p.ExitCode;
    }

    public sealed record ConnectResult(bool Success, string? RdpFilePath, string? Error);
}
