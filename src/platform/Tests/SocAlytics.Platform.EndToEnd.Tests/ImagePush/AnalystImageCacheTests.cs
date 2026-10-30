using Shouldly;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests.ImagePush;

/// <summary>
/// The class-scoped reuse of built and pushed images, always on a fresh <see cref="ImageBuildGate"/> instance injected into the
/// cache and the build delegate (never the process-wide one) and without Docker.
/// </summary>
public sealed class AnalystImageCacheTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly AnalystImageKey Succeed = new("test-analyst", "succeed", "manifest-a");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_tests_needing_the_same_variant_and_inputs_share_one_build_and_push()
    {
        var gate = new ImageBuildGate(1);
        await using var cache = new AnalystImageCache(gate);
        var release = NewSignal();
        var started = NewSignal();
        var builds = 0;

        var tests = Enumerable.Range(0, 5)
            .Select(_ => cache.GetOrBuildAsync(Succeed, Build(gate, "one", async _ =>
            {
                Interlocked.Increment(ref builds);
                started.TrySetResult();
                await release.Task;
            }), Token))
            .ToArray();

        await started.Task.WaitAsync(Patience, Token);
        release.SetResult();
        var images = await Task.WhenAll(tests).WaitAsync(Patience, Token);

        builds.ShouldBe(1);
        cache.BuildCount(Succeed).ShouldBe(1);
        images.Select(i => i.Digest).Distinct().Count().ShouldBe(1);
        gate.ObservedMaximum.ShouldBe(1);

        // A later test of the same class is served the ready image without another build.
        var later = await cache.GetOrBuildAsync(Succeed, Build(gate, "two"), Token);
        later.Digest.ShouldBe(images[0].Digest);
        cache.BuildCount(Succeed).ShouldBe(1);
    }

    [Fact]
    public async Task A_different_variant_or_other_inputs_gets_its_own_image_and_is_never_served_a_mismatching_one()
    {
        var gate = new ImageBuildGate(1);
        await using var cache = new AnalystImageCache(gate);
        var otherVariant = Succeed with { Variant = "media" };
        var otherInputs = Succeed with { Inputs = "manifest-b" };
        var otherHelper = Succeed with { Helper = "reference" };

        var first = await cache.GetOrBuildAsync(Succeed, Build(gate, "succeed-a"), Token);
        var variant = await cache.GetOrBuildAsync(otherVariant, Build(gate, "media-a"), Token);
        var inputs = await cache.GetOrBuildAsync(otherInputs, Build(gate, "succeed-b"), Token);
        var helper = await cache.GetOrBuildAsync(otherHelper, Build(gate, "reference-a"), Token);

        new[] { first, variant, inputs, helper }.Select(i => i.Digest).Distinct().Count().ShouldBe(4);
        foreach (var key in new[] { Succeed, otherVariant, otherInputs, otherHelper })
        {
            cache.BuildCount(key).ShouldBe(1);
        }

        (await cache.GetOrBuildAsync(Succeed, Build(gate, "never"), Token)).Digest.ShouldBe(first.Digest);
        (await cache.GetOrBuildAsync(otherVariant, Build(gate, "never"), Token)).Digest.ShouldBe(variant.Digest);
    }

    [Fact]
    public async Task A_faulted_build_is_reported_to_every_waiting_test_and_removed_so_a_later_test_may_retry()
    {
        var gate = new ImageBuildGate(1);
        await using var cache = new AnalystImageCache(gate);
        var release = NewSignal();
        var started = NewSignal();

        var waiting = Enumerable.Range(0, 3)
            .Select(_ => cache.GetOrBuildAsync(Succeed, Build(gate, "broken", async _ =>
            {
                started.TrySetResult();
                await release.Task;
                throw new InvalidOperationException("push failed");
            }), Token))
            .ToArray();

        await started.Task.WaitAsync(Patience, Token);
        release.SetResult();

        foreach (var test in waiting)
        {
            var failure = await Should.ThrowAsync<InvalidOperationException>(() => test);
            failure.Message.ShouldBe("push failed");
        }

        cache.Contains(Succeed).ShouldBeFalse();
        cache.BuildCount(Succeed).ShouldBe(1);

        // Never cached as success: a later test builds again and succeeds.
        var retried = await cache.GetOrBuildAsync(Succeed, Build(gate, "fixed"), Token);
        retried.LocalName.ShouldContain("fixed");
        cache.BuildCount(Succeed).ShouldBe(2);
        cache.Contains(Succeed).ShouldBeTrue();
        await AssertFree(gate);
    }

    [Fact]
    public async Task Cancellation_of_one_waiting_test_does_not_cancel_the_build_for_the_others()
    {
        var gate = new ImageBuildGate(1);
        await using var cache = new AnalystImageCache(gate);
        var release = NewSignal();
        var started = NewSignal();
        using var cts = new CancellationTokenSource();

        Func<CancellationToken, Task<PushedAnalystImage>> build = Build(gate, "shared", async buildToken =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(buildToken);
        });
        var cancelling = cache.GetOrBuildAsync(Succeed, build, cts.Token);
        var staying = cache.GetOrBuildAsync(Succeed, build, Token);

        await started.Task.WaitAsync(Patience, Token);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => cancelling);

        for (var i = 0; i < 100; i++)
        {
            await Task.Yield();
        }

        staying.IsCompleted.ShouldBeFalse();
        cache.Contains(Succeed).ShouldBeTrue();

        release.SetResult();
        var image = await staying.WaitAsync(Patience, Token);

        image.LocalName.ShouldContain("shared");
        cache.BuildCount(Succeed).ShouldBe(1);
    }

    [Fact]
    public async Task A_build_whose_waiters_all_cancelled_stops_releases_the_gate_and_has_its_entry_removed()
    {
        var gate = new ImageBuildGate(1);
        await using var cache = new AnalystImageCache(gate);
        var started = NewSignal();
        var stopped = NewSignal();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        Func<CancellationToken, Task<PushedAnalystImage>> build = Build(gate, "abandoned", async buildToken =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, buildToken);
            }
            finally
            {
                stopped.TrySetResult();
            }
        });

        var one = cache.GetOrBuildAsync(Succeed, build, first.Token);
        var two = cache.GetOrBuildAsync(Succeed, build, second.Token);
        await started.Task.WaitAsync(Patience, Token);

        await first.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => one);
        stopped.Task.IsCompleted.ShouldBeFalse();
        cache.Contains(Succeed).ShouldBeTrue();

        await second.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => two);

        await stopped.Task.WaitAsync(Patience, Token);
        await AssertFree(gate);
        cache.Contains(Succeed).ShouldBeFalse();
        cache.BuildCount(Succeed).ShouldBe(1);

        // The entry is gone, so a later test starts a fresh build.
        var fresh = await cache.GetOrBuildAsync(Succeed, Build(gate, "fresh"), Token);
        fresh.LocalName.ShouldContain("fresh");
        cache.BuildCount(Succeed).ShouldBe(2);
    }

    [Fact]
    public async Task The_build_count_of_every_key_is_observable()
    {
        var gate = new ImageBuildGate(1);
        await using var cache = new AnalystImageCache(gate);
        var other = Succeed with { Variant = "media" };

        cache.BuildCount(Succeed).ShouldBe(0);

        await cache.GetOrBuildAsync(Succeed, Build(gate, "a"), Token);
        await cache.GetOrBuildAsync(Succeed, Build(gate, "a"), Token);
        await cache.GetOrBuildAsync(other, Build(gate, "b"), Token);

        cache.BuildCount(Succeed).ShouldBe(1);
        cache.BuildCount(other).ShouldBe(1);
    }

    [Fact]
    public async Task A_cached_build_cannot_acquire_the_gate_twice_and_never_deadlocks()
    {
        var gate = new ImageBuildGate(1);
        await using var cache = new AnalystImageCache(gate);
        var keys = new[] { Succeed, Succeed with { Variant = "media" }, Succeed with { Variant = "payload-invalid" } };

        // Two concurrent requests per key on a gate of one: the build delegate takes the gate once, the cache never does.
        var tests = keys.SelectMany(key => new[]
        {
            cache.GetOrBuildAsync(key, Build(gate, key.Variant), Token),
            cache.GetOrBuildAsync(key, Build(gate, key.Variant), Token),
        }).ToArray();

        var images = await Task.WhenAll(tests).WaitAsync(Patience, Token);

        images.Select(i => i.Digest).Distinct().Count().ShouldBe(keys.Length);
        gate.ObservedMaximum.ShouldBe(1);
        foreach (var key in keys)
        {
            cache.BuildCount(key).ShouldBe(1);
        }

        await AssertFree(gate);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A build delegate that holds the gate around its work once, as the helpers' uncached build and push does.</summary>
    private static Func<CancellationToken, Task<PushedAnalystImage>> Build(ImageBuildGate gate, string name, Func<CancellationToken, Task>? work = null) =>
        async buildToken =>
        {
            using var lease = await gate.AcquireAsync(buildToken);
            if (work is not null)
            {
                await work(buildToken);
            }

            return new PushedAnalystImage($"e2e/{name}", "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))), $"local/{name}:{Guid.NewGuid():N}");
        };

    private static async Task AssertFree(ImageBuildGate gate)
    {
        using var lease = await gate.AcquireAsync(CancellationToken.None).AsTask().WaitAsync(Patience, Token);
    }
}
