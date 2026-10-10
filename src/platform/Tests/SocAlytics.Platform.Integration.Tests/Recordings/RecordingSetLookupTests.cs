using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class RecordingSetLookupTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	public class CountingProxy : DispatchProxy
	{
		public IObjectStorage Inner { get; set; } = null!;

		public CountingProxy? Shared { get; set; }

		public int Calls;

		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
		{
			Interlocked.Increment(ref (Shared ?? this).Calls);
			try
			{
				return targetMethod!.Invoke(Inner, args);
			}
			catch (TargetInvocationException e) when (e.InnerException is not null)
			{
				throw e.InnerException;
			}
		}
	}

	[Fact]
	public async Task LookupReturnsTheFrozenLineageWithoutTouchingObjectStorage()
	{
		var ct = TestContext.Current.CancellationToken;
		var counter = new CountingProxy();
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct, configureServices: services =>
		{
			var descriptor = services.Last(d => d.ServiceType == typeof(IObjectStorage));
			services.Remove(descriptor);
			services.AddSingleton<IObjectStorage>(sp =>
			{
				var proxy = DispatchProxy.Create<IObjectStorage, CountingProxy>();
				var typed = (CountingProxy)(object)proxy;
				typed.Inner = (IObjectStorage)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
				typed.Shared = counter;
				return proxy;
			});
		});
		var set = await RecordingFinalizationHelper.FinalizeAsync(s.Coach, s.Hierarchy.MatchId, 2, ct);

		using var read = await s.Coach.SendAsync(HttpMethod.Get, $"/api/v1/matches/{s.Hierarchy.MatchId}/recording-set-versions/{set.RecordingSetVersionId}", null, ct);
		read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync(ct));
		var body = await read.Content.ReadFromJsonAsync<JsonElement>(ct);
		var apiMembers = body.GetProperty("members").EnumerateArray().ToList();

		Interlocked.Exchange(ref counter.Calls, 0);

		await using var scope = s.Factory.Services.CreateAsyncScope();
		var lookup = scope.ServiceProvider.GetRequiredService<IRecordingSetLookup>();
		var lineage = await lookup.GetAsync(set.RecordingSetVersionId, ct);

		lineage.ShouldNotBeNull();
		lineage.RecordingSetVersionId.ShouldBe(set.RecordingSetVersionId);
		lineage.MatchId.ShouldBe(s.Hierarchy.MatchId);
		lineage.TeamId.ShouldBe(Guid.Parse(body.GetProperty("teamId").GetString()!));
		lineage.FinalizedAt.ShouldBe(body.GetProperty("finalizedAt").GetDateTimeOffset());
		lineage.Members.Count.ShouldBe(apiMembers.Count);
		for (var i = 0; i < apiMembers.Count; i++)
		{
			var m = lineage.Members[i];
			m.Position.ShouldBe(apiMembers[i].GetProperty("position").GetInt32());
			m.RecordingVersionId.ShouldBe(set.RecordingVersionIds[i]);
			m.TimelineMappingId.ShouldBe(set.TimelineMappingIds[i]);
			m.RecordingContentDigest.ShouldBe(apiMembers[i].GetProperty("recordingContentDigest").GetString());
			m.RecordingContentDigest.ShouldStartWith("sha-256-parts:");
			m.TimelineMappingDigest.ShouldBe(apiMembers[i].GetProperty("timelineMappingDigest").GetString());
			m.Spans.ShouldBe([new TimelineSpan(0, 10000, 0)]);
		}

		(await lookup.GetAsync(Guid.NewGuid(), ct)).ShouldBeNull();
		counter.Calls.ShouldBe(0);
	}
}
