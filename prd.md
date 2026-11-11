# Product Requirements

Status: proposed baseline for implementation and validation.

## 1. Purpose and document boundaries

This document defines the externally observable behaviour of a governed platform for remote AI engineering teams. It implements the outcomes in [Vision and Goals](./goal.md); the [Technical Design](./tdd.md) selects the implementation.

The supplied assessment and squad specification inform this baseline. The later specification takes precedence where it narrows the earlier hybrid-platform proposal. This document contains no personal attribution, customer examples, private resource identifiers or machine-specific paths.

Requirement IDs in this document are the new baseline, not a claim that the source documents' requirement numbering has been preserved. Must means required for the production baseline; a milestone identifies when evidence is expected. Should is a planned enhancement; Could is optional.

## 2. Product overview

The platform runs existing Squad teams using GitHub Copilot CLI in project-isolated execution environments. A control plane manages runs and enforces project access. The primary operations console exposes conversation, steering, approvals, team state and live logs. Developers can inspect work through supported editor connections and review changes through GitHub.

Approved tools and memory providers can be selected per project. Every run produces correlated operational telemetry and durable audit evidence. Teams keep their authoritative charters, decisions, routing and histories in the repository; external memory is a governed, searchable derivative.

### Operating boundaries

- Each project has a distinct execution, network and credential boundary; each run has its own sandbox.
- Only one active run may write a team's shared state at a time.
- People remain responsible for priority, exceptional permissions and merges.
- Default operation is interactive and governed. Unattended execution is a separately validated mode.
- All agent execution belongs in ACA Sandboxes. Foundry may supply approved knowledge/governance services, but it does not host coding teams, coordinator agents or context-broker agents.

## 3. Personas and user journeys

| Persona | Need | Primary journey |
|---|---|---|
| Platform administrator | Consistent onboarding, approved defaults, quotas and cost visibility | Register project -> validate configuration/access -> provision approved environment -> demonstrate first run. |
| Project operator | Delegate and redirect engineering work | Select team/issues -> start run -> monitor -> steer or queue -> answer approval -> stop or resume. |
| Developer/reviewer | Inspect and validate changes | Open run workspace -> inspect branch/worktree -> run checks -> review pull request -> approve merge through repository policy. |
| Security/assurance reviewer | Verify enforcement and investigate events | Review policy version -> inspect denied/escalated actions -> retrieve manifest -> verify evidence. |
| Product/delivery stakeholder | Follow progress without operational privileges | View sprint/run summaries -> inspect outcomes and blockers -> update backlog priorities. |

### Representative scenarios

1. **Onboard a project:** an administrator selects repositories, a team version, approved feeds/MCP services, memory provider and telemetry destination. Incomplete configuration blocks activation with an actionable error.
2. **Steer active work:** an operator redirects the coordinator or a named member. Immediate delivery is distinguished from queueing; an already-running tool call is not falsely represented as interrupted.
3. **Resolve a risky action:** an operator sees the proposed action, policy reason and expiry. One authorized response wins; other clients see the resolution. No response results in denial.
4. **Recover a run:** an operator resumes after suspension, or reconnects after a service restart. Durable state and event history remain consistent, without a second writer.
5. **Inspect from an editor:** a developer requests an authenticated, user-restricted workspace connection. Shared-session control is offered only when the protocol capability is enabled and validated.
6. **Close a sprint:** the team version and eligible learning are exported/reviewed, run evidence is finalized, and environment cleanup respects retention.
7. **Investigate an incident:** an assurance reviewer follows a run identifier from a pull request through tool decisions, network decisions and the signed audit manifest.

## 4. Functional requirements

