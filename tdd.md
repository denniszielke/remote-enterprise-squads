# Technical Design

Status: proposed design; capability validation and implementation remain outstanding.

## 1. Basis, authority and boundaries

This design implements [Product Requirements](./prd.md), which derives from [Vision and Goals](./goal.md). These documents are aligned but separately governed: a technology substitution does not redefine product acceptance.

The supplied technical assessment motivates separating execution from governance. The later squad specification and explicit runtime clarification establish ACA Sandboxes as the runtime for all agents, with a separate .NET control plane. Foundry is not an agent runtime in this design. The specification also establishes Python 3.13-only automation, Bicep provisioning, macOS-first development, approved feeds and evaluation ownership.

No fresh external capability validation was completed for this document. The architecture-research helper was unavailable. Source descriptions of service availability, versions, flags, quotas and preview behaviour must therefore be revalidated before use. API paths and internal schemas below are proposed product contracts, not representations of existing shipped services.

Personal names, cast nicknames, private account/resource identifiers, customer examples and local source paths are intentionally omitted. Product/protocol names remain where technically necessary. The built-in work monitor and scribe are referenced by function rather than persona.

## 2. Architecture principles and major decisions

| Decision | Approach and rationale | Requirements |
|---|---|---|
| TD-01 Execution isolation | One ACA sandbox per run; one group, dedicated network boundary and managed identity per project. Do not mix project secrets or disks. | FR-02, NFR-01/02 |
| TD-02 Single authority | The runner owns one Copilot client/coordinator session per run; the control plane holds a fenced team-writer lease. Client protocols never become independent owners. | FR-04/06/07, NFR-03 |
| TD-03 Explicit control plane | ACA-hosted ASP.NET Core services own lifecycle, brokering, authorization and evidence; all agents execute in ACA Sandboxes. Foundry is limited to optional non-runtime knowledge/governance services. | FR-01/05/15 |
| TD-04 Durable ordered events | A runner journal and control-plane durable records drive control/conversation replay and evidence. Telemetry subscriptions are not a transaction log. | FR-08/12/13 |
| TD-05 Layered enforcement | Managed settings, root-owned tool hook, SDK deny backstop, network policy and repository protection address different failure modes. | FR-10/11, NFR-01 |
| TD-06 Declarative resources | Bicep owns persistent resources/configuration; Python owns orchestration and deployment. Unsupported persistent settings require an explicit reviewed exception. | NFR-09/12 |
| TD-07 Evidence-based releases | Pin compatibility sets, evaluate behaviour changes and require independent verification. Experimental adapters remain feature-gated. | FR-22/23, NFR-12/13 |

## 3. Technology stack

| Layer | Selection | Constraint |
|---|---|---|
| Runner, control plane, memory MCP service | C# / .NET 10 / ASP.NET Core | Validate packages through approved NuGet sources; no historical package pin is assumed eligible. |
| Agent integration | GitHub Copilot SDK/CLI; existing Squad coordinator/state | Keep team behaviour intact; no bring-your-own-model integration. |
| Team hosting abstraction | Squad.Agents.AI when compatible; otherwise direct SDK/Agent Framework composition | Prove permission override and steering/session access before adoption; never inherit approve-all defaults. |
| Console | React / TypeScript | AG-UI-compatible conversation events plus a separate correlated log stream. |
| Automation and CLI operations | Python 3.13 | Local virtual environment; portable macOS workflow; checked subprocesses and SDK calls. |
| Infrastructure | Bicep; suitable Azure Verified Modules plus thin local modules | Verify ARM API support, including preview gaps; no hidden imperative resource provisioning. |
| Execution and hosting | ACA Sandboxes; private ACA-hosted platform services | Verify region, networking, data-plane connectivity and quotas before deployment. |
| Durable registry and memory | Cosmos DB proposed; separately scoped control and memory data | Partition for project scope and transactional aggregate operations; vector/full-text capability must be validated. |
| Audit/artifacts | Private Azure Blob Storage; signing key in Key Vault | Validate protected append writes and immutability compatibility before committing the storage layout. |
| Observability | OpenTelemetry collectors/gateway; project OTLP destination | Account for producer-specific protocols and missing native raw-log export. |

## 4. Components and trust boundaries

