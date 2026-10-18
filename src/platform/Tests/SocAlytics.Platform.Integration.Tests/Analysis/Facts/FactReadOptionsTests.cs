using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Analysis.Facts;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Facts;

public sealed class FactReadOptionsTests
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
		var options = provider.GetRequiredService<IOptions<FactReadOptions>>().Value;
		options.DefaultPageSize.ShouldBe(100);
		options.MaxPageSize.ShouldBe(500);
	}

	[Theory]
	[InlineData("DefaultPageSize", "0")]
	[InlineData("DefaultPageSize", "-1")]
	[InlineData("DefaultPageSize", "501")]
	[InlineData("MaxPageSize", "0")]
	[InlineData("MaxPageSize", "-1")]
	[InlineData("MaxPageSize", "1001")]
	[InlineData("MaxPageSize", "99")]
	public void Invalid_value_fails_at_start(string field, string value)
	{
		var settings = ValidSettings();
		settings[$"Analysis:Facts:{field}"] = value;
		AssertFails(settings, field);
	}

	[Theory]
	[InlineData("DefaultPageSize")]
	[InlineData("MaxPageSize")]
	public void Missing_value_fails_at_start(string field)
	{
		var settings = ValidSettings();
		settings.Remove($"Analysis:Facts:{field}");
		AssertFails(settings, field);
	}

	[Fact]
	public void Boundary_values_pass()
	{
		var settings = ValidSettings();
		settings["Analysis:Facts:DefaultPageSize"] = "1";
		settings["Analysis:Facts:MaxPageSize"] = "1";
		Should.NotThrow(() => Validate(settings));
		settings["Analysis:Facts:DefaultPageSize"] = "1000";
		settings["Analysis:Facts:MaxPageSize"] = "1000";
		Should.NotThrow(() => Validate(settings));
	}

	private static void AssertFails(Dictionary<string, string?> settings, string field)
	{
		var ex = Should.Throw<OptionsValidationException>(() => Validate(settings));
		ex.Message.ShouldContain(field);
	}
}
