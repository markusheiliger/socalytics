using System.Net;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Tests.Support;

namespace SocAlytics.Analysts.Manager.Tests.Platform;

public class PlatformSignalsTests
{
    private const string Inactive =
        """{"type":"urn:socalytics:problem:analyst-manager-registration-inactive","title":"t","status":401,"code":"analyst-manager-registration-inactive","correlationId":"c","registrationState":"revoked"}""";

    [Fact]
    public void Token_invalid_client_with_state_is_inactive()
    {
        var s = PlatformSignals.ClassifyToken(HttpStatusCode.BadRequest, """{"error":"invalid_client","registration_state":"unregistered"}""");
        s.Outcome.ShouldBe(PlatformOutcome.Inactive);
        s.Reason.ShouldBe(InactiveReason.Unregistered);
    }

    [Fact]
    public void Token_invalid_client_without_state_is_refused()
    {
        PlatformSignals.ClassifyToken(HttpStatusCode.BadRequest, """{"error":"invalid_client"}""").Outcome.ShouldBe(PlatformOutcome.Refused);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, PlatformOutcome.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PlatformOutcome.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, PlatformOutcome.Transient)]
    [InlineData(HttpStatusCode.Forbidden, PlatformOutcome.Refused)]
    public void Other_statuses_are_never_inactive(HttpStatusCode status, PlatformOutcome expected)
    {
        PlatformSignals.ClassifyToken(status, "").Outcome.ShouldBe(expected);
        PlatformSignals.ClassifyResource(status, ["DPoP"], Inactive).Outcome.ShouldBe(expected);
    }

    [Fact]
    public void Resource_401_with_dpop_and_problem_is_inactive()
    {
        var s = PlatformSignals.ClassifyResource(HttpStatusCode.Unauthorized, ["DPoP error=\"invalid_token\""], Inactive);
        s.Outcome.ShouldBe(PlatformOutcome.Inactive);
        s.Reason.ShouldBe(InactiveReason.Revoked);
    }

    [Fact]
    public void Resource_401_without_dpop_challenge_or_problem_is_refused()
    {
        PlatformSignals.ClassifyResource(HttpStatusCode.Unauthorized, ["Bearer"], Inactive).Outcome.ShouldBe(PlatformOutcome.Refused);
        PlatformSignals.ClassifyResource(HttpStatusCode.Unauthorized, ["DPoP"], "{}").Outcome.ShouldBe(PlatformOutcome.Refused);
    }

    [Fact]
    public void Http_endpoint_is_refused_before_sending()
    {
        var client = new PlatformClient(new HttpClient(), new ProofFactory(new FakeTimeProvider()));
        var ex = Should.Throw<PlatformException>(() => client.BuildDiscovery(new Uri("http://stamp.example.test")));
        ex.Code.ShouldBe(ManagerDiagnosticCodes.InsecureEndpoint);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://stamp.example.test/x");
        Should.Throw<PlatformException>(() => client.SendAsync(request)).Code.ShouldBe(ManagerDiagnosticCodes.InsecureEndpoint);
    }
}
