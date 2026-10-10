using Avalonia.Controls;

namespace SocAlytics.Analysts.Manager.Views;

public partial class StatusWindow : Window
{
    public StatusWindow()
    {
        InitializeComponent();
        RegisterButton.Click += (_, _) => RegisterRequested?.Invoke();
    }

    public Action? RegisterRequested { get; set; }
}
