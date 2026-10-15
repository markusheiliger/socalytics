using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Notifications.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Notifications;

[Collection(MailpitCollection.Name)]
public sealed class EmailAddressTests(PostgresContainerFixture postgres, MailpitContainerFixture mailpit)
{
	[Fact]
	// US1 scenario 1
	public async Task SettingAnAddressQueuesExactlyOneConfirmation()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		using var none = await h.Admin.SendAsync(HttpMethod.Get, "/api/v1/me/email", null, ct);
		none.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		var address = EmailApiHarness.NewAddress();
		await h.SetOkAsync(address, null, ct);

		var own = await h.GetOwnAsync(ct);
		own.GetProperty("address").GetString().ShouldBe(address);
		own.GetProperty("state").GetString().ShouldBe("unverified");
		await h.TokenFromMailAsync(address, 1, ct);
		(await h.MailCountAsync(address, ct)).ShouldBe(1);
	}

	[Theory]
	[InlineData("")]
	[InlineData("not-an-address")]
	[InlineData("a@b")]
	[InlineData("two@@example.test")]
	public async Task InvalidAddressesAreRefused(string address)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		using var response = await h.SetAsync(address, null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await response.Content.ReadAsStringAsync(ct)).ShouldContain("email-address-invalid");
		using var none = await h.Admin.SendAsync(HttpMethod.Get, "/api/v1/me/email", null, ct);
		none.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	[Fact]
	// US1 scenario 5
	public async Task ChangingAnAddressResetsVerificationAndStopsMessagesToTheOldAddress()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var oldAddress = EmailApiHarness.NewAddress();
		var version = await h.SetOkAsync(oldAddress, null, ct);
		var oldToken = await h.TokenFromMailAsync(oldAddress, 1, ct);
		(await h.ConfirmAsync(oldToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
		var verified = await h.GetOwnAsync(ct);
		verified.GetProperty("state").GetString().ShouldBe("verified");
		version = verified.GetProperty("version").GetInt64();

		var newAddress = EmailApiHarness.NewAddress();
		await h.SetOkAsync(newAddress, version, ct);

		(await h.GetOwnAsync(ct)).GetProperty("state").GetString().ShouldBe("unverified");
		await h.TokenFromMailAsync(newAddress, 1, ct);
		(await h.MailCountAsync(oldAddress, ct)).ShouldBe(1);
	}

	[Fact]
	public async Task ChangingRequiresTheCurrentVersion()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var version = await h.SetOkAsync(EmailApiHarness.NewAddress(), null, ct);

		using var missing = await h.SetAsync(EmailApiHarness.NewAddress(), null, ct);
		missing.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
		using var stale = await h.SetAsync(EmailApiHarness.NewAddress(), version + 100, ct);
		stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
	}

	[Fact]
	// US1 scenario 6
	public async Task RemovingAnAddressSendsNothingMore()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var address = EmailApiHarness.NewAddress();
		var version = await h.SetOkAsync(address, null, ct);
		var token = await h.TokenFromMailAsync(address, 1, ct);

		using var stale = await h.RemoveAsync(version + 100, ct);
		stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
		using var removed = await h.RemoveAsync(version, ct);
		removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		using var none = await h.Admin.SendAsync(HttpMethod.Get, "/api/v1/me/email", null, ct);
		none.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		using var resend = await h.ResendAsync(ct);
		resend.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		(await h.ConfirmAsync(token, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await h.MailCountAsync(address, ct)).ShouldBe(1);
	}

	[Fact]
	// FR-005
	public async Task ResendIsLimitedPerHourWithRetryAfter()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(
			postgres, mailpit, ct, new Dictionary<string, string?> { ["Email:Confirmation:MaxPerHour"] = "2" });
		var address = EmailApiHarness.NewAddress();
		await h.SetOkAsync(address, null, ct);
		await h.DeliverAsync(ct);

		using var second = await h.ResendAsync(ct);
		second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
		using var third = await h.ResendAsync(ct);
		third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
		third.Headers.RetryAfter.ShouldNotBeNull();
		third.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBeGreaterThan(0);
		(await third.Content.ReadAsStringAsync(ct)).ShouldContain("email-confirmation-rate-limited");

		await h.DeliverAsync(ct);
		await h.Mailpit.WaitForMessagesAsync($"to:{address}", 2, TimeSpan.FromSeconds(30), ct);
		(await h.MailCountAsync(address, ct)).ShouldBe(2);
	}

	[Fact]
	public async Task ResendOfAVerifiedAddressConflicts()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var address = EmailApiHarness.NewAddress();
		await h.SetOkAsync(address, null, ct);
		(await h.ConfirmAsync(await h.TokenFromMailAsync(address, 1, ct), ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

		using var resend = await h.ResendAsync(ct);
		resend.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await resend.Content.ReadAsStringAsync(ct)).ShouldContain("email-already-verified");
	}

	[Fact]
	// FR-007
	public async Task AVerifiedAddressIsNotASignInName()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var address = EmailApiHarness.NewAddress();
		await h.SetOkAsync(address, null, ct);
		(await h.ConfirmAsync(await h.TokenFromMailAsync(address, 1, ct), ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

		using var anonymous = h.Factory.CreateApiClient();
		using var signIn = await anonymous.PostAsJsonAsync(
			"/api/v1/session", new { accountName = address, password = TestMembers.DefaultPassword }, ct);
		signIn.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Fact]
	// SC-003
	public async Task NothingIsSentToAChangedAddressAfterItWasQueued()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var first = EmailApiHarness.NewAddress();
		var second = EmailApiHarness.NewAddress();
		var version = await h.SetOkAsync(first, null, ct);
		await h.SetOkAsync(second, version, ct);

		await h.TokenFromMailAsync(second, 1, ct);
		(await h.MailCountAsync(first, ct)).ShouldBeLessThanOrEqualTo(1);
		var own = await h.GetOwnAsync(ct);
		own.GetProperty("address").GetString().ShouldBe(second);
	}
}
