using Avalonia.Controls;
using SocAlytics.Analysts.Manager.ViewModels;

namespace SocAlytics.Analysts.Manager.Views;

public partial class CustomPauseDialog : Window
{
    public CustomPauseDialog()
    {
        InitializeComponent();
    }

    public CustomPauseDialog(TrayMenuViewModel model, Action<TimeSpan> accepted)
    {
        InitializeComponent();
        PromptText.Text = $"Pause for 1 minute to {(int)model.MaxTimedPause.TotalMinutes} minutes.";
        OkButton.Click += (_, _) => Submit(model, accepted);
        CancelButton.Click += (_, _) => Close();
    }

    public void Submit(TrayMenuViewModel model, Action<TimeSpan> accepted)
    {
        if (!model.TryParseCustom(MinutesBox.Text, out var duration))
        {
            ErrorText.Text = $"Enter a number of minutes from 1 to {(int)model.MaxTimedPause.TotalMinutes}.";
            ErrorText.IsVisible = true;
            return;
        }

        accepted(duration);
        Close();
    }
}
