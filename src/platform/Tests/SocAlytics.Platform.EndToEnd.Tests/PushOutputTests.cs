using Shouldly;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

/// <summary>Reading the pushed manifest digest from a Docker Engine push response without a container engine.</summary>
public sealed class PushOutputTests
{
    private static readonly string DigestA = "sha256:" + new string('a', 64);
    private static readonly string DigestB = "sha256:" + new string('b', 64);

    private static string Join(params string[] lines) => string.Join('\n', lines) + "\n";

    private static string StatusLine(string tag, string digest) =>
        $$$"""{"status":"{{{tag}}}: digest: {{{digest}}} size: 1234"}""";

    private static string AuxLine(string digest) =>
        $$$"""{"progressDetail":{},"aux":{"Tag":"1.0.0","Digest":"{{{digest}}}","Size":1234}}""";

    [Fact]
    public void Status_line_style_returns_the_digest()
    {
        var body = Join(
            """{"status":"The push refers to repository [registry/x]"}""",
            StatusLine("1.0.0", DigestA));

        PushOutput.ReadDigest(body).ShouldBe(DigestA);
    }

    [Fact]
    public void Aux_digest_style_is_unchanged()
    {
        PushOutput.ReadDigest(Join(AuxLine(DigestA))).ShouldBe(DigestA);
    }

    [Fact]
    public void Aux_digest_wins_over_a_status_digest()
    {
        PushOutput.ReadDigest(Join(StatusLine("1.0.0", DigestB), AuxLine(DigestA))).ShouldBe(DigestA);
        PushOutput.ReadDigest(Join(AuxLine(DigestA), StatusLine("1.0.0", DigestB))).ShouldBe(DigestA);
    }

    [Fact]
    public void The_last_aux_digest_is_returned()
    {
        PushOutput.ReadDigest(Join(AuxLine(DigestA), AuxLine(DigestB))).ShouldBe(DigestB);
    }

    [Fact]
    public void An_error_detail_throws_the_push_failure()
    {
        var error = """{"errorDetail":{"message":"denied"},"error":"denied"}""";

        var withoutDigest = Should.Throw<InvalidOperationException>(() => PushOutput.ReadDigest(Join(error)));
        withoutDigest.Message.ShouldStartWith("Pushing the Test Analyst image failed: ");
        withoutDigest.Message.ShouldContain("denied");

        var withDigest = Should.Throw<InvalidOperationException>(
            () => PushOutput.ReadDigest(Join(StatusLine("1.0.0", DigestA), AuxLine(DigestA), error)));
        withDigest.Message.ShouldStartWith("Pushing the Test Analyst image failed: ");
    }

    [Fact]
    public void Status_lines_that_are_not_a_push_digest_are_ignored()
    {
        var body = Join(
            """{"status":"Preparing","progressDetail":{},"id":"0123456789ab"}""",
            """{"status":"Layer already exists","progressDetail":{},"id":"0123456789ab"}""",
            $$$"""{"status":"Pushed","id":"{{{DigestA}}}"}""",
            """{"status":"Pushing","progress":"[==>   ] 1.2kB/3kB","id":"abc"}""");

        var error = Should.Throw<InvalidOperationException>(() => PushOutput.ReadDigest(body));
        error.Message.ShouldStartWith("The push did not report a manifest digest:\n");
    }

    [Theory]
    [InlineData(63, 'a')]
    [InlineData(65, 'a')]
    [InlineData(64, 'A')]
    public void A_malformed_digest_is_rejected(int length, char character)
    {
        var digest = "sha256:" + new string(character, length);

        Should.Throw<InvalidOperationException>(() => PushOutput.ReadDigest(Join(StatusLine("1.0.0", digest))))
            .Message.ShouldStartWith("The push did not report a manifest digest:");
    }

    [Fact]
    public void A_tag_with_dot_and_dash_is_accepted()
    {
        PushOutput.ReadDigest(Join(StatusLine("1.0.0-rc.1", DigestA))).ShouldBe(DigestA);
    }

    [Fact]
    public void Stray_lines_neither_hide_a_later_digest_nor_throw_a_json_error()
    {
        var body = Join("not json at all", "[1,2,3]", "42", "\"text\"", "{broken", StatusLine("1.0.0", DigestA));

        PushOutput.ReadDigest(body).ShouldBe(DigestA);
    }

    [Fact]
    public void A_malformed_aux_neither_throws_nor_hides_a_later_digest()
    {
        var body = Join(
            """{"aux":"text"}""",
            """{"aux":[1]}""",
            """{"aux":{"Digest":42}}""",
            """{"aux":{"Digest":null}}""",
            """{"status":42}""",
            StatusLine("1.0.0", DigestA));

        PushOutput.ReadDigest(body).ShouldBe(DigestA);
    }

    [Fact]
    public void A_stream_without_a_digest_throws_with_the_push_body()
    {
        var body = Join("""{"status":"Pushed"}""");

        var error = Should.Throw<InvalidOperationException>(() => PushOutput.ReadDigest(body));
        error.Message.ShouldBe("The push did not report a manifest digest:\n" + body);
    }
}
