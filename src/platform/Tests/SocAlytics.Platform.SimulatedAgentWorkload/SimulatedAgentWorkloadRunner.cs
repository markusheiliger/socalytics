using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.NKeys;

namespace SocAlytics.Platform.SimulatedAgentWorkload;

/// <summary>
/// Development and Test stand-in for an agent runtime. It speaks the real workload protocol: DPoP-bound workload tokens,
/// optional queue credentials, pulled work notifications, claims, and completions with deterministic advice.
/// </summary>
public sealed class SimulatedAgentWorkloadRunner : IAsyncDisposable
{
	private const string TokenPath = "/api/v1/agent-orchestration/workload/token";
	private const string QueueCredentialsPath = "/api/v1/agent-orchestration/workload/queue-credentials";
	private const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

	private readonly SimulatedAgentWorkloadOptions _options;
	private readonly HttpClient _http;
	private readonly TimeProvider _time;
	private readonly ILogger? _logger;
	private readonly ECDsa _key = ECDsa.Create();
	private readonly string _origin;

	private string? _accessToken;
	private DateTimeOffset _tokenExpiresAt;
	private NatsConnection? _connection;
	private NatsJSContext? _js;
	private string _stream;
	private string _consumer;
	private DateTimeOffset _queueExpiresAt;

	public SimulatedAgentWorkloadRunner(SimulatedAgentWorkloadOptions options, HttpClient http, TimeProvider time, ILogger? logger = null)
	{
		ArgumentNullException.ThrowIfNull(options);
		_options = options;
		_http = http;
		_time = time;
		_logger = logger;
		_stream = options.Stream;
		_consumer = options.Consumer;
		_origin = options.ApiBaseAddress.TrimEnd('/');
		_key.ImportPkcs8PrivateKey(Convert.FromBase64String(options.PrivateKey), out _);
	}

