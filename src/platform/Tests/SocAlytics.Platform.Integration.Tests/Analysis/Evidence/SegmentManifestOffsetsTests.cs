using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Analysis.Evidence;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Evidence;

public sealed class SegmentManifestOffsetsTests
{
	private static string ExamplesDirectory()
	{
		var relative = Path.Combine("contracts", "segments", "segment-manifest", "v1", "examples");
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
		{
			dir = dir.Parent;
		}

		return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException(relative), relative);
	}

	private static IContractValidator Validator()
	{
		var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(TestIdentityAccessSettings.Values)).Build();
		var services = new ServiceCollection();
		services.AddSingleton<IConfiguration>(configuration);
		services.AddLogging();
		services.AddTestHostEnvironment();
		services.AddApplication();
		services.AddInfrastructure();
		return services.BuildServiceProvider().GetRequiredService<IContractValidator>();
	}

	private static JsonElement Load(string relative)
	{
		using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ExamplesDirectory(), relative)));
		return document.RootElement.Clone();
	}

	private static SegmentManifestOffsets Parse(string relative) =>
		SegmentManifestOffsets.TryParse(Load(relative), Validator()).ShouldNotBeNull();

	[Fact]
	public void Single_span_manifest_translates_window_relative_to_encoded_start()
	{
		var offsets = Parse("valid/segment-manifest.json");

		offsets.MediaType.ShouldBe("video/mp4");
		offsets.MediaSizeBytes.ShouldBe(2048576);
		offsets.TryTranslate(0, 10000, out var start, out var end).ShouldBeTrue();
		start.ShouldBe(500);
		end.ShouldBe(10500);
		offsets.TryTranslate(2500, 4000, out start, out end).ShouldBeTrue();
		start.ShouldBe(3000);
		end.ShouldBe(4500);
	}

	[Theory]
	[InlineData(10000, 12000, 500, 2500)]
	[InlineData(12000, 14000, 2500, 4500)]
	[InlineData(16000, 19000, 100500, 103500)]
	[InlineData(17000, 18000, 101500, 102500)]
	public void Clipped_manifest_translates_each_span_with_its_own_mapping(long from, long to, long expectedStart, long expectedEnd)
	{
		var offsets = Parse("valid/clipped-segment-manifest.json");

		offsets.TryTranslate(from, to, out var start, out var end).ShouldBeTrue();
		start.ShouldBe(expectedStart);
		end.ShouldBe(expectedEnd);
	}

	[Theory]
	[InlineData(13000, 17000)]
	[InlineData(14000, 16000)]
	[InlineData(9000, 11000)]
	[InlineData(18000, 20000)]
	[InlineData(12000, 12000)]
	[InlineData(12000, 11000)]
	public void Window_outside_one_span_is_refused(long from, long to)
	{
		var offsets = Parse("valid/clipped-segment-manifest.json");

		offsets.TryTranslate(from, to, out var start, out var end).ShouldBeFalse();
		start.ShouldBe(0);
		end.ShouldBe(0);
	}

	[Theory]
	[InlineData("missing-media-digest.json")]
	[InlineData("plain-digest-form.json")]
	[InlineData("zero-segment-number.json")]
	[InlineData("unknown-property.json")]
	[InlineData("audio-media-type.json")]
	public void Invalid_manifest_examples_are_refused(string file)
	{
		SegmentManifestOffsets.TryParse(Load("invalid/" + file), Validator()).ShouldBeNull();
	}
}
