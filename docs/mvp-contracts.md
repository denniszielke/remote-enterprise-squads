# P01 MVP contract baseline

Status: **implementation draft, pending team contract review**. Starting work does not approve these contracts.
Steve owns this draft; component owners must review it, Livingston independently verifies implementation evidence, and Linus reviews security-sensitive contracts. No accepted ADR or delivered capability is asserted.

Authority: [goals](../goal.md), [PRD](../prd.md), [TDD](../tdd.md), [implementation proposal](../mvp-implementation-proposal.md), [workflow](../.squad/workflow.md), [constraints](../.squad/constraints.md). PRD behaviour is not weakened by this draft.
The charter-referenced `.squad/decisions.md` was absent when read; no decision or approval is inferred.
The architecture-research helper is unavailable; no external platform evidence was obtained. This document refines existing TDD interfaces only; current Azure/CLI/SDK support remains gated.

## 1. Scope and review boundary

M0/M1: one pilot project/repository/team version, one authoritative session and team writer; a second isolated project is a negative-test fixture.
The user approved this narrow product scope on 2026-11-12. This is not approval
of the technical contracts below, capability claims or pilot release.
All agents execute in ACA Sandboxes; the control plane is not an agent host. No cloud API/schema/flag is invented here.
P01 remains draft until cross-component review; dependent implementation must not treat draft choices as accepted decisions.
See the [evidence matrix](./mvp-evidence-matrix.md) for proof obligations, owners and unresolved TDD validation gates (VG).

M2 exclusions: workspace/editor attach, AHP (disabled), ACP (optional/disabled), dynamic MCP registry, external/alternate memory, broad integration API and lifecycle webhook products.
M2/M3 exclusions: general team import/export, migrations, sprint catalogue/closure automation.
M3 exclusions: cost attribution, factory quotas/scale and unattended backlog execution (disabled by default); five projects/50 runs is not M1 acceptance.
The pilot still needs a bounded compute/run limit, approved retention/residency and full applicable governance. No production retention lock is authorized.
Any M1 GitHub webhook must already pass signature, repository-scope and delivery-deduplication checks.
Disabled MCP/memory sources are explicitly inventoried as disabled, never silently omitted from audit requirements.

## 2. Versioned envelopes and authorization

Proposed product API prefix `/v1`; JSON envelopes use `schema_version: "1.0"`. These are internal proposals, not upstream protocol support claims.
Unknown major versions fail with `unsupported_version`; additive optional minor fields are tolerated, but unknown commands/enums/policy outcomes fail closed.
All identifiers below are synthetic; timestamps are UTC. W3C trace context is separate from identity; content capture is off by default.
Persist content only under approved privacy/access policy, using protected references/hashes where appropriate. Never put tokens, secrets or user identities in URLs/logs.

Request: `request_id`, `schema_version`, `project_id`, optional `run_id`, `operation`, `payload`; retryable creates/actions also require an `Idempotency-Key` header.
Actor/client identity comes from authenticated server context, not caller-supplied actor fields. Authenticate every connection, including private runner channels.
Resolve resource ownership and authorize project/action before reading state, replaying an idempotency result, streaming or issuing an artifact reference.
Viewer: scoped read only; Operator: start/steer/answer/lifecycle; Administrator: approved configuration plus Operator actions.
Platform roles never bypass repository protections or independently authorize security-policy exceptions. Editable attachment is unavailable in M1.

Example `POST /v1/runs/run-demo-001/messages`, header `Idempotency-Key: msg-demo-001`:
```json
{"schema_version":"1.0","request_id":"req-demo-001","project_id":"proj-demo-a","run_id":"run-demo-001","operation":"message","payload":{"recipient":{"kind":"member","member_id":"member-demo-01"},"delivery_mode":"immediate","text":"Inspect the synthetic fixture before changing it."}}
```

