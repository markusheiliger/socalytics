using System.Net;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests.ImagePush;

/// <summary>The limits, cancellation, and retries of engine calls, against a fake handler and a fake clock (no real sleeps).</summary>
public sealed class EngineCallPolicyTests
{
    private static readonly Uri Endpoint = new("http://registry.test/v2/analysts/probe/manifests/1.0.0");

    [Fact]
    public async Task A_call_longer_than_the_former_five_minute_limit_but_within_the_push_limit_completes()
    {
        var time = new FakeTimeProvider();
        var calls = 0;

        var task = EngineCallPolicy.ExecuteAsync(
            "push",
            nameof(EngineCallPolicy.PushLimit),
            EngineCallPolicy.PushLimit,
            async token =>
            {
                calls++;
                await Task.Delay(TimeSpan.FromMinutes(10), time, token);
                return "done";
            },
            CancellationToken.None,
            time);

        (await Drive(task, time)).ShouldBe("done");
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_stalled_call_ends_at_the_named_limit_and_is_reported_as_that_limit()
    {
        var time = new FakeTimeProvider();
        var calls = 0;

        var task = EngineCallPolicy.ExecuteAsync<string>(
            "push",
            nameof(EngineCallPolicy.PushLimit),
            EngineCallPolicy.PushLimit,
            async token =>
            {
                calls++;
                await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
                return "never";
            },
            CancellationToken.None,
            time);

        var failure = await Should.ThrowAsync<EngineCallFailedException>(() => Drive(task, time));
        failure.Message.ShouldContain(nameof(EngineCallPolicy.PushLimit));
        failure.Message.ShouldContain(EngineCallPolicy.PushLimit.ToString());
        calls.ShouldBe(EngineCallPolicy.MaxAttempts);
    }

    [Fact]
    public async Task Cancellation_of_the_tests_token_stops_the_call_at_once_without_a_retry()
    {
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var started = new TaskCompletionSource();

        var task = EngineCallPolicy.ExecuteAsync<string>(
            "push",
            nameof(EngineCallPolicy.PushLimit),
            EngineCallPolicy.PushLimit,
            async token =>
            {
                calls++;
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
                return "never";
            },
            cts.Token,
            time);

        await started.Task;
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => task);
        calls.ShouldBe(1);
    }

    public static TheoryData<string> TransientFailures => new()
    {
        "HttpRequestException",
        "IOException",
        "call timeout",
        "HTTP 502",
        "HTTP 503",
        "HTTP 504",
        "HTTP 429",
        "build exception with a transient cause",
    };

    [Theory]
    [MemberData(nameof(TransientFailures))]
    public async Task A_transient_failure_is_repeated_exactly_the_maximum_attempts_with_every_reason_reported(string kind)
    {
        var time = new FakeTimeProvider();
        var calls = 0;

        var task = EngineCallPolicy.ExecuteAsync<string>(
            "build",
            nameof(EngineCallPolicy.BuildLimit),
            EngineCallPolicy.BuildLimit,
            _ =>
            {
                calls++;
                throw Failure(kind, calls);
            },
            CancellationToken.None,
            time);

        var failure = await Should.ThrowAsync<EngineCallFailedException>(() => Drive(task, time));
        calls.ShouldBe(EngineCallPolicy.MaxAttempts);
        failure.Reasons.Count.ShouldBe(EngineCallPolicy.MaxAttempts);
        for (var attempt = 1; attempt <= EngineCallPolicy.MaxAttempts; attempt++)
        {
            failure.Message.ShouldContain($"attempt {attempt}:");
        }
    }