```text
Authorized console / API client / editor
                  |
          authenticated broker
                  |
       Private control plane -------- durable registry / team lease
          |       |     |
          |       |     +------------ audit writer -> immutable store / signing
          |       +------------------ approved MCP registry / memory service
          |
     project sandbox group
       run microVM
         runner -> owned Copilot SDK session -> CLI / Squad / worktrees
           |                  |
           |                  +-> managed settings + policy hook + deny backstop
           +-> journal / durable outbox
           +-> local telemetry collector
           +-> loopback AHP / optional ACP adapter
                  |
        deny-by-default egress proxy -> approved GitHub / feeds / MCP
                  |
           telemetry gateway -> console logs + project OTLP destination
```

The diagram is logical. Verify the supported network route for each connection; it does not assert that a particular preview service has a private endpoint or supports a chosen forwarding path.

### Component responsibilities

- **Control plane:** project settings, run registry, fenced leases, durable commands, lifecycle reconciliation, role enforcement, approval arbitration, attachment brokering, GitHub events and sprint/team catalogue.
- **Runner:** authoritative session, nonblocking serialized command dispatch, tool/input callbacks, append-only journal, agent state, persistence, health/readiness and active-turn lifecycle signals.
- **Console:** run/sprint navigation, targeted conversation, steer/queue state, approvals/questions, member status, log filtering and attach/control affordances. It never directly obtains sandbox credentials.
- **Governance:** signed/versioned policy distribution, managed settings, AGT-compatible tool hook and permission backstop. Policies are protected from agent modification.
- **Memory/MCP service:** scoped memory operations, provenance/classification, backend adapters and checkpoint synchronization. Registry approval is separate from project selection.
- **Audit writer:** durable ordered ingestion, deduplication, per-source hash chains, completeness tracking, signed final manifest and authorized retrieval.
- **Telemetry gateway:** resource enrichment, redaction, buffering and fan-out. It can forward observations to the audit ingestion path but cannot be the only source of critical audit records.
- **Python delivery tools:** environment validation, package eligibility, What-If, deployment, remote builds, digest registration, smoke checks and operational commands.

## 5. Run lifecycle, concurrency and recovery

### Lifecycle

Proposed states: `requested -> provisioning -> starting -> running`.

Additional states include `waiting_for_input`, `idle`, `pausing`, `suspended`, `resuming`, `stopping`, `finalizing`, `completed`, `failed` and `recovery_required`. The registry records desired and observed state separately. The UI derives actions from authorized transitions, not optimistic local guesses.

1. Validate project, role, image/team/configuration versions, quota and idempotency key.
2. Acquire a durable team-writer lease with a monotonically increasing fencing generation; create the run record before requesting compute.
3. Create/fork the sandbox with project identity/network, protected policy, egress deny rules and telemetry already attached. Do not temporarily open egress to install tooling.
4. Runner verifies policy/configuration, persistent session directory, repository state and compatibility set before readiness.
5. Route commands through the broker to the runner. Record command intent before execution; publish sequenced results and maintain client-visible delivery status.
6. Disable auto-suspend during active turns; re-enable only after authoritative idle. A heartbeat alone is not proof that the service considers the sandbox active.
7. On stop, reject new work, settle/cancel outstanding operations, push permitted repository state, synchronize approved memory and drain audit records.
8. Seal the manifest only after source completeness is reconciled. Record `completed` only after successful finalization; otherwise use `recovery_required` and retain evidence.
9. Delete compute according to approved retention/cleanup policy, not merely because the console disconnected.

### Mailbox and callback safety

Use a single-reader command channel for `steer`, `enqueue`, `abort`, `decide`, `answer` and work-monitor commands. Preserve arrival order and command IDs.

The dispatcher must not wait for an entire model turn or human approval before accepting cancellation or decision commands. Start/track long-running SDK work asynchronously while retaining serialized mutation of session-control state. Permission callbacks await separately managed completion handles with deadlines; decision delivery must not require the blocked callback to return first.

### Leases and side effects

Use compare-and-swap lease renewal, expiry and fencing checks on durable writes. A runner that loses its lease stops issuing further tool work; repository updates must pass the supported claim/ref checks. Validate enforcement for in-flight processes: if immediate fencing cannot be guaranteed, fail readiness for a competing writer until the old run is terminated/reconciled.

