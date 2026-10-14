using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Registry.Images;
using SocAlytics.Platform.Infrastructure.Registry.Oci;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class OciImageReaderTests(OciRegistryContainerFixture registry) : IClassFixture<OciRegistryContainerFixture>
{
    private const string Prefix = "analysts/";

    private static readonly byte[] ManifestFile = Encoding.UTF8.GetBytes("{\"manifestVersion\":\"1.0.0\",\"capability\":{\"id\":\"x\"}}");

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage?>? intercept = null) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!.AbsolutePath);
            }

            return intercept?.Invoke(request) ?? await base.SendAsync(request, cancellationToken);
        }
    }

    private IOciImageReader CreateReader(RecordingHandler? handler = null, string? host = null) =>
        new OciImageReader(
            new OciDistributionClient(
                new HttpClient(handler ?? new RecordingHandler()),
                Options.Create(new ImageRegistryOptions { RegistryHost = host ?? registry.RegistryHost, RepositoryPrefix = Prefix, UseHttp = true })),
            Options.Create(new ImageRegistryOptions { RegistryHost = host ?? registry.RegistryHost, RepositoryPrefix = Prefix, UseHttp = true }));

    private static string Repo() => Prefix + "a" + Guid.NewGuid().ToString("N")[..10];

    private static IReadOnlyDictionary<string, string> Labels() => new Dictionary<string, string>
    {
        ["io.socalytics.capability.id"] = "x",
        ["io.socalytics.model.digest"] = "sha256:" + new string('a', 64),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reads_only_manifest_configuration_and_last_layer(bool index)
    {
        using var publisher = new TestImagePublisher(registry.RegistryHost);
        var image = await publisher.PublishAsync(new TestImageSpec { Repository = Repo(), ManifestFile = ManifestFile, Labels = Labels(), WrapInIndex = index });
        var handler = new RecordingHandler();

        var result = await CreateReader(handler).ReadAsync(image.Reference(registry.RegistryHost), TestContext.Current.CancellationToken);

        result.IsRefused.ShouldBeFalse(result.Detail);
        result.Content!.ImageDigest.ShouldBe(image.ManifestDigest);
        result.Content.Labels["io.socalytics.capability.id"].ShouldBe("x");
        result.Content.ManifestJson.ShouldBe(Encoding.UTF8.GetString(ManifestFile));
        result.Content.ManifestSha256.ShouldBe(TestImagePublisher.DigestOf(ManifestFile)["sha256:".Length..]);

        handler.Requests.ShouldNotContain(p => p.EndsWith(image.FillerDigest, StringComparison.Ordinal));
        handler.Requests.ShouldContain(p => p.EndsWith(image.ConfigDigest, StringComparison.Ordinal));
        handler.Requests.ShouldContain(p => p.EndsWith(image.ManifestLayerDigest, StringComparison.Ordinal));
        handler.Requests.ShouldAllBe(p => !p.Contains("/tags/") && !p.Contains("/uploads/"));
    }

    [Fact]
    public async Task Reads_an_uncompressed_last_layer()
    {
        using var publisher = new TestImagePublisher(registry.RegistryHost);
        var image = await publisher.PublishAsync(new TestImageSpec { Repository = Repo(), ManifestFile = ManifestFile, GzipLayer = false });

        var result = await CreateReader().ReadAsync(image.Reference(registry.RegistryHost), TestContext.Current.CancellationToken);

        result.IsRefused.ShouldBeFalse(result.Detail);
    }

    [Fact]
    public async Task Refuses_a_tag_reference()
    {
        var result = await CreateReader().ReadAsync($"{registry.RegistryHost}/{Prefix}x:latest", TestContext.Current.CancellationToken);

        result.RefusalCode.ShouldBe(ImageImportRefusalCodes.ReferenceNotDigest);
    }

    [Theory]
    [InlineData("other.example/analysts/x@sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("HOST/elsewhere/x@sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task Refuses_references_outside_host_and_prefix(string reference)
    {
        var result = await CreateReader().ReadAsync(reference.Replace("HOST", registry.RegistryHost), TestContext.Current.CancellationToken);

        result.RefusalCode.ShouldBe(ImageImportRefusalCodes.ReferenceNotAllowed);
    }

    [Fact]
    public async Task Refuses_an_unknown_image()
    {
        var reference = $"{registry.RegistryHost}/{Repo()}@sha256:{new string('0', 64)}";

        var result = await CreateReader().ReadAsync(reference, TestContext.Current.CancellationToken);

        result.RefusalCode.ShouldBe(ImageImportRefusalCodes.ImageNotFound);
    }

    [Fact]
    public async Task Reports_an_unreachable_registry_as_unavailable()
    {
        var reference = $"127.0.0.1:1/{Repo()}@sha256:{new string('0', 64)}";

        var result = await CreateReader(host: "127.0.0.1:1").ReadAsync(reference, TestContext.Current.CancellationToken);

        result.RefusalCode.ShouldBe(ImageImportRefusalCodes.RegistryUnavailable);
    }

    [Fact]
    public async Task Reports_a_server_error_as_unavailable()
    {
        using var publisher = new TestImagePublisher(registry.RegistryHost);
        var image = await publisher.PublishAsync(new TestImageSpec { Repository = Repo(), ManifestFile = ManifestFile });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await CreateReader(handler).ReadAsync(image.Reference(registry.RegistryHost), TestContext.Current.CancellationToken);

        result.RefusalCode.ShouldBe(ImageImportRefusalCodes.RegistryUnavailable);
    }

    [Fact]
    public async Task Refuses_content_that_differs_from_its_digest()
    {
        using var publisher = new TestImagePublisher(registry.RegistryHost);
        var image = await publisher.PublishAsync(new TestImageSpec { Repository = Repo(), ManifestFile = ManifestFile });
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith(image.ManifestLayerDigest, StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }
            : null);

        var result = await CreateReader(handler).ReadAsync(image.Reference(registry.RegistryHost), TestContext.Current.CancellationToken);

        result.RefusalCode.ShouldBe(ImageImportRefusalCodes.DigestMismatch);
    }

    [Fact]
    public async Task Refuses_a_last_layer_with_additional_files()
    {
        using var publisher = new TestImagePublisher(registry.RegistryHost);
        var image = await publisher.PublishAsync(new TestImageSpec { Repository = Repo(), ManifestFile = ManifestFile, ExtraLayerFiles = ["opt/socalytics/other.txt"] });

        var result = await CreateReader().ReadAsync(image.Reference(registry.RegistryHost), TestContext.Current.CancellationToken);

        result.RefusalCode.ShouldBe(ImageImportRefusalCodes.ManifestMissing);
    }

    [Fact]
    public async Task Refuses_a_last_layer_without_the_manifest_file()
    {
        using var publisher = new TestImagePublisher(registry.RegistryHost);
        var image = await publisher.PublishAsync(new TestImageSpec { Repository = Repo(), ManifestFile = ManifestFile, ManifestPath = "opt/other/file.json" });

        var result = await CreateReader().ReadAsync(image.Reference(registry.RegistryHost), TestContext.Current.CancellationToken);

        result.RefusalCode.ShouldBe(ImageImportRefusalCodes.ManifestMissing);
    }

    [Fact]
    public async Task Refuses_an_unsupported_layer_media_type()
    {
        using var publisher = new TestImagePublisher(registry.RegistryHost);
        var image = await publisher.PublishAsync(new TestImageSpec
        {
            Repository = Repo(),
            ManifestFile = ManifestFile,
            LayerMediaType = "application/vnd.oci.image.layer.v1.tar+zstd",
        });

        var result = await CreateReader().ReadAsync(image.Reference(registry.RegistryHost), TestContext.Current.CancellationToken);

        result.RefusalCode.ShouldBe(ImageImportRefusalCodes.UnsupportedMediaType);
    }
}
