using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Analysis.Evidence;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Evidence;

public sealed class EvidenceOptionsValidationTests
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
		var options = provider.GetRequiredService<IOptions<EvidenceOptions>>().Value;
		options.GrantLifetime.ShouldBe(TimeSpan.FromMinutes(5));
		options.RetryAfter.ShouldBe(TimeSpan.FromSeconds(5));
		options.ResolutionsPerMinute.ShouldBe(60);
		options.MaxDuration.ShouldBe(TimeSpan.FromMinutes(10));
		options.MaxContext.ShouldBe(TimeSpan.FromSeconds(10));
	}

	[Theory]
	[InlineData("GrantLifetime", "00:00:29")]
	[InlineData("GrantLifetime", "01:00:01")]
	[InlineData("RetryAfter", "00:00:00")]
	[InlineData("RetryAfter", "00:05:01")]
	[InlineData("ResolutionsPerMinute", "0")]
	[InlineData("ResolutionsPerMinute", "10001")]
	[InlineData("MaxDuration", "00:00:00")]
	[InlineData("MaxDuration", "01:00:01")]
	[InlineData("MaxContext", "-00:00:01")]
	[InlineData("MaxContext", "00:01:01")]
	public void Out_of_range_value_fails_at_start(string field, string value)
	{
		var settings = ValidSettings();
		settings[$"Analysis:Evidence:{field}"] = value;
		AssertFails(settings, field);
	}

	[Theory]
	[InlineData("GrantLifetime")]
	[InlineData("RetryAfter")]
	[InlineData("ResolutionsPerMinute")]
	[InlineData("MaxDuration")]
	[InlineData("MaxContext")]
	public void Missing_value_fails_at_start(string field)
	{
		var settings = ValidSettings();
		settings.Remove($"Analysis:Evidence:{field}");
		AssertFails(settings, field);
	}

	[Fact]
	public void Boundary_values_pass()
	{
		var settings = ValidSettings();
		settings["Analysis:Evidence:GrantLifetime"] = "00:00:30";
		settings["Analysis:Evidence:RetryAfter"] = "00:00:01";
		settings["Analysis:Evidence:ResolutionsPerMinute"] = "1";
		settings["Analysis:Evidence:MaxDuration"] = "00:00:01";
		settings["Analysis:Evidence:MaxContext"] = "00:00:00";
		Should.NotThrow(() => Validate(settings));
		settings["Analysis:Evidence:GrantLifetime"] = "01:00:00";
		settings["Analysis:Evidence:RetryAfter"] = "00:05:00";
		settings["Analysis:Evidence:ResolutionsPerMinute"] = "10000";
		settings["Analysis:Evidence:MaxDuration"] = "01:00:00";
		settings["Analysis:Evidence:MaxContext"] = "00:01:00";
		Should.NotThrow(() => Validate(settings));
	}

	private static void AssertFails(Dictionary<string, string?> settings, string field)
	{
		var ex = Should.Throw<OptionsValidationException>(() => Validate(settings));
		ex.Message.ShouldContain(field);
	}
}
