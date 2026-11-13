using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RemoteSquads.Core;

/// <summary>
/// Single-account REST transport for a pre-provisioned /partition_key container.
/// Deployment must verify single-write-region topology, Strong account consistency and disabled container TTL.
/// Strong reads deliberately avoid process-local session tokens, which cannot fence restarted clients.
/// </summary>
public sealed class CosmosControlDocumentStore : IControlDocumentStore, IDisposable
{
    private const int MaximumDocumentBytes = 2 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly Uri documents;
    private readonly Func<CancellationToken, ValueTask<string>> accessTokenProvider;
    private readonly TimeSpan requestTimeout;
    private readonly int maxResponseBytes;

    /// <param name="endpoint">Trusted commercial Azure Cosmos account HTTPS root; never caller input.</param>
    /// <param name="accessTokenProvider">Host-owned Entra provider for the Cosmos data-plane audience.</param>
    /// <param name="handler">Trusted test transport only; production must use the owned default transport.</param>
    public CosmosControlDocumentStore(Uri endpoint, string databaseId, string containerId,
        Func<CancellationToken, ValueTask<string>> accessTokenProvider,
        HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null,
        int maxResponseBytes = MaximumDocumentBytes)
    {
        Require(ValidEndpoint(endpoint) && ValidIdentifier(databaseId) && ValidIdentifier(containerId),
            "durability_unavailable");
        Require(accessTokenProvider is not null, "durability_unavailable");
        var timeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        Require(timeout > TimeSpan.Zero && timeout <= TimeSpan.FromMinutes(2) &&
            maxResponseBytes > 0 && maxResponseBytes <= MaximumDocumentBytes, "durability_unavailable");
        this.accessTokenProvider = accessTokenProvider!;
        this.requestTimeout = timeout;
        this.maxResponseBytes = maxResponseBytes;
        documents = new Uri(endpoint, $"dbs/{databaseId}/colls/{containerId}/docs");
        client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = timeout,
            MaxResponseHeadersLength = 16
        }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public ValueTask<bool> CreateAsync(string partitionKey, string id, JsonElement document,
        CancellationToken cancellationToken = default)
    {
        ValidateAddress(partitionKey, id);
        ValidateBinding(document, partitionKey, id, "durability_unavailable");
        return WriteAsync(HttpMethod.Post, partitionKey, id, document, null, cancellationToken);
    }

    public async ValueTask<VersionedDocument?> ReadAsync(string partitionKey, string id,
        CancellationToken cancellationToken = default)
    {
        ValidateAddress(partitionKey, id);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);
        try
        {
            using var request = await RequestAsync(HttpMethod.Get, partitionKey, id, deadline.Token);
            // A weaker account rejects this request rather than silently permitting stale reads.
            request.Headers.Add("x-ms-consistency-level", "Strong");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).WaitAsync(deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            Require(response.StatusCode == HttpStatusCode.OK, "durability_unavailable");
            var body = await ReadBodyAsync(response, deadline.Token);
            ValidateBinding(body, partitionKey, id, "reconciliation_required");
            var etag = ReadETag(response);
            if (body.TryGetProperty("_etag", out var bodyETag))
                Require(bodyETag.ValueKind == JsonValueKind.String && bodyETag.GetString() == etag,
                    "reconciliation_required");
            return new(body, etag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw Failure("durability_unavailable");
        }
        catch (HttpRequestException)
        {
            throw Failure("durability_unavailable");
        }
        catch (IOException)
        {
            throw Failure("durability_unavailable");
        }
    }

    public ValueTask<bool> ReplaceAsync(string partitionKey, string id, JsonElement document,
        string expectedETag, CancellationToken cancellationToken = default)
    {
        ValidateAddress(partitionKey, id);
        ValidateBinding(document, partitionKey, id, "durability_unavailable");
        Require(ValidETag(expectedETag), "version_conflict");
        return WriteAsync(HttpMethod.Put, partitionKey, id, document, expectedETag, cancellationToken);
    }

    private async ValueTask<bool> WriteAsync(HttpMethod method, string partitionKey, string id,
        JsonElement document, string? etag, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        Require(bytes.Length <= MaximumDocumentBytes, "durability_unavailable");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);
        var sent = false;
        try
        {
            using var request = await RequestAsync(method, partitionKey, id, deadline.Token);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("x-ms-cosmos-allow-tentative-writes", "false");
            if (etag is null) request.Headers.Add("x-ms-documentdb-is-upsert", "false");
            else request.Headers.IfMatch.Add(new EntityTagHeaderValue(etag));
            sent = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).WaitAsync(deadline.Token);
            if (method == HttpMethod.Post && response.StatusCode == HttpStatusCode.Conflict) return false;
            if (method == HttpMethod.Put && response.StatusCode == HttpStatusCode.PreconditionFailed) return false;
            if (response.StatusCode == (method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK))
            {
                var body = await ReadBodyAsync(response, deadline.Token);
                ValidateBinding(body, partitionKey, id, "reconciliation_required");
                return true;
            }
            // A response cannot establish whether a failed gateway request reached the writer.
            Require((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.RequestTimeout,
                "reconciliation_required");
            throw Failure("durability_unavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw Failure(sent ? "reconciliation_required" : "durability_unavailable");
        }
        catch (HttpRequestException)
        {
            throw Failure(sent ? "reconciliation_required" : "durability_unavailable");
        }
        catch (IOException)
        {
            throw Failure(sent ? "reconciliation_required" : "durability_unavailable");
        }
    }