Command envelope: `schema_version`, server-generated `command_id`, scope, authenticated `actor_ref`/client, request correlation, lease generation, operation, protected payload reference/digest, durable status and UTC timestamps.
Synthetic internal dispatch example (references/digests must resolve to validated durable records, never caller assertions):
```json
{"schema_version":"1.0","command_id":"cmd-demo-001","request_id":"req-demo-001","project_id":"proj-demo-a","run_id":"run-demo-001","actor_ref":"subject-demo-01","client_id":"client-demo-01","lease_generation":7,"operation":"steer","payload_ref":"payload-demo-001","payload_digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","status":"accepted","created_at":"2026-11-11T21:00:00Z"}
```
Recipient kinds: `coordinator`, `work_monitor`, `member` (requires a member in the pinned team version). Modes: `immediate`, `enqueue`. Work-monitor routing grants no permission or merge authority.
Runner operations map to TDD `steer`, `enqueue`, `abort`, `decide`, `answer` and monitor commands; lifecycle endpoints remain control-plane-owned.
Durable acceptance returns HTTP 202 and an authorized status resource, not a behaviour-completion claim:
```json
{"schema_version":"1.0","request_id":"req-demo-001","project_id":"proj-demo-a","run_id":"run-demo-001","command_id":"cmd-demo-001","status":"accepted","status_url":"/v1/runs/run-demo-001/commands/cmd-demo-001"}
```

Command states: `accepted` (intent durably recorded), `applied` (authoritative adapter confirms the operation at its supported boundary), `failed` (known not applied), `uncertain` (may have applied).
`applied` steering is not task completion and never means an executing tool was interrupted. Report supported delivery boundary and execution correlation separately.
An uncertain state may resolve only with retained reconciliation evidence; operators see it until then. Known terminal results are immutable.

### Idempotency and mismatch errors

Scope keys by authenticated client/subject, project, operation and target resource (including run), plus key. Canonicalize validated semantic payload and bind its digest and schema major version.
Exclude transport-only request/trace IDs from that digest. Persist reservation, intent and acceptance together within the command aggregate; do not dispatch before commit.
Matching retries return the same command/run/decision and recorded status, including across restart. Different semantic payload with the same scoped key returns 409 `idempotency_mismatch`, with no new side effect.
Concurrent equal submissions converge on one result. Durable key/result retention must cover the supported retry/recovery window; never silently expire keys into duplicate execution. Unavailable/expired deduplication evidence fails closed.
Approval IDs, webhook delivery IDs and fenced write receipts provide additional durable deduplication; HTTP keys alone do not ensure exactly-once execution.
Reauthorize retries against current permissions; do not disclose the original payload, actor or inaccessible target on errors.

Structured errors use `application/problem+json`: safe `code`, HTTP `status`, `request_id`, `trace_id`, `retryable`, safe detail and recovery instruction; never echo secrets.
```json
{"schema_version":"1.0","status":409,"code":"idempotency_mismatch","request_id":"req-demo-002","trace_id":"trace-demo-002","retryable":false,"detail":"Key already binds a different request.","recovery":"Inspect the authorized command status; use a new key only for a new intent."}
```

| HTTP / code | Required meaning |
|---|---|
| 400 / `invalid_request`, `unsupported_version` | Invalid schema/recipient/mode; no dispatch. Name missing configuration without values. |
| 401 / `authentication_required` | No valid identity; reconnect/refresh out of band, not through URL credentials. |
| 403 / `forbidden` | Authenticated project member lacks action role; Viewer mutation denied. |
| 404 / `resource_not_found` | Missing or inaccessible cross-project resource; do not reveal its existence. |
| 409 / `scope_mismatch`, `invalid_transition` | Authorized resource conflicts with supplied context/state; no mutation. Inaccessible resources still return non-disclosing 404. |
| 409 / `team_writer_conflict`, `lease_fenced` | Competing writer or stale generation; no retry into a second writer. |
| 409 / `approval_resolved`, `approval_expired` | Conflicting answer or expired request cannot replace the recorded result. |
| 409 / `action_mismatch`, `idempotency_mismatch` | Action/policy digest or key binding differs; no execution. |
| 503 / `durability_unavailable`, `reconciliation_required` | Required evidence/state unavailable; block dispatch and give safe status/recovery path. |

## 3. Ordered events, replay and streams

