using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using SocAlytics.Platform.Integration.Tests.Infrastructure;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

internal sealed class PlatformApiFactory : WebApplicationFactory<Program>
{
	private readonly IsolatedDatabase? _database;
	private readonly IReadOnlyDictionary<string, string?>? _clubBootstrap;
	private readonly IReadOnlyDictionary<string, string?>? _breakGlassRecovery;

	public PlatformApiFactory(
		IsolatedDatabase? database,
		IReadOnlyDictionary<string, string?>? clubBootstrap = null,
		IReadOnlyDictionary<string, string?>? breakGlassRecovery = null)
	{
		_database = database;
		_clubBootstrap = clubBootstrap;
		_breakGlassRecovery = breakGlassRecovery;
	}

	public MutableTimeProvider Time { get; } = new();

	public CapturingLoggerProvider Logs { get; } = new();

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.ConfigureAppConfiguration((_, configuration) =>
		{
			configuration.Sources.Clear();
			var settings = new Dictionary<string, string?>(TestIdentityAccessSettings.Values);
			if (_database is not null)
			{
				settings["ConnectionStrings:socalytics"] = _database.AppConnectionString;
			}

			AddSection(settings, "ClubBootstrap", _clubBootstrap);
			AddSection(settings, "BreakGlassRecovery", _breakGlassRecovery);
			configuration.AddInMemoryCollection(settings);
		});
		builder.ConfigureLogging(logging => logging.AddProvider(Logs));
		builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time)));
	}

	protected override void ConfigureClient(HttpClient client)
	{
		base.ConfigureClient(client);
		client.BaseAddress = new Uri("https://localhost");
	}

	public HttpClient CreateApiClient() => CreateClient(new WebApplicationFactoryClientOptions
	{
		BaseAddress = new Uri("https://localhost"),
		AllowAutoRedirect = false,
		HandleCookies = true,
	});

	public async Task WaitUntilHealthyAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
	{
		using var client = CreateApiClient();
		var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
		while (true)
		{
			using var response = await client.GetAsync("/health", cancellationToken);
			if (response.StatusCode == HttpStatusCode.OK)
			{
				return;
			}

			if (DateTime.UtcNow >= deadline)
			{
				throw new TimeoutException("The API did not become healthy in time.");
			}

			await Task.Delay(200, cancellationToken);
		}
	}

	public async Task<HealthReportEntry> CheckBootstrapAsync(CancellationToken cancellationToken)
	{
		var report = await Services.GetRequiredService<HealthCheckService>()
			.CheckHealthAsync(r => r.Name == "club-bootstrap", cancellationToken);
		return report.Entries["club-bootstrap"];
	}

	private static void AddSection(Dictionary<string, string?> target, string section, IReadOnlyDictionary<string, string?>? values)
	{
		if (values is null)
		{
			return;
		}

		foreach (var (key, value) in values)
		{
			target[$"{section}:{key}"] = value;
		}
	}
}
