namespace RemoteSquads.Core;

public sealed record IdempotencyScope
{
    public string SubjectId { get; }
    public string ClientId { get; }
    public string ProjectId { get; }
    public string Operation { get; }
    public string TargetId { get; }

    public IdempotencyScope(string subjectId, string clientId, string projectId, string operation, string targetId)
        => (SubjectId, ClientId, ProjectId, Operation, TargetId) =
            (Guard.Text(subjectId), Guard.Text(clientId), Guard.Text(projectId),
             Guard.Text(operation), Guard.Text(targetId));
}

public sealed record RequestFingerprint
{
    public int SchemaMajor { get; }
    public string PayloadDigest { get; }

    // The adapter hashes validated, canonical semantic payloads, excluding request/trace IDs.
    public RequestFingerprint(int schemaMajor, string payloadDigest)
    {
        Guard.Require(schemaMajor > 0, "invalid_request", "A positive schema major version is required.");
        Guard.Text(payloadDigest);
        Guard.Require(payloadDigest.Length == 71 && payloadDigest.StartsWith("sha256:", StringComparison.Ordinal) &&
            payloadDigest.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0,
            "invalid_request", "A lowercase SHA-256 payload digest is required.");
        (SchemaMajor, PayloadDigest) = (schemaMajor, payloadDigest);
    }
}

// Commit this binding and its result/intent atomically. Reauthorize before every replay.
// Retain expired bindings as tombstones; deleting one must never enable duplicate dispatch.
public sealed class IdempotencyRecord
{
    public IdempotencyScope Scope { get; }
    public string Key { get; }
    public RequestFingerprint Fingerprint { get; }
    public string ResultReference { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset RetainUntil { get; }

    public IdempotencyRecord(IdempotencyScope scope, string key, RequestFingerprint fingerprint,
        string resultReference, DateTimeOffset createdAt, DateTimeOffset retainUntil)
    {
        Guard.Require(scope is not null && fingerprint is not null, "invalid_request", "Scope and fingerprint are required.");
        Scope = scope!;
        Key = Guard.Text(key);
        Fingerprint = fingerprint!;
        ResultReference = Guard.Text(resultReference);
        CreatedAt = Guard.Utc(createdAt);
        RetainUntil = Guard.Utc(retainUntil);
        Guard.Require(retainUntil > createdAt, "invalid_request", "A positive retention window is required.");
    }

    public string Replay(IdempotencyScope scope, string key, RequestFingerprint fingerprint, DateTimeOffset now)
    {
        Guard.Require(scope is not null && Scope == scope && Key == key,
            "resource_not_found", "No binding exists in the requested scope.");
        Guard.Utc(now);
        Guard.Require(now >= CreatedAt, "invalid_request", "Replay cannot precede creation.");
        Guard.Require(now < RetainUntil, "reconciliation_required", "The supported retry window has expired.");
        Guard.Require(fingerprint is not null && Fingerprint == fingerprint,
            "idempotency_mismatch", "The key already binds a different semantic request.");
        return ResultReference;
    }
}
