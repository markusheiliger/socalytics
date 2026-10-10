using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Infrastructure.Club;
using SocAlytics.Platform.Infrastructure.IdentityAccess;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        PersistenceRegistration.AddPersistence(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAuditTrail, PostgresAuditTrail>();
        services.AddIdentityAccess();
        services.TryAddScoped<IMemberAccountStore, MemberAccountStore>();
        services.TryAddScoped<IAccountCredentialService, AccountCredentialService>();
        services.TryAddScoped<ISessionStore, SessionStore>();
        services.TryAddScoped<IClubHierarchyStore, ClubHierarchyStore>();
        services.AddScoped<ITeamScopeSource, TeamScopeSource>();
        services.AddScoped<ITeamScopeSource, MatchScopeSource>();

        return services;
    }
}