API idempotency does not guarantee exactly-once SDK or shell execution. Persist command intent and acknowledgement; after an ambiguous crash, reconcile session/journal and repository evidence. Mark uncertain delivery instead of blindly resending a possibly executed command. Retried GitHub webhook events and lifecycle requests use durable deduplication keys.

### Journal, replay and pressure

Each event carries a run-wide monotonic sequence. Persist events before acknowledging durable acceptance. Persist session files under a run-specific durable `COPILOT_HOME`; suspension preserves processes where supported, while restart recovery uses SDK session persistence.

Clients resume using a last-seen sequence. Replay durable events or return an authorized fresh snapshot with an explicit replay boundary. Logs and AHP telemetry are separate streams and may have different retention; disclose gaps rather than implying universal replay.

Bound subscriber buffers and outboxes. Disconnect/resynchronize slow clients without blocking the authoritative session. If the journal/audit outbox cannot durably accept required evidence, refuse new governed actions and surface degraded state; never drop evidence and report success.

## 6. Data model

All records have schema version and UTC timestamps. Scope checks precede reads, streams, vector search and artifact URL issuance.

| Entity | Important fields and invariants |
|---|---|
| Project | `project_id`, authorized repository references, role bindings, configuration/policy versions, approved feed/MCP references, memory provider, telemetry secret reference, quotas. No secret values in settings. |
| TeamVersion | `team_id`, immutable version/bundle digest, repository state reference, compatibility set and reviewed classification. |
| Sprint | `sprint_id`, `project_id`, `team_version`, issue selection, mode, state, export/archive references. |
| Run | `run_id`, scope IDs, image digest, snapshot/session/sandbox references, desired/observed state, lease generation, last event sequence, evidence/cost status. |
| TeamLease | scope/team key, holder run, fencing generation, expiry, concurrency token. One writer; no cross-partition transaction assumption. |
| Command | run/command ID, actor reference, client, recipient, mode, payload reference/hash, status and deduplication key. Protect content separately. |
| Event | run ID, sequence, command correlation, event type, trace/span IDs, timestamp, schema version and sanitized payload. |
| Approval/Input | run/request ID, action digest, policy/version, authorization scope, expiry, pending/resolved state, winning response reference and concurrency token. |
| MemoryRecord | organization/project/team/agent/run scope, classification, source/provenance, content reference, embedding metadata and revision. An embedding is not an authorization boundary. |
| MCPRegistration | approved name/version, endpoint/package reference, transport, tool allowlist, authentication mode, scan/provenance results and approval state. |
| AuditRecord | stable source/record ID, scope/sequence, actor reference, action/decision, policy version, payload hash/reference, previous hash and record hash. |
| AuditManifest | counts and sequence boundaries per source, chain heads, evidence-gap status, image/team/configuration versions, signature and signing-key version. |
| EvaluationRun | dataset/scenario/rubric versions, model/runtime/configuration set, baseline reference, scores/failures, latency/cost, reviewer and release decision references. |

Partition control aggregates by project/run as appropriate; place an approval and its resolution in one transactional aggregate. A distinct team lease aggregate uses its own conditional writes. Do not claim one transaction covers lease, sandbox service, GitHub and audit storage.

Use internal subject references in routine telemetry. Store any mapping needed for authorized accountability in access-controlled identity/audit records. Do not use personal names or email addresses as example IDs.

## 7. APIs and integrations

### Proposed control-plane API

All paths are project-authorized even where the project ID is obtained from the run record.

| Surface | Proposed operations |
|---|---|
| Projects | `POST /v1/projects`, `GET/PATCH /v1/projects/{project}` |
| Teams and sprints | `POST /v1/projects/{project}/teams:import`, `GET /v1/teams/{team}:export`, `POST /v1/projects/{project}/sprints`, `POST /v1/sprints/{sprint}:close` |
| Runs | `POST /v1/runs`, `GET /v1/runs/{run}`, `POST /v1/runs/{run}:cancel`, `:pause`, `:resume`, `:stop` |
| Conversation | `POST /v1/runs/{run}/messages`, `GET /v1/runs/{run}/commands/{command}`, `GET /v1/runs/{run}/events` |
| Human decisions | `GET /v1/runs/{run}/approvals`, `POST /v1/runs/{run}/approvals/{approval}`, `POST /v1/runs/{run}/inputs/{input}` |
| Clients and evidence | `POST /v1/runs/{run}/attach`, `GET /v1/runs/{run}/logs`, `GET /v1/runs/{run}/audit` |
| Integrations | `PUT /v1/projects/{project}/mcp-servers/{name}`, `PUT /v1/projects/{project}/memory` |

