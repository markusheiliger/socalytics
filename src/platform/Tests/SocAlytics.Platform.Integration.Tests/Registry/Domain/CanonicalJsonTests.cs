using Shouldly;
using SocAlytics.Platform.Domain.Registry;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry.Domain;

public sealed class CanonicalJsonTests
{
    [Fact]
    public void Rfc8785_section_3_2_2_example_is_canonicalized()
    {
        const string input = """
            {
              "numbers": [333333333.33333329, 1E30, 4.50, 2e-3, 0.000000000000000000000000001],
              "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
              "literals": [null, true, false]
            }
            """;

        CanonicalJson.Canonicalize(input).ShouldBe(
            "{\"literals\":[null,true,false],\"numbers\":[333333333.3333333,1e+30,4.5,0.002,1e-27],\"string\":\"€$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\"}");
    }

    [Fact]
    public void Rfc8785_section_3_2_3_example_sorts_keys_by_utf16_code_units()
    {
        const string input = """
            {
              "\u20ac": "Euro Sign",
              "\r": "Carriage Return",
              "\ufb33": "Hebrew Letter Dalet With Dagesh",
              "1": "One",
              "\ud83d\ude00": "Emoji: Grinning Face",
              "\u0080": "Control",
              "\u00f6": "Latin Small Letter O With Diaeresis"
            }
            """;

        var canonical = CanonicalJson.Canonicalize(input);

        using var document = System.Text.Json.JsonDocument.Parse(canonical);
        document.RootElement.EnumerateObject().Select(p => p.Value.GetString()).ShouldBe(
        [
            "Carriage Return",
            "One",
            "Control",
            "Latin Small Letter O With Diaeresis",
            "Euro Sign",
            "Emoji: Grinning Face",
            "Hebrew Letter Dalet With Dagesh",
        ]);
    }

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(1.0, "1")]
    [InlineData(-1.5, "-1.5")]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(123456789012345680000.0, "123456789012345680000")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(0.0000001, "1e-7")]
    [InlineData(9007199254740991.0, "9007199254740991")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    [InlineData(5e-324, "5e-324")]
    public void Numbers_use_ecmascript_formatting(double value, string expected) =>
        CanonicalJson.FormatNumber(value).ShouldBe(expected);

    [Fact]
    public void Whitespace_and_key_order_do_not_change_the_digest()
    {
        ContentDigest.Of("{ \"b\": 1, \"a\": [1, 2] }").ShouldBe(ContentDigest.Of("{\"a\":[1,2],\"b\":1}"));
        ContentDigest.Of("{\"a\":1}").ShouldNotBe(ContentDigest.Of("{\"a\":2}"));
    }
}
