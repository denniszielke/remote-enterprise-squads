using System.Text.Json;

namespace RemoteSquads.Core;

// Host-derived binding, never an HTTP request's assertion of writer authority.
public sealed record RunnerSession
{
    public string ProjectId { get; }
    public string RunId { get; }
    public string SessionId { get; }
    public long Generation { get; }

    public RunnerSession(string projectId, string runId, string sessionId, long generation)
    {
        ProjectId = BoundedText(projectId);
        RunId = BoundedText(runId);
        SessionId = BoundedText(sessionId);
        Guard.Require(generation > 0, "invalid_request", "A current writer generation is required.");
        Generation = generation;
    }

    private static string BoundedText(string value)
    {
        Guard.Text(value);
        Guard.Require(value.Length <= 256, "invalid_request", "The runner binding is too long.");
        return value;
    }
}

public sealed record RunnerDelivery(RunnerSession Session, string CommandId, long Sequence,
    string AttemptId, MessageTarget Target, string PayloadReference, string PayloadDigest);

// Applied means the adapter observed message delivery, NOT task/tool completion.
public sealed record RunnerAcknowledgement(RunnerSession Session, string CommandId, long Sequence,
    string AttemptId, CommandStatus Outcome);

public interface IRunnerDeliveryAuthority
{
    // Authenticate the runner and verify the current persisted writer before storage access.
    ValueTask AuthorizeSessionAsync(RunnerSession session, CancellationToken cancellationToken);

    // Revalidate current persisted lease, run policy and pinned recipient. Never grant tool permission.
    ValueTask AuthorizeDeliveryAsync(RunnerDelivery delivery, CancellationToken cancellationToken);

    // Additionally verify the previous dispatch owner/process is contained, even for the same session.
    // Journal CAS alone cannot stop an already-running external call.
    ValueTask AuthorizeRecoveryAsync(RunnerSession session, CancellationToken cancellationToken);
}

public interface IRunnerSessionAdapter
{
    // Must fail before starting a journal entry when the adapter is not configured/validated.
    void RequireAvailable();
    ValueTask<RunnerAcknowledgement> DeliverAsync(RunnerDelivery delivery, CancellationToken cancellationToken);
}

public sealed class UnavailableRunnerSessionAdapter : IRunnerSessionAdapter
{
    public void RequireAvailable()
        => throw new DomainException("sdk_unavailable", "No validated Copilot SDK adapter is configured.");

    public ValueTask<RunnerAcknowledgement> DeliverAsync(RunnerDelivery delivery, CancellationToken cancellationToken)
        => throw new DomainException("sdk_unavailable", "No validated Copilot SDK adapter is configured.");
}

// A bounded, durable run journal composed with admitted commands and outcome status.
// No default authorizer, session discovery, process fencing or real SDK implementation.
public sealed class RunnerDeliveryService
{
    private const int MaxCommands = 32;
    private const int MaxBytes = 131072;
    private const int CompletionReserve = 16384;
    private readonly IControlDocumentStore store;
    private readonly IRunnerDeliveryAuthority authority;
    private readonly IRunnerSessionAdapter adapter;
    private readonly CommandStatusService statuses;
    private readonly TimeSpan cleanupBudget;
    private static readonly JsonSerializerOptions JsonOptions = MessageAdmissionService.JsonOptions;

