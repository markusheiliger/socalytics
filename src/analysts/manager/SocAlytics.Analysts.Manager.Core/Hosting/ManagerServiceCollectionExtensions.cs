using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Hosting;

public static class ManagerServiceCollectionExtensions
{
    public static IServiceCollection AddManagerCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ManagerOptions>(configuration.GetSection(ManagerOptions.SectionName));
        services.Configure<Pkcs11Options>(configuration.GetSection(Pkcs11Options.SectionName));
        services.Configure<PlatformOptions>(configuration.GetSection(PlatformOptions.SectionName));
        services.Configure<OperatingOptions>(configuration.GetSection(OperatingOptions.SectionName));
        services.Configure<RuntimeOptions>(configuration.GetSection(RuntimeOptions.SectionName));
        if (OperatingSystem.IsLinux())
        {
            AddPkcs11(services);
        }

        if (OperatingSystem.IsWindows())
        {
            AddWindowsCng(services);
        }

        if (OperatingSystem.IsMacOS())
        {
            AddMacBridge(services);
        }

        services.AddSingleton<DeviceKeyProviderSelector>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ProofFactory>();
        services.AddHttpClient<PlatformClient>();
        services.AddSingleton<Operating.AdmissionGate>();
        services.AddSingleton<Operating.IManagerWorkTracker, Operating.NoManagerWork>();
        services.AddSingleton<Operating.DrainCoordinator>();
        services.AddSingleton(sp => new LocalState.SignedStateStore(
            ManagerStatePaths.Resolve(sp.GetRequiredService<IOptions<ManagerOptions>>().Value.StateDirectory),
            sp.GetServices<IDeviceKeyProvider>()));
        services.AddSingleton<Platform.InactiveSignalSink>();
        services.AddSingleton<Registration.ProtectedStoreFailureHandler>();
        services.AddSingleton<Platform.ManagerTokenCache>();
        services.AddSingleton<Registration.RegistrationStatusMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<Registration.RegistrationStatusMonitor>());
        services.AddSingleton<Registration.RegistrationCoordinator>();
        services.AddSingleton<Registration.RegistrationRestorer>();
        services.AddHostedService<Registration.RestoreHostedService>();
        services.AddSingleton(new Runtime.RuntimeAdapterCatalog([]));
        services.AddSingleton(_ => Runtime.RuntimeCompatibilityList.LoadEmbedded());
        services.AddSingleton<Runtime.PreflightRunner>();
        services.AddSingleton<Runtime.PreflightScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<Runtime.PreflightScheduler>());
        services.AddSingleton<Operating.TimedPauseScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<Operating.TimedPauseScheduler>());
        services.AddSingleton<ManagerControl>();
        services.AddSingleton<IManagerControl>(sp => sp.GetRequiredService<ManagerControl>());
        return services;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddWindowsCng(IServiceCollection services) =>
        services.AddSingleton<IDeviceKeyProvider, WindowsCngKeyProvider>();

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static void AddMacBridge(IServiceCollection services) =>
        services.AddSingleton<IDeviceKeyProvider, MacBridgeKeyProvider>();

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void AddPkcs11(IServiceCollection services) =>
        services.AddSingleton<IDeviceKeyProvider>(sp =>
        {
            var pkcs11 = sp.GetRequiredService<IOptions<Pkcs11Options>>().Value;
            var state = ManagerStatePaths.Resolve(sp.GetRequiredService<IOptions<ManagerOptions>>().Value.StateDirectory);
            return new Pkcs11KeyProvider(
                pkcs11.ModulePath, pkcs11.TokenLabel, state, new Pkcs11ModuleAllowList(pkcs11.ModulePath));
        });
}
