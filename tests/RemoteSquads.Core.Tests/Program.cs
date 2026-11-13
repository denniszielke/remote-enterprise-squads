using System.Text.Json;
using RemoteSquads.Core;

var now = new DateTimeOffset(2026, 11, 11, 22, 0, 0, TimeSpan.Zero);
var binding = new PermissionBinding("action-digest-1", "policy-1", 7);
var ready = new ReadinessEvidence("ready-evidence", true, true, true, true, true, true);
var sealedEvidence = new FinalizationEvidence("verified-manifest", true, true, true, true);
var settled = new SettlementEvidence("settlement-receipt", true, true, true, true);
RunLifecycle Starting() => RunLifecycle.Requested().Advance(RunState.Provisioning).Advance(RunState.Starting);
RunLifecycle Running() => Starting().MarkReady(ready);
PermissionRequest Permission() => new("permission-1", binding, now);
Command Accepted() => Command.Accept("command-1",
    new MessageTarget(new PinnedTeam("team-1", ["member-1"]), RecipientKind.Coordinator, DeliveryMode.Immediate));

var tests = new (string Name, Action Run)[]
{
    ("Writer leases reject competing starts and conditional renewal conflicts", () =>
    {
        var scope = new TeamScope("project-1", "team-1");
        var empty = new TeamLease(scope, now);
        var lease = empty.Acquire("run-1", 0, now, TimeSpan.FromMinutes(1));
        Equal(1L, lease.Generation);
        Equal(1L, lease.Version);
        Check(empty.HolderRunId is null);
        lease.RequireWriter(scope, "run-1", 1, now);
        Error("team_writer_conflict", () => lease.Acquire("run-2", 1, now, TimeSpan.FromMinutes(1)));
        Error("version_conflict", () => lease.Renew("run-1", 1, 0, now, TimeSpan.FromMinutes(2)));
        Error("lease_fenced", () => lease.Renew("other-run", 1, 1, now, TimeSpan.FromMinutes(2)));
        Error("lease_fenced", () => lease.Renew("run-1", 2, 1, now, TimeSpan.FromMinutes(2)));
        Error("invalid_request", () => lease.Renew("run-1", 1, 1, now, TimeSpan.FromSeconds(30)));
        var renewed = lease.Renew("run-1", 1, 1, now.AddSeconds(10), TimeSpan.FromMinutes(1));
        Equal(1L, renewed.Generation);
        Equal(2L, renewed.Version);
        Equal(now.AddSeconds(70), renewed.ExpiresAt!.Value);
    }),
    ("Expired writers stay fenced until bound process and side-effect settlement", () =>
    {
        var scope = new TeamScope("project-1", "team-1");
        var lease = new TeamLease(scope, now).Acquire("run-1", 0, now, TimeSpan.FromSeconds(60));
        var expiry = now.AddSeconds(60);
        var evidence = new WriterSettlement("run-1", 1, "settlement-1", true, true);
        Error("lease_fenced", () => lease.RequireWriter(scope, "run-1", 1, expiry));
        Error("lease_fenced", () => lease.RequireWriter(new("other-project", "team-1"), "run-1", 1, now));
        Error("lease_fenced", () => lease.RequireWriter(new("project-1", "other-team"), "run-1", 1, now));
        Error("lease_fenced", () => lease.Renew("run-1", 1, 1, expiry, TimeSpan.FromMinutes(1)));
        Error("reconciliation_required", () => lease.Acquire("run-2", 1, expiry, TimeSpan.FromMinutes(1)));
        foreach (var invalid in new[] { evidence with { OldProcessesContained = false },
            evidence with { SideEffectsReconciled = false } })
            Error("reconciliation_required", () => lease.Acquire("run-2", 1, expiry, TimeSpan.FromMinutes(1), invalid));
        foreach (var invalid in new[] { evidence with { RunId = "other" }, evidence with { Generation = 2 } })
            Error("lease_fenced", () => lease.Acquire("run-2", 1, expiry, TimeSpan.FromMinutes(1), invalid));
        Error("invalid_request", () => lease.Acquire("run-2", 1, expiry, TimeSpan.FromMinutes(1),
            evidence with { EvidenceReference = " " }));
        var successor = lease.Acquire("run-2", 1, expiry, TimeSpan.FromMinutes(1), evidence);
        Equal(2L, successor.Generation);
        Equal("settlement-1", successor.SettlementReference!);
        successor.RequireWriter(scope, "run-2", 2, expiry);
        Error("lease_fenced", () => successor.RequireWriter(scope, "run-1", 1, expiry));
        Error("version_conflict", () => successor.Acquire("run-3", 1, expiry, TimeSpan.FromMinutes(1), evidence));
        var restarted = lease.Acquire("run-1", 1, expiry, TimeSpan.FromMinutes(1), evidence);
        Equal(2L, restarted.Generation);
    }),
    ("Lease timestamps and durations fail explicitly at invalid boundaries", () =>
    {
        var lease = new TeamLease(new("project", "team"), now);
        Error("invalid_request", () => lease.Acquire("run", 0, now, TimeSpan.Zero));
        Error("invalid_request", () => lease.Acquire("run", 0, now, TimeSpan.FromSeconds(-1)));
        Error("invalid_request", () => lease.Acquire("run", 0, DateTimeOffset.MaxValue, TimeSpan.FromTicks(1)));
        Error("invalid_request", () => lease.Acquire("run", 0, now.AddTicks(-1), TimeSpan.FromMinutes(1)));
        Error("invalid_request", () => lease.Acquire("run", 0, now.ToOffset(TimeSpan.FromHours(1)), TimeSpan.FromMinutes(1)));
        Error("invalid_request", () => lease.Acquire(" ", 0, now, TimeSpan.FromMinutes(1)));
    }),
    ("Idempotency retries bind scope semantic payload and schema, not transport IDs", () =>
    {
        var scope = new IdempotencyScope("subject", "client", "project", "steer", "run");
        var fingerprint = new RequestFingerprint(1, "sha256:" + new string('a', 64));
        var record = new IdempotencyRecord(scope, "key", fingerprint, "command-1", now, now.AddHours(1));
        Equal("command-1", record.Replay(new("subject", "client", "project", "steer", "run"),
            "key", new(1, fingerprint.PayloadDigest), now.AddMinutes(1)));
        Error("idempotency_mismatch", () => record.Replay(scope, "key", new(2, fingerprint.PayloadDigest), now));
        Error("idempotency_mismatch", () => record.Replay(scope, "key", new(1, "sha256:" + new string('b', 64)), now));
        foreach (var other in new[] {
            new IdempotencyScope("other", "client", "project", "steer", "run"),
            new("subject", "other", "project", "steer", "run"),
            new("subject", "client", "other", "steer", "run"),
            new("subject", "client", "project", "other", "run"),
            new("subject", "client", "project", "steer", "other") })
            Error("resource_not_found", () => record.Replay(other, "key", fingerprint, now));
        Error("resource_not_found", () => record.Replay(scope, "other", fingerprint, now));
        Error("reconciliation_required", () => record.Replay(scope, "key", fingerprint, now.AddHours(1)));
        Error("invalid_request", () => record.Replay(scope, "key", fingerprint, now.AddTicks(-1)));
        Error("invalid_request", () => record.Replay(scope, "key", fingerprint, now.ToOffset(TimeSpan.FromHours(1))));
    }),
    ("Idempotency bindings reject malformed fingerprints and retry windows", () =>
    {
        foreach (var invalid in new[] { "sha256:" + new string('A', 64), "sha256:" + new string('g', 64),
            "sha256:" + new string('a', 63), "other:" + new string('a', 64), " " })
            Error("invalid_request", () => new RequestFingerprint(1, invalid));
        var fingerprint = new RequestFingerprint(1, "sha256:" + new string('a', 64));
        var scope = new IdempotencyScope("subject", "client", "project", "start", "team");
        Error("invalid_request", () => new RequestFingerprint(0, fingerprint.PayloadDigest));
        Error("invalid_request", () => new IdempotencyRecord(scope, "key", fingerprint, "run", now, now));
        Error("invalid_request", () => new IdempotencyRecord(scope, " ", fingerprint, "run", now, now.AddHours(1)));
        Error("invalid_request", () => new IdempotencyRecord(scope, "key", fingerprint, " ", now, now.AddHours(1)));
    }),
    ("Recipients validate pinned membership and copied team data", () =>
    {
        var members = new List<string> { "member-1" };
        var team = new PinnedTeam("team-1", members);
        members.Add("late-member");
        Equal("member-1", new MessageTarget(team, RecipientKind.Member, DeliveryMode.Enqueue, "member-1").MemberId!);
        foreach (var kind in new[] { RecipientKind.Coordinator, RecipientKind.WorkMonitor })
            foreach (var mode in Enum.GetValues<DeliveryMode>()) _ = new MessageTarget(team, kind, mode);
        Error("invalid_request", () => new MessageTarget(team, RecipientKind.Member, DeliveryMode.Immediate, "late-member"));
        Error("invalid_request", () => new MessageTarget(team, RecipientKind.Member, DeliveryMode.Immediate));
        Error("invalid_request", () => new MessageTarget(team, RecipientKind.Coordinator, DeliveryMode.Enqueue, "member-1"));
        Error("invalid_request", () => new MessageTarget(team, (RecipientKind)99, DeliveryMode.Immediate));
        Error("invalid_request", () => new MessageTarget(team, RecipientKind.WorkMonitor, (DeliveryMode)99));
    }),
    ("Protocol enum names are snake case and reject unknown and numeric input", () =>
    {
        var options = Protocol.CreateJsonOptions();
        Equal("\"work_monitor\"", JsonSerializer.Serialize(RecipientKind.WorkMonitor, options));
        Equal(DeliveryMode.Enqueue, JsonSerializer.Deserialize<DeliveryMode>("\"enqueue\"", options));
        JsonError(() => JsonSerializer.Deserialize<DeliveryMode>("\"broadcast\"", options));
        JsonError(() => JsonSerializer.Deserialize<DeliveryMode>("1", options));
    }),
    ("All protocol enums require one exact supported wire value", () =>
    {
        StrictWireValues<RecipientKind>();
        StrictWireValues<DeliveryMode>();
        StrictWireValues<CommandStatus>();
        StrictWireValues<PermissionDecision>();
        StrictWireValues<RunState>();
        var options = Protocol.CreateJsonOptions();
        JsonError(() => JsonSerializer.Deserialize<RecipientKind>("\"coordinator, work_monitor\"", options));
        JsonError(() => JsonSerializer.Deserialize<RecipientKind>("\"member, work_monitor\"", options));
        JsonError(() => JsonSerializer.Serialize((RecipientKind)3, options));
    }),
    ("Commands require reconciliation for uncertainty and preserve terminal results", () =>
    {
        var accepted = Accepted();
        var uncertain = accepted.Observe(CommandStatus.Uncertain);
        Equal(CommandStatus.Accepted, accepted.Status);
        Error("invalid_transition", () => uncertain.Observe(CommandStatus.Applied));
        Error("reconciliation_required", () => uncertain.Reconcile(null!));
        Error("action_mismatch", () => uncertain.Reconcile(new("other-command", CommandStatus.Applied, "journal")));
        Error("invalid_request", () => new CommandReconciliation("command-1", CommandStatus.Uncertain, "journal"));
        foreach (var outcome in new[] { CommandStatus.Applied, CommandStatus.Failed })
        {
            var terminal = uncertain.Reconcile(new("command-1", outcome, "journal"));
            Equal(outcome, terminal.Status);
            Error("invalid_transition", () => terminal.Observe(CommandStatus.Uncertain));
            Error("invalid_transition", () => terminal.Reconcile(new("command-1", outcome, "journal")));
            Equal(outcome, accepted.Observe(outcome).Status);
        }
        Error("invalid_request", () => accepted.Observe((CommandStatus)99));
    }),
    ("Permission matching retries are stable and cannot replace a winner", () =>
    {
        var pending = Permission();
        var won = pending.Resolve(binding, "response-1", PermissionDecision.Allow, 0, now.AddSeconds(299));
        Equal(1L, won.Version);
        Equal(PermissionDecision.Allow, won.Resolution!.Decision);
        Check(ReferenceEquals(won, won.Resolve(binding, "response-1", PermissionDecision.Allow, 0, now.AddSeconds(600))));
        Error("approval_resolved", () => won.Resolve(binding, "response-2", PermissionDecision.Deny, 1, now.AddSeconds(299)));
        Error("approval_resolved", () => won.Resolve(binding, "response-1", PermissionDecision.Deny, 1, now.AddSeconds(299)));
        Check(pending.Resolution is null);
        Check(ReferenceEquals(won, won.Expire(0, now.AddSeconds(301))));
    }),
    ("Permission bindings and conditional versions fail closed", () =>
    {
        var pending = Permission();
        foreach (var mismatch in new[] { new PermissionBinding("other", "policy-1", 7),
            new PermissionBinding("action-digest-1", "other", 7), new PermissionBinding("action-digest-1", "policy-1", 8) })
            Error("action_mismatch", () => pending.Resolve(mismatch, "response", PermissionDecision.Allow, 0, now));
        Error("version_conflict", () => pending.Resolve(binding, "response", PermissionDecision.Allow, 1, now));
        Error("invalid_request", () => pending.Resolve(binding, "response", (PermissionDecision)99, 0, now));
        Error("invalid_request", () => pending.Resolve(binding, "response", PermissionDecision.Allow, 0, now.AddTicks(-1)));
        Error("invalid_request", () => new PermissionBinding("action", "policy", 0));
    }),
    ("Permission expiration denies at the exact 300 second boundary and after outage", () =>
    {
        foreach (var seconds in new[] { 300, 3600 })
        {
            var denied = Permission().Resolve(binding, "late-response", PermissionDecision.Allow, 0, now.AddSeconds(seconds));
            Equal(PermissionDecision.Deny, denied.Resolution!.Decision);
            Check(denied.Resolution.TimedOut);
            Check(ReferenceEquals(denied, denied.Resolve(binding, "late-response", PermissionDecision.Allow, 0, now.AddSeconds(seconds))));
            Error("approval_expired", () => denied.Resolve(binding, "other-response", PermissionDecision.Allow, 1, now.AddSeconds(seconds)));
        }
        var timedOut = Permission().Expire(0, now.AddSeconds(300));
        Equal(PermissionDecision.Deny, timedOut.Resolution!.Decision);
        Error("approval_expired", () => timedOut.Resolve(binding, "response", PermissionDecision.Allow, 1, now.AddSeconds(301)));
        Error("invalid_transition", () => Permission().Expire(0, now.AddSeconds(299)));
        Error("version_conflict", () => Permission().Expire(1, now.AddSeconds(300)));
    }),
    ("UTC and deadline overflow inputs are explicitly rejected", () =>
    {
        Error("invalid_request", () => new PermissionRequest("permission", binding, now.ToOffset(TimeSpan.FromHours(1))));
        Error("invalid_request", () => new PermissionRequest("permission", binding, DateTimeOffset.MaxValue));
        Error("invalid_request", () => Permission().Expire(0, now.AddSeconds(300).ToOffset(TimeSpan.FromHours(1))));
    }),
    ("Stop and finalization are separate and completion needs verified evidence", () =>
    {
        var running = Running();
        var stopping = running.Stop();
        Check(!stopping.AcceptsWork);
        Equal(RunState.Running, running.State);
        Error("invalid_transition", () => stopping.Complete(sealedEvidence));
        var finalizing = stopping.BeginFinalization(settled);
        Error("invalid_transition", () => finalizing.Advance(RunState.Completed));
        Error("evidence_required", () => finalizing.Complete(null!));
        Error("evidence_required", () => finalizing.Complete(sealedEvidence with { SignedManifestIndependentlyVerified = false }));
        Error("evidence_required", () => finalizing.Complete(sealedEvidence with { StatePersisted = false }));
        Error("evidence_required", () => finalizing.Complete(sealedEvidence with { RequiredSourcesReconciled = false }));
        var completed = finalizing.Complete(sealedEvidence);
        Equal(RunState.Completed, completed.State);
        Error("invalid_transition", () => completed.Stop());
        Error("invalid_transition", () => completed.RecordFailure("failure", true));
    }),
    ("Finalization rejects unresolved work approvals and missing settlement references", () =>
    {
        var stopping = Running().Stop();
        Error("invalid_transition", () => stopping.Advance(RunState.Finalizing).Complete(sealedEvidence));
        Error("evidence_required", () => stopping.BeginFinalization(null!));
        Error("invalid_request", () => stopping.BeginFinalization(new(" ", true, true, true, true)));
        foreach (var invalid in new[] { new SettlementEvidence("turns-unresolved", false, true, true, true),
            new("tools-unresolved", true, false, true, true), new("operations-unresolved", true, true, false, true),
            new("approvals-unresolved", true, true, true, false) })
            Error("evidence_required", () => stopping.BeginFinalization(invalid));
        Error("invalid_transition", () => Running().BeginFinalization(settled));
        var finalizing = stopping.BeginFinalization(settled);
        Equal(RunState.Stopping, stopping.State);
        Equal(RunState.Finalizing, finalizing.State);
        Equal(settled.Reference, finalizing.SettlementEvidenceReference!);
        var completed = finalizing.Complete(sealedEvidence);
        Equal(settled.Reference, completed.SettlementEvidenceReference!);
        Equal(sealedEvidence.Reference, completed.EvidenceReference!);
        Equal(RunState.Completed, completed.State);
    }),
    ("Suspension requires quiescence durable checkpoint and observed platform suspension", () =>
    {
        var pausing = Running().Advance(RunState.Pausing);
        var evidence = new SuspensionEvidence("checkpoint", true, true, true);
        Error("invalid_transition", () => Running().Suspend(evidence));
        Error("invalid_transition", () => pausing.Advance(RunState.Suspended));
        foreach (var invalid in new[] { evidence with { Quiescent = false },
            evidence with { DurableCheckpoint = false }, evidence with { PlatformSuspended = false } })
            Error("evidence_required", () => pausing.Suspend(invalid));
        Equal(RunState.Suspended, pausing.Suspend(evidence).State);
    }),
    ("Startup and resume require revalidation without direct readiness shortcuts", () =>
    {
        Error("invalid_transition", () => Starting().Advance(RunState.Running));
        var resuming = Running().Advance(RunState.Pausing)
            .Suspend(new("checkpoint", true, true, true)).Advance(RunState.Resuming);
        Error("invalid_transition", () => resuming.Advance(RunState.Idle));
        foreach (var invalid in new[] { ready with { LeaseCurrent = false }, ready with { ConfigurationValidated = false },
            ready with { ProtectedGovernanceReady = false }, ready with { CredentialsRotated = false },
            ready with { NetworkValidated = false }, ready with { SessionValidated = false } })
            Error("evidence_required", () => resuming.MarkReady(invalid));
        Equal(RunState.Idle, resuming.MarkReady(ready, RunState.Idle).State);
        Error("invalid_transition", () => resuming.MarkReady(ready, RunState.Completed));
        Error("invalid_request", () => resuming.Advance((RunState)99));
    }),
    ("Failure remains distinct from completion and recovery can stop for finalization", () =>
    {
        var failed = Running().RecordFailure("failure-evidence", false);
        Equal(RunState.Failed, failed.State);
        Error("invalid_transition", () => failed.Stop());
        Error("invalid_transition", () => failed.Advance(RunState.Running));
        var recovery = Running().Stop().BeginFinalization(settled).RecordFailure("missing-audit", true);
        Equal(RunState.RecoveryRequired, recovery.State);
        Error("invalid_transition", () => recovery.Complete(sealedEvidence));
        Equal(RunState.Completed, recovery.Stop().BeginFinalization(settled).Complete(sealedEvidence).State);
        Equal(RunState.Stopping, RunLifecycle.Requested().Stop().State);
    })
};

