using System.Net;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class HostLabelTests(PostgresContainerFixture postgres)
{
    private const string Path = "/api/v1/analyst-manager-registrations";

    private static object PairingBodyWithLabel(TestAnalystManager manager, string? hostLabel) => new
    {
        stampId = "local-dev-stamp",
        publicKey = manager.PublicJwkObject(),
        nonce = "nonce-0123456789abcdef",
        deviceMetadata = new { os = "linux", osVersion = "6.1", architecture = "x64", managerVersion = "1.0.0", claimedKeyProtection = "tpm", hostLabel },
    };

    [Fact]
    public async Task ControlCharactersInTheHostLabelAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        await using var factory = RegistryTestHost.Create(db);
        await factory.WaitUntilHealthyAsync(ct);
        using var anon = factory.CreateApiClient();

        foreach (var label in new[] { "bad\nlabel", "tab\tlabel", "nul\0label", "esc\u001Blabel", "del\u007Flabel", "c1\u0085label" })
        {
            using var manager = new TestAnalystManager();
            using var response = await RegistryTestHost.PairAsync(anon, PairingBodyWithLabel(manager, label), ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, label);
        }

        using var empty = await RegistryTestHost.PairAsync(anon, PairingBodyWithLabel(new TestAnalystManager(), ""), ct);
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var tooLong = await RegistryTestHost.PairAsync(anon, PairingBodyWithLabel(new TestAnalystManager(), new string('a', 65)), ct);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var valid = await RegistryTestHost.PairAsync(anon, PairingBodyWithLabel(new TestAnalystManager(), "Front desk PC"), ct);
        valid.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task LabelAndFingerprintReachTheRegistrarAndTheFleetList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        await using var factory = RegistryTestHost.Create(db);
        await factory.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        using var registrar = await ApiSession.SignInAsync(factory, "reg-1", TestMembers.DefaultPassword, ct);
        using var anon = factory.CreateApiClient();

        using var manager = new TestAnalystManager();
        using var pair = await RegistryTestHost.PairAsync(anon, PairingBodyWithLabel(manager, "Front desk PC"), ct);
        pair.StatusCode.ShouldBe(HttpStatusCode.Created);
        var paired = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        var fingerprint = paired.GetProperty("deviceFingerprint").GetString()!;

        using var submitted = await registrar.SendAsync(HttpMethod.Post, Path,
            new { pairingCode = paired.GetProperty("pairingCode").GetString(), deviceFingerprint = fingerprint }, ct);
        submitted.StatusCode.ShouldBe(HttpStatusCode.Created, await submitted.Content.ReadAsStringAsync(ct));
        var response = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync(ct)).RootElement;
        response.GetProperty("deviceMetadata").GetProperty("hostLabel").GetString().ShouldBe("Front desk PC");
        response.GetProperty("deviceFingerprint").GetString().ShouldBe(fingerprint);
        var id = response.GetProperty("registrationId").GetGuid();

        using var list = await admin.SendAsync(HttpMethod.Get, Path, null, ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("items")
            .EnumerateArray().Single(i => i.GetProperty("registrationId").GetGuid() == id);
        item.GetProperty("deviceMetadata").GetProperty("hostLabel").GetString().ShouldBe("Front desk PC");
        item.GetProperty("deviceFingerprint").GetString().ShouldBe(fingerprint);
    }
}
