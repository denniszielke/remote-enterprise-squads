using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteSquads.Core;

namespace RemoteSquads.ControlPlane;

public enum ProjectRole { Viewer, Operator, Administrator }
public sealed record VerifiedActor(string SubjectId, string ClientId);
public sealed record AuthorizedRun(string ProjectId, string RunId, ProjectRole Role, PinnedTeam Team, long LeaseGeneration);
public sealed record ProtectedPayload(string Reference, string Digest);

public interface IProjectRunAccessResolver
{
    // Resolve ownership and current role from server-side records; inaccessible runs return non-disclosing 404.
    ValueTask<AuthorizedRun> ResolveAsync(VerifiedActor actor, string runId, CancellationToken cancellationToken);
}

public interface IProtectedMessagePayloadStore
{
    // The host must enforce approved privacy, retention and scoped access before persisting content.
    ValueTask<ProtectedPayload> StoreAsync(string projectId, string runId, string text, CancellationToken cancellationToken);
}

public static class CommandEndpoints
{
    private const int MaxBodyBytes = 65536;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static IEndpointRouteBuilder MapSquadCommands(this IEndpointRouteBuilder endpoints, string authenticationScheme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);
        endpoints.MapPost("/v1/runs/{runId}/messages", (HttpContext context, string runId) =>
            HandleAsync(context, authenticationScheme, runId, null));
        endpoints.MapGet("/v1/runs/{runId}/commands/{commandId}", (HttpContext context, string runId, string commandId) =>
            HandleAsync(context, authenticationScheme, runId, commandId));
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(HttpContext context, string scheme, string runId, string? commandId)
    {
        context.Response.Headers.CacheControl = "no-store";
        string requestId = Guid.NewGuid().ToString("N");
        try
        {
            var actor = await AuthenticateAsync(context, scheme);
            var resolver = context.RequestServices.GetRequiredService<IProjectRunAccessResolver>();
            var run = await resolver.ResolveAsync(actor, runId, context.RequestAborted);
            Require(run is not null && run.RunId == runId && !string.IsNullOrWhiteSpace(run.ProjectId) &&
                run.Team is not null && Enum.IsDefined(run.Role),
                "reconciliation_required");
            if (commandId is not null)
            {
                var store = context.RequestServices.GetRequiredService<IControlDocumentStore>();
                var status = await new CommandStatusService(store, new ReadAuthorization(run!))
                    .GetAsync(run!.ProjectId, run.RunId, commandId, context.RequestAborted);
                return Results.Json(new
                {
                    schema_version = "1.0", request_id = requestId, project_id = run.ProjectId, run_id = run.RunId,
                    command_id = status.CommandId, status = status.Status
                }, JsonOptions);
            }

            Require(run!.Role is ProjectRole.Operator or ProjectRole.Administrator, "forbidden");
            var key = context.Request.Headers["Idempotency-Key"];
            Require(key.Count == 1 && SafeIdentifier(key[0]), "invalid_request");
            var message = await ReadMessageAsync(context);
            Require(SafeIdentifier(message.RequestId), "invalid_request");
            requestId = message.RequestId;
            Require(SupportedVersion(message.SchemaVersion), "unsupported_version");
            Require(message.ProjectId == run.ProjectId && message.RunId == run.RunId, "scope_mismatch");
            Require(message.Operation == "message" && message.Payload is not null &&
                message.Payload.Recipient is not null && !string.IsNullOrWhiteSpace(message.Payload.Text), "invalid_request");
            var payload = message.Payload!;
            var target = new MessageTarget(run.Team, payload.Recipient!.Kind, payload.DeliveryMode, payload.Recipient.MemberId);
            Require(run.LeaseGeneration > 0, "lease_fenced");
            // Validate the entire request before passing content to its separately approved storage boundary.
            var protectedPayload = await context.RequestServices.GetRequiredService<IProtectedMessagePayloadStore>()
                .StoreAsync(run.ProjectId, run.RunId, payload.Text, context.RequestAborted);
            Require(protectedPayload is not null && !string.IsNullOrWhiteSpace(protectedPayload.Reference) &&
                protectedPayload.Digest == HashText(payload.Text), "durability_unavailable");
            var scope = new IdempotencyScope(actor.SubjectId, actor.ClientId, run.ProjectId, "message", run.RunId);
            var submission = new MessageSubmission(scope, key[0]!, target, protectedPayload!.Reference,
                protectedPayload.Digest, run.LeaseGeneration);
            // The production admission authorizer rechecks current roles, team and lease after payload storage.
            var admission = context.RequestServices.GetRequiredService<MessageAdmissionService>();
            var receipt = await admission.SubmitAsync(submission, context.RequestAborted);
            string statusUrl = $"/v1/runs/{Uri.EscapeDataString(run.RunId)}/commands/{receipt.CommandId}";
            context.Response.Headers.Location = statusUrl;
            return Results.Json(new
            {
                schema_version = "1.0", request_id = requestId, project_id = run.ProjectId, run_id = run.RunId,
                command_id = receipt.CommandId, status = receipt.Status, status_url = statusUrl
            }, JsonOptions, statusCode: StatusCodes.Status202Accepted);
        }
        catch (DomainException error)
        {
            var (status, code, retryable) = Problem(error.Code);
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SquadCommandApi")
                .LogWarning("Command request rejected: {Code}", code);
            return Results.Json(new
            {
                schema_version = "1.0", status, code, request_id = requestId, trace_id = context.TraceIdentifier,
                retryable, detail = "The command request could not be completed.",
                recovery = status == 401 ? "Refresh authentication through the configured identity provider."
                    : "Inspect authorized command status and resolve the reported error before retrying."
            }, JsonOptions, "application/problem+json", status);
        }
        catch (JsonException)
        {
            return InvalidJson(context, requestId);
        }
    }

