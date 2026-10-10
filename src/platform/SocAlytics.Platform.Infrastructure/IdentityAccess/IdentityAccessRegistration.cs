using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Infrastructure.IdentityAccess;

internal static class IdentityAccessRegistration
{
    public static IServiceCollection AddIdentityAccess(this IServiceCollection services)
    {
        services.AddIdentityCore<IdentityMemberAccount>()
            .AddUserStore<DapperMemberAccountStore>()
            .AddTokenProvider<OneTimeCredentialTokenProvider>(OneTimeCredentialTokenProvider.SetPasswordPurpose)
            .AddTokenProvider<OneTimeCredentialTokenProvider>(OneTimeCredentialTokenProvider.PasswordResetPurpose);
        services.AddSingleton<IConfigureOptions<IdentityOptions>, ConfigureIdentityOptions>();
        return services;
    }

    private sealed class ConfigureIdentityOptions(IOptions<IdentityAccessOptions> accessOptions) : IConfigureOptions<IdentityOptions>
    {
        public void Configure(IdentityOptions options)
        {
            var access = accessOptions.Value;
            options.Password.RequiredLength = access.Password.RequiredLength;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredUniqueChars = 1;
            options.Lockout.MaxFailedAccessAttempts = access.Lockout.MaxFailedAccessAttempts;
            options.Lockout.DefaultLockoutTimeSpan = access.Lockout.LockoutDuration;
            options.Lockout.AllowedForNewUsers = true;
            options.User.AllowedUserNameCharacters =
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-@";
        }
    }
}
