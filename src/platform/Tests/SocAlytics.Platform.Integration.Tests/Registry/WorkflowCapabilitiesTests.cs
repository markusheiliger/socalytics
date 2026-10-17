using System.Net;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class WorkflowCapabilitiesTests(PostgresContainerFixture postgres)
{
    private const string Path = "/api/v1/analyst-managers/self/workflow-capabilities";

    [Fact]
    public async Task Returns_the_requirements_of_the_approved_profiles_without_digests()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await ActiveManagerHarness.StartAsync(postgres, new Dictionary<string, string?>(), ct);

        using var response = await s.SendAsync(HttpMethod.Get, Path, ct);

        var text = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        text.ShouldNotContain("sha");
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("workflow").GetProperty("id").GetString().ShouldBe("development-default");
        var capabilities = body.GetProperty("capabilities").EnumerateArray().ToList();
        capabilities.Select(c => c.GetProperty("id").GetString()).ShouldBe(["dev-segment-capability", "dev-match-capability"], ignoreOrder: true);
        foreach (var capability in capabilities)
        {
            capability.GetProperty("cpus").GetString().ShouldBe("1");
            capability.GetProperty("memoryBytes").GetInt64().ShouldBe(1024L * 1024 * 1024);
            capability.GetProperty("accelerators").GetArrayLength().ShouldBe(0);
        }
    }

    [Fact]
    public async Task Refuses_revoked_managers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await ActiveManagerHarness.StartAsync(postgres, new Dictionary<string, string?>(), ct);
        await s.RevokeAsync(ct);

        using var response = await s.SendAsync(HttpMethod.Get, Path, ct);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