A message contains `recipient`, `text` and `delivery_mode` (`immediate` or `enqueue`). A durable acceptance response contains command ID and status plus a status resource; it is not an assertion that the instruction has already changed agent behaviour.

Use structured problem responses with safe error code, request/trace ID and recoverability detail. Require idempotency keys for retryable creation/action requests; reject reuse with a different payload. Validate state transitions and return explicit conflict/authorization errors. Expired approvals remain denied; duplicate matching answers return the recorded result, while conflicting answers cannot replace it.

SSE is the initial one-way event subscription option; WebSocket may be added where bidirectional transport is justified. Carry AG-UI-compatible conversation events in a documented versioned envelope alongside platform control metadata. Authenticate reconnects and reauthorize scope; define token refresh/expiry behaviour without putting credentials in URLs.

Publish signed/deduplicated lifecycle webhooks with run/event IDs and bounded retries. GitHub ingestion verifies signatures, repository scope and delivery IDs before queueing work.

### Protocol and provider adapters

- **Copilot SDK:** the internal authoritative control API. Validate current immediate/enqueue/abort, permission/input handlers and session persistence semantics.
- **AG-UI:** console-facing conversation/interrupt contract through the control plane. Verify hosting-library compatibility; development-only inspection UIs are not production surfaces.
- **AHP:** publish the runner-owned session on loopback with connection-token authentication through a broker/tunnel. Feature-gate current runtime support, protocol negotiation, steering history, concurrent approvals, reconnect and flow control.
- **ACP:** optional, disabled by default; publish only validated negotiated capabilities. Do not promise shared-session steering through an incompatible version.
- **Workspace tunnel:** requested by an authorized user, restricted to that user, time-limited and revoked on run termination. Allow tunnel egress only while required.
- **GitHub remote control:** only where runtime identity and documented personal-session support permit it. It is not the shared multi-user control mechanism.
- **MCP:** approved local stdio tools are pre-baked; remote tools use exact approved destinations and proxy authentication. Exclude unattended providers requiring an interactive user token.
- **Memory:** Cosmos DB default with an owned adapter interface; additional Foundry/AI Search/Mem0-style providers require individual network, residency, authentication and semantics review. Retrieval-only providers do not masquerade as writable memory stores.

## 8. Security, identity and privacy

### Identity separation

Use separate provisioning, control-plane, project sandbox-group, service and audit-writer identities. Only the control-plane component that manages sandbox lifecycle receives necessary sandbox data-plane rights. Conversational agents and front-end components receive no broad lifecycle privilege.

Use Entra authentication for platform users/services and project-scoped roles from the PRD. Authenticate the private runner channel using a scoped, short-lived per-run credential, rotate on resume, and validate both scope and caller. A private endpoint is not sufficient authentication.

Configure sandbox identity exposure to prevent agent code retrieving group tokens while retaining supported image-pull/volume use. Verify the actual platform setting/API. Inject approved Azure/GitHub credentials at egress using secret references or supported managed identity transforms; never static token literals in policies.

GitHub App support versus fine-grained PAT fallback is a validation decision, not an assumed capability. Prefer the least-privileged supported identity with short-lived credentials. If only a PAT works, hold it in Key Vault/platform secrets and inject it outside the run; board operations needing broader credentials require separate scoped approval. Validate licensing and remote-control identity binding before enabling the mode.

### Execution policy

Managed settings and hook/policy files are root-owned/read-only to agents. Disable bypass modes and allow only approved MCP registrations. The hook evaluates tool/action context; its verdict is matched to the specific request and policy version. The SDK permission handler denies unknown, invalid, expired or failed decisions, or waits for a valid scoped human approval.

Set permission approval expiry to 300 seconds. Explicitly test hook timeout fail-open behaviour against the SDK deny backstop; framework middleware alone does not establish control over CLI-owned tools. Validate path normalization, command parsing and inherited child processes in the adversarial suite.

