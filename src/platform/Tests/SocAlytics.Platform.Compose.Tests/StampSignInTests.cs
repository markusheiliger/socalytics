using System.Net;
using Shouldly;
using SocAlytics.Platform.Compose.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.Compose.Tests;

public sealed class StampSignInTests
{
    private static readonly TimeSpan ReachableWithin = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Origin_serves_the_shell_and_the_API_and_the_bootstrap_Club_Admin_signs_in_and_changes_the_initial_password()
    {
        var ct = TestContext.Current.CancellationToken;
        var stamp = new StampFixture();
        await stamp.InitializeAsync();
        try
        {
            await stamp.UpAsync();
            using var origin = new OriginClient(stamp.WebOrigin);
            await WaitForApiAsync(origin, ct);

            using var shell = await origin.SendAsync(HttpMethod.Get, "/", null, ct);
            shell.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await shell.Content.ReadAsStringAsync(ct)).ShouldContain("<html", Case.Insensitive);

            using var anonymous = await origin.SendAsync(HttpMethod.Get, "/api/v1/session", null, ct);
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            anonymous.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

            var accountName = stamp.Get("SOCALYTICS_FIRST_CLUB_ADMIN_ACCOUNT_NAME");
            var initialPassword = stamp.Get("SOCALYTICS_FIRST_CLUB_ADMIN_INITIAL_PASSWORD");
            accountName.ShouldNotBeNullOrWhiteSpace();
            initialPassword.ShouldNotBeNullOrWhiteSpace();

            var first = await origin.SignInAsync(accountName, initialPassword, ct);
            origin.HasSession.ShouldBeTrue();
            first.GetProperty("passwordChangeRequired").GetBoolean().ShouldBeTrue();

            var newPassword = "changed-" + Guid.NewGuid().ToString("N");
            using var changed = await origin.ChangeOwnPasswordAsync(initialPassword, newPassword, ct);
            changed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await changed.Content.ReadAsStringAsync(ct));

            var second = await origin.SignInAsync(accountName, newPassword, ct);
            second.GetProperty("passwordChangeRequired").GetBoolean().ShouldBeFalse();

            var profile = await origin.GetOwnProfileAsync(ct);
            profile.GetProperty("accountName").GetString().ShouldBe(accountName);
            profile.GetProperty("clubRoles").EnumerateArray().Select(role => role.GetString()).ShouldContain("club-admin");

            // The initial credential no longer signs in.
            await Should.ThrowAsync<InvalidOperationException>(() => origin.SignInAsync(accountName, initialPassword, ct));
        }
        finally
        {
            await stamp.DisposeAsync();
        }
    }

    private static async Task WaitForApiAsync(OriginClient origin, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReachableWithin;
        var last = "no response";
        do
        {
            try
            {
                using var response = await origin.SendAsync(HttpMethod.Get, "/alive", null, ct);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }

                last = $"HTTP {(int)response.StatusCode}";
            }
            catch (HttpRequestException exception)
            {
                last = exception.Message;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                last = "request timed out";
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Timed out after {ReachableWithin} waiting for /alive through the origin ({last}).");
    }
}
