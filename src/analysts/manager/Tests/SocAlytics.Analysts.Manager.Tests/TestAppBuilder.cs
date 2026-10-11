using Avalonia;
using Avalonia.Headless;
using SocAlytics.Analysts.Manager;
using SocAlytics.Analysts.Manager.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace SocAlytics.Analysts.Manager.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
