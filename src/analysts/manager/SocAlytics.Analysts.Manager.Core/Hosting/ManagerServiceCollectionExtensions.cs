using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<Autostart.IAutostartRegistrar, Autostart.XdgAutostartRegistrar>();
        }
        else if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<Autostart.IAutostartRegistrar, Autostart.WindowsRunKeyRegistrar>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<Autostart.IAutostartRegistrar, Autostart.MacAutostartRegistrar>();
        }

        services.AddSingleton<DeviceKeyProviderSelector>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ProofFactory>();
        services.AddHttpClient<PlatformClient>();
        services.AddSingleton<Operating.AdmissionGate>();
        services.AddSingleton<Work.AttemptRegistry>();
        services.AddSingleton<Operating.IManagerWorkTracker>(sp => sp.GetRequiredService<Work.AttemptRegistry>());
        services.AddSingleton<Work.WorkSessionProvider>();
        services.AddSingleton<Queue.QueueCredentialProvider>();
        services.AddSingleton<Queue.IQueueConnection>(sp => new Queue.RenewingQueueConnection(
            sp.GetRequiredService<Queue.QueueCredentialProvider>(), sp.GetService<IHostEnvironment>()));
        services.AddSingleton(Work.HostCapacity.Detect());
        services.AddSingleton<Work.ConcurrencyLimit>();
        services.AddSingleton<Work.WorkAcquisitionService>();
        services.AddHostedService(sp => sp.GetRequiredService<Work.WorkAcquisitionService>());
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
        services.Configure<Runtime.Simulated.SimulatedAnalystOptions>(
            configuration.GetSection(Runtime.Simulated.SimulatedAnalystOptions.SectionName));
        services.AddSingleton(CreateRuntimeAdapterCatalog);
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

    private static Runtime.RuntimeAdapterCatalog CreateRuntimeAdapterCatalog(IServiceProvider sp)
    {
        var environment = sp.GetService<IHostEnvironment>();
        var simulatedAllowed = environment is not null
            && (environment.IsDevelopment() || environment.IsEnvironment("Test"));
        if (!simulatedAllowed)
        {
            var profile = sp.GetRequiredService<IOptions<RuntimeOptions>>().Value.Profile;
            if (string.Equals(profile, Runtime.Simulated.SimulatedRuntimeAdapter.TestProfile, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Runtime:Profile 'simulated' is only available in the Development and Test environments; select a validated runtime profile.");
            }

            return new Runtime.RuntimeAdapterCatalog([]);
        }

        return new Runtime.RuntimeAdapterCatalog(
        [
            new Runtime.Simulated.SimulatedRuntimeAdapter(
                sp.GetRequiredService<IOptions<Runtime.Simulated.SimulatedAnalystOptions>>(),
                sp.GetRequiredService<TimeProvider>()),
        ]);
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
