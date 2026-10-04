using Microsoft.Extensions.Diagnostics.HealthChecks;
using SocAlytics.Platform.AgentOrchestration;
using SocAlytics.Platform.Api;
using SocAlytics.Platform.Analysis;
using SocAlytics.Platform.Club;
using SocAlytics.Platform.IdentityAccess;
using SocAlytics.Platform.Persistence;
using SocAlytics.Platform.Recordings;
using SocAlytics.Platform.Registry;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
var connectionString = builder.Configuration.GetConnectionString("platform");
var persistenceConfigured = !string.IsNullOrWhiteSpace(connectionString);
if (persistenceConfigured)
{
	builder.Services.AddPlatformPersistence(connectionString!);
}

var readiness = new MigrationReadinessState();
builder.Services.AddSingleton(readiness);
builder.Services.AddHealthChecks()
	.AddCheck<MigrationReadinessHealthCheck>("migrations", HealthStatus.Unhealthy);
builder.Services.AddHostedService(sp => new MigrationStartupService(
	sp, readiness, sp.GetRequiredService<ILogger<MigrationStartupService>>(), persistenceConfigured));
builder.Services.AddAgentOrchestrationModule();
builder.Services.AddAnalysisModule();
builder.Services.AddClubModule();
builder.Services.AddIdentityAccessModule();
builder.Services.AddRecordingsModule();
builder.Services.AddRegistryModule();
builder.Services.AddOpenApi("v1", options =>
{
	options.AddDocumentTransformer((document, _, _) =>
	{
		document.Info.Version = "v1";
		return Task.CompletedTask;
	});
});

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapOpenApi();

app.Run();

public partial class Program;
