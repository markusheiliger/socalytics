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
}
