using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Infrastructure;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

internal static class PlatformServices
{
    public static ServiceProvider Build(
        IsolatedDatabase database,
        CapturingLoggerProvider? logs = null,
        MutableTimeProvider? time = null)
    {
        var values = new Dictionary<string, string?>(TestIdentityAccessSettings.Values)
        {
            ["ConnectionStrings:socalytics"] = database.AppConnectionString,
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging(builder =>
        {
            if (logs is not null)
            {
                builder.AddProvider(logs);
            }
        });
        services.AddSingleton<TimeProvider>(time ?? new MutableTimeProvider());
        services.AddScoped<TestRequestContext>();
        services.AddScoped<IRequestContext>(sp => sp.GetRequiredService<TestRequestContext>());
        services.AddOptions<IdentityAccessOptions>()
            .Bind(configuration.GetSection(IdentityAccessOptions.SectionName));
        services.AddApplication();
        services.AddInfrastructure();

        return services.BuildServiceProvider(validateScopes: true);
    }
}
