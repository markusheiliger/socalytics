using System.Security.Cryptography;
using System.Text;
using Shouldly;
using SocAlytics.Platform.Domain.IdentityAccess;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Notifications;

public sealed class EmailAddressValueTests
{
    [Fact]
    public void TrimsAndNormalizesToLowerCase()
    {
        EmailAddress.TryCreate("  John.Doe@Example.COM \t", out var address, out var error).ShouldBeTrue();
        error.ShouldBeNull();
        address.Value.ShouldBe("John.Doe@Example.COM");
        address.Normalized.ShouldBe("john.doe@example.com");
    }

    [Fact]
    public void HashIsSha256OfTheNormalizedForm()
    {
        EmailAddress.TryCreate("John@Example.com", out var a, out _).ShouldBeTrue();
        EmailAddress.TryCreate("john@example.COM", out var b, out _).ShouldBeTrue();
        EmailAddress.TryCreate("other@example.com", out var c, out _).ShouldBeTrue();

        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("john@example.com")));
        a.Hash.ShouldBe(expected);
        b.Hash.ShouldBe(expected);
        c.Hash.ShouldNotBe(expected);
    }

    [Theory]
    [InlineData("john@example.com", "j***@e***.com")]
    [InlineData("Jane.Roe@Mail.Example.org", "j***@m***.org")]
    [InlineData("a@b.co", "a***@b***.co")]
    public void MasksAddress(string input, string expected)
    {
        EmailAddress.TryCreate(input, out var address, out _).ShouldBeTrue();
        address.Masked.ShouldBe(expected);
        address.ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("", "required")]
    [InlineData("   ", "required")]
    [InlineData("plainaddress", "invalid")]
    [InlineData("@example.com", "invalid")]
    [InlineData("john@", "invalid")]
    [InlineData("john@localhost", "invalid")]
    [InlineData("a@b@example.com", "invalid")]
    [InlineData("john doe@example.com", "invalid")]
    [InlineData(".john@example.com", "invalid")]
    [InlineData("john.@example.com", "invalid")]
    [InlineData("jo..hn@example.com", "invalid")]
    [InlineData("john@-example.com", "invalid")]
    [InlineData("john@example-.com", "invalid")]
    [InlineData("john@example..com", "invalid")]
    [InlineData("john@example.com.", "invalid")]
    [InlineData("john@exämple.com", "invalid")]
    [InlineData("john<script>@example.com", "invalid")]
    [InlineData("john@example.com\r\nBcc: x@example.com", "invalid")]
    public void RefusesInvalidAddresses(string? input, string expectedError)
    {
        EmailAddress.TryCreate(input, out var address, out var error).ShouldBeFalse();
        address.ShouldBeNull();
        error.ShouldBe(expectedError);
    }

    [Fact]
    public void EnforcesLengthLimits()
    {
        var local = new string('a', 64);
        var label = new string('b', 63);
        var maxAddress = $"{local}@{label}.{label}.{new string('c', 254 - 64 - 1 - 63 - 63 - 2)}";
        maxAddress.Length.ShouldBe(EmailAddress.MaxLength);
        EmailAddress.TryCreate(maxAddress, out _, out var error).ShouldBeTrue(error);

        EmailAddress.TryCreate(maxAddress + "c", out _, out error).ShouldBeFalse();
        error.ShouldBe("too-long");

        EmailAddress.TryCreate(new string('a', 65) + "@example.com", out _, out error).ShouldBeFalse();
        error.ShouldBe("invalid");

        EmailAddress.TryCreate($"john@{new string('d', 64)}.com", out _, out error).ShouldBeFalse();
        error.ShouldBe("invalid");
    }
}
