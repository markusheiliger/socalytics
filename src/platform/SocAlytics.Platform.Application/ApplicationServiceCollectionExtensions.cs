using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Recordings;

namespace SocAlytics.Platform.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ApplicationLayerMarker>();
        services.TryAddScoped<ITeamScopeResolver, TeamScopeResolver>();
        services.TryAddScoped<IAccessAuthorizer, AccessAuthorizer>();
        services.TryAddScoped<ApplyBreakGlassRecoveryHandler>();
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
        services.TryAddScoped<UpdateMatchHandler>();
        services.TryAddScoped<GetMatchHandler>();
        services.TryAddScoped<ListTeamMatchesHandler>();
        services.TryAddSingleton<SocAlytics.Platform.Application.Analysis.WorkflowSnapshotMapper>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.RunReadinessCoordinator>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Commands.HandleRecordingsFinalizedNotificationHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Commands.ClaimLogicalJobHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Commands.RenewAttemptLeaseHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Commands.CompleteAttemptHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Results.IssueResultGrantsHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Results.ResultManifestVerifier>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Results.ListUpstreamResultsHandler>();
        services.AddOptions<SocAlytics.Platform.Application.Analysis.Commands.AnalysisLeaseOptions>()
            .BindConfiguration(SocAlytics.Platform.Application.Analysis.Commands.AnalysisLeaseOptions.SectionName);
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Commands.CloseAttemptAsStale>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Commands.FenceManagerAttempts>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Commands.ExpireStaleAttemptsHandler>();
        services.AddOptions<SocAlytics.Platform.Application.Analysis.Commands.AnalysisReconciliationOptions>()
            .BindConfiguration(SocAlytics.Platform.Application.Analysis.Commands.AnalysisReconciliationOptions.SectionName);
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Commands.ReconcileAnalysisPublicationHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Messaging.PurgeOutboxMessagesHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Queries.GetAnalysisRunHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Queries.ListMatchAnalysisRunsHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Facts.ListRunFactsHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Facts.ListRunFactStreamsHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Queries.GetAnalysisOperationalSummaryHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Queries.ListNotificationReceiptsHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Analysis.Commands.RunAnalysisReconciliationHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Messaging.ListOutboxMessagesHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Messaging.GetOutboxMessageHandler>();
        services.TryAddScoped<ValidateSessionHandler>();
        services.TryAddScoped<GetSessionHandler>();
        services.TryAddScoped<SignOutHandler>();
        services.TryAddScoped<SignInHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn.StartExternalSignInHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn.CompleteExternalSignInHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn.UnlinkExternalIdentityHandler>();
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
        services.TryAddScoped<IssueCredentialHandler>();
        services.TryAddScoped<DeactivateMemberHandler>();
        services.TryAddScoped<ReactivateMemberHandler>();
        services.TryAddScoped<EndMemberSessionsHandler>();
        services.TryAddScoped<UnlockMemberHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Recordings.Commands.StartRecordingUploadHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Recordings.Queries.GetRecordingUploadSessionHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Recordings.Commands.IssueRecordingUploadGrantsHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Recordings.Commands.CompleteRecordingUploadHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Recordings.Commands.ReviseTimelineMappingHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Recordings.Commands.ExpireUploadSessionsHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Recordings.Commands.FinalizeRecordingSetHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Recordings.Queries.GetRecordingSetVersionHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Recordings.Queries.GetRecordingVersionHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Recordings.Queries.GetTimelineMappingHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Recordings.Queries.GetMatchRecordingLineageHandler>();

        services.TryAddScoped<SocAlytics.Platform.Application.Notifications.IEmailOutbox, SocAlytics.Platform.Application.Notifications.EmailOutbox>();
        services.TryAddScoped<SocAlytics.Platform.Application.Notifications.RegistrationPendingNotifier>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.Email.GetEmailAddressHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.Email.GetMemberEmailHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.Email.SetEmailAddressHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.Email.RemoveEmailAddressHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.Email.ResendEmailConfirmationHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.Email.ConfirmEmailHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.Email.ListNotificationPreferencesHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.IdentityAccess.Email.SetNotificationPreferenceHandler>();
        services.AddOptions<SocAlytics.Platform.Application.IdentityAccess.Email.EmailConfirmationOptions>()
            .BindConfiguration(SocAlytics.Platform.Application.IdentityAccess.Email.EmailConfirmationOptions.SectionName)
            .Validate(SocAlytics.Platform.Application.IdentityAccess.Email.EmailConfirmationOptions.IsValid, "Email:Confirmation:MaxPerHour and Email:Confirmation:AttemptsPerMinute must be greater than zero.")
            .ValidateOnStart();
        services.AddOptions<SocAlytics.Platform.Application.Notifications.NotificationOptions>()
            .BindConfiguration(SocAlytics.Platform.Application.Notifications.NotificationOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SocAlytics.Platform.Application.Notifications.NotificationOptions>, SocAlytics.Platform.Application.Notifications.NotificationOptionsValidator>());

        services.AddOptions<RecordingUploadOptions>().BindConfiguration(RecordingUploadOptions.SectionName).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RecordingUploadOptions>, RecordingUploadOptionsValidator>());
        services.AddOptions<RecordingSetOptions>().BindConfiguration(RecordingSetOptions.SectionName).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RecordingSetOptions>, RecordingSetOptionsValidator>());

        services.AddOptions<SocAlytics.Platform.Application.Analysis.Results.AnalysisResultOptions>()
            .BindConfiguration(SocAlytics.Platform.Application.Analysis.Results.AnalysisResultOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<SocAlytics.Platform.Application.Analysis.Results.AnalysisResultOptions>,
            SocAlytics.Platform.Application.Analysis.Results.AnalysisResultOptionsValidator>());

        services.AddOptions<SocAlytics.Platform.Application.Analysis.Results.AnalysisReadOptions>()
            .BindConfiguration(SocAlytics.Platform.Application.Analysis.Results.AnalysisReadOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<SocAlytics.Platform.Application.Analysis.Results.AnalysisReadOptions>,
            SocAlytics.Platform.Application.Analysis.Results.AnalysisReadOptionsValidator>());

        services.AddOptions<SocAlytics.Platform.Application.AgentOrchestration.AgentOrchestrationOptions>()
            .BindConfiguration(SocAlytics.Platform.Application.AgentOrchestration.AgentOrchestrationOptions.SectionName)
            .Validate(
                SocAlytics.Platform.Application.AgentOrchestration.AgentOrchestrationOptions.IsValid,
                "AgentOrchestration options are invalid: Agents:Enabled must list unique specialist agents; Execution, Workloads, and Monitor values must be positive; workload clients need a unique ClientId and, when enabled, a PublicJwk.")
            .ValidateOnStart();
        services.TryAddScoped<SocAlytics.Platform.Application.AgentOrchestration.IInvocationScopeEvaluator, SocAlytics.Platform.Application.AgentOrchestration.InvocationScopeEvaluator>();

        services.TryAddScoped<SocAlytics.Platform.Application.AgentOrchestration.Workloads.IssueAgentWorkloadTokenHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.AgentOrchestration.Workloads.AuthenticateAgentWorkloadRequestHandler>();

        services.AddOptions<SocAlytics.Platform.Application.Analysis.Facts.FactReadOptions>()
            .BindConfiguration(SocAlytics.Platform.Application.Analysis.Facts.FactReadOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<SocAlytics.Platform.Application.Analysis.Facts.FactReadOptions>,
            SocAlytics.Platform.Application.Analysis.Facts.FactReadOptionsValidator>());

        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Pairing.StartPairingHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Pairing.GetPairingStatusHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Pairing.ActivateRegistrationHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.MachineAccess.IssueManagerTokenHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.MachineAccess.AuthenticateManagerRequestHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.MachineAccess.GetOwnRegistrationHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.MachineAccess.ReportStatusHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.MachineAccess.BeginUnregistrationHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.MachineAccess.CompleteUnregistrationHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.QueueCredentials.IssueQueueCredentialsHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.RegistryCredentials.IssueRegistryCredentialsHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.WorkflowCapabilities.GetWorkflowCapabilitiesHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.QueueDepth.ReadQueueDepthHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Expiry.ExpireDueRegistrationsHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Decisions.SubmitPairingCodeHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Decisions.GetRegistrationHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Decisions.ListRegistrationsHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Decisions.ApproveRegistrationHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Decisions.RejectRegistrationHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Decisions.RevokeRegistrationHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Images.RegisterAnalystImageHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Images.ListAnalystImagesHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Images.GetAnalystImageHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Profiles.CreateAnalystProfileHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Profiles.ApproveAnalystProfileHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Profiles.RetireAnalystProfileHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Profiles.ListAnalystProfilesHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Profiles.GetAnalystProfileHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Workflows.DefineWorkflowHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Workflows.SetDefaultWorkflowHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Workflows.ListWorkflowsHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Workflows.GetWorkflowHandler>();
        services.TryAddScoped<SocAlytics.Platform.Application.Registry.Workflows.GetDefaultWorkflowHandler>();
        services.AddOptions<SocAlytics.Platform.Application.Registry.RegistryOptions>()
            .BindConfiguration(SocAlytics.Platform.Application.Registry.RegistryOptions.SectionName);
        services.AddOptions<SocAlytics.Platform.Application.Registry.StatusReporting.StatusReportingOptions>()
            .BindConfiguration(SocAlytics.Platform.Application.Registry.StatusReporting.StatusReportingOptions.SectionName)
            .Validate(static o => o.Interval > TimeSpan.Zero, "Registry:StatusReporting:Interval must be positive.")
            .Validate(static o => o.MissedIntervalsBeforeOffline >= 1, "Registry:StatusReporting:MissedIntervalsBeforeOffline must be at least 1.");
        services.AddOptions<SocAlytics.Platform.Application.Registry.StampOptions>()
            .BindConfiguration(SocAlytics.Platform.Application.Registry.StampOptions.SectionName);

        return services;
    }
}

internal sealed class ApplicationLayerMarker;
