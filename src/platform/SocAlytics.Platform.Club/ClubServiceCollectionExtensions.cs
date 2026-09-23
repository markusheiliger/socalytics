using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SocAlytics.Platform.Club;

public static class ClubServiceCollectionExtensions
{
    public static IServiceCollection AddClubModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ClubModuleMarker>();

        return services;
    }
}

internal sealed class ClubModuleMarker;