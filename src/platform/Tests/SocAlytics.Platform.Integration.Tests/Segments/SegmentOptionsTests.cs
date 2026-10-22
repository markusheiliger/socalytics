using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Segments;

public sealed class SegmentOptionsTests
{
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

	private static Dictionary<string, string?> ValidSettings() => new(TestIdentityAccessSettings.Values);

	private static void Validate(Dictionary<string, string?> settings)
	{
		using var provider = Build(settings);
		provider.GetRequiredService<IStartupValidator>().Validate();
	}

	private static void AssertFails(Dictionary<string, string?> settings, string field)
	{
		var ex = Should.Throw<OptionsValidationException>(() => Validate(settings));
		ex.Message.ShouldContain(field);
	}

	[Fact]
	public void Configured_values_bind_and_pass_startup_validation()
	{
		var settings = ValidSettings();
		settings["Segments:Materialization:PollInterval"] = "00:00:02";
		settings["Segments:Materialization:LeaseDuration"] = "00:03:00";
		settings["Segments:Materialization:MaxConcurrentPerInstance"] = "4";
		settings["Segments:Materialization:MaxAttempts"] = "5";
		settings["Segments:Materialization:RetryDelay"] = "00:00:10";
		settings["Segments:Materialization:ExecutionTimeout"] = "00:20:00";
		settings["Segments:Materialization:MaxSegmentBytes"] = "1048576";
		settings["Segments:Materialization:WorkingDirectory"] = "/var/tmp/segments";
		settings["Segments:Toolchain:FfmpegPath"] = "/usr/bin/ffmpeg";
		settings["Segments:Toolchain:FfprobePath"] = "/usr/bin/ffprobe";
		settings["Segments:Toolchain:ExpectedVersionPrefix"] = "7.";

		using var provider = Build(settings);
		provider.GetRequiredService<IStartupValidator>().Validate();

		var materialization = provider.GetRequiredService<IOptions<SegmentMaterializationOptions>>().Value;
		materialization.PollInterval.ShouldBe(TimeSpan.FromSeconds(2));
		materialization.LeaseDuration.ShouldBe(TimeSpan.FromMinutes(3));
		materialization.MaxConcurrentPerInstance.ShouldBe(4);
		materialization.MaxAttempts.ShouldBe(5);
		materialization.RetryDelay.ShouldBe(TimeSpan.FromSeconds(10));
		materialization.ExecutionTimeout.ShouldBe(TimeSpan.FromMinutes(20));
		materialization.MaxSegmentBytes.ShouldBe(1048576);
		materialization.WorkingDirectory.ShouldBe("/var/tmp/segments");

		var toolchain = provider.GetRequiredService<IOptions<SegmentToolchainOptions>>().Value;
		toolchain.FfmpegPath.ShouldBe("/usr/bin/ffmpeg");
		toolchain.FfprobePath.ShouldBe("/usr/bin/ffprobe");
		toolchain.ExpectedVersionPrefix.ShouldBe("7.");
	}

	[Fact]
	public void Absent_sections_bind_valid_defaults()
	{
		using var provider = Build(ValidSettings());
		provider.GetRequiredService<IStartupValidator>().Validate();
		var materialization = provider.GetRequiredService<IOptions<SegmentMaterializationOptions>>().Value;
		materialization.MaxAttempts.ShouldBeGreaterThan(0);
		materialization.WorkingDirectory.ShouldNotBeNullOrWhiteSpace();
		provider.GetRequiredService<IOptions<SegmentToolchainOptions>>().Value.FfmpegPath.ShouldNotBeNullOrWhiteSpace();
	}

	[Theory]
	[InlineData("PollInterval", "00:00:00")]
	[InlineData("PollInterval", "-00:00:01")]
	[InlineData("LeaseDuration", "00:00:00")]
	[InlineData("MaxConcurrentPerInstance", "0")]
	[InlineData("MaxConcurrentPerInstance", "-1")]
	[InlineData("MaxAttempts", "0")]
	[InlineData("RetryDelay", "00:00:00")]
	[InlineData("ExecutionTimeout", "00:00:00")]
	[InlineData("MaxSegmentBytes", "0")]
	[InlineData("WorkingDirectory", " ")]
	public void Invalid_materialization_value_fails_at_start(string field, string value)
	{
		var settings = ValidSettings();
		settings[$"Segments:Materialization:{field}"] = value;
		AssertFails(settings, field);
	}

	[Theory]
	[InlineData("FfmpegPath")]
	[InlineData("FfprobePath")]
	public void Empty_toolchain_path_fails_at_start(string field)
	{
		var settings = ValidSettings();
		settings[$"Segments:Toolchain:{field}"] = " ";
		AssertFails(settings, field);
	}
}
