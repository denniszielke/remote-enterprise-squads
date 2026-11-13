using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteSquads.ControlPlane;
using RemoteSquads.Core;

internal static class CommandEndpointTests
{
    private const string Project = "project-fixture";
    private const string Run = "run-fixture";
    private const string Subject = "subject-fixture";
    private const string Client = "client-fixture";
    private const string Secret = "synthetic-secret-never-return-this";
    private const string Messages = "/v1/runs/" + Run + "/messages";

    public static IEnumerable<(string Name, Action Run)> Cases =>
    [
        ("Command HTTP authentication rejects absent expired and ambiguous identities before storage", Test(async () =>
        {
            await using var host = await Fixture.StartAsync();
            foreach (string? identity in new string?[]
                { null, "unrecognized", "expired", "missing-sub", "missing-client", "missing-exp",
                    "multiple-identities", "duplicate-sub", "expired-ticket" })
            {
                using var response = await host.SendAsync(HttpMethod.Post, Messages, Envelope(), identity);
                await ProblemAsync(response, 401, "authentication_required");
            }
            using var status = await host.SendAsync(HttpMethod.Get, StatusPath("command-" + new string('a', 64)),
                identity: "expired");
            await ProblemAsync(status, 401, "authentication_required");
            Check(host.Resolver.Calls == 0 && host.Payloads.Calls == 0 && host.Store.Accesses == 0,
                "Authentication must precede run resolution and both storage boundaries.");
        })),
        ("Command HTTP operator admission returns snake case receipt and viewer can only read status", Test(async () =>
        {
            await using var host = await Fixture.StartAsync();
            using var accepted = await host.SendAsync(HttpMethod.Post, Messages, Envelope(Secret));
            var receipt = await JsonAsync(accepted, 202);
            Check(receipt.GetProperty("schema_version").GetString() == "1.0", "Receipt schema.");
            Check(receipt.GetProperty("request_id").GetString() == "request-fixture", "Request correlation.");
            Check(receipt.GetProperty("project_id").GetString() == Project &&
                receipt.GetProperty("run_id").GetString() == Run, "Authorized receipt scope.");
            Check(receipt.GetProperty("status").GetString() == "accepted", "Wire status.");
            string command = receipt.GetProperty("command_id").GetString()!;
            string url = receipt.GetProperty("status_url").GetString()!;
            Check(url == StatusPath(command) && accepted.Headers.Location?.OriginalString == url, "Status location.");
            Check(receipt.EnumerateObject().All(property => !property.Name.Any(char.IsUpper)), "Snake case properties.");
            Check(host.Authorizer.Calls == 1 && host.Payloads.Calls == 1 && host.Store.Count == 1,
                "Real admission must authorize and persist one command.");
            Check(host.Store.Documents.Single().Body.GetProperty("payload_reference").GetString()!
                .StartsWith("fixture-protected-", StringComparison.Ordinal), "Protected payload reference.");
            Check(!host.Store.Documents.Single().Body.GetRawText().Contains(Secret, StringComparison.Ordinal),
                "Intent must not contain raw content.");

            using var status = await host.SendAsync(HttpMethod.Get, url, identity: "viewer");
            var state = await JsonAsync(status, 200);
            Check(state.GetProperty("command_id").GetString() == command &&
                state.GetProperty("status").GetString() == "accepted", "Persisted status.");
            Check(state.EnumerateObject().Select(property => property.Name).Order()
                .SequenceEqual(new[] { "command_id", "project_id", "request_id", "run_id", "schema_version", "status" }),
                "Status exposes only its public projection.");
            Check(!state.GetRawText().Contains(Secret, StringComparison.Ordinal) &&
                !state.GetRawText().Contains("fixture-protected-", StringComparison.Ordinal), "Status redacts content.");
            int accesses = host.Store.Accesses;
            using var denied = await host.SendAsync(HttpMethod.Post, Messages, Envelope(), "viewer", "viewer-key");
            await ProblemAsync(denied, 403, "forbidden");
            Check(host.Payloads.Calls == 1 && host.Store.Accesses == accesses && host.Authorizer.Calls == 1,
                "Viewer writes must stop before storage.");
        })),
        ("Command HTTP idempotency converges across retries but rejects semantic mismatch", Test(async () =>
        {
            await using var host = await Fixture.StartAsync();
            using var first = await host.SendAsync(HttpMethod.Post, Messages, Envelope());
            string command = (await JsonAsync(first, 202)).GetProperty("command_id").GetString()!;
            var retryBody = JsonNode.Parse(Envelope())!.AsObject();
            retryBody["request_id"] = "different-transport-request";
            using var retry = await host.SendAsync(HttpMethod.Post, Messages, retryBody.ToJsonString());
            var replay = await JsonAsync(retry, 202);
            Check(replay.GetProperty("command_id").GetString() == command &&
                replay.GetProperty("request_id").GetString() == "different-transport-request", "Retry identity.");
            using var mismatch = await host.SendAsync(HttpMethod.Post, Messages, Envelope("different semantic content"));
            await ProblemAsync(mismatch, 409, "idempotency_mismatch");
            Check(host.Store.Count == 1 && host.Store.Creates == 3 && host.Authorizer.Calls == 3,
                "A conflicting retry must not create another intent.");
        })),
        ("Command HTTP cross run access is non disclosing before payload or command storage", Test(async () =>
        {
            await using var host = await Fixture.StartAsync();
            using var created = await host.SendAsync(HttpMethod.Post, Messages, Envelope());
            string command = (await JsonAsync(created, 202)).GetProperty("command_id").GetString()!;
            int accesses = host.Store.Accesses;
            var other = JsonNode.Parse(Envelope())!.AsObject();
            other["run_id"] = "other-run";
            using var post = await host.SendAsync(HttpMethod.Post, "/v1/runs/other-run/messages", other.ToJsonString());
            await ProblemAsync(post, 404, "resource_not_found");
            using var get = await host.SendAsync(HttpMethod.Get, $"/v1/runs/other-run/commands/{command}", identity: "viewer");
            await ProblemAsync(get, 404, "resource_not_found");
            Check(host.Store.Accesses == accesses && host.Payloads.Calls == 1,
                "Cross-run denial must not touch storage.");
        })),
        ("Command HTTP rejects malformed enums versions scopes media and oversized bodies without intent", Test(async () =>
        {
            await using var host = await Fixture.StartAsync();
            var invalid = new List<(string Body, int Status, string Code)>
            {
                ("{", 400, "invalid_request"),
                ("null", 400, "invalid_request"),
                (Change(body => body["schema_version"] = "2.0"), 400, "unsupported_version"),
                (Change(body => body["project_id"] = "caller-project"), 409, "scope_mismatch"),
                (Change(body => body["run_id"] = "caller-run"), 409, "scope_mismatch"),
                (Change(body => body["operation"] = "steer"), 400, "invalid_request"),
                (Change(body => body["payload"]!["recipient"]!["kind"] = "broadcast"), 400, "invalid_request"),
                (Change(body => body["payload"]!["recipient"]!["kind"] = 0), 400, "invalid_request"),
                (Change(body => body["payload"]!["delivery_mode"] = "Immediate"), 400, "invalid_request"),
                (Change(body => body["payload"]!["delivery_mode"] = 1), 400, "invalid_request"),
                (Change(body => body["payload"]!["recipient"]!["kind"] = "member"), 400, "invalid_request"),
                (Change(body => body["payload"]!["recipient"]!["member_id"] = "untrusted-member"), 400, "invalid_request"),
                (Envelope(new string('x', 66000)), 413, "request_too_large")
            };
            foreach (var item in invalid)
            {
                using var response = await host.SendAsync(HttpMethod.Post, Messages, item.Body);
                await ProblemAsync(response, item.Status, item.Code);
            }
            using var media = await host.SendAsync(HttpMethod.Post, Messages, Envelope(), mediaType: "text/plain");
            await ProblemAsync(media, 415, "unsupported_media_type");
            using var missingKey = await host.SendAsync(HttpMethod.Post, Messages, Envelope(), key: null);
            await ProblemAsync(missingKey, 400, "invalid_request");
            using var invalidKey = await host.SendAsync(HttpMethod.Post, Messages, Envelope(), key: "key with spaces");
            await ProblemAsync(invalidKey, 400, "invalid_request");
            using var chunked = await host.SendAsync(HttpMethod.Post, Messages, Envelope(new string('x', 66000)),
                chunked: true);
            await ProblemAsync(chunked, 413, "request_too_large");
            Check(host.Payloads.Calls == 0 && host.Store.Accesses == 0 && host.Authorizer.Calls == 0,
                "Invalid input must never cross the protected storage boundary.");
        })),
        ("Command HTTP structured failures redact exception details and recheck admission authorization", Test(async () =>
        {
            await using var host = await Fixture.StartAsync();
            host.Payloads.FailureCode = "durability_unavailable";
            using var unavailable = await host.SendAsync(HttpMethod.Post, Messages, Envelope(Secret));
            var failure = await ProblemAsync(unavailable, 503, "durability_unavailable");
            Check(failure.GetProperty("retryable").GetBoolean(), "Durability failures are retryable.");
            Check(host.Store.Accesses == 0, "Failed protected storage must not commit an intent.");
            host.Payloads.FailureCode = "unrecognized-fixture-error";
            using var internalError = await host.SendAsync(HttpMethod.Post, Messages, Envelope(Secret));
            await ProblemAsync(internalError, 500, "internal_error");
            host.Payloads.FailureCode = null;
            host.Authorizer.Deny = true;
            using var revoked = await host.SendAsync(HttpMethod.Post, Messages, Envelope(Secret));
            await ProblemAsync(revoked, 403, "forbidden");
            Check(host.Authorizer.Calls == 1 && host.Store.Accesses == 0,
                "Mandatory admission authorization must recheck access after protected storage.");
        })),
        ("Command HTTP ignores caller supplied identity and role in body and headers", Test(async () =>
        {
            await using var host = await Fixture.StartAsync();
            string spoofed = Change(body =>
            {
                body["subject_id"] = "spoofed-subject";
                body["client_id"] = "spoofed-client";
                body["role"] = "administrator";
                body["actor"] = new JsonObject { ["sub"] = "spoofed-subject", ["azp"] = "spoofed-client" };
            });
            using var accepted = await host.SendAsync(HttpMethod.Post, Messages, spoofed, spoof: true);
            await JsonAsync(accepted, 202);
            var scope = host.Store.Documents.Single().Body.GetProperty("scope");
            Check(scope.GetProperty("subject_id").GetString() == Subject &&
                scope.GetProperty("client_id").GetString() == Client, "Scope must derive from verified claims only.");
            Check(host.Resolver.LastActor == new VerifiedActor(Subject, Client), "Resolver receives authenticated actor.");
            using var denied = await host.SendAsync(HttpMethod.Post, Messages, spoofed, "viewer", spoof: true);
            await ProblemAsync(denied, 403, "forbidden");
            Check(host.Store.Count == 1 && host.Payloads.Calls == 1, "Spoofed operator headers cannot elevate a viewer.");
        })),
        ("Command HTTP rejects missing null and duplicate envelope fields before protected storage", Test(async () =>
        {
            await using var host = await Fixture.StartAsync();
            var invalid = new[]
            {
                Change(body => body.Remove("request_id")),
                Change(body => body["request_id"] = null),
                Change(body => body["payload"] = null),
                Change(body => body["payload"]!["recipient"] = null),
                Change(body => body["payload"]!["text"] = null),
                Change(body => body["payload"]!.AsObject().Remove("delivery_mode")),
                Change(body => body["payload"]!["recipient"]!.AsObject().Remove("kind")),
                Envelope().Replace("\"request_id\":\"request-fixture\"",
                    "\"request_id\":\"first\",\"request_id\":\"request-fixture\"", StringComparison.Ordinal),
                Envelope().Replace("\"delivery_mode\":\"immediate\"",
                    "\"delivery_mode\":\"enqueue\",\"delivery_mode\":\"immediate\"", StringComparison.Ordinal)
            };
            foreach (string body in invalid)
            {
                using var response = await host.SendAsync(HttpMethod.Post, Messages, body);
                await ProblemAsync(response, 400, "invalid_request");
            }
            Check(host.Payloads.Calls == 0 && host.Store.Accesses == 0, "Ambiguous or incomplete input must not persist.");
        }))
    ];

