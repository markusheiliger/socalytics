using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Infrastructure.Club;
using SocAlytics.Platform.Infrastructure.Recordings;
using SocAlytics.Platform.Infrastructure.IdentityAccess;
using SocAlytics.Platform.Infrastructure.ObjectStorage;
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
        services.TryAddScoped<IRecordingRetryOutcomeStore, RecordingRetryOutcomeStore>();
        services.AddScoped<ITeamScopeSource, TeamScopeSource>();
        services.AddScoped<ITeamScopeSource, MatchScopeSource>();

        services.AddOptions<ObjectStorageOptions>()
            .BindConfiguration("ObjectStorage")
            .Validate(static o => !string.IsNullOrWhiteSpace(o.ServiceUrl), "ObjectStorage:ServiceUrl is required.")
            .Validate(static o => !string.IsNullOrWhiteSpace(o.AccessKey), "ObjectStorage:AccessKey is required.")
            .Validate(static o => !string.IsNullOrWhiteSpace(o.SecretKey), "ObjectStorage:SecretKey is required.")
            .Validate(static o => !string.IsNullOrWhiteSpace(o.Bucket), "ObjectStorage:Bucket is required.")
            .ValidateOnStart();
        services.TryAddSingleton<IObjectStorage, S3ObjectStorage>();
        services.AddHostedService<DevelopmentBucketInitializer>();

        return services;
    }
}