Prompt injection from issues, dependencies and retrieved memory is a standing threat. Apply provenance/classification, tool constraints, output handling and reviewed repository promotions. Allowed GitHub hosts still need destination repository/path/method controls to prevent exfiltration via unrelated endpoints.

### Audit and sensitive content

Critical actions enter a durable journal/outbox before dispatch. The audit writer deduplicates stable record IDs and serializes each run/source chain using a defined canonical serialization and SHA-256 linkage. Concurrent producer arrival does not imply a globally ordered cross-source chain; the manifest records source boundaries.

Append audit records to private run/source blobs using the validated protected-append configuration. Keep a recoverable writer checkpoint and verify append position/content on retry. Do not assume a retry after a network timeout means the previous append failed.

At finalization, reconcile journal, platform/egress export and other required sources before signing counts and chain heads with a Key Vault key. If a producer cannot provide completeness evidence, document and resolve that limitation before claiming FR-12/NFR-06. A hash chain alone cannot prove omitted events never existed.

Approve retention duration/residency, verify append/immutability semantics, then explicitly authorize the irreversible production retention lock. Sandboxes have no audit-storage credentials. Ordinary OTLP may be delayed or dropped, so it cannot be the sole durable audit route.

Default prompt/response/tool-content capture is off. Store permitted sensitive evidence separately from routine traces, with scoped access and retention. Redact secrets before persistence/fan-out; operational subject references remain resolvable only by authorized investigators.

## 9. Infrastructure, configuration and deployment

### Declarative landing zone

Bicep entry points cover shared factory and per-project scope. Persistent resources include VNet/subnets, delegated subnet assignments, private DNS/endpoints, ACA platform environment, sandbox groups/network connections, managed identities/RBAC, private ACR/build infrastructure, Key Vault, audit/artifact storage, registry/memory database, telemetry services and approved Foundry/AI Search dependencies.

Use private access by default; size/address subnets only after validating current service delegation and reserved-range constraints. Determine immutable-at-creation network bindings before deployment. Validate whether sandbox group settings, ACR agent pools and task definitions require preview APIs or local modules.

Bicep owns persistent assignments even if an example CLI assigns them after creation. Missing ARM coverage is a documented blocker/exception request, not permission to hide provisioning in Python, deployment scripts or the portal.

### Python deployment flow

1. Validate Python 3.13/tool prerequisites, configuration names, approved feed access, target scope and role permissions.
2. Verify dependency eligibility: authentic upstream artifacts from approved delivery feeds; original publication time strictly before `selection_time_utc - 7 days`; full direct/transitive/development/build graph and integrity evidence.
3. Run Bicep validation/What-If and present network, cost, replacement and irreversible changes for review.
4. Apply the approved Bicep deployment using a provisioning identity. Use bounded polling that accommodates validated long-running operations.
5. Submit remote ACR builds using the approved private agent-pool/task path; no local/CI Docker build or push. Build with locked dependencies, scan/sign, create SBOM/provenance, tag with commit SHA and resolve digest.
6. Register runtime disk images/snapshots through the validated data plane and deploy application revisions by digest. Persistent resource/revision configuration is rendered through the Bicep-owned deployment model.
7. Verify identity/network/policy, health/readiness, an authorized run and audit/telemetry smoke evidence before reporting success.

Python scripts expose plan/dry-run and checked exit status, use argument arrays rather than shell interpolation, distinguish absence from failure and apply bounded retries. CI YAML only invokes this automation. Roll back application image/configuration revisions to a compatible known-good set; data/schema changes need reviewed backward-compatible migration/recovery plans.

### macOS and package policy

Use a project-local virtual environment, portable filesystem/process APIs and Apple Silicon-first instructions with documented Intel differences. Cloud Linux-specific systemd/sandbox/governance integration runs remotely, not via a misleading macOS substitute.

Read local configuration centrally from process environment and a gitignored `.env`, with process values taking precedence. Check in placeholder-only `.env.example`; exclude real configuration from source, agent file reads, logs and build contexts. Cloud services use managed identity/Key Vault references, not deployment-time copies of developer secrets.

