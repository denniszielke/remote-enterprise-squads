# First MVP implementation proposal

Status: Stage A work started following the user's instruction to get to work. Contracts and capability gates still require review; this proposal is not evidence of delivered MVP capabilities.
hand
On 2026-11-12 the user approved the narrow MVP scope and Sweden Central pilot
authorization, accepted the approved-feed requirement, and assigned a pilot
administrator/operator. Resource group and spending cap remain unspecified;
chargeable provisioning is on hold. Actual feed configuration and provenance
evidence, retention/residency policy and technical contract review remain open.
These decisions authorize continued implementation, not a passed release gate.

## 1. Recommendation and basis

Deliver **M0 + M1: one steerable, governed Squad**, followed by a separate M2 decision. The first MVP must prove that a human can delegate a real repository change, intervene safely, review a pull request, and verify what happened.

This proposal follows [the goals](./goal.md), [the PRD](./prd.md), [the TDD](./tdd.md), and the existing [team roster](./.squad/team.md), [routing](./.squad/routing.md), [workflow](./.squad/workflow.md), and [constraints](./.squad/constraints.md). It does not relax their acceptance criteria or change their authority.

The repository currently contains specifications and Squad configuration, not an application implementation. Estimates therefore include establishing the delivery foundation. This is a proposed team allocation, not a claim that the named agents have independently reviewed or approved it.

### Pilot journey

1. An administrator activates one approved project/repository and a reviewed team version.
2. An Operator starts a run from a digest-pinned prepared image/snapshot.
3. The run acquires the team's fenced writer lease and starts its authoritative session in its own ACA Sandbox.
4. The Operator sees conversation, member state, approvals and correlated logs; steers or queues a message to the coordinator, work monitor or a named member.
5. A policy-controlled action requires approval; exactly one authorized answer wins, or it is denied after 300 seconds.
6. The Operator exercises cancel-current-turn, pause, resume and stop as distinct operations.
7. The squad produces a branch and pull request. Humans retain review and merge authority.
8. The platform finalizes verifiable audit evidence before claiming completion and cleans up according to the agreed policy.

**Product shape:** one project, one repository and one reviewed team version in the pilot UI; one active writer for that team. Isolation tests also provision a second project boundary as a negative-test fixture. This is not a full M2 multi-project onboarding demonstration.

## 2. Scope contract

| Include in the MVP | Requirement coverage |
|---|---|
| Approved project configuration, activation validation and project roles | FR-01, FR-09 |
| Project-isolated, per-run ACA Sandbox; approved image/snapshot and compatibility inventory | FR-02, FR-03 |
| Fenced team lease, claims, lifecycle and recovery | FR-04, FR-05 |
| Durable targeted Steer/Queue commands, delivery status and 300-second approvals | FR-06, FR-07 |
| Minimal accessible console, durable event replay and explicit log gaps | FR-08 |
| Protected execution policy, deny backstop, restricted egress and external credential injection | FR-10, FR-11 |
| Complete audit sources, signed manifests and correlated telemetry | FR-12, FR-13 |
| One branch/worktree-to-PR path with evidence references and human merge | M1 portion of FR-14 |
| Versioned project/run/control/event APIs, authorization, structured errors and idempotency | M1 portion of FR-15 |
| Initial reproducible agent evaluations and independent release verification | FR-22 |

Applicable non-functional requirements remain acceptance constraints, especially isolation, recovery, dependency provenance, privacy, accessibility and evidence integrity. A narrower pilot does not waive them.

### Explicitly defer

- Editable workspace/VS Code attachment: M2, FR-16. No attach control is advertised in the MVP.
- AHP and ACP: disabled; neither is required for the console path.
- Dynamic approved MCP registry, external memory services and alternate memory providers: M2, FR-17 to FR-19. Repository Squad records remain authoritative.
- General team import/export, sprint catalogue/closure automation and migrations: M2-M3, FR-20. The MVP records a reviewed team reference/digest.
- Full integration API catalogue and lifecycle webhook products: M2. Any GitHub webhook actually introduced in M1 must already verify signatures and deduplicate deliveries.
- Model-cost accounting, factory quotas/scale and unattended backlog execution: M3, FR-21/23. Enforce a bounded pilot compute/run limit, but do not portray missing usage data as zero.
- Optional Foundry/AI Search services, delegated enterprise access and extra client adapters.

The five-project/50-concurrent-run target is an M3 gate, not an MVP exit criterion. Production retention locking is also not a pilot activity; appropriate pilot retention and residency still require approval.

## 3. Implementation approach