| ID | Requirement and observable acceptance | Priority / milestone | Goal |
|---|---|---|---|
| FR-01 | Register project repositories, approved configuration and telemetry destination. Reject incomplete or unauthorized activation and return specific missing configuration names without secret values. | Must / M0-M1 | GO-06 |
| FR-02 | Create one sandbox per run in the correct project boundary. A cross-project attempt to read files, use credentials or attach must be denied and recorded. | Must / M1 | GO-02 |
| FR-03 | Start from a digest-pinned approved image or prepared team snapshot. Record image digest, team/configuration version and compatibility set for every run. | Must / M1 | GO-04 |
| FR-04 | Enforce a single active writer per team and supported work claims. A concurrent conflicting start must not create another writer; lease loss must stop further writes. | Must / M1 | GO-04 |
| FR-05 | Support cancel-current-turn, pause, resume and stop as distinct actions. Prevent automatic suspension during active work; stop is not complete until state and audit finalization succeed or a visible recovery state is recorded. | Must / M1 | GO-01 |
| FR-06 | Address the coordinator, work monitor or a named member with immediate or queued messages. Return a durable message identifier and expose accepted, applied, failed or uncertain status rather than conflating acknowledgement with completion. | Must / M1 | GO-01 |
| FR-07 | Show permission requests, questions and plan approvals with reason, target and expiry. Resolve competing authorized answers atomically; deny unanswered permission requests after 300 seconds. | Must / M1 | GO-01 |
| FR-08 | Expose conversation, member state, run lifecycle, approvals and filterable logs in the console. After reconnect, recover durable conversation/control events without duplicate state transitions; identify any log-history gap explicitly. | Must / M1 | GO-01, GO-03 |
| FR-09 | Authenticate every connection and enforce project-scoped Viewer, Operator and Administrator permissions. A Viewer cannot steer, approve, attach to an editable workspace or control lifecycle. | Must / M1 | GO-02 |
| FR-10 | Apply versioned managed settings, tool policy and a permission backstop before work begins. An undecided, errored or timed-out decision cannot execute an action without an explicit valid approval. | Must / M1 | GO-02 |
| FR-11 | Deny unapproved egress, restrict destinations/actions and inject credentials outside the sandbox. Image/file/environment inspection must find no long-lived run credential; snapshots must not restore an ungoverned network path. | Must / M1 | GO-02 |
| FR-12 | Write audit records for steering, tool decisions/execution, approvals, connections, lifecycle, configuration, MCP, memory, git and egress activity. Finalize a signed manifest with counts and verifiable chain heads; missing evidence prevents a successful-completion claim. | Must / M1 | GO-03 |
| FR-13 | Export correlated logs, traces and metrics to the configured project destination and show the same run logs in the console. Producer coverage must include CLI, team orchestration, runner, governance, platform and control plane. | Must / M1 | GO-03 |
| FR-14 | Produce repository changes through isolated worktrees/branches and pull requests with run/evidence references. Respect repository protections and human merge policy; verify webhook signatures and deduplicate deliveries. | Must / M1-M2 | GO-03 |
| FR-15 | Provide a versioned API for console actions, event subscriptions and lifecycle notifications. Repeating an idempotent request must not duplicate a run or decision; invalid/unauthorized input returns a structured error. | Must / M1-M2 | GO-06 |
| FR-16 | Broker authenticated, time-limited editor/workspace attachment and close access at run end. A shared-session adapter must use the same authoritative session and approval state, not create a second writer. | Must: workspace attach / M2; Should: AHP / M2; Could: ACP | GO-01 |
| FR-17 | Maintain an approved MCP registry and render per-project allowed tools/configuration without image rebuilds. Reject unapproved servers and unattended use of user-interactive-only authentication; record approved server versions. | Must / M2 | GO-02, GO-04 |
| FR-18 | Provide scoped memory add/search/update/delete, classification, provenance and governed checkpoint/end-of-run synchronization. Reject cross-scope access and treat retrieved instructions as untrusted. Repository team records remain authoritative. | Must / M2 | GO-04 |
| FR-19 | Support a default private memory backend and at least one approved alternative. Selecting a supported backend must not require rebuilding the runner; unsupported privacy/network combinations fail explicitly. | Must: default / M2; Should: alternative / M2 | GO-04 |
| FR-20 | Import/export versioned team bundles, bind a sprint to a team version/issues, and close/archive it. Existing team state must not be overwritten without a reviewed migration; exports must be inspected for sensitive content before reuse. | Must / M2-M3 | GO-04 |
| FR-21 | Enforce project resource limits and expose compute and model-consumption attribution. Distinguish delayed/estimated usage from measured cost; unavailable pricing/usage must not display as zero. | Must / M3 | GO-03, GO-06 |
| FR-22 | Gate agent/prompt/routing/tool/memory/model changes using versioned evaluations, agreed rubrics and thresholds, baseline comparisons and independent review. Failed thresholds block release; retain reproducible results. | Must / M1 onward | GO-05 |
| FR-23 | Offer unattended backlog execution only after proving governance, required state/MCP tools, bounded work and stop controls. It remains disabled by default; enabling it cannot grant blanket permissions. | Could / M3 | GO-01, GO-02 |

