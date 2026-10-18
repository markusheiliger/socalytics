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
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn.IExternalIdentityStore, SocAlytics.Platform.Infrastructure.IdentityAccess.ExternalIdentity.DapperExternalIdentityStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn.IPendingExternalSignInStore, SocAlytics.Platform.Infrastructure.IdentityAccess.ExternalIdentity.DapperPendingExternalSignInStore>();
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
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Results.IResultDeclarationStore, SocAlytics.Platform.Infrastructure.Persistence.Analysis.ResultDeclarationStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Results.IAcceptedResultManifestStore, SocAlytics.Platform.Infrastructure.Persistence.Analysis.AcceptedResultManifestStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Stores.IIndexedFactStore, SocAlytics.Platform.Infrastructure.Persistence.Analysis.IndexedFactStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Results.IUpstreamResultReader, SocAlytics.Platform.Infrastructure.Persistence.Analysis.UpstreamResultReader>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Stores.INotificationReceiptStore, SocAlytics.Platform.Infrastructure.Persistence.Analysis.NotificationReceiptStore>();
        services.TryAddSingleton<SocAlytics.Platform.Application.Registry.Abstractions.IRegistrySecretGenerator, SocAlytics.Platform.Infrastructure.Registry.RegistrySecretGenerator>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Abstractions.IRegistrationStore, SocAlytics.Platform.Infrastructure.Registry.DapperRegistrationStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Abstractions.IProofReplayStore, SocAlytics.Platform.Infrastructure.Registry.DapperProofReplayStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Abstractions.IManagerAccessTokenStore, SocAlytics.Platform.Infrastructure.Registry.DapperManagerAccessTokenStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.QueueCredentials.IQueueCredentialStore, SocAlytics.Platform.Infrastructure.Persistence.Registry.QueueCredentialStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Abstractions.IManagerProofValidator, SocAlytics.Platform.Infrastructure.Registry.JoseManagerProofValidator>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.ICapabilityDeclarationStore, SocAlytics.Platform.Infrastructure.Persistence.Registry.CapabilityDeclarationStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.IAnalystProfileStore, SocAlytics.Platform.Infrastructure.Persistence.Registry.AnalystProfileStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.IWorkflowDefinitionStore, SocAlytics.Platform.Infrastructure.Persistence.Registry.WorkflowDefinitionStore>();
        services.AddOptions<SocAlytics.Platform.Infrastructure.Registry.Oci.ImageRegistryOptions>()
            .BindConfiguration(SocAlytics.Platform.Infrastructure.Registry.Oci.ImageRegistryOptions.SectionName);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Options.IValidateOptions<SocAlytics.Platform.Infrastructure.Registry.Oci.ImageRegistryOptions>, SocAlytics.Platform.Infrastructure.Registry.Oci.ImageRegistryOptionsValidator>());
        services.TryAddSingleton(sp => new SocAlytics.Platform.Infrastructure.Registry.Oci.OciDistributionClient(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) },
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SocAlytics.Platform.Infrastructure.Registry.Oci.ImageRegistryOptions>>()));
        services.TryAddSingleton<SocAlytics.Platform.Application.Registry.RegistryCredentials.IRegistryPullTokenIssuer>(sp => new SocAlytics.Platform.Infrastructure.Registry.Oci.OciPullTokenIssuer(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(15) },
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SocAlytics.Platform.Infrastructure.Registry.Oci.ImageRegistryOptions>>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddSingleton<SocAlytics.Platform.Application.Registry.Images.IOciImageReader, SocAlytics.Platform.Infrastructure.Registry.Oci.OciImageReader>();
        services.TryAddSingleton<SocAlytics.Platform.Application.Registry.Images.IAnalystManifestValidator, SocAlytics.Platform.Infrastructure.Registry.AnalystManifestValidator>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.StatusReporting.IManagerStatusStore, SocAlytics.Platform.Infrastructure.Persistence.Registry.ManagerStatusStore>();
        services.TryAddSingleton<SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn.IOidcProviderClient>(sp => new SocAlytics.Platform.Infrastructure.IdentityAccess.ExternalIdentity.OidcProviderClient(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityAccessOptions>>(),
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) },
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SocAlytics.Platform.Infrastructure.IdentityAccess.ExternalIdentity.OidcProviderClient>>()));
        services.AddScoped<ITeamScopeSource, TeamScopeSource>();
        services.AddScoped<ITeamScopeSource, MatchScopeSource>();
        services.AddScoped<ITeamScopeSource, SocAlytics.Platform.Infrastructure.Analysis.AnalysisRunScopeSource>();
        services.AddScoped<ITeamScopeSource, SocAlytics.Platform.Infrastructure.AgentOrchestration.AgentConversationScopeSource>();
        services.AddScoped<ITeamScopeSource, SocAlytics.Platform.Infrastructure.AgentOrchestration.AgentInvocationScopeSource>();
        services.TryAddScoped<SocAlytics.Platform.Application.AgentOrchestration.IAgentConversationStore, SocAlytics.Platform.Infrastructure.Persistence.AgentOrchestration.AgentConversationStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.AgentOrchestration.IAgentInvocationStore, SocAlytics.Platform.Infrastructure.Persistence.AgentOrchestration.AgentInvocationStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.AgentOrchestration.IAgentAttemptStore, SocAlytics.Platform.Infrastructure.Persistence.AgentOrchestration.AgentAttemptStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.AgentOrchestration.Workloads.IAgentWorkloadOperationStore, SocAlytics.Platform.Infrastructure.Persistence.AgentOrchestration.AgentWorkloadOperationStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.AgentOrchestration.Workloads.IAgentWorkloadTokenStore, SocAlytics.Platform.Infrastructure.Persistence.AgentOrchestration.AgentWorkloadTokenStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Queries.IAnalysisRunQueries, SocAlytics.Platform.Infrastructure.Persistence.Analysis.AnalysisRunQueries>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Facts.IFactQueries, SocAlytics.Platform.Infrastructure.Persistence.Analysis.FactQueries>();
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
        services.AddOptions<NatsAuthenticationOptions>().BindConfiguration(NatsAuthenticationOptions.SectionName);
        services.AddOptions<SocAlytics.Platform.Application.Registry.QueueCredentials.AnalystWorkOptions>()
            .BindConfiguration(SocAlytics.Platform.Application.Registry.QueueCredentials.AnalystWorkOptions.SectionName)
            .Validate(static o => o.MaxConcurrentAnalystsPerManager > 0, "AnalystWork:MaxConcurrentAnalystsPerManager must be a positive integer.")
            .Validate(static o => o.QueueCredentialLifetime > TimeSpan.Zero, "AnalystWork:QueueCredentialLifetime must be positive.")
            .Validate(static o => o.ConsumerAckWait > TimeSpan.Zero, "AnalystWork:ConsumerAckWait must be positive.")
            .Validate(static o => o.UnfitRedeliveryDelay >= TimeSpan.Zero, "AnalystWork:UnfitRedeliveryDelay must not be negative.")
            .ValidateOnStart();
        services.TryAddSingleton<NatsTrust>();
        services.TryAddSingleton<SocAlytics.Platform.Application.Registry.QueueCredentials.IQueueCredentialIssuer, NatsQueueCredentialIssuer>();
        services.TryAddSingleton<SocAlytics.Platform.Application.Registry.QueueCredentials.IQueueAccessRevoker, NatsQueueAccessRevoker>();
        services.TryAddSingleton<SocAlytics.Platform.Application.Registry.QueueDepth.IQueueDepthReader, NatsQueueDepthReader>();
        services.AddHostedService<SocAlytics.Platform.Infrastructure.Workers.QueueRevocationWorker>();
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
        services.TryAddSingleton<SocAlytics.Platform.Application.Registry.IWorkflowDefinitionResolver, SocAlytics.Platform.Infrastructure.Registry.RegistryWorkflowDefinitionResolver>();
        services.AddHostedService<SocAlytics.Platform.Infrastructure.Registry.ConfiguredWorkflowSeeder>();
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

        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.Email.IMemberEmailAddressStore, SocAlytics.Platform.Infrastructure.Persistence.IdentityAccess.MemberEmailAddressStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.Email.IEmailConfirmationStore, SocAlytics.Platform.Infrastructure.Persistence.IdentityAccess.EmailConfirmationStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.Email.INotificationPreferenceStore, SocAlytics.Platform.Infrastructure.Persistence.IdentityAccess.NotificationPreferenceStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.Email.IEmailMessageStore, SocAlytics.Platform.Infrastructure.Persistence.IdentityAccess.EmailMessageStore>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.Email.INotificationRecipientStore, SocAlytics.Platform.Infrastructure.Persistence.IdentityAccess.NotificationRecipientStore>();
        services.AddOptions<SocAlytics.Platform.Infrastructure.Email.EmailOptions>()
            .BindConfiguration(SocAlytics.Platform.Infrastructure.Email.EmailOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Options.IValidateOptions<SocAlytics.Platform.Infrastructure.Email.EmailOptions>, SocAlytics.Platform.Infrastructure.Email.EmailOptionsValidator>());
        services.TryAddSingleton<SocAlytics.Platform.Application.Notifications.IEmailTransport, SocAlytics.Platform.Infrastructure.Email.MailKitEmailTransport>();
        services.TryAddSingleton<SocAlytics.Platform.Application.Notifications.IEmailRenderer, SocAlytics.Platform.Infrastructure.Email.EmailRenderer>();
        services.TryAddScoped<SocAlytics.Platform.Infrastructure.Email.EmailDeliveryProcessor>();
        services.AddHostedService<SocAlytics.Platform.Infrastructure.Email.EmailDeliveryWorker>();

        return services;
    }
}