tests = [.. tests, .. CommandAdmissionTests.Cases, .. CosmosControlDocumentStoreTests.Cases,
    .. CommandEndpointTests.Cases, .. RunnerDeliveryTests.Cases];

int failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (AssertionFailure failure) { failures++; Console.Error.WriteLine($"FAIL {name}: {failure.Message}"); }
    catch (DomainException failure) { failures++; Console.Error.WriteLine($"FAIL {name}: unexpected domain error {failure.Code}"); }
    catch (JsonException) { failures++; Console.Error.WriteLine($"FAIL {name}: unexpected JSON error"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed; {failures} failed.");
return failures == 0 ? 0 : 1;

static void Check(bool condition)
{
    if (!condition) throw new AssertionFailure("Assertion did not hold.");
}
static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual));
static void Error(string code, Action action)
{
    try { action(); }
    catch (DomainException error) { Equal(code, error.Code); return; }
    throw new AssertionFailure($"Expected domain error {code}.");
}
static void JsonError(Action action)
{
    try { action(); }
    catch (JsonException) { return; }
    throw new AssertionFailure("Expected invalid protocol input to fail.");
}
static void StrictWireValues<T>() where T : struct, Enum
{
    var options = Protocol.CreateJsonOptions();
    var values = Enum.GetValues<T>();
    foreach (var value in values)
    {
        string wire = JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
        Equal($"\"{wire}\"", JsonSerializer.Serialize(value, options));
        Equal(value, JsonSerializer.Deserialize<T>($"\"{wire}\"", options));
        JsonError(() => JsonSerializer.Deserialize<T>($"\"{wire}, {wire}\"", options));
        JsonError(() => JsonSerializer.Deserialize<T>($"\" {wire}\"", options));
        JsonError(() => JsonSerializer.Deserialize<T>($"\"{wire.ToUpperInvariant()}\"", options));
    }
    string first = JsonNamingPolicy.SnakeCaseLower.ConvertName(values[0].ToString());
    string last = JsonNamingPolicy.SnakeCaseLower.ConvertName(values[^1].ToString());
    foreach (var invalid in new[] { $"\"{first}, {last}\"", "\"unsupported\"", "\"0\"", "0", "null" })
        JsonError(() => JsonSerializer.Deserialize<T>(invalid, options));
}
sealed class AssertionFailure(string message) : Exception(message);