Proposed event envelope (AG-UI-compatible payloads require conformance testing, not renaming arbitrary events):
```json
{"schema_version":"1.0","event_id":"evt-demo-001","project_id":"proj-demo-a","run_id":"run-demo-001","sequence":42,"source_id":"runner-demo-001","source_sequence":17,"type":"command.applied","occurred_at":"2026-11-11T21:00:00Z","recorded_at":"2026-11-11T21:00:01Z","command_id":"cmd-demo-001","trace_id":"trace-demo-001","payload":{"delivery_boundary":"next_supported_input_boundary"}}
```

The control-plane durable run-stream aggregate is the sole proposed allocator of run-wide `sequence`; scope is `(project_id, run_id)`, strictly increasing, never reused.
Allocate sequence and append the event atomically; runner journal entries retain stable source record IDs/counters until publication. Runner durable acceptance and stream publication are separate receipts.
Runner and control-plane producers never independently allocate the same global sequence. Deduplicate `(source_id, source_sequence)` before assigning one run sequence; retries preserve event identity.
Producer restart preserves its counter or registers a new source epoch linked to the old one; no silent reset. Ordered append is not a claim of causal/global ordering across audit producers.
SSE event ID encodes the run sequence; reconnect supplies last seen sequence, then receives events strictly after it. Authenticate and reauthorize on every subscription/reconnect.
Clients deduplicate event IDs/sequences and apply versioned transitions once. Disconnect on token expiry; refresh then reconnect with the durable cursor.
If cursor history is unavailable, return an authorized snapshot with `snapshot_sequence`, replay boundary and explicit missing range; continue after that boundary without silently skipping state.
Logs have separate source/cursor/retention and explicit gaps; neither logs nor AHP/OTLP subscriptions are the control transaction log.
Bound subscriber buffers; slow consumers disconnect/resynchronize without blocking the authoritative session or dropping required audit records.

## 4. Approval and input atomicity

Requests include `request_id`, kind (`permission`, `question`, `plan`), project/run, target, reason, action digest, policy/version, authorized scope, creation/expiry and concurrency token.
For permission requests, set authoritative `expires_at = created_at + 300 seconds`; at or after expiry the answer is denial, including after outage/restart. Clients cannot extend deadlines.
Questions/plans expose their explicit expiry; a plan/question response never implicitly grants a tool permission.
Responses bind request/action/policy digests and an idempotency key. Recheck authorization, pending state, deadline and bindings within conditional resolution.
In one approval aggregate transaction, record exactly one winning answer or timeout denial, decision receipt and dispatch intent. A timeout and human answer race on the same pending version.
Matching duplicate answers return the recorded result; conflicting answers return `approval_resolved`; expired requests remain denied.
The runner consumes a stable decision receipt once for its bound action/generation; approval success is not a second permission to execute on retry.
Decision persistence and SDK/tool execution are not one transaction. Crash after dispatch without outcome evidence yields `uncertain`, never automatic re-execution.
Publish resolution to all clients; remove stale pending cards on replay/snapshot. Use scoped actor display references, not broadcast personal identity.
Unknown, errored, timed-out or unbound policy results deny unless a still-valid explicit scoped approval exists. Test hook timeout against the SDK deny backstop.

## 5. Nonblocking dispatch and recovery

Use the TDD single-reader mailbox to serialize session-control mutations and preserve command arrival IDs/order.
Start/track model turns asynchronously: never await a whole turn or human callback inside the mailbox reader.
Permission/input callbacks wait on separately managed completion handles; `decide`, `answer`, `abort` and stop handling remain deliverable while a callback waits.
Queued messages retain explicit order and are applied only at validated supported boundaries; immediate steering is best-effort, not interruption of an already-running tool.
Persist intent before SDK/shell dispatch and journal observations after dispatch. Distinguish accepted, adapter-acknowledged and observed execution.
On restart, reconcile intent, persistent session/journal, SDK state and repository/process evidence. Do not equate missing acknowledgement with non-execution.
Ambiguous SDK/shell outcomes remain uncertain; bounded reconciliation/manual recovery is required before repeating a potentially executed side effect.
No exactly-once shell/SDK guarantee is claimed; mock mailbox tests cannot close current compatibility gate VG-04.

## 6. Lifecycle and team fencing