	/// <summary>Processes notifications until cancelled; transient failures are logged and retried after a delay.</summary>
	public async Task RunAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await ProcessNextAsync(_options.FetchExpiry, cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception exception)
			{
				_logger?.LogWarning(exception, "The simulated agent workload failed an iteration and retries.");
				await DisposeConnectionAsync();
				try
				{
					await Task.Delay(_options.RetryDelay, _time, cancellationToken);
				}
				catch (OperationCanceledException)
				{
					return;
				}
			}
		}
	}

	/// <summary>Pulls at most one notification and handles it; false when none arrived within <paramref name="expires"/>.</summary>
	public async Task<bool> ProcessNextAsync(TimeSpan expires, CancellationToken cancellationToken)
	{
		var js = await EnsureQueueAsync(cancellationToken);
		var consumer = await js.GetConsumerAsync(_stream, _consumer, cancellationToken);
		await foreach (var message in consumer.FetchAsync(
			new NatsJSFetchOpts { MaxMsgs = 1, Expires = expires }, NatsRawSerializer<byte[]>.Default, cancellationToken))
		{
			if (await HandleAsync(message.Data ?? [], cancellationToken))
			{
				await message.AckAsync(cancellationToken: cancellationToken);
			}

			return true;
		}

		return false;
	}

	public ValueTask DisposeAsync()
	{
		_key.Dispose();
		return DisposeConnectionAsync();
	}

	// Returns whether the notification is settled and can be acknowledged; transient failures leave it for redelivery.
	private async Task<bool> HandleAsync(byte[] data, CancellationToken cancellationToken)
	{
		Guid invocationId;
		try
		{
			using var notification = JsonDocument.Parse(data);
			invocationId = notification.RootElement.GetProperty("invocationId").GetGuid();
		}
		catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
		{
			_logger?.LogWarning("The simulated agent workload dropped a malformed notification.");
			return true;
		}

		using var claim = await SendAsync(
			HttpMethod.Post, $"/api/v1/agent-orchestration/invocations/{invocationId}/claims",
			new { operationId = NewOperationId() }, cancellationToken);
		if (claim.StatusCode == HttpStatusCode.Conflict || claim.StatusCode == HttpStatusCode.NotFound)
		{
			// An obsolete claim means another attempt or the platform already settled the invocation.
			return true;
		}

		if (!claim.IsSuccessStatusCode)
		{
			_logger?.LogWarning("The claim of {InvocationId} answered {Status}.", invocationId, (int)claim.StatusCode);
			return false;
		}

		var envelope = await claim.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
		var attemptId = envelope.GetProperty("attemptId").GetGuid();
		var agentId = envelope.GetProperty("agentId").GetString()!;
		var fencingToken = envelope.GetProperty("fencingToken").GetString()!;

		using var completion = await SendAsync(
			HttpMethod.Post, $"/api/v1/agent-orchestration/attempts/{attemptId}/completion",
			new
			{
				operationId = NewOperationId(),
				fencingToken,
				advice = $"Simulated advice from {agentId}",
				executionReferences = new
				{
					runtime = "simulated",
					modelProvider = "simulated",
					model = "simulated",
					deployment = "simulated",
					promptRef = "simulated",
					toolPolicyRef = "simulated",
					safetyPolicyRef = "simulated",
				},
			},
			cancellationToken);
		if (completion.IsSuccessStatusCode || completion.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
		{
			return true;
		}

		_logger?.LogWarning("The completion of {InvocationId} answered {Status}.", invocationId, (int)completion.StatusCode);
		return false;
	}

	private async Task<NatsJSContext> EnsureQueueAsync(CancellationToken cancellationToken)
	{
		var now = _time.GetUtcNow();
		if (_js is not null && (_queueExpiresAt == default || now < _queueExpiresAt - (_queueExpiresAt - now) / 3))
		{
			return _js;
		}

		await DisposeConnectionAsync();
		var url = _options.NatsUrl;
		var opts = NatsOpts.Default with { Name = "socalytics-simulated-agent-workload", ConnectTimeout = TimeSpan.FromSeconds(5) };
		_queueExpiresAt = default;

		using var response = await SendAsync(HttpMethod.Post, QueueCredentialsPath, null, cancellationToken, queueRequest: true);
		if (response.IsSuccessStatusCode)
		{
			var credentials = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
			var userKey = _userKey!;
			url = credentials.GetProperty("queueUrl").GetString()!;
			_stream = credentials.GetProperty("stream").GetString()!;
			_consumer = credentials.GetProperty("consumer").GetString()!;
			_queueExpiresAt = credentials.GetProperty("expiresAt").GetDateTimeOffset();
			opts = opts with
			{
				InboxPrefix = credentials.GetProperty("inboxPrefix").GetString()!,
				AuthOpts = new NatsAuthOpts { Jwt = credentials.GetProperty("userJwt").GetString()!, Seed = userKey.GetSeed() },
			};
		}
		else if (response.StatusCode != HttpStatusCode.Conflict)
		{
			response.EnsureSuccessStatusCode();
		}

		_userKey?.Dispose();
		_userKey = null;
		_connection = new NatsConnection(opts with { Url = url });
		await _connection.ConnectAsync();
		_js = new NatsJSContext(_connection);
		return _js;
	}

	private KeyPair? _userKey;

	private async ValueTask DisposeConnectionAsync()
	{
		_js = null;
		var connection = _connection;
		_connection = null;
		if (connection is not null)
		{
			await connection.DisposeAsync();
		}
	}

	private async Task<HttpResponseMessage> SendAsync(
		HttpMethod method, string path, object? body, CancellationToken cancellationToken, bool queueRequest = false)
	{
		var token = await EnsureTokenAsync(cancellationToken);
		if (queueRequest)
		{
			_userKey?.Dispose();
			_userKey = KeyPair.CreatePair(PrefixByte.User);
			body = new { userPublicKey = _userKey.GetPublicKey() };
		}

		using var request = new HttpRequestMessage(method, Absolute(path));
		request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
		request.Headers.Add("DPoP", Proof(method.Method, path, token));
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		return await _http.SendAsync(request, cancellationToken);
	}

	private async Task<string> EnsureTokenAsync(CancellationToken cancellationToken)
	{
		var now = _time.GetUtcNow();
		if (_accessToken is not null && now < _tokenExpiresAt - TimeSpan.FromSeconds(30))
		{
			return _accessToken;
		}

		var form = new Dictionary<string, string>
		{
			["grant_type"] = "client_credentials",
			["client_id"] = _options.ClientId,
			["client_assertion_type"] = AssertionType,
			["client_assertion"] = Assertion(now),
			["scope"] = "agent-workload",
		};
		using var request = new HttpRequestMessage(HttpMethod.Post, Absolute(TokenPath)) { Content = new FormUrlEncodedContent(form) };
		request.Headers.Add("DPoP", Proof("POST", TokenPath, null));
		using var response = await _http.SendAsync(request, cancellationToken);
		response.EnsureSuccessStatusCode();
		var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
		_accessToken = body.GetProperty("access_token").GetString()!;
		_tokenExpiresAt = now + TimeSpan.FromSeconds(body.GetProperty("expires_in").GetInt32());
		return _accessToken;
	}

	private Uri Absolute(string path) => _http.BaseAddress is null ? new Uri(_origin + path) : new Uri(path, UriKind.Relative);

	private string Assertion(DateTimeOffset now) =>
		Sign(
			new Dictionary<string, object> { ["alg"] = "ES256" },
			new Dictionary<string, object>
			{
				["iss"] = _options.ClientId,
				["sub"] = _options.ClientId,
				["aud"] = _origin + TokenPath,
				["iat"] = now.ToUnixTimeSeconds(),
				["exp"] = (now + TimeSpan.FromSeconds(60)).ToUnixTimeSeconds(),
				["jti"] = Guid.NewGuid().ToString("N"),
			});

	private string Proof(string method, string path, string? accessToken)
	{
		var parameters = _key.ExportParameters(false);
		var claims = new Dictionary<string, object>
		{
			["htm"] = method,
			["htu"] = _origin + path,
			["iat"] = _time.GetUtcNow().ToUnixTimeSeconds(),
			["jti"] = Guid.NewGuid().ToString("N"),
		};
		if (accessToken is not null)
		{
			claims["ath"] = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));
		}

		return Sign(
			new Dictionary<string, object>
			{
				["typ"] = "dpop+jwt",
				["alg"] = "ES256",
				["jwk"] = new Dictionary<string, object>
				{
					["kty"] = "EC",
					["crv"] = "P-256",
					["x"] = Base64Url.EncodeToString(parameters.Q.X!),
					["y"] = Base64Url.EncodeToString(parameters.Q.Y!),
				},
			},
			claims);
	}

	private string Sign(Dictionary<string, object> header, Dictionary<string, object> claims)
	{
		var input = $"{Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(header))}.{Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(claims))}";
		var signature = _key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256);
		return $"{input}.{Base64Url.EncodeToString(signature)}";
	}

	private static string NewOperationId() => Guid.NewGuid().ToString("N");
}
