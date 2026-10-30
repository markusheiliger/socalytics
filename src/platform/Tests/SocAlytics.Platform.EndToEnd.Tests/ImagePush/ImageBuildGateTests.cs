using Shouldly;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests.ImagePush;

/// <summary>
/// The bound on concurrent builds and pushes, always on a fresh <see cref="ImageBuildGate"/> instance
/// (never the process-wide one), so these tests neither wait for nor affect real builds.
/// </summary>
public sealed class ImageBuildGateTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(1, 6)]
    [InlineData(2, 8)]
    [InlineData(3, 9)]
    public async Task A_probe_of_concurrent_builds_never_exceeds_the_limit_of_the_instance(int limit, int builds)
    {
        var gate = new ImageBuildGate(limit);
        var running = 0;
        var observed = 0;

        await Task.WhenAll(Enumerable.Range(0, builds).Select(async _ =>
        {
            using var lease = await gate.AcquireAsync(CancellationToken.None);
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref observed, now);
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Interlocked.Decrement(ref running);
        })).WaitAsync(Patience, TestContext.Current.CancellationToken);

        observed.ShouldBeLessThanOrEqualTo(limit);
        gate.ObservedMaximum.ShouldBeLessThanOrEqualTo(limit);
        gate.ObservedMaximum.ShouldBe(observed);
    }

    [Fact]
    public async Task A_second_acquirer_waits_while_a_gate_of_one_is_held_and_proceeds_when_it_is_released()
    {
        var gate = new ImageBuildGate(1);
        var first = await gate.AcquireAsync(CancellationToken.None);

        var second = gate.AcquireAsync(CancellationToken.None).AsTask();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        second.IsCompleted.ShouldBeFalse();

        first.Dispose();

        using var lease = await second.WaitAsync(Patience, TestContext.Current.CancellationToken);
        gate.ObservedMaximum.ShouldBe(1);
    }

    [Fact]
    public async Task The_gate_is_released_after_a_successful_build()
    {
        var gate = new ImageBuildGate(1);

        var result = await Build(gate, () => Task.FromResult("built"));

        result.ShouldBe("built");
        await AssertFree(gate);
    }

    [Fact]
    public async Task The_gate_is_released_after_a_failed_build()
    {
        var gate = new ImageBuildGate(1);

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => Build<string>(gate, () => throw new InvalidOperationException("build failed")));

        failure.Message.ShouldBe("build failed");
        await AssertFree(gate);
    }

    [Fact]
    public async Task The_gate_is_released_after_a_cancelled_build()
    {
        var gate = new ImageBuildGate(1);
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var build = Build(gate, async () =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cts.Token);
            return "never";
        });

        await started.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => build);
        await AssertFree(gate);
    }

    [Fact]
    public async Task A_cancelled_waiter_never_holds_the_gate()
    {
        var gate = new ImageBuildGate(1);
        var holder = await gate.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var waiter = gate.AcquireAsync(cts.Token).AsTask();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        waiter.IsCompleted.ShouldBeFalse();

        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => waiter);

        // The cancelled waiter must not have taken the slot: releasing the holder frees the gate completely.
        holder.Dispose();
        await AssertFree(gate);
        gate.ObservedMaximum.ShouldBe(1);
    }

    [Fact]
    public async Task An_already_cancelled_token_never_acquires_the_gate()
    {
        var gate = new ImageBuildGate(1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => gate.AcquireAsync(cts.Token).AsTask());

        await AssertFree(gate);
    }

    [Fact]
    public async Task Code_that_does_not_build_or_push_never_waits_for_the_gate()
    {
        var gate = new ImageBuildGate(1);
        using var lease = await gate.AcquireAsync(CancellationToken.None);

        // Engine calls that are not builds or pushes (lookups, parsing) never touch the gate.
        var lookup = EngineCallPolicy.ExecuteAsync(
            "lookup",
            nameof(EngineCallPolicy.PushLimit),
            EngineCallPolicy.PushLimit,
            token => Task.FromResult("found"),
            CancellationToken.None,
            TimeProvider.System);

        (await lookup.WaitAsync(Patience, TestContext.Current.CancellationToken)).ShouldBe("found");
        gate.ObservedMaximum.ShouldBe(1);
    }

    private static async Task<T> Build<T>(ImageBuildGate gate, Func<Task<T>> build)
    {
        using var lease = await gate.AcquireAsync(CancellationToken.None);
        return await build();
    }

    private static async Task AssertFree(ImageBuildGate gate)
    {
        using var lease = await gate.AcquireAsync(CancellationToken.None).AsTask().WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}