Implement the TDD baseline rather than inventing a second architecture:

- **Control plane:** private ASP.NET Core/.NET 10 service owning project authorization, durable commands, conditional approval resolution, lifecycle reconciliation and fenced leases.
- **Runner:** .NET 10 process in the run sandbox owning exactly one Copilot client/coordinator session, a nonblocking serialized command mailbox, persistent session state, and journal/outbox.
- **Console:** React/TypeScript with a run list/detail view, targeted conversation, approvals, lifecycle actions, member state and filterable logs. Use SSE for versioned control/conversation events; keep logs a separate correlated stream.
- **Durable state:** validate the TDD's proposed Cosmos DB control aggregates, private Blob audit/artifacts and Key Vault signing before committing their schemas. Do not assume one transaction spans these services.
- **Governance:** protected managed settings and tool hook, SDK deny backstop, deny-by-default egress proxy and repository protections. Credentials are injected outside the sandbox.
- **Delivery:** Bicep for persistent resources; Python 3.13 for bootstrap, eligibility checks, What-If, deployment, remote ACR builds, smoke verification and operational automation. No local container daemon requirement.
- **Observability:** local collection plus a private gateway, redaction and dual delivery to console and project OTLP destination. Durable audit is a separate correctness path, not an OTLP retention assumption.

Prefer a small number of deployables and shared versioned contracts. Do not introduce a general plugin framework, generic workflow engine or additional message broker unless a validated requirement demands it. Keep permission handlers explicit; use Squad.Agents.AI only after proving safe overrides and session access, otherwise the TDD's validated direct SDK composition.

### First contract review

Steve owns approval of these contracts before dependent implementation starts:

1. Project scope and role checks, team/version identity and compatibility inventory.
2. Run desired/observed states, valid transitions, stop/finalization and recovery semantics.
3. Writer lease generation, write fencing and in-flight process reconciliation.
4. Command IDs, idempotency payload checks, recipient/mode and accepted/applied/failed/uncertain states.
5. Approval action digest, authorization, atomic resolution and expiry.
6. Versioned event envelope, sequence allocation, reconnect boundary and bounded subscriber behavior.
7. Audit source inventory, stable record IDs, canonical hashing, completeness checkpoints and manifest verification.
8. Policy/credential distribution and readiness behavior when required dependencies fail.

## 4. Capability-first work breakdown

Each row becomes one or more narrowly scoped implementation issues after proposal approval. Dependencies below are prerequisites to finishing the deliverable; an issue may contain an earlier contract-only subtask to enable parallel development.

| ID | Deliverable and completion evidence | Accountable owner | Prerequisites |
|---|---|---|---|
| P01 | Approve MVP scope, shared contracts, threat boundaries and requirement/evidence matrix | Steve | None |
| P02 | Prove sandbox region/API/private-route feasibility, persistent state, active-turn suspension controls, snapshot/resume and cleanup semantics; retain probe results | Basher | P01 |
| P03 | Prove pinned CLI/SDK/Squad compatibility: session ownership, recipient routing, immediate/queued delivery, cancellation, input callbacks and restart persistence | Rusty | P01 |
| P04 | Establish approved Python/NuGet/npm/base-image sources, full-graph lock/provenance/age evidence and publisher checks; missing evidence blocks restore | Linus | P01 |
| P05 | Prove external credential injection/rotation, identity isolation, restricted egress, protected policy and timeout/error denial on real execution | Linus | P02, P03, P04 |
| P06 | Prove durable audit append/retry/completeness/signing design and producer coverage; decide pilot retention/residency | Reuben | P02, P04 |
| P07 | Establish evaluation scenarios, baseline protocol, benchmark parameters and proposed release thresholds with product approval | Livingston | P01, P03 |
| P08 | Deliver declarative M0 landing zone, private remote signed/digest-pinned build path and Python deployment/smoke workflow with reviewed What-If | Basher | P02, P04, P05, P06 |
| P09 | Implement project activation, role enforcement, run registry, fenced lease, command/approval aggregates and idempotent API contracts | Reuben | P01, P04 |
| P10 | Implement authoritative runner, durable mailbox/journal/outbox, per-run session storage, policy callbacks and lease-loss handling | Rusty | P03, P05, P09 |
| P11 | Implement real sandbox lifecycle reconciliation, readiness, cancel/pause/resume/stop, restart recovery and bounded cleanup | Reuben | P08, P09, P10 |
| P12 | Deliver console run/conversation/approval/log flows, honest delivery states, replay handling, project-role UX and accessible controls | Saul | P09, P10 |
| P13 | Deliver producer telemetry, correlated log fan-out, gap reporting, redaction, health/readiness and actionable failure alerts | Livingston | P08, P10 |
| P14 | Deliver durable audit ingestion, independent manifest verifier, completeness reconciliation and completion gate | Reuben | P06, P10, P11 |
| P15 | Deliver isolated branch/worktree and minimal PR publishing path with run/evidence references and repository protection tests | Reuben | P05, P10, P14 |
| P16 | Execute independent real-cloud E2E, adversarial, recovery, accessibility, performance and agent evaluation acceptance; retain failures and fixes | Livingston | P07, P11, P12, P13, P14, P15 |
| P17 | Review pilot evidence, exercise operator/recovery procedures, approve release or record blocking findings | Steve | P16 |