## 5. Non-functional requirements

| ID | Requirement / acceptance |
|---|---|
| NFR-01 Security | No long-lived credentials in run images, files or environment; no policy bypass in the agreed adversarial suite. Defense in depth must cover tool execution, egress, identity and repository change control. |
| NFR-02 Isolation | Demonstrate project and run isolation, including scoped memory, streams, artifact access and writable team state, using negative authorization tests. |
| NFR-03 Reliability | Recover durable run state after control-plane/runner restart and suspension. Expose ambiguous SDK outcomes; do not blindly replay a possibly executed side effect. |
| NFR-04 Performance | Proposed pilot targets: steering acknowledgement p95 <3 seconds; log visible in both console and destination p95 <10 seconds; snapshot-based readiness p95 <60 seconds. Validate before adoption as service levels. |
| NFR-05 Scale | M3 demonstration: at least five isolated projects and 50 concurrent runs. Check current regional quotas/API rate limits and use bounded backoff; historical limits are not guaranteed capacity. |
| NFR-06 Audit durability | 100% of tested completed runs have complete manifests and verified chains. Production retention is approved and locked before release; duration, residency and sensitive-content policy require explicit approval. |
| NFR-07 Privacy | Content capture is off by default. Keep credentials and unnecessary personal content out of operational logs/evaluations; restrict identity-bearing audit data to authorized investigations. |
| NFR-08 Supply chain | Use authentic publisher artifacts through approved feeds only, with pinned/locked full graphs and verified age strictly greater than seven days at selection. Missing feed/provenance/age evidence fails closed. No direct public PyPI/NuGet fallback. |
| NFR-09 Developer experience | Python 3.13 automation works on Apple Silicon macOS with a local virtual environment and protected local configuration. Document Intel differences; no local Linux container daemon is required for cloud image builds. |
| NFR-10 Accessibility | Target WCAG 2.2 AA for the console, including keyboard-accessible approval/control flows, screen-reader status updates and non-color-only state indication. Verify before claiming conformance. |
| NFR-11 Operability | Health/readiness, stuck-approval/policy/egress/audit alerts, bounded cleanup, recovery procedures and cost visibility must be exercised, not merely configured. |
| NFR-12 Maintainability | Version experimental integrations behind owned interfaces and feature flags. Record compatibility tests and rollback instructions for CLI/SDK/team/protocol upgrades. |
| NFR-13 Evaluation integrity | Record scenario/dataset, model, runtime, configuration, scoring method and result versions. Separate functional correctness, agent quality, latency and cost; do not invent an aggregate quality score or threshold. |

### Performance measurement contract

Before a benchmark, record sample count, warm/cold state, concurrency, region, image/team version, timestamps and failure accounting. Quality engineering and product leadership approve these parameters before running the release benchmark; no numerical results are claimed here.

- Steering latency begins when an authorized message is submitted and ends when the authoritative session acknowledges it, not when the console optimistically renders it.
- Log latency runs from a timestamped producer event to visibility in **both** destinations; report clock synchronization assumptions and per-destination latency.
- Startup runs from an accepted start request to runner/session readiness from the prepared snapshot, including scheduling and resume work.
- Report errors/timeouts alongside percentiles; do not drop failed samples to make the target pass.

## 6. User experience and permission expectations

