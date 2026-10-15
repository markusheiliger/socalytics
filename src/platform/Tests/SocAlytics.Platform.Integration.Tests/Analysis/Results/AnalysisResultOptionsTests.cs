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
		Should.NotThrow(() => Validate(settings));
	}

	private static void AssertFails(Dictionary<string, string?> settings, string field)
	{
		var ex = Should.Throw<OptionsValidationException>(() => Validate(settings));
		ex.Message.ShouldContain(field);
	}
}
