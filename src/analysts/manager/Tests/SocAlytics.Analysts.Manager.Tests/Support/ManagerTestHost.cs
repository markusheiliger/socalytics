using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Hosting;

namespace SocAlytics.Analysts.Manager.Tests.Support;

internal sealed class ManagerTestHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), "socalytics-manager-test-" + Guid.NewGuid().ToString("N"));

    public ManagerTestHost(KeyProtection protection = KeyProtection.Tpm, bool providerAvailable = true,
        Action<IServiceCollection>? configure = null)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        Platform = new FakePlatform(Time);
        Keys = new InMemoryDeviceKeyProvider(protection, providerAvailable);
        var builder = ManagerHost.CreateBuilder();
        ((Microsoft.Extensions.Configuration.IConfigurationBuilder)builder.Configuration).AddInMemory(_stateDirectory);
        builder.Services.RemoveAll<IDeviceKeyProvider>();
        builder.Services.AddSingleton<IDeviceKeyProvider>(Keys);
        builder.Services.RemoveAll<TimeProvider>();
        builder.Services.AddSingleton<TimeProvider>(Time);
        builder.Services.AddHttpClient<SocAlytics.Analysts.Manager.Platform.PlatformClient>().ConfigurePrimaryHttpMessageHandler(() => Platform);
        configure?.Invoke(builder.Services);
        builder.Services.AddFakeLogging();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        _host = builder.Build();
    }

    public string StateDirectory => _stateDirectory;

    public FakeTimeProvider Time { get; }

    public FakePlatform Platform { get; }

    public InMemoryDeviceKeyProvider Keys { get; }

    public IServiceProvider Services => _host.Services;

    public IManagerControl Control => Services.GetRequiredService<IManagerControl>();

    public FakeLogCollector Logs => Services.GetRequiredService<FakeLogCollector>();

    public ValueTask DisposeAsync()
    {
        _host.Dispose();
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, true);
        }

        return ValueTask.CompletedTask;
    }
}

internal static class TestConfiguration
{
    public static void AddInMemory(this Microsoft.Extensions.Configuration.IConfigurationBuilder configuration, string stateDirectory) =>
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Manager:StateDirectory"] = stateDirectory });
}