The console shows project/team/run context, a run list, conversation with recipient and Steer/Queue choices, pending approvals/questions, member activity and live logs. Expose cancellation, suspension and stopping as different operations with confirmation where consequences differ.

Show immediate steering as best-effort: a tool already executing may finish first. Queue order is explicit. Display who or which authorized client resolved an approval using a scoped display reference, without broadcasting unnecessary identity details. Withdraw resolved cards everywhere.

| Role | Allowed capabilities |
|---|---|
| Viewer | Read authorized run/sprint state, redacted conversation and permitted logs. |
| Operator | Viewer access plus start/steer/answer/control runs and request permitted workspace attachment. |
| Administrator | Operator access plus approved project configuration, integrations and resource-limit administration. |

Code merge rights and security-policy approval are separately governed; being a platform Administrator does not bypass repository protection or authorize self-approval of an exception. Failed operations show actionable reasons and safe retry/recovery options.

## 7. Delivery priorities and acceptance gates

| Milestone | Scope | Exit criteria |
|---|---|---|
| M0 Landing zone | Declarative private infrastructure, trusted feeds/builds, configuration and audit storage foundations | Reviewed infrastructure plan; approved feed/provenance contract; remote signed/digest-pinned image path; private connectivity and append test; no production retention lock until approved. |
| M1 Single governed squad | Run/session ownership, console, steering, approvals, enforcement, telemetry, audit and initial evaluations | Real sandbox journey satisfies FR-02 through FR-13 and relevant API/GitHub paths; timeout/authorization/recovery tests pass; performance measured; governance/audit gaps resolved or block release. |
| M2 Clients and context | Workspace attach, optional shared-session control, approved MCP, memory, team portability and integration APIs | Two isolated projects demonstrated; enabled adapters pass capability tests; approved MCP/memory configuration changes need no runner rebuild; absence of AHP does not block the console/workspace path. |
| M3 Factory readiness | Sprint automation, quotas, cost attribution, scale, operational controls and production retention | Five or more projects and 50 concurrent runs; recovery/runbooks exercised; all Must requirements evidenced; approved retention lock; evaluation release gates pass. |

Core acceptance scenarios:

- Steer during a long tool call; acknowledge accurately, preserve the original turn and apply the changed intent at the supported boundary.
- Race two approval answers; exactly one valid decision wins, with no second execution or stale client card.
- Let a permission request expire; deny after 300 seconds and report the reason.
- Attempt Viewer mutation and cross-project attachment/memory access; deny without leaking another project's state.
- Interrupt the control plane, runner, collector and audit destination separately; recover without a second writer or false success.
- Resume from a snapshot with rotated trust/configuration; no open-egress window or stale credential exposure is accepted.
- Try a missing feed, unknown package timestamp or newly published artifact; block restoration rather than using a public source.
- Change a prompt or tool contract; run the versioned evaluation suite and demonstrate that a threshold regression blocks promotion.

## 8. Scope exclusions and unresolved decisions

Non-goals follow the [vision](./goal.md): replacing Squad's semantics, arbitrary model-provider support, uncontrolled autonomous merges, GPU and sovereign-cloud hosting, or running coding teams in Foundry Agent Service.

Delegated enterprise data access, API gateway products, native agent identities and machine-token authentication may be considered later. They are not baseline dependencies and require validated authorization, licensing and integration support. Any agent implementing these integrations must run in ACA Sandboxes, not Foundry.

Open release decisions include approved feed URLs/authentication and evidence sources; Azure region/quota/network feasibility; current SDK/CLI/protocol compatibility; credential type and rotation support; production retention/residency; evaluation thresholds; benchmark sample parameters; and operational availability/recovery objectives. An unavailable or unvalidated capability is a blocker or explicitly deferred feature, not an assumed implementation.

## 9. Change control

Keep outcome IDs stable in the [vision](./goal.md). Behaviour or acceptance changes require a PRD review and an impact assessment against those outcomes. The [TDD](./tdd.md) may change implementation without changing accepted behaviour; a design change that alters permissions, guarantees or UX must first update this PRD.
