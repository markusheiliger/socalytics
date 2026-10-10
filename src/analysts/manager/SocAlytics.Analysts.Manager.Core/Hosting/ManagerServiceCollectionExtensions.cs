using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SocAlytics.Analysts.Manager.DeviceKeys;

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
        services.AddSingleton<DeviceKeyProviderSelector>();
        return services;
    }
}
