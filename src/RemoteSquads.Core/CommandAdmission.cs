using System.Security.Cryptography;
using System.Text.Json;

namespace RemoteSquads.Core;

public sealed record VersionedDocument(JsonElement Body, string ETag);

public interface IControlDocumentStore
{
    ValueTask<bool> CreateAsync(string partitionKey, string id, JsonElement document, CancellationToken cancellationToken);
    ValueTask<VersionedDocument?> ReadAsync(string partitionKey, string id, CancellationToken cancellationToken);
    ValueTask<bool> ReplaceAsync(string partitionKey, string id, JsonElement document, string expectedETag,
        CancellationToken cancellationToken);
}

public interface IMessageAdmissionAuthorizer
{
    // Resolve ownership, authenticate the subject/client, and check role, pinned team and current lease.
    // The host must derive scope from its authenticated context, never caller-supplied actor fields.
    ValueTask AuthorizeAsync(IdempotencyScope scope, MessageTarget target, long leaseGeneration,
        CancellationToken cancellationToken);
}

public sealed class MessageSubmission
{
    public IdempotencyScope Scope { get; }
    public string Key { get; }
    public MessageTarget Target { get; }
    public string PayloadReference { get; }
    public string PayloadDigest { get; }
    public long LeaseGeneration { get; }
    public RequestFingerprint Fingerprint { get; }

    public MessageSubmission(IdempotencyScope scope, string key, MessageTarget target,
        string payloadReference, string payloadDigest, long leaseGeneration)
    {
        Guard.Require(scope is not null && target is not null, "invalid_request", "Scope and target are required.");
        Scope = scope!;
        Target = target!;
        Key = Guard.Text(key);
        PayloadReference = Guard.Text(payloadReference);
        PayloadDigest = new RequestFingerprint(1, payloadDigest).PayloadDigest;
        Guard.Require(leaseGeneration > 0, "invalid_request", "A positive writer generation is required.");
        LeaseGeneration = leaseGeneration;
        Guard.Require(Scope.Operation == "message",
            "invalid_request", "Message admission requires the message operation scope.");
        Fingerprint = new(1, Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            team_version = Target.TeamVersion,
            recipient_kind = Target.Kind,
            member_id = Target.MemberId,
            delivery_mode = Target.DeliveryMode,
            payload_digest = PayloadDigest,
            lease_generation = leaseGeneration
        }, Protocol.CreateJsonOptions())));
    }

    internal static string Hash(ReadOnlySpan<byte> bytes)
        => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public sealed record MessageReceipt(string CommandId, CommandStatus Status, bool Replayed);

public sealed class MessageAdmissionService
{
    internal static readonly JsonSerializerOptions JsonOptions = StorageJsonOptions();
    private readonly IControlDocumentStore store;
    private readonly IMessageAdmissionAuthorizer authorizer;
    private readonly TimeProvider clock;
    private readonly TimeSpan retryWindow;

