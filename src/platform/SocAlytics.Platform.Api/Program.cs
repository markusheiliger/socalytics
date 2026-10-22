using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Api.AgentOrchestration;
using SocAlytics.Platform.Api.Bootstrap;
using SocAlytics.Platform.Api.Endpoints.Club;
using SocAlytics.Platform.Api.Endpoints.IdentityAccess;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Analysis;
using SocAlytics.Platform.Api.Operations;
using SocAlytics.Platform.Api.Recordings;
using SocAlytics.Platform.Api.Registry;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Api.Segments;
using SocAlytics.Platform.Api.WebUi;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn;
using SocAlytics.Platform.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
// The hosting request log would write the external sign-in callback's authorization code (query string) to the logs.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.AddRegistryApi();
builder.Services.AddAgentWorkloadSecurity();
builder.Services.AddEmailRateLimiting();
builder.Services.AddPasswordRecoveryRateLimiting();
builder.Services.AddEvidenceRateLimiting();
builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRequestContext, HttpRequestContext>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentAnalystManager, SocAlytics.Platform.Api.Analysis.HttpCurrentAnalystManager>();
builder.Services.AddOptions<IdentityAccessOptions>()
	.Bind(builder.Configuration.GetSection(IdentityAccessOptions.SectionName))
	.ValidateDataAnnotations()
	.ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<IdentityAccessOptions>>(new ExternalProviderOptionsValidator(
	allowInsecureUrls: builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Test")));
builder.Services.AddMinimumWebVersion(builder.Configuration);
builder.Services.Configure<ClubBootstrapOptions>(builder.Configuration.GetSection(ClubBootstrapOptions.SectionName));
builder.Services.Configure<BreakGlassRecoveryOptions>(builder.Configuration.GetSection(BreakGlassRecoveryOptions.SectionName));
builder.Services.AddSingleton<ClubBootstrapState>();
builder.Services.AddHostedService<ClubBootstrapHostedService>();
builder.Services.AddHealthChecks().AddCheck<ClubBootstrapHealthCheck>(ClubBootstrapHealthCheck.Name);
builder.Services.AddOpenApi("v1", options =>
{
	options.AddDocumentTransformer((document, _, _) =>
	{
		document.Info.Version = "v1";
		return Task.CompletedTask;
	});
	options.AddDocumentTransformer<OpenApiSecuritySchemesTransformer>();
});

builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
	.AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorizationBuilder()
	.AddPolicy(AuthorizationPolicyNames.ActiveMember, policy => policy
		.AddAuthenticationSchemes(SessionAuthenticationHandler.SchemeName)
		.RequireAuthenticatedUser()
		.RequireAssertion(context => !context.User.HasClaim(SessionClaimTypes.PasswordChangeRequired, "true")))
	.AddPolicy(AuthorizationPolicyNames.SessionHolder, policy => policy
		.AddAuthenticationSchemes(SessionAuthenticationHandler.SchemeName)
		.RequireAuthenticatedUser());

var app = builder.Build();

app.UseMinimumWebVersion();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapOpenApi();
app.MapSessionEndpoints();
app.MapExternalSignInEndpoints();
app.MapSelfEndpoints();
app.MapEmailEndpoints();
app.MapPasswordRecoveryEndpoints();
app.MapClubEndpoints();
app.MapSeasonEndpoints();
app.MapTeamEndpoints();
app.MapMatchEndpoints();
app.MapMemberEndpoints();
app.MapCredentialEndpoints();
app.MapRecordingEndpoints();
app.MapAnalysisRunEndpoints();
app.MapAnalysisFactEndpoints();
app.MapAnalysisEvidenceEndpoints();
app.MapMaterializedSegmentEndpoints();
app.MapWorkflowOperationsEndpoints();
app.MapAnalystManagerEndpoints();
app.MapRegistryEndpoints();
app.MapAgentConversationEndpoints();
app.MapAgentCatalogEndpoints();
app.MapAgentWorkloadEndpoints();

app.Run();

public partial class Program;