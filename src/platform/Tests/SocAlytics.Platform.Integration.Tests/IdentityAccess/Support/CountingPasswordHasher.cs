using Microsoft.AspNetCore.Identity;
using SocAlytics.Platform.Infrastructure.IdentityAccess;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

internal sealed class CountingPasswordHasher : IPasswordHasher<IdentityMemberAccount>
{
    private readonly PasswordHasher<IdentityMemberAccount> _inner = new();
    private int _verifications;

    public int Verifications => Volatile.Read(ref _verifications);

    public string HashPassword(IdentityMemberAccount user, string password) => _inner.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(IdentityMemberAccount user, string hashedPassword, string providedPassword)
    {
        Interlocked.Increment(ref _verifications);
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}
