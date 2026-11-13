using System.Text.Json;

namespace RemoteSquads.Core;

public interface ICommandStatusAuthorizer
{
    // Authenticate and resolve run ownership before storage access; Viewer can read only.
    ValueTask AuthorizeReadAsync(string projectId, string runId, CancellationToken cancellationToken);

    // Require the authenticated runner, current persisted writer generation and verified journal evidence.
    // Human project roles alone must never authorize runner outcomes.
    ValueTask AuthorizeOutcomeAsync(string projectId, string runId, string commandId, long generation,
        CommandStatus outcome, string evidenceReference,
        string? reconciliationReference, CancellationToken cancellationToken);
}

public sealed record CommandStatusReceipt(string CommandId, CommandStatus Status);

public sealed class CommandStatusService
{
    private readonly IControlDocumentStore store;
    private readonly ICommandStatusAuthorizer authorizer;

    public CommandStatusService(IControlDocumentStore store, ICommandStatusAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authorizer);
        (this.store, this.authorizer) = (store, authorizer);
    }

    public async ValueTask<CommandStatusReceipt> GetAsync(string projectId, string runId, string commandId,
        CancellationToken cancellationToken = default)
    {
        ValidateAddress(projectId, runId, commandId);
        await authorizer.AuthorizeReadAsync(projectId, runId, cancellationToken);
        var (_, document) = await LoadAsync(projectId, runId, commandId, cancellationToken);
        return new(document.Id, document.Status);
    }

    public async ValueTask<CommandStatusReceipt> RecordOutcomeAsync(string projectId, string runId, string commandId,
        long generation, CommandStatus outcome, string evidenceReference, string? reconciliationReference = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAddress(projectId, runId, commandId);
        Guard.Require(generation > 0, "invalid_request", "A positive writer generation is required.");
        Guard.Require(outcome is CommandStatus.Applied or CommandStatus.Failed or CommandStatus.Uncertain,
            "invalid_request", "An observed command outcome is required.");
        Guard.Text(evidenceReference);
        if (reconciliationReference is not null) Guard.Text(reconciliationReference);
        await authorizer.AuthorizeOutcomeAsync(projectId, runId, commandId, generation, outcome, evidenceReference,
            reconciliationReference, cancellationToken);
        var (versioned, document) = await LoadAsync(projectId, runId, commandId, cancellationToken);
        Guard.Require(document.LeaseGeneration == generation, "lease_fenced", "The command belongs to another writer generation.");
        if (document.Status == outcome && document.OutcomeReference == evidenceReference &&
            document.ReconciliationReference == reconciliationReference)
            return new(document.Id, document.Status);

        Guard.Require(document.Status == CommandStatus.Accepted ||
            (document.Status == CommandStatus.Uncertain && outcome is CommandStatus.Applied or CommandStatus.Failed),
            "invalid_transition", "The recorded outcome cannot be replaced.");
        Guard.Require(document.Status == CommandStatus.Uncertain
            ? reconciliationReference is not null
            : reconciliationReference is null,
            "reconciliation_required", "Only an uncertain outcome requires reconciliation evidence.");
        Guard.Require(!string.IsNullOrWhiteSpace(versioned.ETag),
            "reconciliation_required", "The command concurrency token is missing.");

        var next = document with
        {
            Status = outcome,
            OutcomeReference = evidenceReference,
            ReconciliationReference = reconciliationReference
        };
        bool replaced = await store.ReplaceAsync(document.PartitionKey, document.Id,
            JsonSerializer.SerializeToElement(next, MessageAdmissionService.JsonOptions), versioned.ETag, cancellationToken);
        Guard.Require(replaced, "version_conflict", "The command changed; reauthorize and inspect its current outcome.");
        return new(next.Id, next.Status);
    }

    private async ValueTask<(VersionedDocument Versioned, MessageAdmissionService.MessageDocument Document)> LoadAsync(
        string projectId, string runId, string commandId, CancellationToken cancellationToken)
    {
        var versioned = await store.ReadAsync(MessageAdmissionService.PartitionFor(projectId, runId), commandId, cancellationToken);
        Guard.Require(versioned is not null, "resource_not_found", "The command is unavailable.");
        return (versioned!, MessageAdmissionService.ReadValidatedDocument(versioned!.Body, projectId, runId, commandId));
    }

    private static void ValidateAddress(string projectId, string runId, string commandId)
    {
        Guard.Text(projectId);
        Guard.Text(runId);
        Guard.Text(commandId);
        Guard.Require(commandId.Length == 72 && commandId.StartsWith("command-", StringComparison.Ordinal) &&
            commandId.AsSpan(8).IndexOfAnyExcept("0123456789abcdef") < 0,
            "invalid_request", "A command identifier is required.");
    }
}
