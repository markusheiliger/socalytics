using SocAlytics.Platform.Api;
using SocAlytics.Platform.AgentOrchestration;
using SocAlytics.Platform.Analysis;
using SocAlytics.Platform.Club;
using SocAlytics.Platform.IdentityAccess;
using SocAlytics.Platform.Persistence;
using SocAlytics.Platform.Recordings;
using SocAlytics.Platform.Registry;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddAgentOrchestrationModule();
builder.Services.AddAnalysisModule();
builder.Services.AddClubModule();
builder.Services.AddIdentityAccessModule();
builder.Services.AddRecordingsModule();
builder.Services.AddRegistryModule();
var connectionString = builder.Configuration.GetConnectionString("platform") ?? string.Empty;
builder.Services.AddPersistenceMigrationReadiness(connectionString);
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