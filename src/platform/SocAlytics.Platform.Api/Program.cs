using SocAlytics.Platform.Api.Bootstrap;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRequestContext, HttpRequestContext>();
builder.Services.AddOptions<IdentityAccessOptions>()
	.Bind(builder.Configuration.GetSection(IdentityAccessOptions.SectionName))
	.ValidateDataAnnotations()
	.ValidateOnStart();
builder.Services.Configure<ClubBootstrapOptions>(builder.Configuration.GetSection(ClubBootstrapOptions.SectionName));
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
});

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapOpenApi();

app.Run();

public partial class Program;