    public MessageAdmissionService(IControlDocumentStore store, IMessageAdmissionAuthorizer authorizer,
        TimeProvider clock, TimeSpan retryWindow)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(clock);
        Guard.Require(retryWindow > TimeSpan.Zero, "invalid_request", "A positive retry window is required.");
        (this.store, this.authorizer, this.clock, this.retryWindow) = (store, authorizer, clock, retryWindow);
    }

    public async ValueTask<MessageReceipt> SubmitAsync(MessageSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        await authorizer.AuthorizeAsync(submission.Scope, submission.Target, submission.LeaseGeneration, cancellationToken);
        var now = Guard.Utc(clock.GetUtcNow());
        Guard.Require(retryWindow <= DateTimeOffset.MaxValue - now, "invalid_request", "The retry deadline is out of range.");
        var scope = submission.Scope;
        var id = "command-" + DigestParts(scope.SubjectId, scope.ClientId, scope.ProjectId,
            scope.Operation, scope.TargetId, submission.Key);
        var partition = PartitionFor(scope.ProjectId, scope.TargetId);
        var document = new MessageDocument("1.0", id, partition, -1, scope, submission.Key,
            submission.Fingerprint, submission.PayloadReference, submission.PayloadDigest,
            submission.Target.TeamVersion, submission.Target.Kind, submission.Target.DeliveryMode,
            submission.Target.MemberId, submission.LeaseGeneration, CommandStatus.Accepted, now, now.Add(retryWindow));

        // The binding and dispatch intent are one document. Never dispatch from an uncommitted candidate.
        if (await store.CreateAsync(partition, id, JsonSerializer.SerializeToElement(document, JsonOptions), cancellationToken))
            return new(id, CommandStatus.Accepted, false);

        var existing = await store.ReadAsync(partition, id, cancellationToken);
        Guard.Require(existing is not null, "reconciliation_required", "The conflicting command cannot be reconciled.");
        var recorded = ReadValidatedDocument(existing!.Body, scope.ProjectId, scope.TargetId, id);
        Guard.Require(recorded.Scope == scope && recorded.Key == submission.Key,
            "reconciliation_required", "The stored command binding is inconsistent.");
        var binding = new IdempotencyRecord(recorded.Scope, recorded.Key, recorded.Fingerprint, recorded.Id,
            recorded.CreatedAt, recorded.RetainUntil);
        binding.Replay(scope, submission.Key, submission.Fingerprint, now);
        return new(recorded.Id, recorded.Status, true);
    }

    private static void ValidateIntent(MessageDocument document)
    {
        try
        {
            var members = document.MemberId is null ? Array.Empty<string>() : [document.MemberId];
            var target = new MessageTarget(new PinnedTeam(document.TeamVersion, members),
                document.RecipientKind, document.DeliveryMode, document.MemberId);
            var intent = new MessageSubmission(document.Scope, document.Key, target, document.PayloadReference,
                document.PayloadDigest, document.LeaseGeneration);
            Guard.Require(document.Fingerprint is not null && intent.Fingerprint == document.Fingerprint,
                "reconciliation_required", "The stored intent fingerprint is inconsistent.");
            Guard.Defined(document.Status);
            Guard.Require(document.Status == CommandStatus.Accepted
                ? document.OutcomeReference is null && document.ReconciliationReference is null
                : !string.IsNullOrWhiteSpace(document.OutcomeReference),
                "reconciliation_required", "The stored command outcome lacks evidence.");
            Guard.Require(document.ReconciliationReference is null ||
                (document.Status is CommandStatus.Applied or CommandStatus.Failed &&
                 !string.IsNullOrWhiteSpace(document.ReconciliationReference)),
                "reconciliation_required", "The stored reconciliation evidence is inconsistent.");
            Guard.Utc(document.CreatedAt);
            Guard.Utc(document.RetainUntil);
            Guard.Require(document.RetainUntil > document.CreatedAt,
                "reconciliation_required", "The stored retry window is inconsistent.");
        }
        catch (DomainException)
        {
            throw new DomainException("reconciliation_required", "The stored command intent cannot be verified.");
        }
    }

    internal static MessageDocument ReadValidatedDocument(JsonElement body, string projectId, string runId, string id)
    {
        var document = ReadDocument(body);
        Guard.Require(document.SchemaVersion == "1.0" && document.Id == id &&
            document.PartitionKey == PartitionFor(projectId, runId) && document.Ttl == -1 &&
            document.Scope is not null && document.Scope.ProjectId == projectId && document.Scope.TargetId == runId,
            "reconciliation_required", "The stored command binding is inconsistent.");
        ValidateIntent(document);
        var scope = document.Scope!;
        Guard.Require(document.Id == "command-" + DigestParts(scope.SubjectId, scope.ClientId,
            projectId, scope.Operation, runId, document.Key),
            "reconciliation_required", "The stored command identity is inconsistent.");
        return document;
    }

    internal static string PartitionFor(string projectId, string runId) => DigestParts(projectId, runId);

    private static MessageDocument ReadDocument(JsonElement body)
    {
        try
        {
            return body.Deserialize<MessageDocument>(JsonOptions)
                ?? throw new DomainException("reconciliation_required", "The stored command is missing.");
        }
        catch (JsonException)
        {
            throw new DomainException("reconciliation_required", "The stored command schema cannot be verified.");
        }
        catch (DomainException)
        {
            throw new DomainException("reconciliation_required", "The stored command fields cannot be verified.");
        }
    }

    private static string DigestParts(params string[] parts)
        => MessageSubmission.Hash(JsonSerializer.SerializeToUtf8Bytes(parts))[7..];

    private static JsonSerializerOptions StorageJsonOptions()
    {
        var options = Protocol.CreateJsonOptions();
        options.RespectRequiredConstructorParameters = true;
        return options;
    }

    internal sealed record MessageDocument(string SchemaVersion, string Id, string PartitionKey, int Ttl,
        IdempotencyScope Scope, string Key, RequestFingerprint Fingerprint, string PayloadReference, string PayloadDigest,
        string TeamVersion, RecipientKind RecipientKind, DeliveryMode DeliveryMode, string? MemberId, long LeaseGeneration,
        CommandStatus Status, DateTimeOffset CreatedAt, DateTimeOffset RetainUntil)
    {
        public string? OutcomeReference { get; init; }
        public string? ReconciliationReference { get; init; }
    }
}
