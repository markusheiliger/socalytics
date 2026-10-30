using Shouldly;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests.ImagePush;

/// <summary>The digest determination over recorded push response bodies, without Docker.</summary>
public sealed class PushDigestTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherDigest = "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    private const string Repository = "localhost:5000/analysts/probe";
    private const string Tag = "1.0.0";

    private const string Progress = """{"status":"Pushing","progressDetail":{"current":10,"total":20},"id":"0123456789ab"}""";

    private static readonly string ProgressRecord = $$$"""{"progressDetail":{},"aux":{"Tag":"1.0.0","Digest":"{{{Digest}}}","Size":1234}}""";

    private static readonly string StatusLine = $$"""{"status":"1.0.0: digest: {{Digest}} size: 1234"}""";

    [Fact]
    public async Task Progress_record_alone_yields_the_digest_without_lookups()
    {
        var lookups = new Lookups();

        var result = await DetermineAsync(Body(Progress, ProgressRecord), lookups);

        result.Succeeded.ShouldBeTrue();
        result.Digest.ShouldBe(Digest);
        result.Source.ShouldBe(DigestSource.ProgressRecord);
        lookups.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Status_line_alone_yields_the_digest_without_lookups()
    {
        var lookups = new Lookups();

        var result = await DetermineAsync(Body(Progress, StatusLine), lookups);

        result.Succeeded.ShouldBeTrue();
        result.Digest.ShouldBe(Digest);
        result.Source.ShouldBe(DigestSource.StatusLine);
        lookups.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Agreeing_record_and_status_line_succeed_without_lookups()
    {
        var lookups = new Lookups();

        var result = await DetermineAsync(Body(Progress, StatusLine, ProgressRecord), lookups);

        result.Succeeded.ShouldBeTrue();
        result.Digest.ShouldBe(Digest);
        lookups.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Disagreeing_record_and_status_line_fail_with_diagnostics()
    {
        var status = $$"""{"status":"1.0.0: digest: {{OtherDigest}} size: 1234"}""";
        var lookups = new Lookups();

        var result = await DetermineAsync(Body(ProgressRecord, status), lookups);

        result.Succeeded.ShouldBeFalse();
        result.Digest.ShouldBeNull();
        result.FailureMessage!.ShouldContain(Digest);
        result.FailureMessage!.ShouldContain(OtherDigest);
        result.FailureMessage!.ShouldContain("ProgressRecord: found");
        result.FailureMessage!.ShouldContain("StatusLine: found");
        result.FailureMessage!.ShouldContain(status);
        lookups.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Disagreeing_record_and_status_line_fail_in_either_order()
    {
        var status = $$"""{"status":"1.0.0: digest: {{OtherDigest}} size: 1234"}""";
        var lookups = new Lookups { Registry = Digest, Engine = Digest };

        foreach (var body in new[] { Body(status, ProgressRecord), Body(ProgressRecord, status) })
        {
            var result = await DetermineAsync(body, lookups);

            result.Succeeded.ShouldBeFalse();
            result.Digest.ShouldBeNull();
            result.FailureMessage!.ShouldContain("conflicting manifest digests");
        }

        lookups.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("v1.2")]
    [InlineData("1.0.0-rc.1")]
    [InlineData("v1_2-rc_3")]
    public async Task Tags_with_dots_dashes_and_underscores_are_matched(string tag)
    {
        var status = $$"""{"status":"{{tag}}: digest: {{Digest}} size: 1234"}""";
        var lookups = new Lookups();

        var result = await PushDigest.DetermineAsync(Body(Progress, status), Repository, tag, lookups.LookUpRegistryAsync, lookups.LookUpEngineAsync, CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        result.Digest.ShouldBe(Digest);
        result.Source.ShouldBe(DigestSource.StatusLine);
        lookups.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task The_last_of_several_status_lines_wins()
    {
        var earlier = $$"""{"status":"1.0.0: digest: {{OtherDigest}} size: 1234"}""";
        var lookups = new Lookups();

        var result = await DetermineAsync(Body(earlier, Progress, StatusLine), lookups);

        result.Succeeded.ShouldBeTrue();
        result.Digest.ShouldBe(Digest);
        result.Source.ShouldBe(DigestSource.StatusLine);
        lookups.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task The_last_of_several_progress_records_wins()
    {
        var earlier = $$$"""{"aux":{"Tag":"1.0.0","Digest":"{{{OtherDigest}}}","Size":1234}}""";
        var lookups = new Lookups();

        var result = await DetermineAsync(Body(earlier, Progress, ProgressRecord), lookups);

        result.Succeeded.ShouldBeTrue();
        result.Digest.ShouldBe(Digest);
        result.Source.ShouldBe(DigestSource.ProgressRecord);
        lookups.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Non_string_status_values_are_ignored()
    {
        var lookups = new Lookups();

        var result = await DetermineAsync(Body("""{"status":42}""", """{"status":null}""", """{"status":{"a":1}}""", StatusLine), lookups);

        result.Succeeded.ShouldBeTrue();
        result.Digest.ShouldBe(Digest);
        result.Source.ShouldBe(DigestSource.StatusLine);

        var onlyNonString = await DetermineAsync(Body("""{"status":42}"""), new Lookups());

        onlyNonString.Succeeded.ShouldBeFalse();
        onlyNonString.FailureMessage!.ShouldContain("StatusLine: absent");
    }

    [Fact]
    public async Task Progress_record_with_a_null_digest_yields_no_digest()
    {
        var lookups = new Lookups();

        var result = await DetermineAsync(Body("""{"aux":{"Digest":null}}"""), lookups);

        result.Succeeded.ShouldBeFalse();
        result.Digest.ShouldBeNull();
        result.FailureMessage!.ShouldContain("ProgressRecord: absent");
        result.FailureMessage!.ShouldContain("The push did not report a manifest digest");
    }

    [Fact]
    public async Task Registry_digest_is_used_when_the_response_carries_none()
    {
        var lookups = new Lookups { Registry = Digest };

        var result = await DetermineAsync(Body(Progress), lookups);

        result.Succeeded.ShouldBeTrue();
        result.Digest.ShouldBe(Digest);
        result.Source.ShouldBe(DigestSource.RegistryManifest);
        lookups.RegistryCalls.ShouldBe(1);
        lookups.EngineCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Engine_record_is_used_when_only_the_engine_has_the_digest()
    {
        var lookups = new Lookups { Registry = null, Engine = Digest };

        var result = await DetermineAsync(Body(Progress), lookups);

        result.Succeeded.ShouldBeTrue();
        result.Digest.ShouldBe(Digest);
        result.Source.ShouldBe(DigestSource.EngineImageRecord);
        lookups.RegistryCalls.ShouldBe(1);
        lookups.EngineCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Engine_record_is_used_when_the_registry_is_unavailable()
    {
        var lookups = new Lookups { RegistryFailure = new HttpRequestException("registry down"), Engine = Digest };

        var result = await DetermineAsync(Body(Progress), lookups);

        result.Succeeded.ShouldBeTrue();
        result.Source.ShouldBe(DigestSource.EngineImageRecord);
    }

    [Fact]
    public async Task No_source_fails_with_the_full_body_and_each_sources_result()
    {
        var body = Body(Progress, """{"status":"Pushed","id":"0123456789ab"}""");
        var lookups = new Lookups { EngineFailure = new InvalidOperationException("boom") };

        var result = await DetermineAsync(body, lookups);

        result.Succeeded.ShouldBeFalse();
        result.FailureMessage!.ShouldContain("The push did not report a manifest digest");
        result.FailureMessage!.ShouldContain($"{Repository}:{Tag}");
        result.FailureMessage!.ShouldContain("ProgressRecord: absent");
        result.FailureMessage!.ShouldContain("StatusLine: absent");
        result.FailureMessage!.ShouldContain("RegistryManifest: absent");
        result.FailureMessage!.ShouldContain("EngineImageRecord: unavailable: boom");
        result.FailureMessage!.ShouldContain(body);
    }

    [Theory]
    [InlineData("""{"errorDetail":{"message":"denied: requested access is denied"},"error":"denied: requested access is denied"}""")]
    [InlineData("""{"error":"unauthorized: authentication required"}""")]
    public async Task Error_line_fails_with_that_error_before_any_digest_and_without_lookups(string errorLine)
    {
        var lookups = new Lookups { Registry = Digest, Engine = Digest };

        var result = await DetermineAsync(Body(ProgressRecord, StatusLine, errorLine), lookups);

        result.Succeeded.ShouldBeFalse();
        result.FailureMessage!.ShouldContain("The push reported an error");
        result.FailureMessage!.ShouldContain(errorLine.Contains("denied") ? "denied" : "unauthorized");
        lookups.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Status_line_for_another_tag_is_ignored()
    {
        var other = $$"""{"status":"latest: digest: {{OtherDigest}} size: 1234"}""";
        var lookups = new Lookups { Registry = Digest };

        var result = await DetermineAsync(Body(Progress, other), lookups);

        result.Succeeded.ShouldBeTrue();
        result.Digest.ShouldBe(Digest);
        result.Source.ShouldBe(DigestSource.RegistryManifest);
    }

    [Theory]
    [InlineData("sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("sha256:0123")]
    [InlineData("sha512:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public async Task Malformed_digest_is_never_accepted_from_any_source(string malformed)
    {
        var record = $$$"""{"aux":{"Tag":"1.0.0","Digest":"{{{malformed}}}","Size":1}}""";
        var status = $$"""{"status":"1.0.0: digest: {{malformed}} size: 1234"}""";
        var lookups = new Lookups { Registry = malformed, Engine = malformed };

        var result = await DetermineAsync(Body(record, status), lookups);

        result.Succeeded.ShouldBeFalse();
        result.Digest.ShouldBeNull();
        result.FailureMessage!.ShouldContain("ProgressRecord: unavailable: malformed digest");
        result.FailureMessage!.ShouldContain("StatusLine: unavailable: malformed digest");
        result.FailureMessage!.ShouldContain("RegistryManifest: unavailable: malformed digest");
        result.FailureMessage!.ShouldContain("EngineImageRecord: unavailable: malformed digest");
    }

    [Fact]
    public async Task Malformed_json_line_fails_deterministically_with_the_body()
    {
        var body = Body(Progress, "{\"status\": \"Pushed\"", ProgressRecord);
        var lookups = new Lookups { Registry = Digest };

        var result = await DetermineAsync(body, lookups);

        result.Succeeded.ShouldBeFalse();
        result.FailureMessage!.ShouldContain("malformed");
        result.FailureMessage!.ShouldContain(body);
        lookups.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Failure_message_never_contains_registry_credentials()
    {
        const string Credential = "eyJ1c2VybmFtZSI6InVzZXIiLCJwYXNzd29yZCI6InNlY3JldCJ9";
        var lookups = new Lookups { RegistryFailure = new HttpRequestException("registry unreachable"), EngineFailure = new IOException("engine unreachable") };

        var result = await DetermineAsync(Body(Progress), lookups);

        result.Succeeded.ShouldBeFalse();
        result.FailureMessage!.ShouldNotContain("X-Registry-Auth");
        result.FailureMessage!.ShouldNotContain(Credential);
    }

    private static string Body(params string[] lines) => string.Join('\n', lines) + "\n";

    private static Task<PushDigestResult> DetermineAsync(string body, Lookups lookups) =>
        PushDigest.DetermineAsync(body, Repository, Tag, lookups.LookUpRegistryAsync, lookups.LookUpEngineAsync, CancellationToken.None);

    private sealed class Lookups
    {
        public string? Registry { get; init; }

        public string? Engine { get; init; }

        public Exception? RegistryFailure { get; init; }

        public Exception? EngineFailure { get; init; }

        public int RegistryCalls { get; private set; }

        public int EngineCalls { get; private set; }

        public int Calls => RegistryCalls + EngineCalls;

        public Task<string?> LookUpRegistryAsync(CancellationToken ct)
        {
            RegistryCalls++;
            return RegistryFailure is null ? Task.FromResult(Registry) : Task.FromException<string?>(RegistryFailure);
        }

        public Task<string?> LookUpEngineAsync(CancellationToken ct)
        {
            EngineCalls++;
            return EngineFailure is null ? Task.FromResult(Engine) : Task.FromException<string?>(EngineFailure);
        }
    }
}
