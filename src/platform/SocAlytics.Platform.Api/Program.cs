using SocAlytics.Platform.Api;

var builder = WebApplication.CreateBuilder(args);

builder.AddPlatformApi();

var app = builder.Build();

app.MapPlatformApi();

app.Run();
