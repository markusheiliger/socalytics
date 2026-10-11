using Avalonia.Controls;
using Avalonia.Layout;

namespace SocAlytics.Analysts.Manager.Views;

public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, Action confirmed)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        var ok = new Button { Name = "ConfirmButton", Content = "Confirm" };
        var cancel = new Button { Name = "CancelButton", Content = "Cancel" };
        ok.Click += (_, _) =>
        {
            confirmed();
            Close();
        };
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ok, cancel } },
            },
        };
    }
}
