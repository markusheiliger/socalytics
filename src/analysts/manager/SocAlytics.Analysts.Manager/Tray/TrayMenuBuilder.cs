using Avalonia.Controls;
using SocAlytics.Analysts.Manager.ViewModels;

namespace SocAlytics.Analysts.Manager.Tray;

public static class TrayMenuBuilder
{
    public static NativeMenu Build(
        TrayMenuViewModel model, Action showStatus, Action showRegister, Action showCustomPause, Action confirmUnregister, Action confirmDiscard)
    {
        var status = Item("Status", showStatus);
        var register = Item("Register…", showRegister);
        var pause = Item("Pause", () => _ = model.PauseAsync());
        var pauseMenu = new NativeMenu();
        foreach (var (label, duration) in TrayMenuViewModel.Presets)
        {
            var d = duration;
            pauseMenu.Items.Add(Item(label, () => _ = model.PauseForAsync(d)));
        }

        pauseMenu.Items.Add(Item("Custom…", showCustomPause));
        var pauseFor = new NativeMenuItem("Pause for") { Menu = pauseMenu };
        var resume = Item("Resume", () => _ = model.ResumeAsync());
        var exit = Item("Safe exit", () => _ = model.SafeExitAsync());
        var unregister = Item("Unregister…", confirmUnregister);
        var discard = Item("Discard local registration…", confirmDiscard);
        var autostart = new NativeMenuItem("Start at sign-in") { ToggleType = Avalonia.Controls.MenuItemToggleType.CheckBox };
        autostart.Click += (_, _) => _ = model.ToggleAutostartAsync();

        void Sync()
        {
            register.IsEnabled = model.RegisterEnabled;
            pause.IsEnabled = model.PauseEnabled;
            pauseFor.IsEnabled = model.PauseEnabled;
            resume.IsEnabled = model.ResumeEnabled;
            resume.Header = model.ResumeText;
            unregister.IsEnabled = model.UnregisterEnabled;
            discard.IsEnabled = model.DiscardEnabled;
            autostart.IsChecked = model.AutostartChecked;
        }

        Sync();
        model.PropertyChanged += (_, _) => Sync();
        return [status, register, new NativeMenuItemSeparator(), pause, pauseFor, resume, new NativeMenuItemSeparator(), exit, unregister, discard, autostart];
    }

    private static NativeMenuItem Item(string header, Action click)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => click();
        return item;
    }
}
