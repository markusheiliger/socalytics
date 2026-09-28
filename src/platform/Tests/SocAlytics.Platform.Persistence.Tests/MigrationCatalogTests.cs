using Shouldly;
using SocAlytics.Platform.Persistence;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class MigrationCatalogTests
{
    [Fact]
    public void MigrationsAreOrderedByAdoptedModuleOrderThenSequence()
    {
        var catalog = MigrationCatalog.Create(
        [
            new StubContributor(ModuleKey.AgentOrchestration, Descriptor(ModuleKey.AgentOrchestration, 1, "ao_0001")),
            new StubContributor(
                ModuleKey.Club,
                Descriptor(ModuleKey.Club, 2, "club_0002"),
                Descriptor(ModuleKey.Club, 1, "club_0001")),
            new StubContributor(ModuleKey.IdentityAccess, Descriptor(ModuleKey.IdentityAccess, 1, "ia_0001"))
        ]);

        catalog.Migrations.Select(descriptor => descriptor.ScriptIdentity).ShouldBe(
        [
            "club_0001",
            "club_0002",
            "ia_0001",
            "ao_0001"
        ]);
    }

    [Fact]
    public void DuplicateScriptIdentityWithinAModuleIsRejected()
    {
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create(
        [
            new StubContributor(
                ModuleKey.Club,
                Descriptor(ModuleKey.Club, 1, "duplicate"),
                Descriptor(ModuleKey.Club, 2, "duplicate"))
        ]));
    }

    [Fact]
    public void DuplicateSequenceWithinAModuleIsRejected()
    {
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create(
        [
            new StubContributor(
                ModuleKey.Club,
                Descriptor(ModuleKey.Club, 1, "first"),
                Descriptor(ModuleKey.Club, 1, "second"))
        ]));
    }

    [Fact]
    public void SameSequenceAndIdentityAreAllowedAcrossDifferentModules()
    {
        var catalog = MigrationCatalog.Create(
        [
            new StubContributor(ModuleKey.Club, Descriptor(ModuleKey.Club, 1, "0001_initial")),
            new StubContributor(ModuleKey.IdentityAccess, Descriptor(ModuleKey.IdentityAccess, 1, "0001_initial"))
        ]);

        catalog.Migrations.Count.ShouldBe(2);
    }

    [Fact]
    public void ContributorCannotRegisterAMigrationForAnotherModule()
    {
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create(
        [
            new StubContributor(ModuleKey.Club, Descriptor(ModuleKey.IdentityAccess, 1, "0001_initial"))
        ]));
    }

    private static MigrationDescriptor Descriptor(ModuleKey moduleKey, int sequence, string scriptIdentity) =>
        new(moduleKey, sequence, scriptIdentity, [1, 2, 3]);

    private sealed class StubContributor(ModuleKey moduleKey, params MigrationDescriptor[] migrations) : IModuleMigrationContributor
    {
        public ModuleKey ModuleKey { get; } = moduleKey;

        public IReadOnlyList<MigrationDescriptor> GetMigrations() => migrations;
    }
}
