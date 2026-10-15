using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SocAlytics.Analysts.Manager.Hosting;

public static class ManagerHost
{
    public static HostApplicationBuilder CreateBuilder(string[]? args = null, ApplicationProfile profile = ApplicationProfile.Tray)
    {
        var builder = Host.CreateApplicationBuilder(args ?? []);
        builder.Services.AddSingleton(typeof(ApplicationProfile), profile);
        builder.Services.AddManagerCore(builder.Configuration);
        return builder;
    }
}
