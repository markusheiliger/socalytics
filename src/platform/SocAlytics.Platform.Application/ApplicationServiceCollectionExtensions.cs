using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ApplicationLayerMarker>();
        services.TryAddScoped<ITeamScopeResolver, TeamScopeResolver>();
        services.TryAddScoped<IAccessAuthorizer, AccessAuthorizer>();
        services.TryAddScoped<BootstrapClubHandler>();
        services.TryAddScoped<ValidateSessionHandler>();
        services.TryAddScoped<GetSessionHandler>();
        services.TryAddScoped<SignOutHandler>();
        services.TryAddScoped<SignInHandler>();

        return services;
    }
}

internal sealed class ApplicationLayerMarker;
