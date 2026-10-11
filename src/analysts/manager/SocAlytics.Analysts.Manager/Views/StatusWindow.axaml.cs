using Avalonia.Controls;
using SocAlytics.Analysts.Manager.ViewModels;

namespace SocAlytics.Analysts.Manager.Views;

public partial class StatusWindow : Window
{
    public StatusWindow()
    {
        InitializeComponent();
        RegisterButton.Click += (_, _) => RegisterRequested?.Invoke();
        PauseButton.Click += (_, _) => _ = Commands?.PauseAsync();
        PauseForButton.Click += (_, _) => CustomPauseRequested?.Invoke();
        ResumeButton.Click += (_, _) => _ = Commands?.ResumeAsync();
        ExitButton.Click += (_, _) => _ = Commands?.SafeExitAsync();
        UnregisterButton.Click += (_, _) => UnregisterRequested?.Invoke();
        DiscardButton.Click += (_, _) => DiscardRequested?.Invoke();
        AutostartBox.Click += (_, _) => _ = Commands?.ToggleAutostartAsync();
    }

    public Action? RegisterRequested { get; set; }

    public Action? CustomPauseRequested { get; set; }

    public Action? UnregisterRequested { get; set; }

    public Action? DiscardRequested { get; set; }

    private TrayMenuViewModel? Commands => (DataContext as StatusWindowViewModel)?.Commands;
}
