using Avalonia.Controls;
using SocAlytics.Analysts.Manager.ViewModels;

namespace SocAlytics.Analysts.Manager.Views;

public partial class RegisterWindow : Window
{
    public RegisterWindow()
    {
        InitializeComponent();
        RegisterButton.Click += async (_, _) =>
        {
            if (DataContext is RegisterViewModel vm)
            {
                await vm.RegisterAsync();
            }
        };
        CancelButton.Click += async (_, _) =>
        {
            if (DataContext is RegisterViewModel vm)
            {
                await vm.CancelAsync();
            }
        };
    }
}
