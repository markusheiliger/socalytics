using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Shouldly;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Tray;
using SocAlytics.Analysts.Manager.ViewModels;
using SocAlytics.Analysts.Manager.Views;

namespace SocAlytics.Analysts.Manager.Tests.Ui;

public class HeadlessWindowTests
{
    [AvaloniaFact]
    public async Task Register_window_renders_code_and_fingerprint()
    {
        var vm = new RegisterViewModel(new FakeManagerControl());
        var window = new RegisterWindow { DataContext = vm };
        window.Show();
        await vm.RegisterAsync();
        window.FindControl<TextBlock>("CodeText")!.Text.ShouldBe("ABCD-1234");
        window.FindControl<TextBlock>("FingerprintText")!.Text.ShouldBe("fp-xyz");
    }

    [AvaloniaFact]
    public void Status_window_renders_fields()
    {
        var window = new StatusWindow { DataContext = new StatusWindowViewModel(new FakeManagerControl { Fingerprint = "fp-1" }) };
        window.Show();
        window.FindControl<TextBlock>("StateText")!.Text.ShouldBe("unregistered");
        window.FindControl<TextBlock>("FingerprintText")!.Text.ShouldBe("fp-1");
        window.FindControl<TextBlock>("StampText")!.Text.ShouldBe("stamp-1");
    }

    [AvaloniaFact]
    public void Status_window_opens_at_start_without_tray_host()
    {
        var app = (App)Avalonia.Application.Current!;
        app.Control = new FakeManagerControl();
        app.Detector = new TrayHostDetector(() => false);
        app.ShowStatusIfNoTray();
        app.StatusWindowOpen.ShouldBe(!app.Detector.IsTrayAvailable());
    }
}
