using System.Collections.Frozen;

namespace RemoteSquads.Core;

public enum RecipientKind { Coordinator, WorkMonitor, Member }
public enum DeliveryMode { Immediate, Enqueue }
public enum CommandStatus { Accepted, Applied, Failed, Uncertain }

public sealed class PinnedTeam
{
    private readonly FrozenSet<string> members;
    public string Version { get; }

    public PinnedTeam(string version, IEnumerable<string> memberIds)
    {
        Version = Guard.Text(version);
        Guard.Require(memberIds is not null, "invalid_request", "Pinned members are required.");
        members = memberIds!.Select(Guard.Text).ToFrozenSet(StringComparer.Ordinal);
    }

    internal bool Contains(string memberId) => members.Contains(memberId);
}

public sealed class MessageTarget
{
    public RecipientKind Kind { get; }
    public DeliveryMode DeliveryMode { get; }
    public string? MemberId { get; }
    public string TeamVersion { get; }

    public MessageTarget(PinnedTeam team, RecipientKind kind, DeliveryMode deliveryMode, string? memberId = null)
    {
        Guard.Require(team is not null, "invalid_request", "A pinned team is required.");
        Kind = Guard.Defined(kind);
        DeliveryMode = Guard.Defined(deliveryMode);
        Guard.Require(kind == RecipientKind.Member ? !string.IsNullOrWhiteSpace(memberId) : memberId is null,
            "invalid_request", "Only a member recipient requires a member reference.");
        Guard.Require(kind != RecipientKind.Member || team!.Contains(memberId!),
            "invalid_request", "The member is not in the pinned team.");
        MemberId = memberId;
        TeamVersion = team!.Version;
    }
}

public sealed class CommandReconciliation
{
    public string CommandId { get; }
    public CommandStatus Outcome { get; }
    public string EvidenceReference { get; }

    public CommandReconciliation(string commandId, CommandStatus outcome, string evidenceReference)
    {
        CommandId = Guard.Text(commandId);
        Guard.Require(outcome is CommandStatus.Applied or CommandStatus.Failed,
            "invalid_request", "Reconciliation must establish a known outcome.");
        Outcome = outcome;
        EvidenceReference = Guard.Text(evidenceReference);
    }
}

// These values describe intent/outcome only; acceptance must be persisted before dispatch by the caller.
public sealed class Command
{
    public string Id { get; }
    public MessageTarget Target { get; }
    public CommandStatus Status { get; }
    public string? ReconciliationReference { get; }

    private Command(string id, MessageTarget target, CommandStatus status, string? evidence = null)
        => (Id, Target, Status, ReconciliationReference) = (id, target, status, evidence);

    public static Command Accept(string id, MessageTarget target)
    {
        Guard.Require(target is not null, "invalid_request", "A message target is required.");
        return new(Guard.Text(id), target!, CommandStatus.Accepted);
    }

    public Command Observe(CommandStatus outcome)
    {
        Guard.Defined(outcome);
        Guard.Require(Status == CommandStatus.Accepted && outcome != CommandStatus.Accepted,
            "invalid_transition", "Only accepted commands can receive an initial outcome.");
        return new(Id, Target, outcome);
    }

    public Command Reconcile(CommandReconciliation evidence)
    {
        Guard.Require(Status == CommandStatus.Uncertain, "invalid_transition", "The command is not uncertain.");
        Guard.Require(evidence is not null, "reconciliation_required", "Retained reconciliation evidence is required.");
        Guard.Require(evidence!.CommandId == Id, "action_mismatch", "Evidence belongs to another command.");
        return new(Id, Target, evidence.Outcome, evidence.EvidenceReference);
    }
}
