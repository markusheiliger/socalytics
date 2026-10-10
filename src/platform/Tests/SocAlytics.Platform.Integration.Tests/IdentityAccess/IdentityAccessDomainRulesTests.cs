using Shouldly;
using SocAlytics.Platform.Domain.IdentityAccess;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class IdentityAccessDomainRulesTests
{
    [Theory]
    [InlineData(null, "required")]
    [InlineData("", "required")]
    [InlineData("ab", "too-short")]
    [InlineData("has space", "invalid")]
    [InlineData("umlaut-ü", "invalid")]
    [InlineData("a/b/c", "invalid")]
    public void AccountName_rejects_invalid_input(string? input, string code)
    {
        AccountName.TryCreate(input, out var name, out var error).ShouldBeFalse();
        name.ShouldBeNull();
        error.ShouldBe(code);
    }

    [Fact]
    public void AccountName_enforces_length_limits()
    {
        AccountName.TryCreate(new string('a', 3), out _, out _).ShouldBeTrue();
        AccountName.TryCreate(new string('a', 64), out _, out _).ShouldBeTrue();
        AccountName.TryCreate(new string('a', 65), out _, out var error).ShouldBeFalse();
        error.ShouldBe("too-long");
    }

    [Fact]
    public void AccountName_allows_symbols_and_normalizes_without_changing_entered_value()
    {
        AccountName.TryCreate("Jane.Doe_1-x@club", out var name, out _).ShouldBeTrue();
        name!.Value.ShouldBe("Jane.Doe_1-x@club");
        name.Normalized.ShouldBe("JANE.DOE_1-X@CLUB");
    }

    [Fact]
    public void Membership_transitions_follow_the_rules()
    {
        MembershipStatus.Active.CanDeactivate().ShouldBeTrue();
        MembershipStatus.Active.CanReactivate().ShouldBeFalse();
        MembershipStatus.Deactivated.CanReactivate().ShouldBeTrue();
        MembershipStatus.Deactivated.CanDeactivate().ShouldBeFalse();
    }

    [Fact]
    public void Wire_values_round_trip()
    {
        MembershipStatus.Active.ToWireValue().ShouldBe("active");
        MembershipStatus.Deactivated.ToWireValue().ShouldBe("deactivated");
        ClubRole.ClubAdmin.ToWireValue().ShouldBe("club-admin");
        ClubRole.Registrar.ToWireValue().ShouldBe("registrar");
        TeamRole.Coach.ToWireValue().ShouldBe("coach");
        TeamRole.Viewer.ToWireValue().ShouldBe("viewer");

        ClubRoleRules.TryParse("club-admin", out var club).ShouldBeTrue();
        club.ShouldBe(ClubRole.ClubAdmin);
        TeamRoleRules.TryParse("viewer", out var team).ShouldBeTrue();
        team.ShouldBe(TeamRole.Viewer);
        MembershipStatusRules.TryParse("active", out var status).ShouldBeTrue();
        status.ShouldBe(MembershipStatus.Active);
        ClubRoleRules.TryParse("ClubAdmin", out _).ShouldBeFalse();
    }

    [Fact]
    public void ClubAdmin_includes_Registrar_authority_but_not_the_reverse()
    {
        ClubRole.ClubAdmin.Includes(ClubRole.Registrar).ShouldBeTrue();
        ClubRole.ClubAdmin.Includes(ClubRole.ClubAdmin).ShouldBeTrue();
        ClubRole.Registrar.Includes(ClubRole.Registrar).ShouldBeTrue();
        ClubRole.Registrar.Includes(ClubRole.ClubAdmin).ShouldBeFalse();
    }
}