    public RunnerDeliveryService(IControlDocumentStore store, IRunnerDeliveryAuthority authority,
        IRunnerSessionAdapter adapter, CommandStatusService statuses, TimeSpan cleanupBudget)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(statuses);
        Guard.Require(cleanupBudget > TimeSpan.Zero && cleanupBudget <= TimeSpan.FromSeconds(30),
            "invalid_request", "A bounded host cleanup budget of at most 30 seconds is required.");
        (this.store, this.authority, this.adapter, this.statuses, this.cleanupBudget) =
            (store, authority, adapter, statuses, cleanupBudget);
    }

    public async ValueTask<CommandStatusReceipt> DeliverAsync(RunnerSession session, string commandId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateCommandId(commandId);
        await authority.AuthorizeSessionAsync(session, cancellationToken);
        var command = await LoadCommandAsync(session, commandId, cancellationToken);
        var snapshot = await LoadJournalAsync(session, cancellationToken);
        var prior = snapshot?.Journal.Events.LastOrDefault(e => e.CommandId == commandId);
        var delivery = Delivery(session, command, prior?.Sequence ?? NextSequence(snapshot),
            prior?.AttemptId ?? Guid.NewGuid().ToString("N"));
        await authority.AuthorizeDeliveryAsync(delivery, cancellationToken);

        if (prior is not null)
        {
            RequireBinding(prior, command);
            Guard.Require(prior.Kind != "started", "runner_busy", "Dispatch is in flight or requires contained recovery.");
            if (prior.Kind == "settled")
            {
                // Status may have advanced between the initial command read and journal read.
                var settled = await LoadCommandAsync(session, commandId, cancellationToken);
                Guard.Require(settled.Status == prior.Outcome, "reconciliation_required", "Settled status is inconsistent.");
                return new(settled.Id, settled.Status);
            }
            // Only the active tail can be observed but not settled.
            using var cleanup = new CancellationTokenSource(cleanupBudget);
            return await CompleteAsync(session, snapshot!, command, cleanup.Token);
        }

        // An existing external outcome is never permission to send again.
        if (command.Status != CommandStatus.Accepted) return new(command.Id, command.Status);
        Guard.Require(snapshot is null || snapshot.Journal.Events[^1].Kind == "settled",
            "runner_busy", "The prior command has not settled.");
        adapter.RequireAvailable();
        cancellationToken.ThrowIfCancellationRequested();
        var started = new JournalEvent(delivery.Sequence, commandId, command.Fingerprint.PayloadDigest,
            delivery.AttemptId, "started", null);
        var journal = snapshot?.Journal ?? new Journal("1.0", JournalId(session), Partition(session), -1, session, []);
        Guard.Require(delivery.Sequence <= MaxCommands, "journal_capacity", "The bounded journal requires archival review.");
        var candidate = Append(journal, started);
        Guard.Require(Serialize(candidate).GetRawText().Length <= MaxBytes - CompletionReserve &&
            JsonSerializer.SerializeToUtf8Bytes(candidate, JsonOptions).Length <= MaxBytes - CompletionReserve,
            "journal_capacity", "Journal completion capacity must be reserved before dispatch.");
        await authority.AuthorizeDeliveryAsync(delivery, cancellationToken);

        // No invocation if this write throws or its response is ambiguous, including commit-then-throw.
        var claim = await PersistAsync(snapshot, candidate, cancellationToken);
        await authority.AuthorizeDeliveryAsync(delivery, cancellationToken);
        CommandStatus outcome;
        try
        {
            var acknowledgement = await adapter.DeliverAsync(delivery, cancellationToken);
            outcome = acknowledgement is not null && acknowledgement.Session == session &&
                acknowledgement.CommandId == commandId && acknowledgement.Sequence == delivery.Sequence &&
                acknowledgement.AttemptId == delivery.AttemptId &&
                acknowledgement.Outcome is CommandStatus.Applied or CommandStatus.Failed
                ? acknowledgement.Outcome : CommandStatus.Uncertain;
        }
        catch (Exception)
        {
            // A thrown adapter call, including cooperative cancellation, does not prove non-delivery.
            outcome = CommandStatus.Uncertain;
        }

        // Caller cancellation cannot erase dispatch evidence. Host budget still bounds cleanup.
        using var settlement = new CancellationTokenSource(cleanupBudget);
        await authority.AuthorizeSessionAsync(session, settlement.Token);
        var observed = await PersistAsync(claim,
            Append(claim.Journal, started with { Kind = "observed", Outcome = outcome }), settlement.Token);
        return await CompleteAsync(session, observed, command, settlement.Token);
    }

    public async ValueTask<CommandStatusReceipt?> RecoverAsync(RunnerSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await authority.AuthorizeSessionAsync(session, cancellationToken);
        await authority.AuthorizeRecoveryAsync(session, cancellationToken);
        var snapshot = await LoadJournalAsync(session, cancellationToken);
        if (snapshot is null) return null;
        var tail = snapshot.Journal.Events[^1];
        var command = await LoadCommandAsync(session, tail.CommandId, cancellationToken);
        RequireBinding(tail, command);
        if (tail.Kind == "settled")
        {
            Guard.Require(command.Status == tail.Outcome, "reconciliation_required", "Settled status is inconsistent.");
            return new(command.Id, command.Status);
        }

        using var cleanup = new CancellationTokenSource(cleanupBudget);
        await authority.AuthorizeRecoveryAsync(session, cleanup.Token);
        if (tail.Kind == "started")
            snapshot = await PersistAsync(snapshot, Append(snapshot.Journal,
                tail with { Kind = "observed", Outcome = CommandStatus.Uncertain }), cleanup.Token);
        // Never calls the SDK. Uncertain blocks new dispatch until a separate reviewed reconciliation.
        return await CompleteAsync(session, snapshot, command, cleanup.Token);
    }

    private async ValueTask<CommandStatusReceipt> CompleteAsync(RunnerSession session, Snapshot snapshot,
        MessageAdmissionService.MessageDocument command, CancellationToken cancellationToken)
    {
        var observed = snapshot.Journal.Events[^1];
        Guard.Require(observed.Kind == "observed", "reconciliation_required", "Observed delivery evidence is required.");
        RequireBinding(observed, command);
        await authority.AuthorizeSessionAsync(session, cancellationToken);
        var receipt = await statuses.RecordOutcomeAsync(session.ProjectId, session.RunId, command.Id, session.Generation,
            observed.Outcome!.Value, EvidenceReference(snapshot.Journal, snapshot.Journal.Events.Length),
            cancellationToken: cancellationToken);
        if (receipt.Status != CommandStatus.Uncertain)
        {
            await authority.AuthorizeSessionAsync(session, cancellationToken);
            await PersistAsync(snapshot, Append(snapshot.Journal, observed with { Kind = "settled" }), cancellationToken);
        }
        return receipt;
    }

    private async ValueTask<MessageAdmissionService.MessageDocument> LoadCommandAsync(RunnerSession session,
        string commandId, CancellationToken cancellationToken)
    {
        var stored = await store.ReadAsync(Partition(session), commandId, cancellationToken);
        Guard.Require(stored is not null, "resource_not_found", "Only committed admitted commands can be delivered.");
        var command = MessageAdmissionService.ReadValidatedDocument(stored!.Body, session.ProjectId, session.RunId, commandId);
        Guard.Require(command.LeaseGeneration == session.Generation, "lease_fenced", "The admitted writer generation differs.");
        return command;
    }

    private async ValueTask<Snapshot?> LoadJournalAsync(RunnerSession session, CancellationToken cancellationToken)
    {
        var stored = await store.ReadAsync(Partition(session), JournalId(session), cancellationToken);
        if (stored is null) return null;
        Journal journal;
        try
        {
            journal = stored.Body.Deserialize<Journal>(JsonOptions)
                ?? throw new JsonException();
        }
        catch (Exception error) when (error is JsonException or DomainException)
        {
            throw new DomainException("reconciliation_required", "The runner journal cannot be verified.");
        }
        Guard.Require(!string.IsNullOrWhiteSpace(stored.ETag) && journal.SchemaVersion == "1.0" &&
            journal.Id == JournalId(session) && journal.PartitionKey == Partition(session) && journal.Ttl == -1 &&
            journal.Session == session, "reconciliation_required", "The journal session binding cannot be verified.");
        ValidateJournal(journal);
        return new(journal, stored.ETag);
    }

    private async ValueTask<Snapshot> PersistAsync(Snapshot? previous, Journal journal, CancellationToken cancellationToken)
    {
        ValidateJournal(journal);
        var body = Serialize(journal);
        bool committed = previous is null
            ? await store.CreateAsync(journal.PartitionKey, journal.Id, body, cancellationToken)
            : await store.ReplaceAsync(journal.PartitionKey, journal.Id, body, previous.ETag, cancellationToken);
        Guard.Require(committed, "version_conflict", "Another runner changed the journal; do not dispatch.");
        var recorded = await LoadJournalAsync(journal.Session, cancellationToken);
        Guard.Require(recorded is not null && Serialize(recorded.Journal).GetRawText() == body.GetRawText(),
            "version_conflict", "The committed journal no longer belongs to this operation.");
        return recorded!;
    }

    private static void ValidateJournal(Journal journal)
    {
        Guard.Require(journal.Events is { Length: > 0 and <= MaxCommands * 3 } &&
            JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions).Length <= MaxBytes,
            "reconciliation_required", "The bounded journal cannot be verified.");
        var commands = new HashSet<string>(StringComparer.Ordinal);
        JournalEvent? previous = null;
        foreach (var entry in journal.Events!)
        {
            Guard.Require(entry is not null, "reconciliation_required", "A journal event is missing.");
            var current = entry!;
            ValidateCommandId(current.CommandId);
            Guard.Require(current.AttemptId is { Length: 32 } && Guid.TryParseExact(current.AttemptId, "N", out _) &&
                current.Fingerprint is { Length: 71 } && current.Fingerprint.StartsWith("sha256:", StringComparison.Ordinal) &&
                current.Fingerprint.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0,
                "reconciliation_required", "The journal intent binding cannot be verified.");
            if (current.Kind == "started")
            {
                Guard.Require((previous is null || previous.Kind == "settled") &&
                    current.Sequence == (previous?.Sequence ?? 0) + 1 && current.Sequence <= MaxCommands &&
                    current.Outcome is null && commands.Add(current.CommandId),
                    "reconciliation_required", "The journal dispatch ordering cannot be verified.");
            }
            else
            {
                Guard.Require(previous is not null && current.Sequence == previous.Sequence &&
                    current.CommandId == previous.CommandId && current.Fingerprint == previous.Fingerprint &&
                    current.AttemptId == previous.AttemptId &&
                    (current.Kind == "observed" && previous.Kind == "started" &&
                        current.Outcome is CommandStatus.Applied or CommandStatus.Failed or CommandStatus.Uncertain ||
                     current.Kind == "settled" && previous.Kind == "observed" &&
                        previous.Outcome is CommandStatus.Applied or CommandStatus.Failed && current.Outcome == previous.Outcome),
                    "reconciliation_required", "The journal event transition cannot be verified.");
            }
            previous = current;
        }
    }

    private static void RequireBinding(JournalEvent entry, MessageAdmissionService.MessageDocument command)
        => Guard.Require(entry.CommandId == command.Id && entry.Fingerprint == command.Fingerprint.PayloadDigest,
            "reconciliation_required", "The journal and admitted command differ.");

    private static RunnerDelivery Delivery(RunnerSession session, MessageAdmissionService.MessageDocument command,
        long sequence, string attemptId)
        => new(session, command.Id, sequence, attemptId,
            new MessageTarget(new PinnedTeam(command.TeamVersion, command.MemberId is null ? [] : [command.MemberId]),
                command.RecipientKind, command.DeliveryMode, command.MemberId), command.PayloadReference, command.PayloadDigest);

    private static long NextSequence(Snapshot? snapshot) => (snapshot?.Journal.Events[^1].Sequence ?? 0) + 1;
    private static Journal Append(Journal journal, JournalEvent entry) => journal with { Events = [.. journal.Events, entry] };
    private static JsonElement Serialize(Journal journal) => JsonSerializer.SerializeToElement(journal, JsonOptions);
    private static string Partition(RunnerSession session) => MessageAdmissionService.PartitionFor(session.ProjectId, session.RunId);
    private static string JournalId(RunnerSession session) => "runner-journal-" + Partition(session);
    private static string EvidenceReference(Journal journal, int index) => $"{journal.Id}/events/{index}";

    private static void ValidateCommandId(string commandId)
        => Guard.Require(commandId is { Length: 72 } && commandId.StartsWith("command-", StringComparison.Ordinal) &&
            commandId.AsSpan(8).IndexOfAnyExcept("0123456789abcdef") < 0,
            "invalid_request", "A command identifier is required.");

    private sealed record Journal(string SchemaVersion, string Id, string PartitionKey, int Ttl,
        RunnerSession Session, JournalEvent[] Events);
    private sealed record JournalEvent(long Sequence, string CommandId, string Fingerprint,
        string AttemptId, string Kind, CommandStatus? Outcome);
    private sealed record Snapshot(Journal Journal, string ETag);
}
