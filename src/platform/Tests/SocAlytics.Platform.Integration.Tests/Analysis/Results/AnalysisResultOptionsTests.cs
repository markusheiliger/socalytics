using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Results;

public sealed class AnalysisResultOptionsTests
{
	private static Dictionary<string, string?> ValidSettings() => new(TestIdentityAccessSettings.Values);

	private static ServiceProvider Build(Dictionary<string, string?> settings)
	{
		var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
		var services = new ServiceCollection();
		services.AddSingleton<IConfiguration>(configuration);
		services.AddLogging();
		services.AddTestHostEnvironment();
		services.AddApplication();
		services.AddInfrastructure();
		return services.BuildServiceProvider();
	}

	private static void Validate(Dictionary<string, string?> settings)
	{
		using var provider = Build(settings);
		provider.GetRequiredService<IStartupValidator>().Validate();
	}

	[Fact]
	public void Development_values_pass_startup_validation_and_bind()
	{
		using var provider = Build(ValidSettings());
		provider.GetRequiredService<IStartupValidator>().Validate();
		var options = provider.GetRequiredService<IOptions<AnalysisResultOptions>>().Value;
		options.GrantLifetime.ShouldBe(TimeSpan.FromMinutes(10));
		options.MaxObjectsPerAttempt.ShouldBe(16);
		options.MaxObjectBytes.ShouldBe(104_857_600);
		options.MaxAttemptBytes.ShouldBe(1_073_741_824);
		options.MaxManifestBytes.ShouldBe(1_048_576);
		options.MaxFactArtifactBytes.ShouldBe(16_777_216);
		options.MaxFactPayloadBytes.ShouldBe(16_384);
		options.MaxFactsPerResult.ShouldBe(50_000);
	}

	[Theory]
	[InlineData("GrantLifetime", "00:00:00")]
	[InlineData("GrantLifetime", "-00:01:00")]
	[InlineData("GrantLifetime", "01:00:01")]
	[InlineData("MaxObjectsPerAttempt", "0")]
	[InlineData("MaxObjectsPerAttempt", "-1")]
	[InlineData("MaxManifestBytes", "1023")]
	[InlineData("MaxObjectBytes", "1048575")]
	[InlineData("MaxAttemptBytes", "104857599")]
	[InlineData("MaxFactArtifactBytes", "1023")]
	[InlineData("MaxFactArtifactBytes", "104857601")]
	[InlineData("MaxFactPayloadBytes", "255")]
	[InlineData("MaxFactPayloadBytes", "1048577")]
	[InlineData("MaxFactsPerResult", "0")]
	[InlineData("MaxFactsPerResult", "1000001")]
	public void Invalid_value_fails_at_start(string field, string value)
	{
		var settings = ValidSettings();
		settings[$"Analysis:Results:{field}"] = value;
		AssertFails(settings, field);
	}

	[Theory]
	[InlineData("GrantLifetime")]
	[InlineData("MaxObjectsPerAttempt")]
	[InlineData("MaxObjectBytes")]
	[InlineData("MaxAttemptBytes")]
	[InlineData("MaxManifestBytes")]
	[InlineData("MaxFactArtifactBytes")]
	[InlineData("MaxFactPayloadBytes")]
	[InlineData("MaxFactsPerResult")]
	public void Missing_value_fails_at_start(string field)
	{
		var settings = ValidSettings();
		settings.Remove($"Analysis:Results:{field}");
		AssertFails(settings, field);
	}

	[Fact]
	public void Boundary_values_pass()
	{
		var settings = ValidSettings();
		settings["Analysis:Results:GrantLifetime"] = "01:00:00";
		settings["Analysis:Results:MaxObjectsPerAttempt"] = "1";
		settings["Analysis:Results:MaxManifestBytes"] = "1024";
		settings["Analysis:Results:MaxObjectBytes"] = "1024";
		settings["Analysis:Results:MaxAttemptBytes"] = "1024";
		settings["Analysis:Results:MaxFactArtifactBytes"] = "1024";
		settings["Analysis:Results:MaxFactPayloadBytes"] = "256";
		settings["Analysis:Results:MaxFactsPerResult"] = "1";
		Should.NotThrow(() => Validate(settings));
		settings["Analysis:Results:MaxObjectBytes"] = "104857600";
		settings["Analysis:Results:MaxAttemptBytes"] = "104857600";
		settings["Analysis:Results:MaxFactArtifactBytes"] = "104857600";
		settings["Analysis:Results:MaxFactPayloadBytes"] = "1048576";
		settings["Analysis:Results:MaxFactsPerResult"] = "1000000";
		Should.NotThrow(() => Validate(settings));
	}

	private static void AssertFails(Dictionary<string, string?> settings, string field)
	{
		var ex = Should.Throw<OptionsValidationException>(() => Validate(settings));
		ex.Message.ShouldContain(field);
	}
}
