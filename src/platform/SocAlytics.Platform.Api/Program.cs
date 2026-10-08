using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddOptions<IdentityAccessOptions>()
	.Bind(builder.Configuration.GetSection(IdentityAccessOptions.SectionName))
	.ValidateDataAnnotations()
	.ValidateOnStart();
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