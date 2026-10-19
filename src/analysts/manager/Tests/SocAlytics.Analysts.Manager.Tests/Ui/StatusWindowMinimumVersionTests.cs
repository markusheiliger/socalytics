using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Shouldly;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Tray;
using SocAlytics.Analysts.Manager.ViewModels;
using SocAlytics.Analysts.Manager.Views;

namespace SocAlytics.Analysts.Manager.Tests.Ui;

public class StatusWindowMinimumVersionTests
{
    private const string Sentence = "Update required: Manager 0.1.0 is below the stamp minimum 99.0.0.";

    [AvaloniaFact]
    public void Status_window_shows_the_sentence_with_its_accessible_name_only_while_required()
    {
        var control = new UpdateControl();
        var window = new StatusWindow { DataContext = new StatusWindowViewModel(control) };
        window.Show();
        var text = window.FindControl<TextBlock>("UpdateText")!;
        text.IsVisible.ShouldBeFalse();

        control.Update = new UpdateRequiredStatus(true, "0.1.0", "99.0.0");
        control.Raise();
        text.IsVisible.ShouldBeTrue();
        text.Text.ShouldBe(Sentence);
        AutomationProperties.GetName(text).ShouldBe(Sentence);

        control.Update = null;
        control.Raise();
        text.IsVisible.ShouldBeFalse();
        text.Text.ShouldBeNullOrEmpty();
    }

    [AvaloniaFact]
    public void Status_window_hides_the_notice_when_the_report_says_not_required()
    {
        var control = new UpdateControl { Update = new UpdateRequiredStatus(false, "0.1.0", "0.1.0") };
        var window = new StatusWindow { DataContext = new StatusWindowViewModel(control) };
        window.Show();
        window.FindControl<TextBlock>("UpdateText")!.IsVisible.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void Tray_menu_shows_a_disabled_line_only_while_required()
    {
        var control = new UpdateControl();
        var model = new TrayMenuViewModel(control);
        var menu = TrayMenuBuilder.Build(model, () => { }, () => { }, () => { }, () => { }, () => { });
        var line = (NativeMenuItem)menu.Items[1];
        line.IsVisible.ShouldBeFalse();

        control.Update = new UpdateRequiredStatus(true, "0.1.0", "99.0.0");
        control.Raise();
        line.IsVisible.ShouldBeTrue();
        line.IsEnabled.ShouldBeFalse();
        line.Header.ShouldBe("Update required (minimum 99.0.0)");

        control.Update = null;
        control.Raise();
        line.IsVisible.ShouldBeFalse();
    }

    private sealed class UpdateControl : IManagerControl
    {
        private readonly FakeManagerControl _inner = new() { State = "active" };

        public UpdateRequiredStatus? Update { get; set; }

        public event EventHandler? StatusChanged
        {
            add => _inner.StatusChanged += value;
            remove => _inner.StatusChanged -= value;
        }

        public event EventHandler? ExitCompleted
        {
            add => _inner.ExitCompleted += value;
            remove => _inner.ExitCompleted -= value;
        }

        public event EventHandler? ShowStatusRequested
        {
            add => _inner.ShowStatusRequested += value;
            remove => _inner.ShowStatusRequested -= value;
        }

        public void Raise() => _inner.Raise();

        public void RequestShowStatus() => _inner.RequestShowStatus();

        public ManagerStatusSnapshot GetStatus() => _inner.GetStatus() with { UpdateRequired = Update };

        public Task<ControlResult> PauseAsync() => _inner.PauseAsync();

        public Task<ControlResult> PauseAsync(TimeSpan duration) => _inner.PauseAsync(duration);

        public Task<ControlResult> PauseUntilAsync(DateTimeOffset endUtc) => _inner.PauseUntilAsync(endUtc);

        public Task<ControlResult> ResumeAsync() => _inner.ResumeAsync();

        public Task<ControlResult> ExitAsync() => _inner.ExitAsync();

        public Task<ControlResult> SessionEndingAsync() => _inner.SessionEndingAsync();

        public SocAlytics.Analysts.Manager.Work.ConcurrencySnapshot GetConcurrency() => _inner.GetConcurrency();

        public Task<ControlResult> SetHostLocalConcurrencyAsync(int limit) => _inner.SetHostLocalConcurrencyAsync(limit);

        public Task<ControlResult> SetAutostartAsync(bool enabled) => _inner.SetAutostartAsync(enabled);

        public Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) =>
            _inner.RegisterAsync(request, cancellationToken);

        public Task CancelRegistrationAsync() => _inner.CancelRegistrationAsync();

        public Task<ControlResult> UnregisterAsync() => _inner.UnregisterAsync();

        public Task<ControlResult> DiscardLocalRegistrationAsync() => _inner.DiscardLocalRegistrationAsync();
    }
}
