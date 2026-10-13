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
using SocAlytics.Platform.Infrastructure.Messaging.Nats;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        PersistenceRegistration.AddPersistence(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IContractValidator, SocAlytics.Platform.Infrastructure.Contracts.JsonSchemaContractValidator>();
        services.TryAddSingleton<ICanonicalJson, SocAlytics.Platform.Infrastructure.Contracts.JsonCanonicalizer>();
        services.TryAddScoped<IAuditTrail, PostgresAuditTrail>();
        services.TryAddScoped<IOutbox, SocAlytics.Platform.Infrastructure.Persistence.Messaging.Outbox>();
        services.TryAddScoped<SocAlytics.Platform.Infrastructure.Persistence.Messaging.OutboxStore>();
        services.AddIdentityAccess();
        services.TryAddScoped<IMemberAccountStore, MemberAccountStore>();
        services.TryAddScoped<IAccountCredentialService, AccountCredentialService>();
        services.TryAddScoped<ISessionStore, SessionStore>();
        services.TryAddScoped<IClubHierarchyStore, ClubHierarchyStore>();
        services.TryAddScoped<IRecordingRetryOutcomeStore, RecordingRetryOutcomeStore>();
        services.TryAddScoped<IRecordingStore, RecordingStore>();
        services.TryAddScoped<IRecordingLineageQueries, RecordingLineageQueries>();
        services.TryAddScoped<IRecordingSetLookup, RecordingSetLookup>();
        services.TryAddSingleton<SocAlytics.Platform.Application.Analysis.IAnalysisSegmentSource, SocAlytics.Platform.Infrastructure.Analysis.LineageAnalysisSegmentSource>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Stores.IAnalysisRunStore, SocAlytics.Platform.Infrastructure.Persistence.Analysis.AnalysisRunStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Stores.ILogicalJobStore, SocAlytics.Platform.Infrastructure.Persistence.Analysis.LogicalJobStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Stores.IExecutionAttemptStore, SocAlytics.Platform.Infrastructure.Persistence.Analysis.ExecutionAttemptStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Stores.IAcceptedResultStore, SocAlytics.Platform.Infrastructure.Persistence.Analysis.AcceptedResultStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Stores.ICompletionOutcomeStore, SocAlytics.Platform.Infrastructure.Persistence.Analysis.CompletionOutcomeStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Stores.INotificationReceiptStore, SocAlytics.Platform.Infrastructure.Persistence.Analysis.NotificationReceiptStore>();
        services.TryAddSingleton<SocAlytics.Platform.Application.Registry.Abstractions.IRegistrySecretGenerator, SocAlytics.Platform.Infrastructure.Registry.RegistrySecretGenerator>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Abstractions.IRegistrationStore, SocAlytics.Platform.Infrastructure.Registry.DapperRegistrationStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Abstractions.IProofReplayStore, SocAlytics.Platform.Infrastructure.Registry.DapperProofReplayStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Abstractions.IManagerAccessTokenStore, SocAlytics.Platform.Infrastructure.Registry.DapperManagerAccessTokenStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Abstractions.IManagerProofValidator, SocAlytics.Platform.Infrastructure.Registry.JoseManagerProofValidator>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.ICapabilityDeclarationStore, SocAlytics.Platform.Infrastructure.Persistence.Registry.CapabilityDeclarationStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.IAnalystProfileStore, SocAlytics.Platform.Infrastructure.Persistence.Registry.AnalystProfileStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.IWorkflowDefinitionStore, SocAlytics.Platform.Infrastructure.Persistence.Registry.WorkflowDefinitionStore>();
        services.AddScoped<ITeamScopeSource, TeamScopeSource>();
        services.AddScoped<ITeamScopeSource, MatchScopeSource>();
        services.AddScoped<ITeamScopeSource, SocAlytics.Platform.Infrastructure.Analysis.AnalysisRunScopeSource>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Queries.IAnalysisRunQueries, SocAlytics.Platform.Infrastructure.Persistence.Analysis.AnalysisRunQueries>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Queries.IAnalysisOperationalQueries, SocAlytics.Platform.Infrastructure.Persistence.Analysis.AnalysisOperationalQueries>();
        services.TryAddScoped<SocAlytics.Platform.Application.Messaging.IOutboxMessageQueries, SocAlytics.Platform.Infrastructure.Persistence.Messaging.OutboxMessageQueries>();

        services.AddOptions<ObjectStorageOptions>()
            .BindConfiguration("ObjectStorage")
            .Validate(static o => !string.IsNullOrWhiteSpace(o.ServiceUrl), "ObjectStorage:ServiceUrl is required.")
            .Validate(static o => !string.IsNullOrWhiteSpace(o.AccessKey), "ObjectStorage:AccessKey is required.")
            .Validate(static o => !string.IsNullOrWhiteSpace(o.SecretKey), "ObjectStorage:SecretKey is required.")
            .Validate(static o => !string.IsNullOrWhiteSpace(o.Bucket), "ObjectStorage:Bucket is required.")
            .ValidateOnStart();
        services.TryAddSingleton<IObjectStorage, S3ObjectStorage>();
        services.AddHostedService<DevelopmentBucketInitializer>();
        services.AddHostedService<UploadSessionExpiryWorker>();

        services.AddOptions<NatsOptions>().BindConfiguration(NatsOptions.SectionName);
        services.TryAddSingleton<NatsConnectionProvider>();
        services.TryAddSingleton<TransportReconnectSignal>();
        services.TryAddSingleton<NatsTopology>();
        services.AddSingleton<NatsHealthCheck>();
        services.AddOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>()
            .Configure<IServiceProvider>((options, serviceProvider) =>
            {
                var configuration = serviceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
                if (!string.IsNullOrWhiteSpace(configuration?["ConnectionStrings:nats"]))
                {
                    options.Registrations.Add(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(
                        NatsHealthCheck.Name,
                        sp => sp.GetRequiredService<NatsHealthCheck>(),
                        Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
                        tags: [],
                        timeout: TimeSpan.FromSeconds(10)));
                }
            });
        services.AddHostedService<NatsConnectionStarter>();
        services.TryAddSingleton<IMessageTransport, NatsJetStreamTransport>();

        services.AddOptions<SocAlytics.Platform.Infrastructure.Registry.ConfiguredWorkflowOptions>()
            .BindConfiguration(SocAlytics.Platform.Infrastructure.Registry.ConfiguredWorkflowOptions.SectionName);
        services.TryAddSingleton<SocAlytics.Platform.Application.Registry.IWorkflowDefinitionResolver, SocAlytics.Platform.Infrastructure.Registry.ConfiguredWorkflowDefinitionResolver>();
        services.TryAddSingleton<SocAlytics.Platform.Application.Registry.IWorkflowDefinitionAvailability, SocAlytics.Platform.Infrastructure.Registry.WorkflowDefinitionAvailability>();
        services.AddSingleton<SocAlytics.Platform.Infrastructure.Registry.AnalysisWorkflowHealthCheck>();
        services.AddOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>()
            .Configure(options => options.Registrations.Add(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(
                SocAlytics.Platform.Infrastructure.Registry.AnalysisWorkflowHealthCheck.Name,
                sp => sp.GetRequiredService<SocAlytics.Platform.Infrastructure.Registry.AnalysisWorkflowHealthCheck>(),
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                tags: [])));
        services.AddOptions<SocAlytics.Platform.Application.Messaging.OutboxPublicationOptions>()
            .BindConfiguration(SocAlytics.Platform.Application.Messaging.OutboxPublicationOptions.SectionName);
        services.AddOptions<SocAlytics.Platform.Infrastructure.Workers.AnalysisWorkerOptions>()
            .BindConfiguration(SocAlytics.Platform.Infrastructure.Workers.AnalysisWorkerOptions.SectionName);
        services.TryAddScoped<SocAlytics.Platform.Application.Messaging.IOutboxPublicationStore, SocAlytics.Platform.Infrastructure.Persistence.Messaging.OutboxPublicationStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Messaging.PublishOutboxBatch>();
        services.AddHostedService<SocAlytics.Platform.Infrastructure.Workers.OutboxPublisherWorker>();
        services.AddOptions<SocAlytics.Platform.Application.Analysis.AnalysisNotificationOptions>()
            .BindConfiguration(SocAlytics.Platform.Application.Analysis.AnalysisNotificationOptions.SectionName);
        services.AddHostedService<SocAlytics.Platform.Infrastructure.Workers.RecordingsFinalizedConsumerWorker>();
        services.AddHostedService<SocAlytics.Platform.Infrastructure.Workers.LeaseExpiryWorker>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Stores.IPublicationReconciliationStore, SocAlytics.Platform.Infrastructure.Persistence.Messaging.PublicationReconciliationStore>();
        services.AddHostedService<SocAlytics.Platform.Infrastructure.Workers.PublicationReconciliationWorker>();
        services.TryAddScoped<SocAlytics.Platform.Application.Messaging.IOutboxRetentionStore, SocAlytics.Platform.Infrastructure.Persistence.Messaging.OutboxRetentionStore>();
        services.AddHostedService<SocAlytics.Platform.Infrastructure.Workers.OutboxRetentionWorker>();

        return services;
    }
}