    private async ValueTask<HttpRequestMessage> RequestAsync(HttpMethod method, string partitionKey,
        string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string token;
        try
        {
            token = await accessTokenProvider(cancellationToken).AsTask().WaitAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw Failure("durability_unavailable");
        }
        catch (ArgumentException)
        {
            throw Failure("durability_unavailable");
        }
        Require(!string.IsNullOrWhiteSpace(token) && token.Length <= 32768 &&
            token.All(c => c > ' ' && c < 127 && c != '&'), "durability_unavailable");
        var request = new HttpRequestMessage(method,
            method == HttpMethod.Post ? documents : new Uri(documents.AbsoluteUri + "/" + id));
        request.Headers.Add("Authorization", Uri.EscapeDataString("type=aad&ver=1.0&sig=" + token));
        request.Headers.Add("x-ms-date", DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture));
        request.Headers.Add("x-ms-version", "2018-12-31");
        request.Headers.Add("x-ms-documentdb-partitionkey", JsonSerializer.Serialize(new[] { partitionKey }));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async ValueTask<JsonElement> ReadBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        Require(response.Content.Headers.ContentLength is not long length || length <= maxResponseBytes,
            "reconciliation_required");
        await using var stream = await response.Content.ReadAsStreamAsync(token).WaitAsync(token);
        using var buffer = new MemoryStream();
        var block = new byte[Math.Min(8192, maxResponseBytes + 1)];
        while (true)
        {
            var count = await stream.ReadAsync(block.AsMemory(0,
                (int)Math.Min(block.Length, maxResponseBytes + 1L - buffer.Length)), token).AsTask().WaitAsync(token);
            if (count == 0) break;
            buffer.Write(block, 0, count);
            Require(buffer.Length <= maxResponseBytes, "reconciliation_required");
        }
        try
        {
            using var json = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length),
                new JsonDocumentOptions { MaxDepth = 64 });
            return json.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw Failure("reconciliation_required");
        }
    }

    private static string ReadETag(HttpResponseMessage response)
    {
        Require(response.Headers.TryGetValues("ETag", out var values), "reconciliation_required");
        var tags = values!.Take(2).ToArray();
        Require(tags.Length == 1 && ValidETag(tags[0]), "reconciliation_required");
        return tags[0];
    }

    private static void ValidateBinding(JsonElement document, string partition, string id, string code)
    {
        Require(document.ValueKind == JsonValueKind.Object, code);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.EnumerateObject())
            Require(names.Add(property.Name), code);
        Require(document.TryGetProperty("id", out var documentId) &&
            documentId.ValueKind == JsonValueKind.String && documentId.GetString() == id &&
            document.TryGetProperty("partition_key", out var key) &&
            key.ValueKind == JsonValueKind.String && key.GetString() == partition, code);
        // Never introduce expiry into a retained control record or tombstone.
        if (document.TryGetProperty("ttl", out var ttl))
            Require(ttl.ValueKind == JsonValueKind.Number && ttl.TryGetInt32(out var seconds) && seconds == -1, code);
    }

    private static void ValidateAddress(string partition, string id)
        => Require(ValidIdentifier(id) && !string.IsNullOrWhiteSpace(partition) &&
            Encoding.UTF8.GetByteCount(partition) <= 2048 && !partition.Any(char.IsControl),
            "durability_unavailable");

    private static bool ValidIdentifier(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= 255 && value is not "." and not ".." &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static bool ValidEndpoint(Uri? endpoint)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.Port != 443 || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 ||
            endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/" ||
            endpoint.OriginalString.Contains('\\')) return false;
        var authority = endpoint.GetLeftPart(UriPartial.Authority);
        if (!string.Equals(endpoint.OriginalString, authority, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(endpoint.OriginalString, authority + "/", StringComparison.OrdinalIgnoreCase)) return false;
        const string suffix = ".documents.azure.com";
        var host = endpoint.IdnHost;
        if (!host.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var account = host[..^suffix.Length];
        return account.Length is >= 1 and <= 63 &&
            account.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
            account[0] != '-' && account[^1] != '-';
    }

    private static bool ValidETag(string? etag)
        => etag is { Length: >= 3 and <= 256 } && etag[0] == '"' && etag[^1] == '"' &&
            etag.AsSpan(1, etag.Length - 2).IndexOfAnyExceptInRange((char)0x21, (char)0x7e) < 0 &&
            !etag.AsSpan(1, etag.Length - 2).Contains('"');

    private static void Require(bool condition, string code)
    {
        if (!condition) throw Failure(code);
    }

    private static DomainException Failure(string code)
        => new(code, code == "reconciliation_required"
            ? "The control document outcome must be reconciled before further action."
            : code == "version_conflict" ? "A concrete control document version is required."
            : "The durable control document store is unavailable.");

    public void Dispose() => client.Dispose();
}
