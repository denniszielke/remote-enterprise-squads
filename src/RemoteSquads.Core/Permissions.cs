namespace RemoteSquads.Core;

public enum PermissionDecision { Allow, Deny }

public sealed record PermissionBinding
{
    public string ActionDigest { get; }
    public string PolicyVersion { get; }
    public long Generation { get; }

    public PermissionBinding(string actionDigest, string policyVersion, long generation)
    {
        ActionDigest = Guard.Text(actionDigest);
        PolicyVersion = Guard.Text(policyVersion);
        Guard.Require(generation > 0, "invalid_request", "A positive fencing generation is required.");
        Generation = generation;
    }
}

public sealed class PermissionResolution
{
    public PermissionDecision Decision { get; }
    public bool TimedOut { get; }
    public string? ResponseKey { get; }
    public PermissionDecision? SubmittedDecision { get; }
    public DateTimeOffset ResolvedAt { get; }

    internal PermissionResolution(PermissionDecision decision, bool timedOut, string? key,
        PermissionDecision? submittedDecision, DateTimeOffset resolvedAt)
        => (Decision, TimedOut, ResponseKey, SubmittedDecision, ResolvedAt) =
            (decision, timedOut, key, submittedDecision, resolvedAt);
}

// Pure conditional aggregate: caller authorizes every attempt (including retries), then durably
// CAS-commits the returned version, resolution receipt and dispatch intent in one transaction.
// Two callers can derive different successors from one snapshot; this object is not a storage lock.
public sealed class PermissionRequest
{
    public string Id { get; }
    public PermissionBinding Binding { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public long Version { get; }
    public PermissionResolution? Resolution { get; }

    public PermissionRequest(string id, PermissionBinding binding, DateTimeOffset createdAt)
    {
        Id = Guard.Text(id);
        Guard.Require(binding is not null, "invalid_request", "A permission binding is required.");
        Binding = binding!;
        CreatedAt = Guard.Utc(createdAt);
        Guard.Require(createdAt <= DateTimeOffset.MaxValue.AddSeconds(-300),
            "invalid_request", "The permission deadline is outside the supported range.");
        ExpiresAt = createdAt.AddSeconds(300);
    }

    private PermissionRequest(PermissionRequest previous, PermissionResolution resolution)
    {
        (Id, Binding, CreatedAt, ExpiresAt) = (previous.Id, previous.Binding, previous.CreatedAt, previous.ExpiresAt);
        Version = previous.Version + 1;
        Resolution = resolution;
    }

    public PermissionRequest Resolve(PermissionBinding binding, string responseKey, PermissionDecision decision,
        long expectedVersion, DateTimeOffset now)
    {
        Guard.Text(responseKey);
        Guard.Defined(decision);
        CheckTime(now);
        Guard.Require(binding is not null && Binding == binding,
            "action_mismatch", "The action, policy or generation does not match.");
        if (Resolution is { } recorded)
        {
            if (recorded.ResponseKey == responseKey && recorded.SubmittedDecision == decision) return this;
            throw new DomainException(recorded.TimedOut ? "approval_expired" : "approval_resolved",
                "The permission already has a recorded resolution.");
        }
        CheckVersion(expectedVersion);
        bool timedOut = now >= ExpiresAt;
        return new(this, new(timedOut ? PermissionDecision.Deny : decision, timedOut, responseKey, decision, now));
    }

    public PermissionRequest Expire(long expectedVersion, DateTimeOffset now)
    {
        CheckTime(now);
        if (Resolution is not null) return this;
        CheckVersion(expectedVersion);
        Guard.Require(now >= ExpiresAt, "invalid_transition", "The permission deadline has not elapsed.");
        return new(this, new(PermissionDecision.Deny, true, null, null, now));
    }

    private void CheckTime(DateTimeOffset now)
    {
        Guard.Utc(now);
        Guard.Require(now >= CreatedAt, "invalid_request", "Resolution cannot precede creation.");
    }

    private void CheckVersion(long expectedVersion)
        => Guard.Require(expectedVersion == Version, "version_conflict", "The permission version has changed.");
}
