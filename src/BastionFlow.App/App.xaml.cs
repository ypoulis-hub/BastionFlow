using System.Windows;
using System.Windows.Interop;
using BastionFlow.App.ViewModels;
using BastionFlow.App.Views;
using BastionFlow.Core.Auth;
using BastionFlow.Core.Cache;
using Microsoft.Extensions.DependencyInjection;

namespace BastionFlow.App;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    private static MainWindow? _mainWindow;

    /// <summary>HWND of the main window — required by MSAL WAM broker for the auth dialog parent.</summary>
    private static IntPtr GetMainWindowHandle()
    {
        if (_mainWindow is null) return IntPtr.Zero;
        return new WindowInteropHelper(_mainWindow).EnsureHandle();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDeviceNameCache>(_ => new JsonFileDeviceNameCache());
        services.AddSingleton<AzureSignInService>(_ => new AzureSignInService(parentWindowHandleProvider: GetMainWindowHandle));
        services.AddSingleton<MsalTokenCredential>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        Services = services.BuildServiceProvider();

        _mainWindow = Services.GetRequiredService<MainWindow>();
        _mainWindow.DataContext = Services.GetRequiredService<MainViewModel>();
        _mainWindow.Show();

        base.OnStartup(e);
    }
}
