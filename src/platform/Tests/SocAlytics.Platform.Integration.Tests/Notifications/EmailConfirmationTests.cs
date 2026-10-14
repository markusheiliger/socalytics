using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Notifications.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Notifications;

public sealed class EmailConfirmationTests(PostgresContainerFixture postgres, MailpitContainerFixture mailpit)
{
	[Fact]
	// US1 scenarios 2 and 7: the confirmation is anonymous and single use
	public async Task ConfirmingVerifiesTheAddressOnceWithoutASession()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var address = EmailApiHarness.NewAddress();
		await h.SetOkAsync(address, null, ct);
		var token = await h.TokenFromMailAsync(address, 1, ct);

		// Reading the link without the confirm call verifies nothing (scanner case).
		(await h.GetOwnAsync(ct)).GetProperty("state").GetString().ShouldBe("unverified");

		using var confirmed = await h.ConfirmAsync(token, ct);
		confirmed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		var own = await h.GetOwnAsync(ct);
		own.GetProperty("state").GetString().ShouldBe("verified");
		own.GetProperty("verifiedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);

		using var again = await h.ConfirmAsync(token, ct);
		again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await again.Content.ReadAsStringAsync(ct)).ShouldContain("email-confirmation-invalid");
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("unknown-token")]
	public async Task UnknownTokensAreRefusedGenerically(string? token)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		using var response = await h.ConfirmAsync(token, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await response.Content.ReadAsStringAsync(ct)).ShouldContain("email-confirmation-invalid");
	}

	[Fact]
	// US1 scenario 4
	public async Task ResendingSupersedesEarlierLinks()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var address = EmailApiHarness.NewAddress();
		await h.SetOkAsync(address, null, ct);
		var first = await h.TokenFromMailAsync(address, 1, ct);

		using var resend = await h.ResendAsync(ct);
		resend.StatusCode.ShouldBe(HttpStatusCode.Accepted);
		var second = await h.TokenFromMailAsync(address, 2, ct);
		second.ShouldNotBe(first);

		(await h.ConfirmAsync(first, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await h.ConfirmAsync(second, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
	}

	[Fact]
	// US1 scenario 3
	public async Task ExpiredLinksAreRefusedAndChangeNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var address = EmailApiHarness.NewAddress();
		await h.SetOkAsync(address, null, ct);
		var token = await h.TokenFromMailAsync(address, 1, ct);

		h.Factory.Time.Advance(TimeSpan.FromDays(2));

		(await h.ConfirmAsync(token, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		// The advanced clock also ends the session, so the state is read from a new one.
		using var fresh = await ApiSession.SignInAsync(h.Factory, EmailApiHarness.AdminName, TestMembers.DefaultPassword, ct);
		using var own = await fresh.SendAsync(HttpMethod.Get, "/api/v1/me/email", null, ct);
		(await own.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("state").GetString().ShouldBe("unverified");
	}

	[Fact]
	public async Task LinksOfAChangedAddressAreRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var oldAddress = EmailApiHarness.NewAddress();
		var version = await h.SetOkAsync(oldAddress, null, ct);
		var oldToken = await h.TokenFromMailAsync(oldAddress, 1, ct);
		await h.SetOkAsync(EmailApiHarness.NewAddress(), version, ct);

		(await h.ConfirmAsync(oldToken, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await h.GetOwnAsync(ct)).GetProperty("state").GetString().ShouldBe("unverified");
	}

	[Fact]
	public async Task AttemptsBeyondThePerClientLimitAreRateLimited()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(
			postgres, mailpit, ct, new Dictionary<string, string?> { ["Email:Confirmation:AttemptsPerMinute"] = "3" });

		for (var i = 0; i < 3; i++)
		{
			(await h.ConfirmAsync("unknown-token", ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		}

		using var limited = await h.ConfirmAsync("unknown-token", ct);
		limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
		limited.Headers.RetryAfter.ShouldNotBeNull();
	}
}