P02-P07 are feasibility and evidence work, not permission to bypass P04 for dependency installation. Use existing approved tools initially; restore/build promotion waits for approved sources and evidence.

### Supporting duties and independent review

- **Tess:** supports P01/P06/P09 with project partitioning, scope checks and context boundaries. Does not build an unused memory service for M1.
- **Linus:** mandatory reviewer for security-sensitive changes, including infrastructure, auth, runner permissions, audit and GitHub credentials. For Linus-authored changes, Steve assigns a separate competent reviewer; Linus cannot self-approve.
- **Livingston:** independently verifies implementation and acceptance claims. For Livingston-authored telemetry/evaluation tooling, Steve assigns a different qualified verifier and reviewer.
- **Fact Checker:** validates capability and source claims against retained evidence; unresolved assumptions remain explicit.
- **Rai:** reviews prompt injection, approval UX, privacy and human-accountability risks before pilot release.
- **Scribe:** records reviewed decisions, evidence and handoffs; does not convert an unreviewed proposal into an accepted decision.
- **Ralph:** surfaces only dependency-ready work with supported claims; no unattended execution loop, permission approval or merge authority is introduced.

All work follows `ready -> claimed -> implemented -> verified -> reviewed -> done`, with isolated branches/worktrees and evidence. Authors are never their sole verifier or reviewer.

## 5. Sequencing and planning envelope

**Proposed planning envelope: 8-10 elapsed weeks after access, approved feeds, pilot ownership and staffing are available.** This is an estimate, not a commitment or a statement of measured agent throughput. It assumes parallel availability across infrastructure/security, harness/control plane, console and independent quality work. Preview limitations or blocked validation gates require re-estimation, not a security workaround.

| Stage | Indicative window | Outcome and parallelism |
|---|---|---|
| A: scope and feasibility | Weeks 1-2 | P01, then P02-P07 in parallel within approved tooling constraints. Stop/go review on sandbox, governance, identity and audit feasibility. |
| B: M0 foundation and durable core | Weeks 3-4 | P08 and P09 in parallel after their gates. Begin contract-backed runner and console work as prerequisites become available. |
| C: first governed vertical slice | Weeks 5-6 | P10-P14 integration: start -> conversation -> approval -> controlled tool -> stop -> verified manifest. Build P12 against agreed contracts, not assumed SDK behavior. |
| D: repository outcome and hardening | Weeks 7-8 | P15, recovery/adversarial tests, accessibility fixes, evaluation baselines and measured pilot targets. |
| E: contingency and release | Weeks 9-10 if needed | Resolve capability/integration findings, repeat affected tests, exercise runbooks and execute P16/P17 sign-off. |

**Critical path:** contracts -> sandbox/SDK/feed feasibility -> governance and audit proof -> trusted landing zone -> durable runner/lifecycle -> full evidence and PR path -> independent acceptance.

The first integration demonstration should happen at the start of Stage C, not at project end. UI polish and nonessential dashboards yield before any governance, isolation, recovery or audit gate.

## 6. MVP acceptance and release gates

Livingston owns the evidence index; component owners reproduce and fix failures. Tests must exercise real sandbox paths where mocks cannot prove platform behavior.

