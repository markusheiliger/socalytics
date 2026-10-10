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
        services.TryAddScoped<GetClubHandler>();
        services.TryAddScoped<UpdateClubSettingsHandler>();
        services.TryAddScoped<CreateSeasonHandler>();
        services.TryAddScoped<ActivateSeasonHandler>();
        services.TryAddScoped<ArchiveSeasonHandler>();
        services.TryAddScoped<GetSeasonHandler>();
        services.TryAddScoped<ListSeasonsHandler>();
        services.TryAddScoped<CreateTeamHandler>();
        services.TryAddScoped<UpdateTeamHandler>();
        services.TryAddScoped<GetTeamHandler>();
        services.TryAddScoped<ListTeamsHandler>();
        services.TryAddScoped<CreateMatchHandler>();
        services.TryAddScoped<GetMatchHandler>();
        services.TryAddScoped<ListTeamMatchesHandler>();
        services.TryAddScoped<ValidateSessionHandler>();
        services.TryAddScoped<GetSessionHandler>();
        services.TryAddScoped<SignOutHandler>();
        services.TryAddScoped<SignInHandler>();
        services.TryAddScoped<GetCurrentMemberHandler>();
        services.TryAddScoped<ChangeOwnPasswordHandler>();
        services.TryAddScoped<CreateMemberHandler>();
        services.TryAddScoped<ListMembersHandler>();
        services.TryAddScoped<GetMemberHandler>();
        services.TryAddScoped<RedeemCredentialHandler>();
        services.TryAddScoped<AssignClubRoleHandler>();
        services.TryAddScoped<AssignTeamRoleHandler>();
        services.TryAddScoped<RevokeTeamRoleHandler>();
        services.TryAddScoped<RevokeClubRoleHandler>();
        services.TryAddScoped<DeactivateMemberHandler>();
        services.TryAddScoped<ReactivateMemberHandler>();
        services.TryAddScoped<EndMemberSessionsHandler>();
        services.TryAddScoped<UnlockMemberHandler>();

        return services;
    }
}

internal sealed class ApplicationLayerMarker;