    private static async Task<VerifiedActor> AuthenticateAsync(HttpContext context, string scheme)
    {
        var result = await context.AuthenticateAsync(scheme);
        Require(result.Succeeded && result.Principal is not null, "authentication_required");
        var identities = result.Principal!.Identities.Where(identity => identity.IsAuthenticated).ToArray();
        Require(identities.Length == 1, "authentication_required");
        var identity = identities[0];
        string subject = OneClaim(identity, "sub");
        string client = OneClaim(identity, "azp");
        string expiry = OneClaim(identity, "exp");
        Require(long.TryParse(expiry, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeSeconds(),
            "authentication_required");
        if (result.Properties?.ExpiresUtc is { } ticketExpiry)
            Require(ticketExpiry > context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow(), "authentication_required");
        return new(subject, client);
    }

    private static string OneClaim(ClaimsIdentity identity, string type)
    {
        var claims = identity.FindAll(type).ToArray();
        Require(claims.Length == 1 && !string.IsNullOrWhiteSpace(claims[0].Value), "authentication_required");
        return claims[0].Value;
    }

    private static async Task<MessageRequest> ReadMessageAsync(HttpContext context)
    {
        Require(MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var type) &&
            string.Equals(type.MediaType, "application/json", StringComparison.OrdinalIgnoreCase), "unsupported_media_type");
        Require(context.Request.ContentLength is null or <= MaxBodyBytes, "request_too_large");
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        while (true)
        {
            int read = await context.Request.Body.ReadAsync(chunk.AsMemory(0,
                (int)Math.Min(chunk.Length, MaxBodyBytes + 1 - buffer.Length)), context.RequestAborted);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
            Require(buffer.Length <= MaxBodyBytes, "request_too_large");
        }
        return JsonSerializer.Deserialize<MessageRequest>(buffer.ToArray(), JsonOptions)
            ?? throw new DomainException("invalid_request", "A message is required.");
    }

    private static IResult InvalidJson(HttpContext context, string requestId)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SquadCommandApi")
            .LogWarning("Command request rejected: invalid_request");
        return Results.Json(new
        {
            schema_version = "1.0", status = 400, code = "invalid_request", request_id = requestId,
            trace_id = context.TraceIdentifier, retryable = false, detail = "A valid JSON request is required.",
            recovery = "Correct the request schema before retrying."
        }, JsonOptions, "application/problem+json", 400);
    }

    private static (int Status, string Code, bool Retryable) Problem(string code) => code switch
    {
        "authentication_required" => (401, code, false),
        "forbidden" => (403, code, false),
        "resource_not_found" => (404, code, false),
        "request_too_large" => (413, code, false),
        "unsupported_media_type" => (415, code, false),
        "invalid_request" or "unsupported_version" => (400, code, false),
        "scope_mismatch" or "idempotency_mismatch" or "lease_fenced" or "invalid_transition" or "version_conflict"
            => (409, code, false),
        "durability_unavailable" or "reconciliation_required" => (503, code, true),
        _ => (500, "internal_error", false)
    };

    private static bool SafeIdentifier(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool SupportedVersion(string version) => version is { Length: >= 3 and <= 16 } &&
        version.StartsWith("1.", StringComparison.Ordinal) && version.AsSpan(2).IndexOfAnyExcept("0123456789") < 0;

    private static string HashText(string text) => "sha256:" +
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void Require([DoesNotReturnIf(false)] bool condition, string code)
    {
        if (!condition) throw new DomainException(code, "The request does not meet the command API contract.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = Protocol.CreateJsonOptions();
        options.RespectRequiredConstructorParameters = true;
        options.AllowDuplicateProperties = false;
        return options;
    }

    private sealed class ReadAuthorization(AuthorizedRun run) : ICommandStatusAuthorizer
    {
        public ValueTask AuthorizeReadAsync(string projectId, string runId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(projectId == run.ProjectId && runId == run.RunId, "resource_not_found");
            return ValueTask.CompletedTask;
        }

        public ValueTask AuthorizeOutcomeAsync(string projectId, string runId, string commandId, long generation,
            CommandStatus outcome, string evidenceReference, string? reconciliationReference, CancellationToken cancellationToken)
            => throw new DomainException("forbidden", "Public command endpoints cannot acknowledge runner outcomes.");
    }

    private sealed record MessageRequest(string SchemaVersion, string RequestId, string ProjectId, string RunId,
        string Operation, MessagePayload Payload);
    private sealed record MessagePayload(Recipient Recipient, DeliveryMode DeliveryMode, string Text);
    private sealed record Recipient(RecipientKind Kind, string? MemberId = null);
}
