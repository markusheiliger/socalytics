using System.Runtime.CompilerServices;

namespace SocAlytics.Platform.Host.Tests;

// Host tests that start the API without an explicit configuration need the required email and notification settings.
internal static class EmailTestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Set("Email__Smtp__Host", "127.0.0.1");
        Set("Email__Smtp__Port", "1");
        Set("Email__Smtp__Security", "StartTlsWhenAvailable");
        Set("Email__SenderAddress", "socalytics@localhost");
        Set("Email__SenderName", "SocAlytics");
        Set("Notifications__WebUiBaseUri", "http://localhost:5173");
    }

    private static void Set(string name, string value)
    {
        if (Environment.GetEnvironmentVariable(name) is null)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
