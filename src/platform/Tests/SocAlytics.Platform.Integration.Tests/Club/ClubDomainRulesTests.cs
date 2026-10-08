using Shouldly;
using SocAlytics.Platform.Domain.Club;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Club;

public sealed class ClubDomainRulesTests
{
    [Theory]
    [InlineData(0, false, "required")]
    [InlineData(1, true, null)]
    [InlineData(100, true, null)]
    [InlineData(101, false, "too-long")]
    public void DisplayName_enforces_limits(int length, bool valid, string? code)
    {
        var ok = DisplayName.TryCreate(new string('a', length), out var name, out var error);

        ok.ShouldBe(valid);
        error.ShouldBe(code);
        if (valid)
        {
            name.Value.Length.ShouldBe(length);
        }
    }

    [Theory]
    [InlineData("   ")]
    [InlineData(null)]
    public void DisplayName_rejects_blank(string? input)
    {
        DisplayName.TryCreate(input, out _, out var error).ShouldBeFalse();
        error.ShouldBe("required");
    }

    [Fact]
    public void DisplayName_is_stored_trimmed_and_limit_applies_after_trimming()
    {
        DisplayName.TryCreate("  Reds  ", out var name, out _).ShouldBeTrue();
        name.Value.ShouldBe("Reds");

        DisplayName.TryCreate("  " + new string('a', 100) + "  ", out _, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData(SeasonState.Draft, true, false)]
    [InlineData(SeasonState.Active, false, true)]
    [InlineData(SeasonState.Archived, false, false)]
    public void Season_transitions_follow_the_state_machine(SeasonState state, bool canActivate, bool canArchive)
    {
        DisplayName.TryCreate("2026", out var name, out _);
        var season = new Season(Guid.NewGuid(), name, state, DateTimeOffset.UnixEpoch, null, null, 1);

        season.CanActivate.ShouldBe(canActivate);
        season.CanArchive.ShouldBe(canArchive);
    }

    [Fact]
    public void Wire_values_round_trip()
    {
        foreach (var state in Enum.GetValues<SeasonState>())
        {
            SeasonStateWire.TryParse(state.ToWire(), out var parsed).ShouldBeTrue();
            parsed.ShouldBe(state);
        }

        SeasonState.Draft.ToWire().ShouldBe("draft");
        SeasonState.Active.ToWire().ShouldBe("active");
        SeasonState.Archived.ToWire().ShouldBe("archived");

        foreach (var value in Enum.GetValues<HomeAway>())
        {
            HomeAwayWire.TryParse(value.ToWire(), out var parsed).ShouldBeTrue();
            parsed.ShouldBe(value);
        }

        HomeAway.Home.ToWire().ShouldBe("home");
        HomeAway.Away.ToWire().ShouldBe("away");
        HomeAway.Neutral.ToWire().ShouldBe("neutral");
        SeasonStateWire.TryParse("Draft", out _).ShouldBeFalse();
        HomeAwayWire.TryParse(null, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void Competition_limits(int length, bool valid)
    {
        var competition = length == 0 ? null : new string('a', length);

        var ok = MatchDetails.TryCreate(DateTimeOffset.UnixEpoch, HomeAway.Home, competition, out var details, out var field, out var error);

        ok.ShouldBe(valid);
        if (valid)
        {
            details!.Competition.ShouldBe(competition);
        }
        else
        {
            field.ShouldBe("competition");
            error.ShouldBe("too-long");
        }
    }

    [Fact]
    public void Blank_competition_is_rejected()
    {
        MatchDetails.TryCreate(DateTimeOffset.UnixEpoch, HomeAway.Home, "  ", out _, out _, out var error).ShouldBeFalse();
        error.ShouldBe("required");
    }

    [Fact]
    public void Kickoff_is_required_and_must_be_utc()
    {
        MatchDetails.TryCreate(null, HomeAway.Home, null, out _, out _, out var error).ShouldBeFalse();
        error.ShouldBe("required");

        MatchDetails.TryCreate(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(2)), HomeAway.Home, null, out _, out _, out error).ShouldBeFalse();
        error.ShouldBe("invalid");
    }
}
