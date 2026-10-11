using Shouldly;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.ViewModels;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Ui;

public class TrayMenuViewModelTests
{
    [Theory]
    [InlineData("unregistered", null, null, true, false, false, false, false)]
    [InlineData("active", "running", "running", false, true, false, true, false)]
    [InlineData("active", "paused", "paused", false, true, true, true, false)]
    [InlineData("active", "draining", "paused-until", false, false, true, true, false)]
    [InlineData("restore-failed", null, null, false, false, false, false, true)]
    public void Enabled_states(string reg, string? op, string? intent, bool register, bool pause, bool resume, bool unreg, bool discard)
    {
        var vm = new TrayMenuViewModel(new FakeManagerControl { State = reg, OperatingState = op, Intent = intent });
        vm.RegisterEnabled.ShouldBe(register);
        vm.PauseEnabled.ShouldBe(pause);
        vm.ResumeEnabled.ShouldBe(resume);
        vm.UnregisterEnabled.ShouldBe(unreg);
        vm.DiscardEnabled.ShouldBe(discard);
    }

    [Fact]
    public async Task Commands_call_control_and_presets_pass_durations()
    {
        var c = new FakeManagerControl { Autostart = false };
        var vm = new TrayMenuViewModel(c);
        await vm.PauseAsync();
        foreach (var (_, d) in TrayMenuViewModel.Presets)
        {
            await vm.PauseForAsync(d);
        }

        await vm.ResumeAsync();
        await vm.SafeExitAsync();
        await vm.UnregisterAsync();
        await vm.DiscardLocalAsync();
        await vm.ToggleAutostartAsync();
        c.Calls.ShouldBe(["pause", "pause:30", "pause:60", "pause:120", "pause:240", "pause:480", "resume", "exit", "unregister", "discard", "autostart:True"]);
    }

    [Fact]
    public void Custom_bounds_and_remaining()
    {
        var vm = new TrayMenuViewModel(new FakeManagerControl { PauseRemaining = 5400 }) { MaxTimedPause = TimeSpan.FromHours(2) };
        vm.TryParseCustom("0", out _).ShouldBeFalse();
        vm.TryParseCustom("121", out _).ShouldBeFalse();
        vm.TryParseCustom("x", out _).ShouldBeFalse();
        vm.TryParseCustom("90", out var d).ShouldBeTrue();
        d.ShouldBe(TimeSpan.FromMinutes(90));
        vm.PauseRemaining.ShouldBe("1 h 30 min");
    }
}
