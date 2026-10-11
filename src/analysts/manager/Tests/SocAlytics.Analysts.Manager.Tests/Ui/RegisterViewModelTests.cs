using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.ViewModels;

namespace SocAlytics.Analysts.Manager.Tests.Ui;

public class RegisterViewModelTests
{
    [Fact]
    public async Task Shows_code_and_fingerprint_together()
    {
        var vm = new RegisterViewModel(new FakeManagerControl());
        await vm.RegisterAsync();
        vm.PairingCode.ShouldBe("ABCD-1234");
        vm.DeviceFingerprint.ShouldBe("fp-xyz");
        vm.VerificationUri.ShouldNotBeNull();
        vm.InstructionText.ShouldBe("Give both to your Registrar in person");
    }

    [Fact]
    public void Register_enabled_only_when_unregistered()
    {
        var control = new FakeManagerControl();
        var vm = new RegisterViewModel(control);
        vm.CanRegister.ShouldBeTrue();
        control.State = "active";
        vm.CanRegister.ShouldBeFalse();
        new TrayMenuViewModel(control).RegisterEnabled.ShouldBeFalse();
    }
}
