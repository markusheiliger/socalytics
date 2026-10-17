#pragma warning disable xUnit1051 // The fake handler answers synchronously; there is nothing to cancel.
using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Runtime.Docker;

namespace SocAlytics.Analysts.Manager.Tests.Runtime.Docker;

public sealed class ImageCacheTests : IDisposable
{
    private const string Repository = "registry.test/socalytics/test-analyst";
    private const long Gib = 1024L * 1024 * 1024;

    private static string Digest(char c) => "sha256:" + new string(c, 64);

    private static string Reference(char c) => $"{Repository}@{Digest(c)}";

    private sealed class FakeEngine : HttpMessageHandler
    {
        public Dictionary<string, long> Images { get; } = [];
        public HashSet<string> RefuseRemoval { get; } = [];
        public List<string> Pulled { get; } = [];
        public List<string> Removed { get; } = [];
        public long PullSize { get; set; } = Gib;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            if (path == "/version")
            {
                return Task.FromResult(Json("""{"Version":"28.0.4","ApiVersion":"1.47","Os":"linux","Arch":"amd64"}"""));
            }

            if (path.EndsWith("/images/create", StringComparison.Ordinal))
            {
                var reference = Uri.UnescapeDataString(request.RequestUri.Query).Split("fromImage=")[1];
                Pulled.Add(reference);
                Images[reference] = PullSize;
                return Task.FromResult(Json("""{"status":"Pulling"}"""));
            }

            var index = path.IndexOf("/images/", StringComparison.Ordinal);
            var image = index < 0 ? string.Empty : path[(index + "/images/".Length)..];
            if (request.Method == HttpMethod.Get && image.EndsWith("/json", StringComparison.Ordinal))
            {
                image = image[..^"/json".Length];
                return Task.FromResult(Images.TryGetValue(image, out var size)
                    ? Json($$"""{"Id":"sha256:id","Os":"linux","Architecture":"amd64","Size":{{size}}}""")
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (request.Method == HttpMethod.Delete)
            {
                if (RefuseRemoval.Contains(image))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = new StringContent("""{"message":"in use"}""", Encoding.UTF8, "application/json"),
                    });
                }

                Removed.Add(image);
                Images.Remove(image);
                return Task.FromResult(Json("[]"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-17T09:00:00Z"));
    private readonly FakeEngine _handler = new();
    private readonly DockerEngineClient _engine;
    private readonly string _state = Path.Combine(Path.GetTempPath(), "imagecache-test-" + Guid.NewGuid().ToString("N"));
    private readonly DockerRuntimeOptions _options = new() { ImageCacheBudgetBytes = 2 * Gib, ImageCacheMaxIdle = TimeSpan.FromDays(14) };

    public ImageCacheTests()
    {
        _engine = new DockerEngineClient(new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") });
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, true);
        }
    }

    private ImageCache NewCache() => new(() => _engine, _state, _options, _time);

    private async Task PullAsync(ImageCache cache, char digest)
    {
        await cache.EnsurePulledAsync(_engine, Reference(digest), "token", CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Reuses_a_cached_digest_without_a_pull_and_persists_the_record()
    {
        var cache = NewCache();
        await PullAsync(cache, 'a');
        await PullAsync(cache, 'a');

        _handler.Pulled.ShouldBe([Reference('a')]);
        var json = await File.ReadAllTextAsync(Path.Combine(_state, ImageCache.FileName));
        json.ShouldContain("\"version\": 1");
        json.ShouldContain(Digest('a'));
        json.ShouldContain("lastUsedAt");

        var reloaded = NewCache();
        await reloaded.PruneAsync(CancellationToken.None);
        reloaded.State!.Images.ShouldBe(1);
    }

    [Fact]
    public async Task Removes_least_recently_used_images_until_within_the_budget()
    {
        var cache = NewCache();
        await PullAsync(cache, 'a');
        await PullAsync(cache, 'b');
        await PullAsync(cache, 'c');
        await PullAsync(cache, 'a');

        var state = await cache.PruneAsync(CancellationToken.None);

        _handler.Removed.ShouldBe([Reference('b')]);
        state!.Images.ShouldBe(2);
        state.UsedBytes.ShouldBe(2 * Gib);
        state.BudgetBytes.ShouldBe(2 * Gib);
        state.Removed.ShouldBe(1);
    }

    [Fact]
    public async Task Removes_every_image_idle_longer_than_the_period_even_within_the_budget()
    {
        var cache = NewCache();
        await PullAsync(cache, 'a');
        _time.Advance(TimeSpan.FromDays(15));
        await PullAsync(cache, 'b');

        await cache.PruneAsync(CancellationToken.None);

        _handler.Removed.ShouldBe([Reference('a')]);
    }

    [Fact]
    public async Task Keeps_images_used_by_an_active_attempt()
    {
        var cache = NewCache();
        await PullAsync(cache, 'a');
        await PullAsync(cache, 'b');
        await PullAsync(cache, 'c');
        cache.Acquire(Reference('a'));

        await cache.PruneAsync(CancellationToken.None);

        _handler.Removed.ShouldBe([Reference('b')]);
        _handler.Images.ShouldContainKey(Reference('a'));
    }

    [Fact]
    public async Task Never_records_or_removes_an_image_that_was_present_before_the_pull()
    {
        _handler.Images[Reference('f')] = 10 * Gib;
        var cache = NewCache();
        await PullAsync(cache, 'f');
        _time.Advance(TimeSpan.FromDays(30));

        var state = await cache.PruneAsync(CancellationToken.None);

        _handler.Pulled.ShouldBeEmpty();
        _handler.Removed.ShouldBeEmpty();
        state!.Images.ShouldBe(0);
        _handler.Images.ShouldContainKey(Reference('f'));
    }

    [Fact]
    public async Task Keeps_a_failed_removal_recorded_and_retries_it_with_the_next_pass()
    {
        var cache = NewCache();
        await PullAsync(cache, 'a');
        _time.Advance(TimeSpan.FromDays(15));
        _handler.RefuseRemoval.Add(Reference('a'));

        var failed = await cache.PruneAsync(CancellationToken.None);

        failed!.Leftovers.ShouldBe([$"image {Digest('a')}"]);
        failed.Images.ShouldBe(1);
        _handler.RefuseRemoval.Clear();

        var retried = await cache.PruneAsync(CancellationToken.None);

        retried!.Leftovers.ShouldBeEmpty();
        retried.Images.ShouldBe(0);
        _handler.Removed.ShouldBe([Reference('a')]);
    }

    [Fact]
    public void Status_text_shows_the_cache_use_against_its_budget_and_what_could_not_be_removed()
    {
        var result = new SocAlytics.Analysts.Manager.Runtime.PreflightResult(
            _time.GetUtcNow(), "docker-desktop", "Docker Desktop", "4.40", "linux", true, [], [])
        {
            ImageCache = new SocAlytics.Analysts.Manager.Runtime.ImageCacheState(_time.GetUtcNow(), 2, 3 * Gib, 20 * Gib, 1, [$"image {Digest('a')}"]),
        };

        var text = string.Join('\n', SocAlytics.Analysts.Manager.Diagnostics.RuntimeStatusText.Lines(result));

        text.ShouldContain("Image cache: 2 images, 3 GiB of 20 GiB budget");
        text.ShouldContain($"Could not remove: image {Digest('a')}");
    }
}
