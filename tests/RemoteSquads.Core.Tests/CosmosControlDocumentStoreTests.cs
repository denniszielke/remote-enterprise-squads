using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using RemoteSquads.Core;

public static class CosmosControlDocumentStoreTests
{
    private const string SyntheticToken = "synthetic.header.payload+signature/=";
    private const string Body = """{"id":"command-1","partition_key":"project-1","ttl":-1,"status":"accepted"}""";
    private static readonly Uri Endpoint = new("https://synthetic-account.documents.azure.com/");

    public static IEnumerable<(string Name, Action Run)> Cases =>
    [
        ("Cosmos create sends encoded Entra headers and unchanged create-only document", () =>
        {
            var handler = new FakeHandler(async (request, ct) =>
            {
                Equal(HttpMethod.Post, request.Method);
                Equal("https://synthetic-account.documents.azure.com/dbs/control/colls/commands/docs",
                    request.RequestUri!.AbsoluteUri);
                Equal("type=aad&ver=1.0&sig=" + SyntheticToken,
                    Uri.UnescapeDataString(Header(request, "Authorization")));
                Equal("2018-12-31", Header(request, "x-ms-version"));
                Check(DateTimeOffset.TryParseExact(Header(request, "x-ms-date"), "R",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
                Equal("""["project-1"]""", Header(request, "x-ms-documentdb-partitionkey"));
                Equal("false", Header(request, "x-ms-documentdb-is-upsert"));
                Equal("false", Header(request, "x-ms-cosmos-allow-tentative-writes"));
                Check(!request.Headers.Contains("If-Match") && !request.Headers.Contains("Cookie"));
                Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                Equal(Body, await request.Content.ReadAsStringAsync(ct));
                return Response(HttpStatusCode.Created);
            });
            using var store = Store(handler);
            Check(store.CreateAsync("project-1", "command-1", Document()).AsTask().GetAwaiter().GetResult());
            Equal(1, handler.Calls);
        }),
        ("Cosmos read preserves ETag and replace uses exact If-Match CAS", () =>
        {
            var handler = new FakeHandler((request, _) =>
            {
                Equal("https://synthetic-account.documents.azure.com/dbs/control/colls/commands/docs/command-1",
                    request.RequestUri!.AbsoluteUri);
                Equal("""["project-1"]""", Header(request, "x-ms-documentdb-partitionkey"));
                if (request.Method == HttpMethod.Get)
                {
                    Check(request.Content is null);
                    Equal("Strong", Header(request, "x-ms-consistency-level"));
                    Check(!request.Headers.Contains("x-ms-session-token"));
                    return Task.FromResult(Response(HttpStatusCode.OK, Body, "\"version-1\""));
                }
                Equal(HttpMethod.Put, request.Method);
                Equal("\"version-1\"", Header(request, "If-Match"));
                Check(!request.Headers.Contains("x-ms-documentdb-is-upsert"));
                return Task.FromResult(Response(HttpStatusCode.OK));
            });
            using var store = Store(handler);
            var recorded = store.ReadAsync("project-1", "command-1").AsTask().GetAwaiter().GetResult()!;
            Equal("\"version-1\"", recorded.ETag);
            Equal("accepted", recorded.Body.GetProperty("status").GetString());
            Check(store.ReplaceAsync("project-1", "command-1", recorded.Body, recorded.ETag)
                .AsTask().GetAwaiter().GetResult());
            Equal(2, handler.Calls);
        }),
        ("Cosmos expected statuses are operation-specific, not interchangeable conflicts", () =>
        {
            foreach (var status in new[] { HttpStatusCode.Conflict, HttpStatusCode.PreconditionFailed })
            {
                var handler = Fixed(status);
                using var store = Store(handler);
                if (status == HttpStatusCode.Conflict)
                {
                    Check(!Create(store));
                    Error("durability_unavailable", () => Replace(store));
                }
                else
                {
                    Error("durability_unavailable", () => Create(store));
                    Check(!Replace(store));
                }
                Equal(2, handler.Calls);
            }
            using var missing = Store(Fixed(HttpStatusCode.NotFound));
            Check(missing.ReadAsync("project-1", "command-1").AsTask().GetAwaiter().GetResult() is null);
        }),
        ("Cosmos failures do not retry or expose response bodies and credentials", () =>
        {
            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden,
                HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.RequestTimeout })
            {
                var handler = Fixed(status, SyntheticToken + Endpoint);
                using var store = Store(handler);
                Error((int)status >= 500 || status == HttpStatusCode.RequestTimeout
                    ? "reconciliation_required" : "durability_unavailable", () => Create(store));
                Error("durability_unavailable", () => Read(store));
                Equal(2, handler.Calls);
            }
            var network = new FakeHandler((_, _) => throw new HttpRequestException(SyntheticToken + Endpoint));
            using var disconnected = Store(network);
            Error("reconciliation_required", () => Replace(disconnected));
            Error("durability_unavailable", () => Read(disconnected));
            Equal(2, network.Calls);
        }),
        ("Cosmos bounds transport deadlines and preserves explicit caller cancellation", () =>
        {
            var stalled = new FakeHandler(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Response(HttpStatusCode.OK);
            });
            using var store = Store(stalled, TimeSpan.FromMilliseconds(30));
            Error("reconciliation_required", () => Create(store));
            Error("durability_unavailable", () => Read(store));
            Equal(2, stalled.Calls);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try
            {
                store.CreateAsync("project-1", "command-1", Document(), canceled.Token)
                    .AsTask().GetAwaiter().GetResult();
                throw new AssertionFailure("Expected caller cancellation.");
            }
            catch (OperationCanceledException) { }
            Equal(2, stalled.Calls);
            using var inFlight = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            try
            {
                store.ReplaceAsync("project-1", "command-1", Document(), "\"version-1\"", inFlight.Token)
                    .AsTask().GetAwaiter().GetResult();
                throw new AssertionFailure("Expected in-flight caller cancellation.");
            }
            catch (OperationCanceledException) when (inFlight.IsCancellationRequested) { }
            Equal(3, stalled.Calls);
        }),
        ("Cosmos bounds token acquisition and never sends after token failure", () =>
        {
            var handler = Fixed(HttpStatusCode.Created);
            foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1), TimeSpan.FromMinutes(3) })
                Error("durability_unavailable", () => Store(handler, timeout));
            foreach (var maximum in new[] { 0, -1, 2 * 1024 * 1024 + 1 })
                Error("durability_unavailable", () => Store(handler, maxBytes: maximum));
            using var store = new CosmosControlDocumentStore(Endpoint, "control", "commands",
                async ct =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return SyntheticToken;
                }, handler, TimeSpan.FromMilliseconds(30));
            Error("durability_unavailable", () => Create(store));
            Equal(0, handler.Calls);
            using var noToken = new CosmosControlDocumentStore(Endpoint, "control", "commands",
                _ => ValueTask.FromResult(""), new FakeHandler((_, _) => throw new AssertionFailure("No network allowed.")));
            Error("durability_unavailable", () => Read(noToken));
            using var invalidProvider = new CosmosControlDocumentStore(Endpoint, "control", "commands",
                _ => throw new InvalidOperationException(SyntheticToken + Endpoint), handler);
            Error("durability_unavailable", () => Create(invalidProvider));
            Equal(0, handler.Calls);
        }),
        ("Cosmos refuses all redirects without following their credential target", () =>
        {
            foreach (var status in new[] { 301, 302, 303, 307, 308 })
            {
                var handler = new FakeHandler((_, _) =>
                {
                    var response = Response((HttpStatusCode)status, SyntheticToken);
                    response.Headers.Location = new Uri("https://untrusted.invalid/collect");
                    return Task.FromResult(response);
                });
                using var store = Store(handler);
                Error("durability_unavailable", () => Create(store));
                Error("durability_unavailable", () => Read(store));
                Equal(2, handler.Calls);
            }
        }),
        ("Cosmos rejects malformed, mismatched and oversized successful responses", () =>
        {
            foreach (var body in new[] { "", "{", "null", "[]",
                """{"id":"other","partition_key":"project-1"}""",
                """{"id":"command-1","partition_key":"other"}""",
                """{"id":"other","id":"command-1","partition_key":"project-1"}""",
                """{"id":"command-1","partition_key":"project-1","ttl":1}""",
                """{"id":"command-1","partition_key":"project-1","_etag":"other"}""" })
            {
                using var store = Store(Fixed(HttpStatusCode.OK, body));
                Error("reconciliation_required", () => Read(store));
            }
            var oversized = new string(' ', 129);
            using var limited = Store(Fixed(HttpStatusCode.OK, oversized), maxBytes: 128);
            Error("reconciliation_required", () => Read(limited));
            using var chunked = Store(new FakeHandler((_, _) =>
            {
                var response = Response(HttpStatusCode.OK);
                response.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(oversized)));
                return Task.FromResult(response);
            }), maxBytes: 128);
            Error("reconciliation_required", () => Read(chunked));
            using var invalidWrite = Store(Fixed(HttpStatusCode.Created, "{"));
            Error("reconciliation_required", () => Create(invalidWrite));
        }),
        ("Cosmos requires a concrete unambiguous ETag from reads and for replacements", () =>
        {
            foreach (var etag in new[] { "", "*", "W/\"weak\"", "\"\"", "\"bad\r\nheader\"" })
            {
                using var store = Store(Fixed(HttpStatusCode.OK, Body, etag));
                Error("reconciliation_required", () => Read(store));
                Error("version_conflict", () => store.ReplaceAsync("project-1", "command-1",
                    Document(), etag).AsTask().GetAwaiter().GetResult());
            }
            using var missing = Store(new FakeHandler((_, _) =>
            {
                var response = Response(HttpStatusCode.OK);
                response.Headers.Remove("ETag");
                return Task.FromResult(response);
            }));
            Error("reconciliation_required", () => Read(missing));
        }),
        ("Cosmos validates trusted configuration and document bindings before any network", () =>
        {
            var handler = Fixed(HttpStatusCode.Created);
            foreach (var endpoint in new[] { "http://synthetic-account.documents.azure.com/",
                "https://synthetic-account.documents.azure.com/path", "https://untrusted.invalid/",
                "https://synthetic-account.documents.azure.com/?query=1",
                "https://user@synthetic-account.documents.azure.com/",
                "https://synthetic-account.documents.azure.com:444/",
                "https://synthetic-account.documents.azure.com/#fragment",
                "https://synthetic-account.documents.azure.com/path/..",
                "https://nested.account.documents.azure.com/" })
                Error("durability_unavailable", () => new CosmosControlDocumentStore(new Uri(endpoint),
                    "control", "commands", _ => ValueTask.FromResult(SyntheticToken), handler));
            foreach (var id in new[] { "", " ", ".", "..", "a/b", "a\\b", "a?b", "a#b", "a%2fb", new string('x', 256) })
            {
                Error("durability_unavailable", () => new CosmosControlDocumentStore(Endpoint,
                    id, "commands", _ => ValueTask.FromResult(SyntheticToken), handler));
                Error("durability_unavailable", () => new CosmosControlDocumentStore(Endpoint,
                    "control", id, _ => ValueTask.FromResult(SyntheticToken), handler));
                using var store = Store(handler);
                Error("durability_unavailable", () => store.ReadAsync("project-1", id)
                    .AsTask().GetAwaiter().GetResult());
            }
            using var valid = Store(handler);
            foreach (var body in new[] { "{}", "null", Body.Replace("command-1", "other"),
                Body.Replace("project-1", "other"), Body.Replace("\"ttl\":-1", "\"ttl\":60"),
                """{"id":"other","id":"command-1","partition_key":"project-1"}""" })
                Error("durability_unavailable", () => valid.CreateAsync("project-1", "command-1",
                    JsonDocument.Parse(body).RootElement).AsTask().GetAwaiter().GetResult());
            foreach (var key in new[] { "", " ", "bad\nkey", new string('x', 2049) })
                Error("durability_unavailable", () => valid.ReadAsync(key, "command-1")
                    .AsTask().GetAwaiter().GetResult());
            Equal(0, handler.Calls);
        }),
        ("Cosmos safely serializes partition header values without changing bindings", () =>
        {
            const string partition = "project-\"quoted\"\\segment";
            var body = JsonSerializer.SerializeToElement(new { id = "command-1", partition_key = partition, ttl = -1 });
            var handler = new FakeHandler((request, _) =>
            {
                var keys = JsonSerializer.Deserialize<string[]>(Header(request, "x-ms-documentdb-partitionkey"))!;
                Equal(partition, keys.Single());
                return Task.FromResult(Response(HttpStatusCode.Created, body.GetRawText()));
            });
            using var store = Store(handler);
            Check(store.CreateAsync(partition, "command-1", body).AsTask().GetAwaiter().GetResult());
            Equal(1, handler.Calls);
        })
    ];

    private static CosmosControlDocumentStore Store(FakeHandler handler, TimeSpan? timeout = null,
        int maxBytes = 2 * 1024 * 1024)
        => new(Endpoint, "control", "commands", _ => ValueTask.FromResult(SyntheticToken),
            handler, timeout, maxBytes);

    private static JsonElement Document()
    {
        using var document = JsonDocument.Parse(Body);
        return document.RootElement.Clone();
    }

    private static bool Create(CosmosControlDocumentStore store)
        => store.CreateAsync("project-1", "command-1", Document()).AsTask().GetAwaiter().GetResult();

    private static bool Replace(CosmosControlDocumentStore store)
        => store.ReplaceAsync("project-1", "command-1", Document(), "\"version-1\"")
            .AsTask().GetAwaiter().GetResult();

    private static VersionedDocument? Read(CosmosControlDocumentStore store)
        => store.ReadAsync("project-1", "command-1").AsTask().GetAwaiter().GetResult();

    private static FakeHandler Fixed(HttpStatusCode status, string body = Body, string etag = "\"version-1\"")
        => new((_, _) => Task.FromResult(Response(status, body, etag)));

    private static HttpResponseMessage Response(HttpStatusCode status, string body = Body,
        string etag = "\"version-1\"")
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }

    private static string Header(HttpRequestMessage request, string name)
        => request.Headers.GetValues(name).Single();

    private static void Check(bool condition)
    {
        if (!condition) throw new AssertionFailure("Cosmos assertion failed.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionFailure("Cosmos values did not match.");
    }

    private static void Error(string code, Action action)
    {
        try { action(); }
        catch (DomainException exception)
        {
            Equal(code, exception.Code);
            Check(!exception.ToString().Contains(SyntheticToken, StringComparison.Ordinal));
            Check(!exception.ToString().Contains(Endpoint.AbsoluteUri, StringComparison.Ordinal));
            return;
        }
        throw new AssertionFailure("Expected safe Cosmos domain failure.");
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}
