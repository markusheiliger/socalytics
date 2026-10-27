using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

/// <summary>The push digest reader accepts the manifest digest from an <c>aux</c> entry, from the status line, or from both, without Docker.</summary>
public sealed class PushDigestReaderTests
{
    private static readonly string AuxHex = new('a', 64);
    private static readonly string StatusHex = new('b', 64);

    private static string Aux(string hex) => $$$"""{"aux":{"Tag":"latest","Digest":"sha256:{{{hex}}}","Size":1234}}""";

    private static string Status(string text) => JsonSerializer.Serialize(new { status = text });

    private static string StatusLine(string hex, string tag = "latest") => Status($"{tag}: digest: sha256:{hex} size: 1234");

    private static string Body(params string[] lines) => string.Join('\n', lines) + "\n";

    private static void ShouldReportNoDigest(string body) =>
        Should.Throw<InvalidOperationException>(() => PushDigestReader.Read(body))
            .Message.ShouldContain("The push did not report a manifest digest");

    [Fact]
    public void Aux_only_returns_the_aux_digest()
    {
        PushDigestReader.Read(Body(Aux(AuxHex))).ShouldBe("sha256:" + AuxHex);
    }

    [Fact]
    public void Status_line_only_returns_the_status_digest()
    {
        PushDigestReader.Read(Body(StatusLine(StatusHex))).ShouldBe("sha256:" + StatusHex);
    }

    [Fact]
    public void Aux_wins_over_the_status_line_in_either_order()
    {
        PushDigestReader.Read(Body(StatusLine(StatusHex), Aux(AuxHex))).ShouldBe("sha256:" + AuxHex);
        PushDigestReader.Read(Body(Aux(AuxHex), StatusLine(StatusHex))).ShouldBe("sha256:" + AuxHex);
    }

    [Fact]
    public void Neither_digest_fails_and_names_the_engine_output()
    {
        var body = Body(Status("Pushed"));
        Should.Throw<InvalidOperationException>(() => PushDigestReader.Read(body))
            .Message.ShouldContain(body);
        ShouldReportNoDigest("");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Error_detail_fails_wherever_it_appears(bool beforeDigest)
    {
        const string error = """{"errorDetail":{"message":"denied: requested access to the resource is denied"},"error":"denied"}""";
        var body = beforeDigest ? Body(error, Aux(AuxHex)) : Body(Aux(AuxHex), StatusLine(StatusHex), error);

        var exception = Should.Throw<InvalidOperationException>(() => PushDigestReader.Read(body));
        exception.Message.ShouldContain("Pushing the Test Analyst image failed");
        exception.Message.ShouldContain("denied: requested access to the resource is denied");
    }

    [Theory]
    [InlineData("latest: digest: sha256:{63} size: 10")]
    [InlineData("latest: digest: sha256:{65} size: 10")]
    [InlineData("latest: digest: sha256:{upper} size: 10")]
    [InlineData("latest: digest: sha512:{64} size: 10")]
    [InlineData("pushed latest: digest: sha256:{64} size: 10")]
    [InlineData("latest: digest: sha256:{64} size: 10 extra")]
    [InlineData("latest: digest: sha256:{64} size: 10\n")]
    [InlineData("latest: digest: sha256:{64} size: 10 ")]
    [InlineData("latest: digest: sha256:{nonhex} size: 10")]
    [InlineData("latest: digest: sha256:{64} size: ")]
    public void Malformed_status_digests_report_no_digest(string template)
    {
        var text = template
            .Replace("{63}", new string('c', 63))
            .Replace("{65}", new string('c', 65))
            .Replace("{upper}", new string('C', 64))
            .Replace("{nonhex}", new string('g', 64))
            .Replace("{64}", new string('c', 64));

        ShouldReportNoDigest(Body(Status(text)));
    }

    [Fact]
    public void Unrelated_progress_lines_are_not_mistaken_for_a_digest()
    {
        var body = Body(
            Status("The push refers to repository [localhost:5000/analyst]"),
            """{"status":"Preparing","progressDetail":{},"id":"0123456789ab"}""",
            """{"status":"Pushing","progressDetail":{"current":1,"total":2},"progress":"[=>  ]","id":"0123456789ab"}""",
            """{"status":"Pushed","progressDetail":{},"id":"0123456789ab"}""");

        ShouldReportNoDigest(body);
    }

    [Fact]
    public void A_non_string_status_is_ignored()
    {
        PushDigestReader.Read(Body("""{"status":42}""", """{"status":null}""", """{"status":{"a":1}}""", StatusLine(StatusHex)))
            .ShouldBe("sha256:" + StatusHex);
        ShouldReportNoDigest(Body("""{"status":42}"""));
    }

    [Fact]
    public void A_non_json_line_throws_a_json_exception()
    {
        Should.Throw<JsonException>(() => PushDigestReader.Read(Body(StatusLine(StatusHex), "not json")));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void A_non_object_json_line_is_rejected(string line)
    {
        Should.Throw<InvalidOperationException>(() => PushDigestReader.Read(Body(line)));
    }

    [Theory]
    [InlineData("""{"aux":"text"}""")]
    [InlineData("""{"aux":null}""")]
    [InlineData("""{"aux":[1]}""")]
    public void A_non_object_aux_is_rejected(string line)
    {
        Should.Throw<InvalidOperationException>(() => PushDigestReader.Read(Body(line)));
    }

    [Theory]
    [InlineData("""{"aux":{"Digest":42}}""")]
    [InlineData("""{"aux":{"Digest":{"a":1}}}""")]
    [InlineData("""{"aux":{"Digest":null}}""")]
    public void A_non_string_aux_digest_is_rejected(string line)
    {
        Should.Throw<InvalidOperationException>(() => PushDigestReader.Read(Body(line)));
    }

    [Fact]
    public void A_null_aux_digest_after_a_valid_one_does_not_overwrite_it()
    {
        Should.Throw<InvalidOperationException>(() => PushDigestReader.Read(Body(Aux(AuxHex), """{"aux":{"Digest":null}}""")));
    }

    [Fact]
    public void A_different_tag_is_accepted()
    {
        PushDigestReader.Read(Body(StatusLine(StatusHex, "v1.2-rc_3"))).ShouldBe("sha256:" + StatusHex);
    }

    [Fact]
    public void The_last_matching_status_line_wins()
    {
        var later = new string('d', 64);
        PushDigestReader.Read(Body(StatusLine(StatusHex), Status("Pushed"), StatusLine(later))).ShouldBe("sha256:" + later);
    }

    [Fact]
    public void The_last_aux_digest_wins()
    {
        var later = new string('e', 64);
        PushDigestReader.Read(Body(Aux(AuxHex), Aux(later))).ShouldBe("sha256:" + later);
    }
}