Configure approved Python/NuGet/npm sources explicitly, clearing inherited public fallbacks and using package-source mapping where needed. No direct public PyPI/NuGet access or alternative public endpoint workaround. Feed URLs/authentication and age/provenance evidence sources remain deployment inputs.

Lock the resolved graph and use frozen/locked restore modes locally, in CI and in remote builds, including build-isolation dependencies. Record package/version/publisher, upstream timestamp, approved delivery source, digest and selection cutoff. Prefer stable eligible releases; previews and emergency age exceptions require explicit reviewed approval, scope and expiry. Unknown evidence, revoked/vulnerable artifacts or Python incompatibility block promotion. Base-image triggers cannot bypass these checks.

## 10. Testing, evaluation and observability

| Layer | Coverage and evidence |
|---|---|
| Unit/component | State transitions, command validation, conditional approvals/leases, memory scope filters, package eligibility and canonical audit hashing. |
| Contract/conformance | CLI/SDK/Squad compatibility; permission default rejection; AG-UI event semantics; enabled AHP/ACP capabilities; Azure data-plane adapters; webhook deduplication. |
| Integration | Real private image pull, remote build, egress credential injection, denied destinations, managed identity isolation, approved MCP/memory access and signed audit manifest. |
| End-to-end | Operator start/steer/queue/approve/cancel/pause/resume/stop; Viewer denial; editor attach/revocation; team export and sprint close. |
| Adversarial | Prompt/memory injection, path/command bypass, unknown tool, hook failure, metadata/credential access, allowed-host exfiltration and cross-project access attempts. |
| Recovery/chaos | Lease loss, in-flight SDK ambiguity, service restart, duplicate delivery, slow subscribers, unavailable collector/audit/feed, rotated CA and snapshot policy restoration. |
| Performance/scale | PRD measurement contract for <3s steering, <10s dual-destination log visibility, <60s readiness; M3 five-project/50-run demonstration. |
| Accessibility | Keyboard/screen-reader control and approval flows; WCAG 2.2 AA assessment. |

### Agent evaluation

The quality/evaluation role owns versioned representative tasks for completion quality, correct routing, appropriate tool use, steering adherence, retrieval grounding, refusal/approval behaviour and memory contamination resistance. Security reviews the adversarial/governance cases; component owners reproduce and fix failures.

Record scenario inputs with privacy review, expected outcomes, rubric/scorer version, model/runtime/CLI/team/configuration set, baseline, repeated-run parameters, result artifacts and reviewer disposition. Use deterministic checks where possible and documented human/model judgment where necessary. Judge access, cost and approved-feed requirements remain explicit prerequisites, not silent external calls.

Establish numerical quality thresholds from pilot evidence before release. Compare candidate/baseline under the same conditions; report variability and failures, not just averages. Behaviour-changing releases require this evaluation gate in addition to functional tests and security checks.

### Observability pipeline

Emit project/team/sprint/run/sandbox and agent references plus W3C trace context. Include event sequence and command/approval IDs where applicable. Keep actual user identifiers out of routine span attributes.

The design assumes a local collector can bridge the CLI's OTLP/HTTP output and file-based raw logs to the platform path; validate supported receiver/export protocols. Collect runner, orchestration, governance, stdout/stderr and network decisions explicitly; package spans do not imply raw-log coverage.

The private gateway enriches/redacts and forwards to the approved project OTLP endpoint and console log service. Monitor stuck approvals, missing heartbeats, lease loss, policy errors, denied egress, telemetry lag, audit backlog/completeness and budget pressure. Do not place an access token in a log/tunnel URL, process argument or telemetry attribute.

## 11. Ownership and delivery process

Use neutral engineering roles:

- **Technical lead:** architecture/contracts, scope, traceability and cross-component review.
- **Infrastructure/delivery engineer:** Bicep, Python automation, macOS bootstrap, private builds and package eligibility.
- **Harness engineer:** authoritative runner/session, mailbox/journal and protocol adapters.
- **Control-plane engineer:** lifecycle, authorization, GitHub integration, audit writer and team/sprint APIs.
- **Console/developer-experience engineer:** accessible operations UI and attach/client workflows.
- **Security/governance engineer:** threat model, tool/network policy, identity and supply-chain review.
- **Quality/evaluation/reliability engineer:** test suites, evaluation baselines/thresholds, observability and operational evidence.
- **Memory/MCP engineer:** scoped memory, provider adapters and registry integration.
- **Scribe function:** reviewed decisions, histories and evidence indexing.
- **Work-monitor function:** bounded readiness/claims/status reporting; no permission approval or independent merge authority.

