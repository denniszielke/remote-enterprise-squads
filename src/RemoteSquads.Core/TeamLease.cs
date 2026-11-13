namespace RemoteSquads.Core;

public sealed record TeamScope
{
    public string ProjectId { get; }
    public string TeamId { get; }

    public TeamScope(string projectId, string teamId)
        => (ProjectId, TeamId) = (Guard.Text(projectId), Guard.Text(teamId));
}

// Attested by the caller; a production adapter must verify process isolation and repository reconciliation.
public sealed record WriterSettlement(string RunId, long Generation, string EvidenceReference,
    bool OldProcessesContained, bool SideEffectsReconciled);

// Immutable CAS candidate, not a distributed lock. Persist before dispatch and fence every write.
public sealed class TeamLease
{
    public TeamScope Scope { get; }
    public string? HolderRunId { get; }
    public long Generation { get; }
    public long Version { get; }
    public DateTimeOffset ChangedAt { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public string? SettlementReference { get; }

    public TeamLease(TeamScope scope, DateTimeOffset now)
    {
        Guard.Require(scope is not null, "invalid_request", "A team scope is required.");
        Scope = scope!;
        ChangedAt = Guard.Utc(now);
    }

    private TeamLease(TeamLease previous, string runId, DateTimeOffset now,
        DateTimeOffset expiresAt, long generation, string? settlementReference)
    {
        Guard.Require(previous.Version < long.MaxValue, "reconciliation_required", "Lease version is exhausted.");
        (Scope, HolderRunId, ChangedAt, ExpiresAt, Generation, Version, SettlementReference) =
            (previous.Scope, runId, now, expiresAt, generation, previous.Version + 1, settlementReference);
    }

    public TeamLease Acquire(string runId, long expectedVersion, DateTimeOffset now, TimeSpan duration,
        WriterSettlement? settlement = null)
    {
        Guard.Text(runId);
        CheckMutation(expectedVersion, now);
        var deadline = Deadline(now, duration);
        if (HolderRunId is not null)
        {
            Guard.Require(now >= ExpiresAt!.Value, "team_writer_conflict", "The team already has a current writer.");
            Guard.Require(settlement is not null, "reconciliation_required",
                "Expiry alone does not establish that the previous writer is safe.");
            Guard.Require(settlement!.RunId == HolderRunId && settlement.Generation == Generation,
                "lease_fenced", "Settlement belongs to another writer generation.");
            Guard.Text(settlement.EvidenceReference);
            Guard.Require(settlement.OldProcessesContained && settlement.SideEffectsReconciled,
                "reconciliation_required", "Previous processes and side effects must be reconciled.");
        }
        Guard.Require(Generation < long.MaxValue, "reconciliation_required", "Lease generation is exhausted.");
        return new(this, runId, now, deadline, Generation + 1,
            HolderRunId is null ? null : settlement!.EvidenceReference);
    }

    public TeamLease Renew(string runId, long generation, long expectedVersion,
        DateTimeOffset now, TimeSpan duration)
    {
        CheckMutation(expectedVersion, now);
        RequireWriter(Scope, runId, generation, now);
        var deadline = Deadline(now, duration);
        Guard.Require(deadline > ExpiresAt!.Value, "invalid_request", "Renewal must extend the current deadline.");
        return new(this, runId, now, deadline, generation, SettlementReference);
    }

    public void RequireWriter(TeamScope scope, string runId, long generation, DateTimeOffset now)
    {
        CheckTime(now);
        Guard.Require(scope is not null && Scope == scope && HolderRunId == runId &&
            generation > 0 && Generation == generation && ExpiresAt is { } deadline && now < deadline,
            "lease_fenced", "A current scoped writer generation is required.");
    }

    private void CheckMutation(long expectedVersion, DateTimeOffset now)
    {
        CheckTime(now);
        Guard.Require(expectedVersion == Version, "version_conflict", "The lease version has changed.");
    }

    private void CheckTime(DateTimeOffset now)
    {
        Guard.Utc(now);
        Guard.Require(now >= ChangedAt, "invalid_request", "Lease time cannot move backwards.");
    }

    private static DateTimeOffset Deadline(DateTimeOffset now, TimeSpan duration)
    {
        Guard.Require(duration > TimeSpan.Zero && duration <= DateTimeOffset.MaxValue - now,
            "invalid_request", "A positive representable lease duration is required.");
        return now.Add(duration);
    }
}
