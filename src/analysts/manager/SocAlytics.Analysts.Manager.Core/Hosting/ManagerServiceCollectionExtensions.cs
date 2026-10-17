using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Hosting;

public static class ManagerServiceCollectionExtensions
{
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void AddLinuxAutostart(IServiceCollection services)
    {
        services.AddSingleton<Autostart.IProcessRunner, Autostart.SystemProcessRunner>();
        services.AddSingleton<Autostart.IAutostartRegistrar>(sp =>
            sp.GetRequiredService<ApplicationProfile>() == ApplicationProfile.HeadlessServer
                ? new Autostart.SystemdUserUnitRegistrar(
                    sp.GetRequiredService<Autostart.IProcessRunner>(), sp.GetRequiredService<IOptions<OperatingOptions>>())
                : new Autostart.XdgAutostartRegistrar());
    }

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
            AddLinuxAutostart(services);
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
        services.AddHttpClient(Work.ResultPublisher.StorageClientName);
        services.AddSingleton(sp =>
        {
            var environment = sp.GetService<IHostEnvironment>();
            var loopbackHttp = environment is not null && (environment.IsDevelopment() || environment.IsEnvironment("Test"));
            return new Work.ResultPublisher(
                sp.GetRequiredService<PlatformClient>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(Work.ResultPublisher.StorageClientName),
                sp.GetRequiredService<TimeProvider>(), loopbackHttp,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Work.ResultPublisher>());
        });
        services.Configure<Work.StagingOptions>(configuration.GetSection(Work.StagingOptions.SectionName));
        services.AddSingleton(sp =>
        {
            var environment = sp.GetService<IHostEnvironment>();
            var loopbackHttp = environment is not null && (environment.IsDevelopment() || environment.IsEnvironment("Test"));
            var staging = sp.GetRequiredService<IOptions<Work.StagingOptions>>().Value;
            staging.Validate();
            return new Work.UpstreamStager(
                sp.GetRequiredService<PlatformClient>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(Work.ResultPublisher.StorageClientName),
                sp.GetRequiredService<TimeProvider>(), staging, loopbackHttp,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Work.UpstreamStager>());
        });
        services.AddSingleton<Operating.AdmissionGate>();
        services.AddSingleton<Work.AttemptRegistry>();
        services.AddSingleton<Operating.IManagerWorkTracker>(sp => sp.GetRequiredService<Work.AttemptRegistry>());
        services.AddSingleton<Work.WorkSessionProvider>();
        services.AddSingleton<Queue.QueueCredentialProvider>();
        services.AddSingleton<Queue.QueueDepthState>();
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
        services.AddSingleton<Diagnostics.StatusReportBuilder>();
        services.AddSingleton(_ => Diagnostics.HostUtilizationReaders.ForCurrentOs());
        services.AddSingleton<Diagnostics.HostUtilizationSampler>();
        services.AddHostedService(sp => sp.GetRequiredService<Diagnostics.HostUtilizationSampler>());
        services.AddSingleton<Registration.RegistrationStatusMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<Registration.RegistrationStatusMonitor>());
        services.AddSingleton<Registration.RegistrationCoordinator>();
        services.AddSingleton<Registration.RegistrationRestorer>();
        services.AddHostedService<Registration.RestoreHostedService>();
        services.Configure<Runtime.Simulated.SimulatedAnalystOptions>(
            configuration.GetSection(Runtime.Simulated.SimulatedAnalystOptions.SectionName));
        services.AddSingleton(CreateRuntimeAdapterCatalog);
        services.Configure<Runtime.Docker.DockerRuntimeOptions>(
            configuration.GetSection(Runtime.Docker.DockerRuntimeOptions.SectionName));
        services.AddSingleton(sp => new Runtime.Docker.RegistryCredentialProvider(
            sp.GetRequiredService<PlatformClient>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<Runtime.Docker.RegistryCredentialProvider>()));
        services.AddSingleton(sp =>
        {
            var environment = sp.GetService<IHostEnvironment>();
            var validation = environment is not null && (environment.IsDevelopment() || environment.IsEnvironment("Test"));
            return Runtime.RuntimeCompatibilityList.Load(
                sp.GetRequiredService<IOptions<RuntimeOptions>>().Value.CompatibilityListPath, validation);
        });
        services.AddSingleton(sp =>
        {
            var catalog = sp.GetRequiredService<Runtime.RuntimeAdapterCatalog>();
            var coordinator = sp.GetRequiredService<Registration.RegistrationCoordinator>();
            return new Runtime.Docker.DockerReconciliationService(
                () => (catalog.Find(Runtime.Docker.DockerRuntimeAdapter.ProfileId) as Runtime.Docker.DockerRuntimeAdapter)?.Engine,
                () => coordinator.Active?.ManagerId, sp.GetRequiredService<Work.AttemptRegistry>(), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Runtime.Docker.DockerReconciliationService>());
        });
        services.AddSingleton(sp => new Runtime.Docker.ImageCache(
            () => (sp.GetRequiredService<Runtime.RuntimeAdapterCatalog>().Find(Runtime.Docker.DockerRuntimeAdapter.ProfileId)
                as Runtime.Docker.DockerRuntimeAdapter)?.Engine,
            ManagerStatePaths.Resolve(sp.GetRequiredService<IOptions<ManagerOptions>>().Value.StateDirectory),
            sp.GetRequiredService<IOptions<Runtime.Docker.DockerRuntimeOptions>>().Value, sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<Runtime.Docker.ImageCache>()));
        services.AddSingleton(_ => new Runtime.AccountShell());
        services.AddSingleton<Runtime.PreflightRunner>();
        services.AddSingleton<Runtime.PreflightScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<Runtime.PreflightScheduler>());
        services.AddSingleton<Operating.TimedPauseScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<Operating.TimedPauseScheduler>());
        services.TryAdd(ServiceDescriptor.Singleton(typeof(ApplicationProfile), ApplicationProfile.Tray));
        services.AddSingleton(_ => new LingeringCheck());
        services.AddHostedService(sp => new LingeringWarningService(
            sp.GetRequiredService<ApplicationProfile>(), sp.GetRequiredService<LingeringCheck>(),
            sp.GetRequiredService<ILogger<LingeringWarningService>>(), Console.Error));
        services.AddSingleton<ManagerControl>();
        services.AddSingleton<IManagerControl>(sp => sp.GetRequiredService<ManagerControl>());
        return services;
    }

    private static Runtime.RuntimeAdapterCatalog CreateRuntimeAdapterCatalog(IServiceProvider sp)
    {
        var environment = sp.GetService<IHostEnvironment>();
        var validation = environment is not null
            && (environment.IsDevelopment() || environment.IsEnvironment("Test"));
        var profile = sp.GetRequiredService<IOptions<RuntimeOptions>>().Value.Profile;
        if (!validation
            && string.Equals(profile, Runtime.Simulated.SimulatedRuntimeAdapter.TestProfile, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Runtime:Profile 'simulated' is only available in the Development and Test environments; select a validated runtime profile.");
        }

        var adapters = new List<Runtime.IContainerRuntimeAdapter>();
        if (validation)
        {
            adapters.Add(new Runtime.Simulated.SimulatedRuntimeAdapter(
                sp.GetRequiredService<IOptions<Runtime.Simulated.SimulatedAnalystOptions>>(),
                sp.GetRequiredService<TimeProvider>()));
        }

        if (string.Equals(profile, Runtime.Docker.DockerRuntimeAdapter.ProfileId, StringComparison.Ordinal))
        {
            adapters.Add(CreateDockerAdapter(sp, environment));
        }

        return new Runtime.RuntimeAdapterCatalog(adapters);
    }

    private static Runtime.Docker.DockerRuntimeAdapter CreateDockerAdapter(IServiceProvider sp, IHostEnvironment? environment)
    {
        var options = sp.GetRequiredService<IOptions<Runtime.Docker.DockerRuntimeOptions>>().Value;
        options.Validate();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<Runtime.Docker.DockerRuntimeAdapter>();
        var endpoint = Runtime.Docker.DockerEndpointPolicy.Evaluate(options.Endpoint, environment);
        var engine = endpoint.Allowed ? Runtime.Docker.DockerEngineClient.Create(endpoint, logger) : null;
        var sessions = sp.GetRequiredService<Work.WorkSessionProvider>();
        var coordinator = sp.GetRequiredService<Registration.RegistrationCoordinator>();
        return new Runtime.Docker.DockerRuntimeAdapter(
            engine, endpoint, options, sp.GetRequiredService<Runtime.Docker.RegistryCredentialProvider>(),
            sp.GetRequiredService<PlatformClient>(),
            async ct =>
            {
                await sessions.RefreshAsync(ct);
                return sessions.Current;
            },
            () => coordinator.Active?.ManagerId, logger, imageCache: sp.GetRequiredService<Runtime.Docker.ImageCache>());
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