Assign one accountable role per issue. Resolve ambiguous routing through the technical lead; use isolated branches/worktrees and supported claims. Authors are not their sole reviewers. Review shared contracts before parallel implementation, What-If before shared infrastructure changes, evaluation regressions before behaviour promotion and audit closure at sprint completion.

## 12. Technical constraints, trade-offs and validation backlog

| Gate | Decision still needing evidence | Consequence if unresolved |
|---|---|---|
| VG-01 | Current regional sandbox availability, ARM/data-plane support, private routes, subnet constraints and quotas | M0/M1 provisioning blocked; do not invent resource schema or advertise capacity. |
| VG-02 | Auto-suspend toggling, maximum lifetime, deletion units and snapshot/resume semantics | Do not enable lifecycle automation that can suspend active tools or delete unsealed evidence. |
| VG-03 | Egress transform precedence, token injection/rotation, policy persistence and CA refresh | M1 security gate blocked; no open-egress workaround. |
| VG-04 | CLI/SDK/Squad compatibility, session callbacks/persistence and Squad.Agents.AI handler override | Use validated direct composition if behaviour is preserved; otherwise runner readiness blocked. |
| VG-05 | Permission timeout/error backstop and policy parser/path semantics | M1 release blocked until adversarial tests prove enforcement. |
| VG-06 | Platform telemetry protocols and egress-source delivery/completeness guarantees | Log target and complete-audit claims remain unproven; required gaps block release. |
| VG-07 | AHP runtime availability, protocol negotiation, steering/completion/history and backpressure | Leave AHP disabled; console and workspace tunnel remain the baseline paths. |
| VG-08 | ACP negotiated capabilities and GitHub remote-control identity binding | Leave unsupported client modes disabled; do not emulate capabilities unsafely. |
| VG-09 | GitHub credential/licensing support, repository and board operations | Approve the least-privileged validated option; no borrowed user token or broad default. |
| VG-10 | Approved feeds, original artifact timestamps, provenance and Python 3.13 compatibility | No dependency restore/build promotion until evidence is available. |
| VG-11 | Cosmos memory/search capability and additional backend privacy/network semantics | Default or alternative adapter delivery blocked/deferred explicitly; no cross-scope fallback. |
| VG-12 | Blob append/immutability, retry checkpointing, completeness and signing verification; retention/residency approval | No production retention lock or successful audit-completion claim until verified. |
| VG-13 | Benchmark parameters, quality thresholds, availability/recovery objectives and operating budget | No service-level or quality/financial benefit claim; product/operations must approve baselines. |

The main trade-offs are deliberate: operating an in-sandbox runner costs engineering effort but preserves local tool semantics; single-writer leases limit concurrency but protect team state; immutable evidence and full governance add latency/cost but make completion accountable; strict feed/age gates may delay urgent upgrades but cannot be bypassed silently.

Foundry knowledge services, delegated data access, API gateway products and native agent identities may later improve integration. They add authorization/network/operating dependencies and remain separate reviewed proposals, not implicit prerequisites for M1. None moves agent execution to Foundry; any coordinator or context-broker agent runs in ACA Sandboxes.

## 13. Traceability and design evolution

| Design area | Product requirements |
|---|---|
| Isolation, registry, lifecycle and leases | FR-01 to FR-05; NFR-01 to NFR-05 |
| Session host, console, approvals and client protocols | FR-06 to FR-09, FR-15/16; NFR-10/12 |
| Governance, credentials and evidence | FR-10 to FR-14; NFR-01/02/06/07 |
| MCP, memory and portable teams | FR-17 to FR-20; NFR-02/07/12 |
| Quotas, cost and operating model | FR-21/23; NFR-05/11 |
| Evaluation and trusted delivery | FR-22; NFR-08/09/13 |

Record substantial design changes in reviewed architecture decisions with affected requirement IDs, evidence and rollback consequences. Update the PRD first if a proposed implementation changes observable behaviour or accepted guarantees. Update the vision only when intended outcomes, beneficiaries or scope materially change.
