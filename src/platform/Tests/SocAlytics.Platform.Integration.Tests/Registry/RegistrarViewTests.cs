using System.Net;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class RegistrarViewTests(PostgresContainerFixture postgres)
{
    private const string Path = "/api/v1/analyst-manager-registrations";

    private static async Task<Guid> SubmitAsync(ApiSession member, HttpClient anon, CancellationToken ct)
    {
        using var manager = new TestAnalystManager();
        using var pair = await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(manager), ct);
        pair.StatusCode.ShouldBe(HttpStatusCode.Created);
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        using var submitted = await member.SendAsync(HttpMethod.Post, Path,
            new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
        submitted.StatusCode.ShouldBe(HttpStatusCode.Created, await submitted.Content.ReadAsStringAsync(ct));
        return JsonDocument.Parse(await submitted.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();

    [Fact]
    public async Task RegistrarsSeeOnlyTheirOwnRequestsWithoutOperationalData()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        await using var factory = RegistryTestHost.Create(db);
        await factory.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "both-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "reg-2", clubRoles: ["registrar"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "plain-1", cancellationToken: ct);
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        using var both = await ApiSession.SignInAsync(factory, "both-1", TestMembers.DefaultPassword, ct);
        using var reg1 = await ApiSession.SignInAsync(factory, "reg-1", TestMembers.DefaultPassword, ct);
        using var reg2 = await ApiSession.SignInAsync(factory, "reg-2", TestMembers.DefaultPassword, ct);
        using var plain = await ApiSession.SignInAsync(factory, "plain-1", TestMembers.DefaultPassword, ct);
        using var anon = factory.CreateApiClient();

        var first = await SubmitAsync(reg1, anon, ct);
        var second = await SubmitAsync(reg2, anon, ct);
        using var unsubmitted = await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(new TestAnalystManager()), ct);
        unsubmitted.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Scenario 1: each Registrar sees exactly their own requests, without operational data.
        using var list1 = await reg1.SendAsync(HttpMethod.Get, Path, null, ct);
        list1.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items1 = (await ReadAsync(list1, ct)).GetProperty("items").EnumerateArray().ToArray();
        items1.Select(i => i.GetProperty("registrationId").GetGuid()).ShouldBe([first]);
        items1[0].TryGetProperty("status", out _).ShouldBeFalse();
        items1[0].TryGetProperty("connectivity", out _).ShouldBeFalse();
        items1[0].GetProperty("state").GetString().ShouldBe("pending_approval");
        items1[0].GetProperty("deviceFingerprint").GetString().ShouldNotBeNullOrEmpty();
        items1[0].GetProperty("submittedAt").ValueKind.ShouldBe(JsonValueKind.String);

        using var list2 = await reg2.SendAsync(HttpMethod.Get, Path, null, ct);
        (await ReadAsync(list2, ct)).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("registrationId").GetGuid()).ShouldBe([second]);

        using var own = await reg1.SendAsync(HttpMethod.Get, $"{Path}/{first}", null, ct);
        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await ReadAsync(own, ct);
        detail.TryGetProperty("status", out _).ShouldBeFalse();
        detail.TryGetProperty("connectivity", out _).ShouldBeFalse();

        // The Registrar sees the Club Admin's decision.
        using var approved = await admin.SendAsync(HttpMethod.Post, $"{Path}/{first}/approval", null, ct);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var after = await reg1.SendAsync(HttpMethod.Get, $"{Path}/{first}", null, ct);
        var decided = await ReadAsync(after, ct);
        decided.GetProperty("state").GetString().ShouldBe("approved");
        decided.GetProperty("decidedAt").ValueKind.ShouldBe(JsonValueKind.String);

        // Scenario 2: another member's request is refused like an unknown one.
        using var foreign = await reg1.SendAsync(HttpMethod.Get, $"{Path}/{second}", null, ct);
        using var unknown = await reg1.SendAsync(HttpMethod.Get, $"{Path}/{Guid.NewGuid()}", null, ct);
        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        RegistryTestHost.WithoutCorrelation(await foreign.Content.ReadAsStringAsync(ct))
            .ShouldBe(RegistryTestHost.WithoutCorrelation(await unknown.Content.ReadAsStringAsync(ct)));

        // Scenario 3: a Club Admin who is also a Registrar sees the whole fleet as Club Admin.
        using var fleet = await both.SendAsync(HttpMethod.Get, Path, null, ct);
        fleet.StatusCode.ShouldBe(HttpStatusCode.OK);
        var all = (await ReadAsync(fleet, ct)).GetProperty("items").EnumerateArray().ToArray();
        all.Length.ShouldBeGreaterThanOrEqualTo(3);
        all.All(i => i.TryGetProperty("connectivity", out var _)).ShouldBeTrue();
        using var adminDetail = await both.SendAsync(HttpMethod.Get, $"{Path}/{second}", null, ct);
        adminDetail.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(adminDetail, ct)).TryGetProperty("connectivity", out _).ShouldBeTrue();

        // Other members are forbidden.
        using var deniedList = await plain.SendAsync(HttpMethod.Get, Path, null, ct);
        deniedList.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var deniedDetail = await plain.SendAsync(HttpMethod.Get, $"{Path}/{first}", null, ct);
        deniedDetail.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