Registry stores desired and observed state separately, operation/command IDs, generation and state version; accepted lifecycle requests are not completed transitions.
Normal path: `requested -> provisioning -> starting -> running`; ready only after scope, image/team/compatibility, protected policy, egress, audit and session checks pass.
`running`, `waiting_for_input` and `idle` reflect authoritative session activity; waiting for a permission is not proof of safe auto-suspension.
Cancel-current-turn requests `abort`; it does not stop the run, discard prior work or promise child-process termination. Record observed settlement/uncertainty.
Pause enters `pausing`, blocks new turns and waits for authoritative quiescence/checkpoint. An active tool may finish; pause is not cancellation.
Never report `suspended` until no active turn/tool is unresolved, durable state/evidence is safe and supported platform suspension is observed.
Disable automatic suspension during active work; a heartbeat is not proof. Unsupported active-turn controls block readiness (VG-02).
Resume: `suspended -> resuming -> running/idle` only after lease, policy/trust/credential rotation, network and session validation; no open-egress interval.
Stop from any nonterminal state enters `stopping`, rejects new work, settles/cancels pending work and approvals, then `finalizing`.
Finalizing persists allowed repository state, synchronizes only enabled approved context, drains/reconciles required audit sources and verifies the signed manifest.
Only successful state/audit finalization permits `completed`; failures/uncertainties produce visible `recovery_required`, retaining evidence and a recovery operation.
Known failures may use `failed`, with evidence status still explicit; neither `failed` nor compute deletion substitutes for audit closure.
Cleanup follows reviewed bounded retention/recovery policy; disconnection or a stop acknowledgement never authorizes deleting unsealed evidence.

Team lease scope is `(project_id, team_id)` with holder run, monotonically increasing generation, expiry and CAS renewal token; work claims add repository/ref checks.
Record run/lease intent durably before requesting compute; reconcile failures across the separate lease, run and sandbox aggregates. No cross-service transaction is assumed.
Every shared-state write, command dispatch and repository publication validates current lease generation/claim. A stale runner stops issuing tools and writes on lease loss.
Fencing durable writes alone does not fence an already-running child process or a remote git side effect.
Before a successor writer becomes ready, terminate/isolate the old process tree through validated mechanisms and reconcile filesystem/worktree/ref and pending side effects.
If in-flight fencing/termination cannot be proved, retain a blocked/recovery state and deny competing readiness even after lease expiry. Never infer safety from a missed heartbeat.

## 7. Fail-closed evidence and completion

Required source inventory covers steering, decisions/execution, approvals, connections, lifecycle, configuration, MCP, memory, git and egress; producer coverage includes CLI, orchestration, runner, governance, platform and control plane.
For each enabled source, register source/epoch, stable record IDs, sequence domain, expected event categories, durable receipt/checkpoint mechanism and shutdown/final watermark.
An unavailable source with required activity is a gap, not an empty source. Disabled features require explicit configuration evidence; zero observed records is not proof of zero actions.
Journal/outbox must durably accept required intent/evidence before governed action dispatch or durable acknowledgement. An OTLP send is not a durable audit receipt.
Bound disk/outbox queues; on durability failure/full backlog reject new governed actions, mark degraded/not-ready and alert. Never drop audit records to keep execution moving.
Safety-only cancellation/termination may continue to contain harm, but missing safety-action evidence remains a gap requiring recovery; it cannot produce successful completion.
Audit ingestion deduplicates stable IDs, canonicalizes versioned record bytes and serializes SHA-256 chains per run/source; checkpoint append position/content before retry after ambiguous timeouts.
Manifest records scope, image/team/config/policy/compatibility versions, each source/epoch, counts, sequence boundaries, chain heads, reconciliation references, gaps and signing-key version/signature.
Before sealing, reconcile each source's expected records/watermarks with durable receipts and independent platform/egress exports; document late arrivals and incomplete exporters.
**Hashes prove linkage/integrity of supplied records, not completeness.** Missing source guarantees/checkpoints (VG-06/12) block a complete manifest claim even if every supplied hash verifies.
An independent verifier checks signature/key provenance, canonical hashes, duplicate/missing IDs, counts/ranges, inventory coverage and external completeness references. Authors are not their sole verifiers.
Exactly 100% of tested completed runs must have complete verified manifests; a failed check prevents completion/release, not just a warning.
Review pilot retention/residency/privacy before evidence collection; production irreversible locking is excluded. No credentials in run images/files/environment or routine telemetry.
