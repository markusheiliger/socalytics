using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.SimulatedAgentWorkload;

// The simulated workload stands in for an agent runtime and must never run in a production environment.
var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
if (environment is not ("Development" or "Test"))
{
	Console.Error.WriteLine("The simulated agent workload runs only when DOTNET_ENVIRONMENT is Development or Test.");
	return 1;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<SimulatedAgentWorkloadOptions>(builder.Configuration.GetSection(SimulatedAgentWorkloadOptions.SectionName));
builder.Services.AddHostedService<SimulatedAgentWorkloadService>();

using var host = builder.Build();
await host.RunAsync();
return 0;

internal sealed class SimulatedAgentWorkloadService(
	IOptions<SimulatedAgentWorkloadOptions> options, ILogger<SimulatedAgentWorkloadService> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using var http = new HttpClient();
		await using var runner = new SimulatedAgentWorkloadRunner(options.Value, http, TimeProvider.System, logger);
		await runner.RunAsync(stoppingToken);
	}
}
