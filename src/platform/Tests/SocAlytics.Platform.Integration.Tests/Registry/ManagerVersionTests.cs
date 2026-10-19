using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Domain.Registry;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

// Runs every case of the shared comparison fixtures (contracts/analyst-manager/status/v1) against ManagerVersion.
public sealed class ManagerVersionTests
{
    private static readonly string FixturesPath = Path.Combine(
        AppContext.BaseDirectory, "contracts", "analyst-manager", "status", "v1", "minimum-version.fixtures.json");

    public static TheoryData<string?, string?, bool> Cases()
    {
        var data = new TheoryData<string?, string?, bool>();
        foreach (var item in JsonDocument.Parse(File.ReadAllText(FixturesPath)).RootElement.EnumerateArray())
        {
            data.Add(Read(item, "reported"), Read(item, "minimum"), item.GetProperty("below").GetBoolean());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void IsBelow_matches_the_shared_fixture(string? reported, string? minimum, bool below)
    {
        ManagerVersion? parsed = null;
        if (minimum is not null)
        {
            ManagerVersion.TryParse(minimum, out parsed).ShouldBeTrue(minimum);
        }

        ManagerVersion.IsBelow(reported, parsed).ShouldBe(below);
    }

    [Fact]
    public void Fixture_file_has_the_five_cases() =>
        JsonDocument.Parse(File.ReadAllText(FixturesPath)).RootElement.GetArrayLength().ShouldBe(5);

    [Theory]
    [InlineData("0.2")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-beta")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0.0")]
    [InlineData("")]
    [InlineData(null)]
    public void Malformed_values_do_not_parse(string? value) => ManagerVersion.TryParse(value, out _).ShouldBeFalse();

    [Fact]
    public void Ordering_compares_major_then_minor_then_patch()
    {
        ManagerVersion.TryParse("1.0.0", out var a).ShouldBeTrue();
        ManagerVersion.TryParse("0.99.99", out var b).ShouldBeTrue();
        ManagerVersion.TryParse("1.0.1", out var c).ShouldBeTrue();
        a!.CompareTo(b).ShouldBeGreaterThan(0);
        c!.CompareTo(a).ShouldBeGreaterThan(0);
    }

    private static string? Read(JsonElement item, string name) =>
        item.GetProperty(name).ValueKind == JsonValueKind.Null ? null : item.GetProperty(name).GetString();
}