| Gate | Required evidence |
|---|---|
| Trusted delivery | Remote approved-feed build, locked full dependency graph, verified publisher/provenance and artifact age strictly greater than seven days at selection; signed/digest-pinned image. Unknown age, missing feed or public fallback attempt fails closed. |
| Authorization/isolation | Viewer mutations denied; cross-project file, credential, stream, artifact and runner access denied without disclosure; editable attach remains unavailable. Negative tests use a second isolated fixture. |
| Single writer | Conflicting concurrent starts cannot create two writers. Lease loss stops new writes; an old in-flight process is terminated/reconciled before another writer becomes ready. |
| Steering | Long-running tool scenario preserves honest semantics; coordinator, monitor and named-member routing work; durable ID and accepted/applied/failed/uncertain states survive reconnect. |
| Human decisions | Race two authorized answers: one winner and one execution. Expire permission requests at 300 seconds: denial. Unknown, errored and timed-out policy decisions never execute without a valid explicit approval. |
| Lifecycle/recovery | Distinct cancel/pause/resume/stop behavior; no active-turn auto-suspension; control-plane and runner restart, duplicate request and ambiguous SDK outcome recovery without blind side-effect replay. |
| Runtime containment | No long-lived credentials in image/files/environment; deny unapproved egress, metadata/token access and unrelated GitHub destinations; resume with rotated trust/policy exposes no ungoverned interval. Zero bypasses in the agreed adversarial suite. |
| Durable evidence | 100% of tested completed runs have independently verifiable manifests, source counts/boundaries and hash chains. Audit outage/backpressure prevents unsupported new governed actions; incomplete finalization yields visible recovery state, not completion. |
| Observability | CLI, orchestration, runner, governance, platform and control-plane coverage; destination-specific timestamps, redaction and explicit stream gaps. Collector and destination interruption tested independently. |
| Repository outcome | At least one representative task produces a reviewable PR with run/evidence references; protections and human merge policy remain effective. Any implemented webhook path passes signature and replay tests. |
| Performance | Measure steering acknowledgement p95 <3s, dual-destination log visibility p95 <10s and prepared-snapshot readiness p95 <60s under the approved PRD measurement contract. Retain errors/timeouts, sample counts and environment versions. |
| Agent quality | Approved numerical thresholds derived from baseline; versioned scenarios, runtime/model/configuration and scoring evidence; demonstrate that a behavior regression blocks promotion. Functional test success alone is insufficient. |
| Accessible operation | Keyboard and screen-reader testing of steering, approval, lifecycle and status flows; target WCAG 2.2 AA and record findings before any conformance claim. |
| Operations | Exercise stuck-approval, policy, egress, audit-backlog and heartbeat alerts; authorized cleanup, rollback, incident evidence retrieval and recovery procedures. |

Unmet proposed performance targets require explicit product disposition; do not silently relabel them achieved. Governance bypass, credential exposure, split writer, missing required evidence and unapproved quality thresholds block MVP release. The Must baseline cannot be waived by marking an issue done; behavior changes require PRD review.

## 7. Decisions, risks and stop conditions

| Decision/risk | Owner | Required disposition |
|---|---|---|
| ACA region, quota, private network/API support and lifetime/snapshot behavior | Basher | Close VG-01/02 with real probes before committing the deployment path. Unsupported required behavior blocks the pilot. |
| Current SDK/CLI/Squad semantics and protected hook/backstop support | Rusty | Close VG-04 with conformance tests; Linus closes VG-05. No approve-all middleware or unsafe emulation. |
| Credential type/licensing, egress transforms, rotation and identity exposure | Linus | Close VG-03/09; use only the least-privileged validated option. No token copied into the runner. |
| Audit append/retry, completeness and platform/egress event delivery | Reuben | Close VG-06/12 with source inventory and independent verification. Hash linkage alone does not establish completeness. |
| Approved feeds, auth, publication evidence and eligible package set | Linus | Close VG-10 before restore/build promotion. Missing inputs are explicit blockers. |
| Pilot retention/residency, evaluation/benchmark parameters, budget and recovery objectives | Steve | Obtain product/platform approval with Livingston before release (VG-12/13). No irreversible production retention lock. |
| Shared-session protocols and memory/search capability | Steve | VG-07/08/11 remain deferred while those features are disabled; reopening them belongs to M2 planning. |

External capability validation has not been performed for this proposal. The architecture-research skill was consulted, but its configured search script was absent locally, so no search results or fresh platform support claims are asserted. The TDD remains a proposed implementation until the validation work above closes its relevant gates.

## 8. Proposed repository deliverables

Create these only as implementation begins; they are not existing code:

- `src/ControlPlane/`, `src/Runner/`, and a small shared contracts project.
- `src/Console/` for the React/TypeScript operations UI.
- `infra/` for shared and per-project Bicep modules.
- `tools/` for Python 3.13 bootstrap, dependency eligibility, build/deploy and operational commands.
- `tests/` for contract, integration, recovery, adversarial and E2E suites; `evals/` for versioned agent scenarios/rubrics.
- Documentation for approved configuration, operator/recovery/rollback procedures, capability evidence and release verification.

