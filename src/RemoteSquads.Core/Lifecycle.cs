namespace RemoteSquads.Core;

public enum RunState
{
    Requested, Provisioning, Starting, Running, WaitingForInput, Idle, Pausing, Suspended,
    Resuming, Stopping, Finalizing, Completed, Failed, RecoveryRequired
}

// Evidence fields are caller attestations, not verification performed by this library.
public sealed record SuspensionEvidence(string Reference, bool Quiescent, bool DurableCheckpoint, bool PlatformSuspended);
public sealed record ReadinessEvidence(string Reference, bool LeaseCurrent, bool ConfigurationValidated,
    bool ProtectedGovernanceReady, bool CredentialsRotated, bool NetworkValidated, bool SessionValidated);
public sealed record FinalizationEvidence(string Reference, bool StatePersisted, bool ContextSynchronized,
    bool RequiredSourcesReconciled, bool SignedManifestIndependentlyVerified);
public sealed record SettlementEvidence
{
    public string Reference { get; }
    public bool TurnsSettled { get; }
    public bool ToolsSettled { get; }
    public bool OperationsSettled { get; }
    public bool ApprovalsSettled { get; }

    public SettlementEvidence(string reference, bool turnsSettled, bool toolsSettled, bool operationsSettled, bool approvalsSettled)
    {
        Reference = Guard.Text(reference);
        (TurnsSettled, ToolsSettled, OperationsSettled, ApprovalsSettled) =
            (turnsSettled, toolsSettled, operationsSettled, approvalsSettled);
    }
}

// Observed phases only, not a platform driver or desired-state scheduler.
public sealed class RunLifecycle
{
    public RunState State { get; }
    public string? EvidenceReference { get; }
    public string? SettlementEvidenceReference { get; }
    public bool AcceptsWork => State is RunState.Running or RunState.WaitingForInput or RunState.Idle;
    public static RunLifecycle Requested() => new(RunState.Requested);

    private RunLifecycle(RunState state, string? evidenceReference = null, string? settlementEvidenceReference = null)
        => (State, EvidenceReference, SettlementEvidenceReference) = (state, evidenceReference, settlementEvidenceReference);

    public RunLifecycle Advance(RunState next)
    {
        Guard.Defined(next);
        bool active = State is RunState.Running or RunState.WaitingForInput or RunState.Idle;
        bool allowed = (State, next) switch
        {
            (RunState.Requested, RunState.Provisioning) => true,
            (RunState.Provisioning, RunState.Starting) => true,
            _ when active && next != State && next is RunState.Running or RunState.WaitingForInput or RunState.Idle => true,
            _ when active && next == RunState.Pausing => true,
            (RunState.Suspended, RunState.Resuming) => true,
            _ => false
        };
        Guard.Require(allowed, "invalid_transition", "The lifecycle transition is not permitted.");
        return new(next);
    }

    public RunLifecycle MarkReady(ReadinessEvidence evidence, RunState next = RunState.Running)
    {
        Guard.Defined(next);
        Guard.Require(State is RunState.Starting or RunState.Resuming && next is RunState.Running or RunState.Idle,
            "invalid_transition", "Readiness requires a starting or resuming run and an active target.");
        Guard.Require(evidence is not null, "evidence_required", "Readiness evidence is required.");
        Guard.Text(evidence!.Reference);
        Guard.Require(evidence.LeaseCurrent && evidence.ConfigurationValidated && evidence.ProtectedGovernanceReady &&
            evidence.CredentialsRotated && evidence.NetworkValidated && evidence.SessionValidated,
            "evidence_required", "Lease, trust, credentials, network and session validation must succeed.");
        return new(next, evidence.Reference);
    }

    public RunLifecycle Suspend(SuspensionEvidence evidence)
    {
        Guard.Require(State == RunState.Pausing, "invalid_transition", "Suspension requires a pausing run.");
        Guard.Require(evidence is not null, "evidence_required", "Suspension evidence is required.");
        Guard.Text(evidence!.Reference);
        Guard.Require(evidence.Quiescent && evidence.DurableCheckpoint && evidence.PlatformSuspended,
            "evidence_required", "Quiescence, durable checkpoint and observed suspension are required.");
        return new(RunState.Suspended, evidence.Reference);
    }

    public RunLifecycle Stop()
    {
        Guard.Require(State is not (RunState.Completed or RunState.Failed),
            "invalid_transition", "Terminal runs cannot be stopped.");
        return State == RunState.Stopping ? this : new(RunState.Stopping);
    }

    public RunLifecycle BeginFinalization(SettlementEvidence evidence)
    {
        Guard.Require(State == RunState.Stopping, "invalid_transition", "Finalization requires a stopping run.");
        Guard.Require(evidence is not null, "evidence_required", "Referenced settlement evidence is required.");
        Guard.Require(evidence!.TurnsSettled && evidence.ToolsSettled && evidence.OperationsSettled && evidence.ApprovalsSettled,
            "evidence_required", "All turns, tools, operations and approvals must be settled.");
        return new(RunState.Finalizing, evidence.Reference, evidence.Reference);
    }

    public RunLifecycle Complete(FinalizationEvidence evidence)
    {
        Guard.Require(State == RunState.Finalizing, "invalid_transition", "Completion requires finalizing.");
        Guard.Require(evidence is not null, "evidence_required", "Verified finalization evidence is required.");
        Guard.Text(evidence!.Reference);
        Guard.Require(evidence.StatePersisted && evidence.ContextSynchronized && evidence.RequiredSourcesReconciled &&
            evidence.SignedManifestIndependentlyVerified, "evidence_required", "State and audit finalization must be verified.");
        return new(RunState.Completed, evidence.Reference, SettlementEvidenceReference);
    }

    public RunLifecycle RecordFailure(string evidenceReference, bool requiresRecovery)
    {
        Guard.Require(State is not (RunState.Completed or RunState.Failed),
            "invalid_transition", "Terminal runs are immutable.");
        return new(requiresRecovery ? RunState.RecoveryRequired : RunState.Failed, Guard.Text(evidenceReference));
    }
}