    private static Action Test(Func<Task> run) => () =>
    {
        try { run().GetAwaiter().GetResult(); }
        catch (AssertionFailure) { throw; }
        catch (Exception error)
        {
            throw new AssertionFailure($"Command endpoint fixture failed ({error.GetType().Name}); no bind retry was attempted.");
        }
    };

    private static string StatusPath(string command) => $"/v1/runs/{Run}/commands/{command}";

    private static string Envelope(string text = "fixture-message") => JsonSerializer.Serialize(new
    {
        schema_version = "1.0", request_id = "request-fixture", project_id = Project, run_id = Run,
        operation = "message",
        payload = new { recipient = new { kind = "coordinator" }, delivery_mode = "immediate", text }
    });

    private static string Change(Action<JsonObject> change)
    {
        var body = JsonNode.Parse(Envelope())!.AsObject();
        change(body);
        return body.ToJsonString();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, int status)
    {
        Check((int)response.StatusCode == status,
            $"Expected HTTP {status}, received {(int)response.StatusCode}.");
        Check(response.Headers.CacheControl?.NoStore == true, "Command responses must not be cached.");
        string body = await response.Content.ReadAsStringAsync();
        Check(!body.Contains(Secret, StringComparison.Ordinal), "Response leaked synthetic protected content.");
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new AssertionFailure("Command endpoint did not return JSON."); }
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, int status, string code)
    {
        var body = await JsonAsync(response, status);
        Check(response.Content.Headers.ContentType?.MediaType == "application/problem+json", "Structured problem media type.");
        Check(body.GetProperty("schema_version").GetString() == "1.0" &&
            body.GetProperty("status").GetInt32() == status && body.GetProperty("code").GetString() == code,
            "Structured problem code and status.");
        foreach (string field in new[] { "request_id", "trace_id", "detail", "recovery" })
            Check(!string.IsNullOrWhiteSpace(body.GetProperty(field).GetString()), "Problem correlation and recovery.");
        Check(body.GetProperty("retryable").ValueKind is JsonValueKind.True or JsonValueKind.False, "Retryability flag.");
        Check(!body.GetRawText().Contains("Exception", StringComparison.Ordinal) &&
            !body.TryGetProperty("stack_trace", out _), "Problem must not expose exception internals.");
        return body;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new AssertionFailure(message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication application;
        private readonly HttpClient client;
        public FixtureResolver Resolver { get; } = new();
        public FixturePayloadStore Payloads { get; } = new();
        public FixtureDocumentStore Store { get; } = new();
        public FixtureAuthorizer Authorizer { get; } = new();

        private Fixture(WebApplication application, HttpClient client)
            => (this.application, this.client) = (application, client);

        public static async Task<Fixture> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Production",
                ApplicationName = typeof(CommandEndpoints).Assembly.GetName().Name,
                Args = []
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, FixtureAuthenticationHandler>(
                "fixture", _ => { });
            builder.Services.AddAuthorization();
            var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
                { Timeout = TimeSpan.FromSeconds(10) };
            Fixture? fixture = null;
            builder.Services.AddSingleton<IProjectRunAccessResolver>(_ => fixture!.Resolver);
            builder.Services.AddSingleton<IProtectedMessagePayloadStore>(_ => fixture!.Payloads);
            builder.Services.AddSingleton<IControlDocumentStore>(_ => fixture!.Store);
            builder.Services.AddSingleton<IMessageAdmissionAuthorizer>(_ => fixture!.Authorizer);
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddSingleton(services => new MessageAdmissionService(
                services.GetRequiredService<IControlDocumentStore>(),
                services.GetRequiredService<IMessageAdmissionAuthorizer>(),
                services.GetRequiredService<TimeProvider>(), TimeSpan.FromHours(1)));
            var app = builder.Build();
            fixture = new Fixture(app, client);
            app.MapSquadCommands("fixture");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await app.StartAsync(timeout.Token);
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
                Check(addresses is not null && addresses.Addresses.Count == 1, "One dynamically bound loopback endpoint.");
                var address = new Uri(addresses!.Addresses.Single());
                Check(address.Scheme == "http" && address.Host == "127.0.0.1" && address.Port > 0,
                    "Tests must bind only an ephemeral IPv4 loopback endpoint.");
                client.BaseAddress = address;
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body = null,
            string? identity = "operator", string? key = "key-fixture", string mediaType = "application/json",
            bool spoof = false, bool chunked = false)
        {
            using var request = new HttpRequestMessage(method, path);
            if (identity is not null) request.Headers.Add("X-Fixture-Identity", identity);
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            if (spoof)
            {
                request.Headers.Add("X-Subject-Id", "spoofed-subject");
                request.Headers.Add("X-Client-Id", "spoofed-client");
                request.Headers.Add("X-Project-Role", "administrator");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-not-an-identity");
            }
            if (body is not null)
                request.Content = chunked ? new ChunkedContent(body) : new StringContent(body, Encoding.UTF8, mediaType);
            return await client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await application.StopAsync(timeout.Token);
            }
            finally { await application.DisposeAsync(); }
        }
    }

    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] bytes;

        public ChunkedContent(string body)
        {
            bytes = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(bytes).AsTask();
    }

    private sealed class FixtureAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? token = Request.Headers["X-Fixture-Identity"].SingleOrDefault();
            if (token is null) return Task.FromResult(AuthenticateResult.NoResult());
            if (token is not ("operator" or "viewer" or "expired" or "missing-sub" or "missing-client" or
                "missing-exp" or "multiple-identities" or "duplicate-sub" or "expired-ticket"))
                return Task.FromResult(AuthenticateResult.Fail("Unknown synthetic fixture identity."));
            var claims = new List<Claim>();
            if (token != "missing-sub") claims.Add(new Claim("sub", token == "viewer" ? "viewer-fixture" : Subject));
            if (token != "missing-client") claims.Add(new Claim("azp", Client));
            if (token != "missing-exp")
                claims.Add(new Claim("exp", DateTimeOffset.UtcNow.AddMinutes(token == "expired" ? -5 : 10)
                    .ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
            if (token == "duplicate-sub") claims.Add(new Claim("sub", "second-subject"));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "fixture"));
            if (token == "multiple-identities")
                principal.AddIdentity(new ClaimsIdentity(claims, "second-fixture"));
            var properties = new AuthenticationProperties();
            if (token == "expired-ticket") properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, properties, "fixture")));
        }
    }

    private sealed class FixtureResolver : IProjectRunAccessResolver
    {
        public int Calls { get; private set; }
        public VerifiedActor? LastActor { get; private set; }

        public ValueTask<AuthorizedRun> ResolveAsync(VerifiedActor actor, string runId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastActor = actor;
            if (runId != Run || actor.ClientId != Client || actor.SubjectId is not (Subject or "viewer-fixture"))
                throw new DomainException("resource_not_found", Secret);
            return ValueTask.FromResult(new AuthorizedRun(Project, Run,
                actor.SubjectId == Subject ? ProjectRole.Operator : ProjectRole.Viewer,
                new PinnedTeam("team-fixture", ["member-fixture"]), 7));
        }
    }

    private sealed class FixturePayloadStore : IProtectedMessagePayloadStore
    {
        public int Calls { get; private set; }
        public string? FailureCode { get; set; }

        public ValueTask<ProtectedPayload> StoreAsync(string projectId, string runId, string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Check(projectId == Project && runId == Run, "Protected payload scope.");
            if (FailureCode is not null) throw new DomainException(FailureCode, Secret);
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            return ValueTask.FromResult(new ProtectedPayload("fixture-protected-" + hash, "sha256:" + hash));
        }
    }

    private sealed class FixtureAuthorizer : IMessageAdmissionAuthorizer
    {
        public int Calls { get; private set; }
        public bool Deny { get; set; }

        public ValueTask AuthorizeAsync(IdempotencyScope scope, MessageTarget target, long leaseGeneration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Deny || scope != new IdempotencyScope(Subject, Client, Project, "message", Run) ||
                target.TeamVersion != "team-fixture" || leaseGeneration != 7)
                throw new DomainException("forbidden", Secret);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixtureDocumentStore : IControlDocumentStore
    {
        private readonly ConcurrentDictionary<(string Partition, string Id), VersionedDocument> documents = new();
        private int accesses;
        private int creates;
        private long revision;
        public int Accesses => accesses;
        public int Creates => creates;
        public int Count => documents.Count;
        public IEnumerable<VersionedDocument> Documents => documents.Values;

        public ValueTask<bool> CreateAsync(string partitionKey, string id, JsonElement document,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref accesses);
            Interlocked.Increment(ref creates);
            return ValueTask.FromResult(documents.TryAdd((partitionKey, id),
                new VersionedDocument(document.Clone(), Interlocked.Increment(ref revision).ToString(CultureInfo.InvariantCulture))));
        }

        public ValueTask<VersionedDocument?> ReadAsync(string partitionKey, string id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref accesses);
            return ValueTask.FromResult(documents.TryGetValue((partitionKey, id), out var document) ? document : null);
        }

        public ValueTask<bool> ReplaceAsync(string partitionKey, string id, JsonElement document, string expectedETag,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref accesses);
            var key = (partitionKey, id);
            if (!documents.TryGetValue(key, out var current) || current.ETag != expectedETag)
                return ValueTask.FromResult(false);
            var next = new VersionedDocument(document.Clone(),
                Interlocked.Increment(ref revision).ToString(CultureInfo.InvariantCulture));
            return ValueTask.FromResult(documents.TryUpdate(key, next, current));
        }
    }
}