    [Fact]
    public async Task Success_on_a_later_attempt_stops_the_retries()
    {
        var time = new FakeTimeProvider();
        var calls = 0;

        var task = EngineCallPolicy.ExecuteAsync(
            "push",
            nameof(EngineCallPolicy.PushLimit),
            EngineCallPolicy.PushLimit,
            _ => ++calls < 2 ? throw new HttpRequestException("reset") : Task.FromResult("done"),
            CancellationToken.None,
            time);

        (await Drive(task, time)).ShouldBe("done");
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_registry_that_is_not_yet_answering_is_repeated_within_the_lookup_limit_and_is_not_a_missing_digest()
    {
        var time = new FakeTimeProvider();
        var handler = new FakeHandler(requests => requests <= 3
            ? throw new HttpRequestException("connection refused")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);

        var task = EngineCallPolicy.ExecuteWithinAsync(
            "registry lookup",
            nameof(EngineCallPolicy.RegistryLookupLimit),
            EngineCallPolicy.RegistryLookupLimit,
            async token =>
            {
                using var response = await http.GetAsync(Endpoint, token);
                return response.StatusCode;
            },
            CancellationToken.None,
            time);

        (await Drive(task, time)).ShouldBe(HttpStatusCode.NotFound);
        handler.Requests.ShouldBe(4);
    }

    [Fact]
    public async Task A_registry_that_never_answers_ends_at_the_lookup_limit_with_every_reason()
    {
        var time = new FakeTimeProvider();
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        using var http = new HttpClient(handler);

        var task = EngineCallPolicy.ExecuteWithinAsync(
            "registry lookup",
            nameof(EngineCallPolicy.RegistryLookupLimit),
            EngineCallPolicy.RegistryLookupLimit,
            async token =>
            {
                using var response = await http.GetAsync(Endpoint, token);
                return response.StatusCode;
            },
            CancellationToken.None,
            time);

        var failure = await Should.ThrowAsync<EngineCallFailedException>(() => Drive(task, time));
        failure.Message.ShouldContain(nameof(EngineCallPolicy.RegistryLookupLimit));
        failure.Reasons.ShouldAllBe(reason => reason.Contains("connection refused"));
        failure.Reasons.Count.ShouldBeGreaterThan(1);
    }

    public static TheoryData<string> DeterministicFailures => new()
    {
        "an error in the response",
        "a malformed response",
        "a digest conflict",
        "HTTP 400",
        "HTTP 404",
        "HTTP 409",
        "build exception without a transient cause",
    };

    [Theory]
    [MemberData(nameof(DeterministicFailures))]
    public async Task A_deterministic_failure_is_tried_once(string kind)
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var expected = Failure(kind, 1);

        var task = EngineCallPolicy.ExecuteAsync<string>(
            "push",
            nameof(EngineCallPolicy.PushLimit),
            EngineCallPolicy.PushLimit,
            _ =>
            {
                calls++;
                throw expected;
            },
            CancellationToken.None,
            time);

        var thrown = await Should.ThrowAsync<Exception>(() => Drive(task, time));
        thrown.ShouldBeSameAs(expected);
        calls.ShouldBe(1);
    }

    private static Exception Failure(string kind, int attempt) => kind switch
    {
        "HttpRequestException" => new HttpRequestException($"reset {attempt}"),
        "IOException" => new IOException($"closed {attempt}"),
        "call timeout" => new OperationCanceledException($"timeout {attempt}"),
        "HTTP 502" => new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway),
        "HTTP 503" => new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable),
        "HTTP 504" => new HttpRequestException("gateway timeout", null, HttpStatusCode.GatewayTimeout),
        "HTTP 429" => new HttpRequestException("too many", null, HttpStatusCode.TooManyRequests),
        "HTTP 400" => new HttpRequestException("bad request", null, HttpStatusCode.BadRequest),
        "HTTP 404" => new HttpRequestException("not found", null, HttpStatusCode.NotFound),
        "HTTP 409" => new HttpRequestException("conflict", null, HttpStatusCode.Conflict),
        "build exception with a transient cause" => new InvalidOperationException("build failed", new IOException($"pipe closed {attempt}")),
        "build exception without a transient cause" => new InvalidOperationException("build failed", new InvalidDataException("step failed")),
        "an error in the response" => new InvalidOperationException("The push failed: {\"message\":\"denied\"}"),
        "a malformed response" => new InvalidOperationException("The push reported a line that is not a JSON object"),
        "a digest conflict" => new InvalidOperationException("The digests disagree"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Advances the fake clock until the task completes, without waiting in real time.</summary>
    private static async Task<T> Drive<T>(Task<T> task, FakeTimeProvider time)
    {
        for (var steps = 0; !task.IsCompleted; steps++)
        {
            steps.ShouldBeLessThan(100_000, "the call neither completed nor reached a limit");
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        return await task;
    }

    private sealed class FakeHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            return Task.FromResult(respond(Requests));
        }
    }
}
