using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class RecordingOptionsValidationTests
{
	private static Dictionary<string, string?> ValidSettings() => new()
	{
		["ObjectStorage:ServiceUrl"] = "http://127.0.0.1:9",
		["ObjectStorage:AccessKey"] = "access",
		["ObjectStorage:SecretKey"] = "secret",
		["ObjectStorage:Bucket"] = "bucket",
		["Recordings:Upload:SessionLifetime"] = "00:30:00",
		["Recordings:Upload:GrantLifetime"] = "00:15:00",
		["Recordings:Upload:ExpirySweepInterval"] = "00:01:00",
		["Recordings:Upload:MaxObjectSizeBytes"] = "5497558138880",
		["Recordings:Upload:MinPartSizeBytes"] = "5242880",
		["Recordings:Upload:MaxPartSizeBytes"] = "5368709120",
		["Recordings:Upload:MaxPartCount"] = "10000",
		["Recordings:Upload:MaxGrantsPerRequest"] = "1000",
		["Recordings:Upload:AllowedContentTypes:0"] = "video/mp4",
		["Recordings:Sets:MaxMembers"] = "100",
	};

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
	public void Valid_settings_pass_startup_validation()
	{
		Should.NotThrow(() => Validate(ValidSettings()));
	}

	[Fact]
	public void Absent_max_members_binds_default()
	{
		var settings = ValidSettings();
		settings.Remove("Recordings:Sets:MaxMembers");
		using var provider = Build(settings);
		provider.GetRequiredService<IStartupValidator>().Validate();
		provider.GetRequiredService<IOptions<RecordingSetOptions>>().Value.MaxMembers.ShouldBe(100);
	}

	[Theory]
	[InlineData("SessionLifetime")]
	[InlineData("GrantLifetime")]
	[InlineData("ExpirySweepInterval")]
	[InlineData("MaxObjectSizeBytes")]
	[InlineData("MinPartSizeBytes")]
	[InlineData("MaxPartSizeBytes")]
	[InlineData("MaxPartCount")]
	[InlineData("MaxGrantsPerRequest")]
	[InlineData("AllowedContentTypes")]
	public void Missing_required_upload_value_fails(string field)
	{
		var settings = ValidSettings();
		foreach (var key in settings.Keys.Where(k => k.StartsWith($"Recordings:Upload:{field}", StringComparison.Ordinal)).ToList())
		{
			settings.Remove(key);
		}

		AssertFails(settings, field);
	}

	[Theory]
	[InlineData("MaxObjectSizeBytes", "5497558138881")]
	[InlineData("MinPartSizeBytes", "5242879")]
	[InlineData("MaxPartSizeBytes", "5368709121")]
	[InlineData("MaxPartCount", "0")]
	[InlineData("MaxPartCount", "10001")]
	[InlineData("MaxGrantsPerRequest", "0")]
	[InlineData("MaxGrantsPerRequest", "1001")]
	[InlineData("SessionLifetime", "00:00:00")]
	[InlineData("SessionLifetime", "-00:01:00")]
	[InlineData("GrantLifetime", "00:00:00")]
	[InlineData("GrantLifetime", "-00:01:00")]
	[InlineData("GrantLifetime", "7.00:00:01")]
	[InlineData("ExpirySweepInterval", "00:00:00")]
	[InlineData("ExpirySweepInterval", "-00:01:00")]
	public void Invalid_upload_value_fails(string field, string value)
	{
		var settings = ValidSettings();
		settings[$"Recordings:Upload:{field}"] = value;
		AssertFails(settings, field);
	}

	[Fact]
	public void Min_part_size_above_max_fails()
	{
		var settings = ValidSettings();
		settings["Recordings:Upload:MinPartSizeBytes"] = "10485760";
		settings["Recordings:Upload:MaxPartSizeBytes"] = "5242880";
		AssertFails(settings, "MinPartSizeBytes");
	}

	[Fact]
	public void Empty_allowed_content_types_fail()
	{
		var settings = ValidSettings();
		settings["Recordings:Upload:AllowedContentTypes:0"] = "";
		AssertFails(settings, "AllowedContentTypes");
	}

	[Theory]
	[InlineData("0")]
	[InlineData("1001")]
	public void Invalid_max_members_fails(string value)
	{
		var settings = ValidSettings();
		settings["Recordings:Sets:MaxMembers"] = value;
		AssertFails(settings, "MaxMembers");
	}

	[Theory]
	[InlineData("ServiceUrl")]
	[InlineData("AccessKey")]
	[InlineData("SecretKey")]
	[InlineData("Bucket")]
	public void Missing_object_storage_value_fails(string field)
	{
		var settings = ValidSettings();
		settings.Remove($"ObjectStorage:{field}");
		AssertFails(settings, field);
	}

	private static void AssertFails(Dictionary<string, string?> settings, string field)
	{
		var ex = Should.Throw<OptionsValidationException>(() => Validate(settings));
		ex.Message.ShouldContain(field);
	}
}
