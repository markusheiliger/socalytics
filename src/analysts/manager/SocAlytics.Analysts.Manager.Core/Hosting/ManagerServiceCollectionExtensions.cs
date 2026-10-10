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

        services.AddSingleton<DeviceKeyProviderSelector>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ProofFactory>();
        services.AddHttpClient<PlatformClient>();
        return services;
    }

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
