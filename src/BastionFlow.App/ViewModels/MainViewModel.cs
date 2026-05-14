using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using BastionFlow.Core.Auth;
using BastionFlow.Core.Azure;
using BastionFlow.Core.Bastion;
using BastionFlow.Core.Cache;
using BastionFlow.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BastionFlow.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AzureSignInService _signIn;
    private readonly MsalTokenCredential _credential;
    private readonly IDeviceNameCache _cache;

    private string? _tenantId;
    private IReadOnlyList<BastionEndpoint>? _bastions;
    private VnetReachabilityIndex? _reach;
    private BastionPicker? _picker;

    public ObservableCollection<AzureVm> Vms { get; } = new();
    public ICollectionView VmsView { get; }

    [ObservableProperty] private string statusText = "Ready. Sign in to discover your VMs.";
    [ObservableProperty] private string? statusDetail;   // full multi-line text for tooltip
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? account;
    [ObservableProperty] private bool isSignedIn;
    [ObservableProperty] private int vmCount;
    [ObservableProperty] private int resolvedCount;
    [ObservableProperty] private string filterText = string.Empty;

    /// <summary>Sets status to a single-line summary, with full text available as a tooltip.</summary>
    private void SetStatus(string summary, string? detail = null)
    {
        StatusText = summary;
        StatusDetail = detail;
    }

    private void SetError(string contextLabel, Exception ex)
    {
        var firstLine = (ex.Message ?? "").Split('\n', '\r').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "(no message)";
        // Trim runaway lengths so the status bar never explodes.
        if (firstLine.Length > 200) firstLine = firstLine[..200] + "…";
        SetStatus($"{contextLabel}: {firstLine}", ex.ToString());
    }

    // 293004 fallback banner state
    [ObservableProperty] private bool showFallbackBanner;
    [ObservableProperty] private string fallbackBannerText = string.Empty;
    private AzureVm? _fallbackVm;

    public MainViewModel(AzureSignInService signIn, MsalTokenCredential credential, IDeviceNameCache cache)
    {
        _signIn = signIn;
        _credential = credential;
        _cache = cache;

        VmsView = CollectionViewSource.GetDefaultView(Vms);
        VmsView.Filter = FilterVm;
        VmsView.SortDescriptions.Add(new SortDescription(nameof(AzureVm.Name), ListSortDirection.Ascending));
    }

    partial void OnFilterTextChanged(string value) => VmsView.Refresh();

    private bool FilterVm(object obj)
    {
        if (obj is not AzureVm vm) return false;
        if (string.IsNullOrWhiteSpace(FilterText)) return true;
        var q = FilterText.Trim();
        return vm.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.ResourceGroup.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (vm.AadDeviceName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (vm.AvdHostPool?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusText = "Signing in via Windows account broker...";
            var token = await _signIn.AcquireTokenForArmAsync();
            _tenantId = token.TenantId;
            Account = token.Account.Username;
            IsSignedIn = true;
            await LoadVmsAsync();
        }
        catch (Exception ex) { SetError("Sign-in failed", ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy || !IsSignedIn) return;
        IsBusy = true;
        try
        {
            _bastions = null; _reach = null; _picker = null;
            await LoadVmsAsync();
        }
        catch (Exception ex) { SetError("Refresh failed", ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void SignOut()
    {
        Vms.Clear();
        VmCount = ResolvedCount = 0;
        Account = null;
        _tenantId = null;
        _bastions = null; _reach = null; _picker = null;
        IsSignedIn = false;
        ShowFallbackBanner = false;
        StatusText = "Signed out. Sign in to discover your VMs.";
    }

    [RelayCommand]
    private void ShowAbout()
    {
        var dlg = new Views.AboutWindow { Owner = App.Current.MainWindow };
        dlg.ShowDialog();
    }

    [RelayCommand]
    private void DismissFallback()
    {
        ShowFallbackBanner = false;
        _fallbackVm = null;
    }

    [RelayCommand]
    private Task RetryWithLocalAdminAsync()
    {
        var vm = _fallbackVm;
        ShowFallbackBanner = false;
        _fallbackVm = null;
        return vm is null ? Task.CompletedTask : ConnectInternalAsync(vm, ConnectMode.LocalAdmin);
    }

    private async Task LoadVmsAsync()
    {
        StatusText = $"Discovering VMs in tenant {_tenantId}...";
        Vms.Clear();
        VmCount = ResolvedCount = 0;
        ShowFallbackBanner = false;

        var discovery = new VmDiscoveryService(_credential);
        var entra = new EntraDeviceLookup(_signIn);
        var resolver = new AadDeviceResolver(_credential, _cache, entra);
        var avdIndexPerSub = new Dictionary<string, AvdSessionHostIndex>(StringComparer.OrdinalIgnoreCase);

        await foreach (var vm in discovery.ListVmsAsync())
        {
            if (!avdIndexPerSub.TryGetValue(vm.SubscriptionId, out var avd))
            {
                avd = await AvdSessionHostIndex.BuildAsync(_credential, vm.SubscriptionId);
                avdIndexPerSub[vm.SubscriptionId] = avd;
            }

            var entry = avd.Lookup(vm.ResourceId);
            var enriched = vm with
            {
                AvdHostPool = entry?.HostPoolName,
                AvdSessionHostStatus = entry?.Status,
            };

            var resolution = await resolver.ResolveAsync(
                _tenantId!, enriched, avd, osProfileComputerName: enriched.OsProfileComputerName);
            if (resolution is not null) enriched = enriched with { AadDeviceName = resolution.DeviceName };

            App.Current.Dispatcher.Invoke(() =>
            {
                Vms.Add(enriched);
                VmCount = Vms.Count;
                ResolvedCount = Vms.Count(v => !string.IsNullOrEmpty(v.AadDeviceName));
                StatusText = $"Discovered {VmCount} VM(s)...";
            });
        }

        // Build Bastion picker once VMs are known (so we can include their vnets in the reachability graph).
        await EnsureBastionPickerAsync();
        var bastionsLabel = _bastions is { Count: > 0 } ? $" · {_bastions.Count} Bastion(s)" : " · no Bastion reachable";
        var vmsLabel = VmCount == 0
            ? "No Windows VMs found you can access in this tenant"
            : $"{VmCount} VM(s) · {ResolvedCount} with AAD device resolved";
        SetStatus($"Signed in as {Account} · {vmsLabel}{bastionsLabel}");
    }

    private async Task EnsureBastionPickerAsync()
    {
        if (_picker is not null) return;
        var discovery = new BastionDiscoveryService(_credential);
        _bastions = await discovery.ListAllAsync();
        // Collect all unique vnet ids (bastions + VMs) so the reachability index has the right anchors.
        var allVnets = _bastions.Select(b => b.VnetId)
                                .Concat(Vms.Select(v => v.VnetId))
                                .Where(v => !string.IsNullOrEmpty(v))!
                                .Cast<string>();
        _reach = await VnetReachabilityIndex.BuildAsync(_credential, allVnets);
        _picker = new BastionPicker(_bastions, _reach);
    }

    [RelayCommand]
    private Task ConnectAsync(AzureVm? vm) => vm is null ? Task.CompletedTask : ConnectInternalAsync(vm, ConnectMode.AadRdp);

    [RelayCommand]
    private Task ConnectLocalAdminAsync(AzureVm? vm) => vm is null ? Task.CompletedTask : ConnectInternalAsync(vm, ConnectMode.LocalAdmin);

    private async Task ConnectInternalAsync(AzureVm vm, ConnectMode mode)
    {
        if (IsBusy) return;
        IsBusy = true;
        ShowFallbackBanner = false;
        try
        {
            // Resolve AAD device name on-demand for AAD path only.
            if (mode == ConnectMode.AadRdp && string.IsNullOrEmpty(vm.AadDeviceName) && _tenantId is not null)
            {
                vm = await ResolveLiveHostnameAsync(vm) ?? vm;
            }

            await EnsureBastionPickerAsync();
            var bastion = _picker?.Pick(vm);
            if (bastion is null)
            {
                StatusText = "No Bastion found in any reachable subscription.";
                return;
            }

            var modeLabel = mode == ConnectMode.AadRdp ? "AAD" : "local admin";
            StatusText = $"Connecting to {vm.Name} via {bastion.Name} ({modeLabel})...";
            var orchestrator = new BastionRdpOrchestrator();
            var result = await orchestrator.ConnectAsync(vm, bastion, vm.AadDeviceName, mode);
            if (!result.Success)
            {
                StatusText = $"Connect failed: {result.Error}";
                return;
            }

            StatusText = mode == ConnectMode.AadRdp
                ? "Launched. Accept the publisher warning and sign in with your Entra account."
                : "Launched. Type a local admin credential (e.g. hotelbrainadm) at the prompt.";

            // After AAD-RDP, poll Entra sign-in logs for a 293004 against this user.
            if (mode == ConnectMode.AadRdp && !string.IsNullOrEmpty(Account))
            {
                _ = WatchForTargetDeviceNotFoundAsync(vm, Account!);
            }
        }
        catch (Exception ex) { SetError("Connect failed", ex); }
        finally { IsBusy = false; }
    }

    private async Task<AzureVm?> ResolveLiveHostnameAsync(AzureVm vm)
    {
        if (_tenantId is null) return null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var ticker = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var s = (int)elapsed.Elapsed.TotalSeconds;
                App.Current.Dispatcher.Invoke(() =>
                    StatusText = $"Resolving live hostname for {vm.Name} via run-command... ({s}s elapsed, up to 120s)");
                try { await Task.Delay(1000, cts.Token); } catch (TaskCanceledException) { break; }
            }
        }, cts.Token);

        var probe = new LiveHostnameProbe(_credential);
        var entra = new EntraDeviceLookup(_signIn);
        var resolver = new AadDeviceResolver(_credential, _cache, entra,
            liveHostnameFetcher: (sub, rg, name, ct) => probe.RunAsync(sub, rg, name, ct));
        try
        {
            var resolution = await resolver.ResolveAsync(_tenantId, vm, avdIndex: null,
                osProfileComputerName: vm.OsProfileComputerName, ct: cts.Token);
            if (resolution is null) return null;

            var idx = Vms.IndexOf(vm);
            var updated = vm with { AadDeviceName = resolution.DeviceName };
            if (idx >= 0) App.Current.Dispatcher.Invoke(() =>
            {
                Vms[idx] = updated;
                ResolvedCount = Vms.Count(v => !string.IsNullOrEmpty(v.AadDeviceName));
            });
            StatusText = $"AAD device resolved: {updated.AadDeviceName} ({resolution.Strategy}, {elapsed.Elapsed.TotalSeconds:F0}s)";
            return updated;
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Live hostname probe timed out after {elapsed.Elapsed.TotalSeconds:F0}s.";
            return null;
        }
        finally
        {
            cts.Cancel();
            try { await ticker; } catch { }
        }
    }

    /// <summary>
    /// Poll Entra sign-in logs for ~2 minutes after Connect. If we see a 293004
    /// against the Microsoft Remote Desktop app for this user, surface the
    /// fallback banner. Status bar shows that polling is in progress so the
    /// user knows it's not stuck.
    /// </summary>
    private async Task WatchForTargetDeviceNotFoundAsync(AzureVm vm, string upn)
    {
        var probe = new SignInFailureProbe(_signIn);
        var pollStart = DateTime.UtcNow;
        var deadline = pollStart.AddMinutes(2);
        // First check at 20 s (typical msrdc → Entra round-trip), then every 15 s.
        try { await Task.Delay(TimeSpan.FromSeconds(20)); } catch { return; }

        while (DateTime.UtcNow < deadline)
        {
            var failure = await probe.FindRecentFailureAsync(upn, TimeSpan.FromMinutes(3));
            if (failure is not null)
            {
                var since = (DateTime.UtcNow - failure.At.UtcDateTime).TotalSeconds;
                if (failure.ErrorCode == SignInFailureProbe.TargetDeviceNotFoundErrorCode && since < 180)
                {
                    App.Current.Dispatcher.Invoke(() =>
                    {
                        _fallbackVm = vm;
                        FallbackBannerText = $"Entra ID rejected the AAD-RDP target for {vm.Name} (\"device not found\"). Retry with a local admin account instead?";
                        ShowFallbackBanner = true;
                        StatusText = $"Detected AAD-RDP failure (293004) for {vm.Name} — fallback offered.";
                    });
                    return;
                }
            }
            var elapsed = (int)(DateTime.UtcNow - pollStart).TotalSeconds;
            App.Current.Dispatcher.Invoke(() =>
            {
                // Only update status if user hasn't typed something more important.
                if (StatusText.StartsWith("Launched.") || StatusText.StartsWith("Watching"))
                {
                    StatusText = $"Watching Entra sign-in logs for {vm.Name} ({elapsed}s elapsed) — close the popup if AAD failed; CredSSP fallback will appear here.";
                }
            });
            try { await Task.Delay(TimeSpan.FromSeconds(15)); } catch { return; }
        }
    }
}