Keep the first backlog implementation-ready: one accountable owner, explicit prerequisites, affected requirement IDs, a reviewer/verifier, testable acceptance and evidence links per issue. Do not create cloud resources, install dependencies, activate work-monitor loops or start application implementation merely by approving the planning document.

## 9. Approval recommendation

Approve **Stage A planning and capability validation first**, with the M0/M1 scope above held fixed. Authorize implementation after the capability gate review establishes a feasible, fail-closed path and the accountable owners accept the contracts.

The decision at MVP exit is whether the governed single-squad path is safe, useful and evidenced enough to pilot. Only then plan M2 workspace attachment, approved MCP/memory and broader project onboarding.

## 10. Stage A implementation handoff

The user authorized starting work after this proposal. Initial work delivers [draft shared contracts](./docs/mvp-contracts.md), [a requirement/evidence matrix](./docs/mvp-evidence-matrix.md), and [dependency-free Python preflight checks](./docs/preflight.md). This does not constitute acceptance of unreviewed contracts or permission to bypass the validation gates.

Read-only local checks and Azure provider metadata inspection are recorded in [the capability inventory](./docs/capability-checks.json). Installed tooling includes Python 3.13, .NET 10, Azure CLI, Bicep, Copilot CLI and Squad. Availability alone does not establish package eligibility, runtime compatibility, licensing or production readiness.

The installed Azure CLI does not recognize `az containerapp sandbox`. Microsoft.App provider metadata does advertise sandbox resource types, including sandbox group API version `2026-07-01`. These observations do not prove a deployable network/data-plane path, quota or availability in a chosen pilot region. No CLI extension was installed and no resource was provisioned.

Existing Squad diagnostics also expose setup blockers: a missing decisions file and coordinator integration, and a non-kebab-case Fact Checker registry key. They are reported rather than silently repaired or treated as successful team-runtime validation.

The user has since supplied the Sweden Central tenant/subscription target in
protected local configuration; this is not deployment authorization. Next gates
remain authenticated build-feed/provenance inputs, resource scope/budget,
real sandbox/session/governance/audit probes, and independent review of the draft
contracts. No M0/M1 capability is marked verified solely because local checks pass.

The subsequent instruction to start coding without deployment initiated an
[offline control-domain slice](./docs/development.md): validated messages,
command outcomes, conditional permission decisions and lifecycle guards.
The local no-package .NET Release build passes 13 behavioral cases, alongside
22 Python tests. This is not a running control plane or closure of the durable
storage, authorization, SDK, platform or audit validation gates.

The next offline P09 slice adds scoped writer-lease fencing and idempotency
bindings with five additional behavioral cases. The Release build using existing
restore assets and independent execution pass all 18 C# cases; 22 Python tests
also pass. Bounded independent review found no significant aggregate issues.
The standard offline restore script was denied due to its audit-disable option
and was not retried. See [development evidence](./docs/development.md) for the
validation boundary. Durable CAS, authorization and real process containment
remain unimplemented; this is not P09 completion or MVP approval.

The subsequent command-admission slice adds a mandatory authorization seam,
fixed-shape semantic fingerprints and atomic idempotency/intent documents,
plus a package-free Cosmos REST create/read/ETag-CAS adapter. Independent
no-restore Release build and execution pass 38 C# cases and 22 Python tests;
bounded independent review found no significant issues. The adapter was tested
only with fake transport, including composed service/replay behavior. No
authenticated HTTP host, real Cosmos validation, SDK dispatch or deployment
is claimed. See [the implementation boundaries](./docs/development.md).

The next offline slice connects admitted commands to authorized status reads
and ETag-conditional runner outcomes, with retained observation/reconciliation
references and terminal-result protection. Independent validation passes
44 C# cases with a clean no-restore Release build. An evidence-binding review
finding was fixed and independently rechecked. This is a tested application
service flow, not an authenticated HTTP host, actual runner/SDK integration
or real-cloud evidence. Restricted role/history reads were not retried.

The HTTP boundary slice adds an ASP.NET Core endpoint library for message
submission and command status, using the installed shared framework. It
requires explicit named authentication, server-side run/role resolution and
an approved protected-payload store; no permissive implementations are shipped.
Independent validation passes 52 C# cases, including eight real loopback HTTP
cases, plus 22 Python tests; independent review found no significant issues.
New project assets were generated with package feeds cleared and auditing
enabled, without package downloads. Tests use synthetic identity/backends;
production host/identity configuration, durable role/lease adapters and actual
runner/SDK delivery remain open. No cloud deployment or restricted role/history
read was attempted.
