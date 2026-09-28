using Shouldly;
using SocAlytics.Platform.Persistence;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class ModuleKeyTests
{
    [Fact]
    public void ModuleKeysAreTheSixFixedAdoptedModules()
    {
        Enum.GetValues<ModuleKey>().ShouldBe(
        [
            ModuleKey.Club,
            ModuleKey.IdentityAccess,
            ModuleKey.Recordings,
            ModuleKey.Registry,
            ModuleKey.Analysis,
            ModuleKey.AgentOrchestration
        ]);
    }

    [Theory]
    [InlineData(ModuleKey.Club, "club")]
    [InlineData(ModuleKey.IdentityAccess, "identity_access")]
    [InlineData(ModuleKey.Recordings, "recordings")]
    [InlineData(ModuleKey.Registry, "registry")]
    [InlineData(ModuleKey.Analysis, "analysis")]
    [InlineData(ModuleKey.AgentOrchestration, "agent_orchestration")]
    public void EachModuleKeyMapsToItsAdoptedSchemaName(ModuleKey moduleKey, string expectedSchemaName)
    {
        moduleKey.ToSchemaName().ShouldBe(expectedSchemaName);
    }

    [Fact]
    public void SchemaNamesAreUniqueAcrossAllModuleKeys()
    {
        var moduleKeys = Enum.GetValues<ModuleKey>();
        var schemaNames = moduleKeys.Select(moduleKey => moduleKey.ToSchemaName());

        schemaNames.Distinct(StringComparer.Ordinal).Count().ShouldBe(moduleKeys.Length);
    }

    [Fact]
    public void UnknownModuleKeyValueIsRejected()
    {
        var unknownModuleKey = (ModuleKey)(-1);

        Should.Throw<ArgumentOutOfRangeException>(() => unknownModuleKey.ToSchemaName());
    }
}
