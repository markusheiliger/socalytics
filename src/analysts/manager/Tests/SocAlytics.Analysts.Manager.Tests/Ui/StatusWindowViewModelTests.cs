using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.ViewModels;

namespace SocAlytics.Analysts.Manager.Tests.Ui;

public class StatusWindowViewModelTests
{
    [Fact]
    public void Reflects_status_and_raises_on_change()
    {
        var control = new FakeManagerControl { Fingerprint = "fp-1" };
        var vm = new StatusWindowViewModel(control);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.DeviceFingerprint.ShouldBe("fp-1");
        vm.Stamp.ShouldBe("stamp-1");
        vm.KeyProtection.ShouldBe("tpm");
        control.State = "active";
        control.Raise();
        vm.RegistrationState.ShouldBe("active");
        raised.ShouldContain(nameof(StatusWindowViewModel.RegistrationState));
    }

    [Fact]
    public void Shows_preflight_failures_with_remediation_and_fields()
    {
        var pf = new SocAlytics.Analysts.Manager.Runtime.PreflightResult(
            DateTimeOffset.UnixEpoch, null, null, null, null, false,
            [new("gpu", false, "missing", "install driver"), new("disk", true, null, null)], []);
        var vm = new StatusWindowViewModel(new FakeManagerControl { State = "active", OperatingState = "paused", Intent = "paused", PreflightValue = pf, PauseRemaining = 600 });
        vm.Preflight.ShouldContain("gpu: missing — install driver");
        vm.Preflight.ShouldNotContain("disk");
        vm.OperatingState.ShouldBe("paused (intent: paused)");
        vm.PauseRemaining.ShouldBe("10 min");
        vm.Connectivity.ShouldBe("unknown");
        vm.Club.ShouldBe("—");
        vm.DrainDeadline.ShouldBe("—");
        vm.Commands.PauseEnabled.ShouldBeTrue();
    }
}
