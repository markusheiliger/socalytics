using Xunit;
using Shouldly;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class ModuleKeyTests
{
    [Fact]
    public void AdoptedModulesAreFixedAndOrdered()
    {
        ModuleKey.All.Select(m => m.Name).ShouldBe(
            ["club", "identity_access", "recordings", "registry", "analysis", "agent_orchestration"]);
        ModuleKey.Club.OwnerRole.ShouldBe("club_owner");
        ModuleKey.Club.RuntimeRole.ShouldBe("club_runtime");
        ModuleKey.Registry.Schema.ShouldBe("registry");
    }

    [Fact]
    public void ModuleKeyHasNoPublicConstructor()
    {
        typeof(ModuleKey).GetConstructors().ShouldBeEmpty();
    }
}
