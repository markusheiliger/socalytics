using System.Text.Json;
using Shouldly;
using SocAlytics.Analysts.Manager.Platform;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.GoldenFixtures;

public class QueueDepthFixtureTests
{
    private static readonly string Folder = Path.Combine(
        AppContext.BaseDirectory, "contracts", "analyst-manager", "queue-depth", "v1", "examples", "valid");

    public static TheoryData<string> ValidFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ValidFiles))]
    public void Client_parses_every_valid_example(string file)
    {
        var json = File.ReadAllText(Path.Combine(Folder, file));
        var parsed = JsonSerializer.Deserialize<QueueDepthResponse>(json, PlatformJson.Options);

        parsed.ShouldNotBeNull();
        parsed.Capabilities.ShouldNotBeNull();
        parsed.Capabilities.Sum(c => c.Waiting).ShouldBe(parsed.Total);
        parsed.ObservedAt.Year.ShouldBe(2026);
    }

    [Fact]
    public void Available_example_carries_the_counts()
    {
        var parsed = JsonSerializer.Deserialize<QueueDepthResponse>(
            File.ReadAllText(Path.Combine(Folder, "available.json")), PlatformJson.Options)!;

        parsed.Capabilities.ShouldNotBeEmpty();
        parsed.Total.ShouldBeGreaterThan(0);
    }
}
